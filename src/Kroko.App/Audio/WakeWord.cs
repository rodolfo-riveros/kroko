using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Kroko.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vosk;

namespace Kroko.App.Audio;

/// <summary>
/// Modelo de reconocimiento de voz local (Vosk, español). Se descarga una sola vez del sitio oficial,
/// se verifica con SHA-256 y se guarda en %LOCALAPPDATA%\Kroko\voz. Se carga en memoria una sola vez.
/// </summary>
public sealed class VoskModelManager(IOptions<KrokoOptions> options, ILogger<VoskModelManager> log) : IDisposable
{
    private readonly Lock _lock = new();
    private Model? _model;

    private WakeWordSettings Settings => options.Value.Voice.WakeWord;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", "voz");

    public string ModelPath => Path.Combine(Folder, Settings.ModelName);

    public bool IsInstalled => File.Exists(Path.Combine(ModelPath, "am", "final.mdl"));

    /// <summary>El modelo cargado (null si no está instalado o no se pudo cargar).</summary>
    public Model? Load()
    {
        lock (_lock)
        {
            if (_model is not null) return _model;
            if (!IsInstalled) return null;
            try
            {
                Vosk.Vosk.SetLogLevel(-1);
                _model = new Model(ModelPath);
                log.LogInformation("Modelo de voz local cargado ({Name})", Settings.ModelName);
            }
            catch (Exception ex) when (ex is IOException or DllNotFoundException or BadImageFormatException or InvalidOperationException)
            {
                log.LogWarning(ex, "No se pudo cargar el modelo de voz local");
            }
            return _model;
        }
    }

    /// <summary>Descarga, verifica y descomprime el modelo. progress: 0–1.</summary>
    public async Task DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var s = Settings;
        if (!s.ModelUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La dirección del modelo debe ser https.");

        Directory.CreateDirectory(Folder);
        var zip = Path.Combine(Folder, s.ModelName + ".zip.part");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
        using (var res = await http.GetAsync(s.ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? s.ModelBytes;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress?.Report(Math.Min(0.97, (double)done / Math.Max(1, total)));
            }
        }

        // Nada que no sea exactamente el archivo esperado se descomprime
        await using (var check = File.OpenRead(zip))
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct));
            if (!string.Equals(hash, s.ModelSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(zip);
                throw new InvalidDataException("El archivo descargado no coincide con el oficial (SHA-256). No lo instalé.");
            }
        }

        var target = Path.GetFullPath(Folder);
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries)
            {
                var path = Path.GetFullPath(Path.Combine(target, entry.FullName));
                if (!path.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("El zip contiene rutas fuera de la carpeta del modelo.");
                if (entry.FullName.EndsWith('/')) Directory.CreateDirectory(path);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    entry.ExtractToFile(path, overwrite: true);
                }
            }
        }
        File.Delete(zip);
        progress?.Report(1);
        log.LogInformation("Modelo de voz local instalado en {Path}", ModelPath);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _model?.Dispose();
            _model = null;
        }
    }
}

/// <summary>Lo que oyó el detector: si había una orden después de «Oye Kroko» y el audio de toda la frase.</summary>
public sealed record WakeDetection(string Heard, bool HasCommand, byte[] UtterancePcm);

/// <summary>
/// Detector de la frase de activación sobre un flujo PCM 16 kHz. Solo decide cuando Vosk cierra una frase
/// (no con resultados a medias): así casi no hay falsas alarmas. Guarda el audio de la frase en curso
/// para poder enviar "oye kroko, abre Excel" de una sola vez.
/// </summary>
public sealed class WakeWordDetector : IDisposable
{
    private const int MaxUtteranceBytes = 16000 * 2 * 15; // 15 s
    private readonly VoskRecognizer _recognizer;
    private readonly string[] _phrases;
    private readonly MemoryStream _utterance = new();

    public WakeWordDetector(Model model, IEnumerable<string> phrases, IEnumerable<string> decoys)
    {
        _phrases = phrases.Select(Normalize).Where(p => p.Length > 0).Distinct().ToArray();
        var grammar = JsonSerializer.Serialize(_phrases.Concat(decoys.Select(Normalize)).Append("[unk]").Distinct());
        _recognizer = new VoskRecognizer(model, 16000f, grammar);
    }

    public WakeDetection? Feed(byte[] pcm, int count)
    {
        if (_utterance.Length + count > MaxUtteranceBytes) _utterance.SetLength(0);
        _utterance.Write(pcm, 0, count);
        if (!_recognizer.AcceptWaveform(pcm, count)) return null;
        return Decide(Text(_recognizer.Result()));
    }

    /// <summary>Vacía el estado (tras grabar una orden, para no arrastrar audio viejo).</summary>
    public void Reset()
    {
        _recognizer.Reset();
        _utterance.SetLength(0);
    }

    private WakeDetection? Decide(string heard)
    {
        var audio = _utterance.ToArray();
        _utterance.SetLength(0);
        heard = Normalize(heard);
        if (heard.Length == 0) return null;

        // La frase puede venir al principio o en medio ("ya, oye kroko, apaga…")
        foreach (var phrase in _phrases)
        {
            var at = (" " + heard + " ").IndexOf(" " + phrase + " ", StringComparison.Ordinal);
            if (at < 0) continue;
            var after = heard[Math.Min(heard.Length, at + phrase.Length)..].Trim();
            return new WakeDetection(heard, after.Length > 0, audio);
        }
        return null;
    }

    internal static string Normalize(string s) => string.Join(' ',
        s.ToLowerInvariant().Split([' ', ',', '.', '¡', '!', '¿', '?'], StringSplitOptions.RemoveEmptyEntries));

    private static string Text(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
    }

    public void Dispose()
    {
        _recognizer.Dispose();
        _utterance.Dispose();
    }
}

/// <summary>Subtítulos en vivo: reconocimiento libre (sin gramática) mientras grabas una orden.</summary>
public sealed class LiveTranscriber(Model model) : IDisposable
{
    private readonly VoskRecognizer _recognizer = new(model, 16000f);
    private string _final = "";

    /// <summary>Texto acumulado hasta ahora (frases cerradas + la que está en curso).</summary>
    public string Feed(byte[] pcm, int count)
    {
        if (_recognizer.AcceptWaveform(pcm, count))
        {
            var text = Read(_recognizer.Result(), "text");
            if (text.Length > 0) _final = (_final + " " + text).Trim();
            return _final;
        }
        return (_final + " " + Read(_recognizer.PartialResult(), "partial")).Trim();
    }

    private static string Read(string json, string field)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(field, out var t) ? t.GetString() ?? "" : "";
    }

    public void Dispose() => _recognizer.Dispose();
}

/// <summary>
/// Escucha siempre (en esta PC, nada sale a internet) la frase «Oye Kroko» / «Hola Kroko».
/// El reconocimiento corre en un hilo aparte para no afectar la animación; los avisos llegan al hilo de la interfaz.
/// Mientras se graba una orden, el mismo hilo hace los subtítulos en vivo.
/// </summary>
public sealed class WakeWordService : IDisposable
{
    private const string User = "activacion";

    private readonly MicrophoneStream _mic;
    private readonly VoiceRecorder _recorder;
    private readonly VoskModelManager _models;
    private readonly KrokoOptions _options;
    private readonly ILogger<WakeWordService> _log;
    private readonly System.Windows.Threading.Dispatcher _ui;
    private Channel<(byte[] Pcm, int Generation)>? _queue;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private bool _fullscreen;
    private bool _userPaused;
    private volatile bool _captionsWanted;
    // Cada grabación tiene su número: 0 = escuchando la frase. Así nada de una orden vieja se mezcla con la nueva.
    private volatile int _generation;
    private int _lastGeneration;

    public WakeWordService(MicrophoneStream mic, VoiceRecorder recorder, VoskModelManager models, IOptions<KrokoOptions> options,
        ILogger<WakeWordService> log)
    {
        _mic = mic;
        // Conectaste (o quitaste) un micrófono con Kroko abierto: empieza (o deja) de escuchar sin reiniciar
        _mic.DevicesChanged += _ => Refresh();
        _recorder = recorder;
        _models = models;
        _options = options.Value;
        _log = log;
        _ui = System.Windows.Application.Current.Dispatcher;
        _recorder.Chunk += OnRecordedChunk;
    }

    /// <summary>Oyó «Oye Kroko» (en el hilo de la interfaz).</summary>
    public event Action<WakeDetection>? Detected;

    /// <summary>Subtítulo en vivo de lo que estás diciendo (en el hilo de la interfaz).</summary>
    public event Action<string>? Caption;

    /// <summary>Cambió si está escuchando la frase (para la bandeja y Ajustes).</summary>
    public event Action<bool>? ListeningChanged;

    public bool IsModelInstalled => _models.IsInstalled;

    /// <summary>true = el micrófono está abierto esperando «Oye Kroko».</summary>
    public bool IsListening => _mic.IsUsedBy(User);

    /// <summary>Pausa elegida por el usuario desde la bandeja (no se guarda).</summary>
    public bool UserPaused
    {
        get => _userPaused;
        set
        {
            _userPaused = value;
            Refresh();
        }
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _queue = Channel.CreateBounded<(byte[], int)>(new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest });
        _worker = Task.Run(() => RunAsync(_cts.Token));
        Refresh();
    }

    /// <summary>Lo llama la burbuja al entrar o salir de pantalla completa.</summary>
    public void SetFullscreen(bool fullscreen)
    {
        _fullscreen = fullscreen;
        Refresh();
    }

    /// <summary>Tras instalar el modelo o cambiar ajustes.</summary>
    public void Refresh()
    {
        var wake = _options.Voice.WakeWord;
        var want = wake.Enabled && !_userPaused && (!_fullscreen || wake.ListenInFullscreen) && _models.IsInstalled
                   && MicrophoneStream.HasMicrophone && _worker is not null;
        var listening = IsListening;
        if (want && !listening)
        {
            _mic.Data += OnMicData;
            if (_mic.TryAcquire(User, _options.Voice.MicrophoneDevice)) _log.LogInformation("Escuchando «Oye Kroko»");
        }
        else if (!want && listening)
        {
            _mic.Data -= OnMicData;
            _mic.Release(User);
            _log.LogInformation("Ya no escucho «Oye Kroko»");
        }
        if (want != listening) ListeningChanged?.Invoke(want);
    }

    private volatile TaskCompletionSource<bool?>? _answer;

    /// <summary>
    /// Escucha un «sí, hazlo» o un «cancela» durante unos segundos (para aprobar por voz las acciones medianas).
    /// true = sí, false = no, null = no respondió o no se entendió.
    /// </summary>
    public async Task<bool?> AskYesNoAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (!_models.IsInstalled || !MicrophoneStream.HasMicrophone || _worker is null) return null;
        const string user = "respuesta";
        var borrowed = !IsListening;
        if (borrowed)
        {
            _mic.Data += OnMicData;
            if (!_mic.TryAcquire(user, _options.Voice.MicrophoneDevice))
            {
                _mic.Data -= OnMicData;
                return null;
            }
        }
        var tcs = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _answer = tcs;
        try
        {
            return await tcs.Task.WaitAsync(timeout, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _answer = null;
            if (borrowed)
            {
                _mic.Data -= OnMicData;
                _mic.Release(user);
            }
        }
    }

    /// <summary>Al empezar a grabar una orden: los subtítulos se calculan en el hilo del detector.</summary>
    public void BeginCaptions()
    {
        _captionsWanted = _options.Voice.LiveCaptions && _models.IsInstalled;
        _generation = Math.Max(1, _lastGeneration + 1);
        _lastGeneration = _generation;
    }

    /// <summary>Al terminar de grabar: se vuelve a escuchar la frase desde cero (sin el audio de la orden).</summary>
    public void EndCaptions()
    {
        _captionsWanted = false;
        _generation = 0;
    }

    private void OnMicData(byte[] buffer, int count)
    {
        // Mientras graba una orden, el detector no escucha (la grabadora manda los bloques para subtítulos)
        if (_recorder.IsRecording) return;
        _queue?.Writer.TryWrite((buffer[..count], 0));
    }

    private void OnRecordedChunk(byte[] buffer, int count)
    {
        if (_captionsWanted) _queue?.Writer.TryWrite((buffer[..count], _generation));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        WakeWordDetector? detector = null;
        LiveTranscriber? captions = null;
        VoskRecognizer? answerRec = null;
        TaskCompletionSource<bool?>? answerFor = null;
        try
        {
            var current = 0;
            await foreach (var (pcm, generation) in _queue!.Reader.ReadAllAsync(ct))
            {
                if (generation != _generation) continue; // audio de una grabación que ya terminó
                var model = _models.Load();
                if (model is null) continue;
                var wake = _options.Voice.WakeWord;
                detector ??= new WakeWordDetector(model, wake.EffectivePhrases, wake.EffectiveDecoys);

                if (generation != current)
                {
                    // Empieza o termina una grabación: subtítulos nuevos y detector limpio
                    current = generation;
                    captions?.Dispose();
                    captions = null;
                    detector.Reset();
                }

                if (generation > 0)
                {
                    captions ??= new LiveTranscriber(model);
                    var text = captions.Feed(pcm, pcm.Length);
                    if (text.Length > 0) _ = _ui.BeginInvoke(() => { if (_generation == generation) Caption?.Invoke(text); });
                    continue;
                }

                // Esperando «sí, hazlo» o «cancela»: esas frases tienen prioridad sobre «Oye Kroko»
                if (_answer is { } ask)
                {
                    if (!ReferenceEquals(answerFor, ask))
                    {
                        answerRec?.Dispose();
                        answerRec = new VoskRecognizer(model, 16000f, JsonSerializer.Serialize(
                            Kroko.Shared.Tools.VoiceConfirm.YesPhrases.Concat(Kroko.Shared.Tools.VoiceConfirm.NoPhrases).Append("[unk]")));
                        answerFor = ask;
                    }
                    if (answerRec!.AcceptWaveform(pcm, pcm.Length))
                    {
                        using var doc = JsonDocument.Parse(answerRec.Result());
                        var heard = doc.RootElement.GetProperty("text").GetString() ?? "";
                        if (Kroko.Shared.Tools.VoiceConfirm.Interpret(heard) is { } yes)
                        {
                            _log.LogInformation("Respuesta por voz: «{Heard}»", heard);
                            ask.TrySetResult(yes);
                        }
                    }
                    continue;
                }

                if (detector.Feed(pcm, pcm.Length) is { } hit)
                {
                    _log.LogInformation("Activación: «{Heard}» (orden incluida: {HasCommand})", hit.Heard, hit.HasCommand);
                    _ = _ui.BeginInvoke(() => Detected?.Invoke(hit));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "El detector de «Oye Kroko» se detuvo");
        }
        finally
        {
            detector?.Dispose();
            captions?.Dispose();
            answerRec?.Dispose();
        }
    }

    public void Dispose()
    {
        _recorder.Chunk -= OnRecordedChunk;
        if (IsListening)
        {
            _mic.Data -= OnMicData;
            _mic.Release(User);
        }
        _cts?.Cancel();
        _queue?.Writer.TryComplete();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException) { }
        _cts?.Dispose();
    }
}
