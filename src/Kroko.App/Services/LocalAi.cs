using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kroko.App.Services;

/// <summary>
/// La IA que corre en esta PC (pedido por Pol, 17/11/2026: «aquí usan IA local, para que no cargue todo Gemini»).
/// Descarga una sola vez llama.cpp (Vulkan) y Qwen 3 4B (≈2,5 GB) en %LOCALAPPDATA%\Kroko\ia-local, arranca su servidor
/// en 127.0.0.1 (solo esta PC lo ve) y responde las peticiones que le manda el cerebro (<see cref="LocalLlmRequestMessage"/>).
/// El cerebro solo le pasa lo simple; lo difícil sigue con Gemini.
/// </summary>
public sealed class LocalAiService : IDisposable
{
    private readonly LocalAiSettings _opt;
    private readonly BrainConnection _brain;
    private readonly ILogger<LocalAiService> _log;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private Process? _server;
    private CancellationTokenSource? _download;

    public LocalAiService(IOptions<KrokoOptions> options, BrainConnection brain, ILogger<LocalAiService> log)
    {
        _opt = options.Value.LocalAi;
        _brain = brain;
        _log = log;
        _brain.MessageReceived += m =>
        {
            if (m is LocalLlmRequestMessage request) _ = AnswerAsync(request);
        };
    }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", "ia-local");
    private static string ServerFolder => Path.Combine(Folder, "llama");
    private string ServerExe => Path.Combine(ServerFolder, "llama-server.exe");
    private string ModelPath => Path.Combine(Folder, Path.GetFileName(new Uri(_opt.ModelUrl).LocalPath));

    public bool IsInstalled => File.Exists(ServerExe) && File.Exists(ModelPath) && new FileInfo(ModelPath).Length > 500_000_000;

    /// <summary>Encendida, instalada y con su servidor respondiendo.</summary>
    public bool IsReady { get; private set; }

    public bool IsDownloading => _download is not null;

    public string ModelName => _opt.ModelName;

    public bool Enabled
    {
        get => _opt.Enabled;
        set => _opt.Enabled = value;
    }

    /// <summary>Cambió el estado (lista, apagada, descargando…): la App vuelve a avisarle al cerebro.</summary>
    public event Action? StateChanged;

    // ---------------------------------------------------------------- descarga (solo cuando el dueño la pide)

    /// <summary>Descarga llama.cpp y el modelo. progress: 0..1 y un texto («Descargando el modelo… 1,2 de 2,5 GB»).</summary>
    public async Task DownloadAsync(IProgress<(double Value, string Text)> progress)
    {
        if (_download is not null) return;
        _download = new CancellationTokenSource();
        var ct = _download.Token;
        try
        {
            Directory.CreateDirectory(Folder);
            if (!File.Exists(ServerExe))
            {
                var zip = Path.Combine(Folder, "llama.zip");
                await DownloadFileAsync(_opt.ServerZipUrl, zip, "el motor de IA", 0, 0.05, progress, ct);
                progress.Report((0.05, "Instalando el motor de IA…"));
                if (Directory.Exists(ServerFolder)) Directory.Delete(ServerFolder, recursive: true);
                ZipFile.ExtractToDirectory(zip, ServerFolder);
                File.Delete(zip);
                // Algunas versiones traen todo dentro de una subcarpeta
                if (!File.Exists(ServerExe) && Directory.EnumerateFiles(ServerFolder, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault() is { } inner)
                {
                    var dir = Path.GetDirectoryName(inner)!;
                    foreach (var file in Directory.EnumerateFiles(dir)) File.Move(file, Path.Combine(ServerFolder, Path.GetFileName(file)), true);
                }
            }
            if (!File.Exists(ModelPath) || new FileInfo(ModelPath).Length < 500_000_000)
                await DownloadFileAsync(_opt.ModelUrl, ModelPath, "el modelo", 0.05, 1, progress, ct);
            progress.Report((1, "Listo. Encendiendo la IA de tu PC…"));
            _opt.Enabled = true;
            await StartAsync();
        }
        finally
        {
            _download.Dispose();
            _download = null;
            StateChanged?.Invoke();
        }
    }

    public void CancelDownload() => _download?.Cancel();

    private async Task DownloadFileAsync(string url, string target, string what, double from, double to,
        IProgress<(double, string)> progress, CancellationToken ct)
    {
        var part = target + ".parte";
        long already = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (already > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(already, null); // sigue donde quedó
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        if (res.StatusCode != System.Net.HttpStatusCode.PartialContent) already = 0;
        var total = already + (res.Content.Headers.ContentLength ?? 0);

        await using (var input = await res.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(part, already > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buffer = new byte[1 << 20];
            var done = already;
            var lastReport = DateTime.MinValue;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if ((DateTime.Now - lastReport).TotalMilliseconds < 250) continue;
                lastReport = DateTime.Now;
                var f = total > 0 ? (double)done / total : 0;
                progress.Report((from + (to - from) * f, total > 0
                    ? $"Descargando {what}… {done / 1e9:0.00} de {total / 1e9:0.00} GB"
                    : $"Descargando {what}… {done / 1e6:0} MB"));
            }
        }
        File.Move(part, target, overwrite: true);
    }

    // ---------------------------------------------------------------- servidor local

    /// <summary>Arranca llama-server si está instalada y encendida (al abrir Kroko y al terminar de descargar).</summary>
    public async Task StartAsync()
    {
        if (!_opt.Enabled || !IsInstalled || IsReady) return;
        try
        {
            if (!await HealthyAsync())
            {
                var args = $"-m \"{ModelPath}\" --host 127.0.0.1 --port {_opt.Port} -c {_opt.ContextSize} -ngl {_opt.GpuLayers} --jinja --no-webui";
                _server = Process.Start(new ProcessStartInfo(ServerExe, args)
                {
                    WorkingDirectory = ServerFolder,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                });
                _server?.BeginErrorReadLine();
                _server?.BeginOutputReadLine();
                for (var i = 0; i < 120 && !await HealthyAsync(); i++) // el modelo tarda unos segundos en cargarse
                {
                    if (_server is { HasExited: true }) throw new InvalidOperationException("el motor de IA se cerró al arrancar");
                    await Task.Delay(500);
                }
            }
            IsReady = await HealthyAsync();
            _log.LogInformation(IsReady ? "IA de la PC lista ({Model})" : "La IA de la PC no arrancó a tiempo ({Model})", _opt.ModelName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            _log.LogWarning("No pude arrancar la IA de la PC: {Message}", ex.Message);
            IsReady = false;
        }
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        IsReady = false;
        try
        {
            if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        _server?.Dispose();
        _server = null;
        StateChanged?.Invoke();
    }

    private async Task<bool> HealthyAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var res = await _http.GetAsync($"http://127.0.0.1:{_opt.Port}/health", cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- pensar (lo pide el cerebro)

    private async Task AnswerAsync(LocalLlmRequestMessage request)
    {
        LocalLlmResultMessage result;
        try
        {
            if (!IsReady) throw new InvalidOperationException("la IA de la PC no está encendida");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var content = new StringContent(request.Body, Encoding.UTF8, "application/json");
            var started = Stopwatch.StartNew();
            using var res = await _http.PostAsync($"http://127.0.0.1:{_opt.Port}/v1/chat/completions", content, cts.Token);
            var body = await res.Content.ReadAsStringAsync(cts.Token);
            _log.LogInformation("IA de la PC respondió en {Ms} ms", started.ElapsedMilliseconds);
            result = res.IsSuccessStatusCode
                ? new LocalLlmResultMessage { CallId = request.CallId, Ok = true, Body = body }
                : new LocalLlmResultMessage { CallId = request.CallId, Ok = false, Error = $"{(int)res.StatusCode}: {(body.Length > 200 ? body[..200] : body)}" };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            result = new LocalLlmResultMessage { CallId = request.CallId, Ok = false, Error = ex.Message };
        }
        await _brain.SendAsync(result);
    }

    public void Dispose()
    {
        _download?.Cancel();
        Stop();
        _http.Dispose();
    }
}
