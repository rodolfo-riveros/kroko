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
    private readonly bool _requireAtStart;
    private readonly double _minConfidence;

    // Últimos segundos de audio con su posición en el flujo (Vosk da los tiempos de cada palabra desde que se creó el
    // reconocedor, también después de Reset): sirve para saber si hablabas justo antes de «oye kroko».
    private const int RecentSamples = 16000 * 5;
    private const double PauseBeforeSeconds = 0.6;
    private const int SpeechRms = 1200; // de 32768: voz clara; el ruido de una habitación suele quedar por debajo de 400
    private readonly short[] _recent = new short[RecentSamples];
    private long _samples;

    /// <param name="requireAtStart">
    /// La frase tiene que abrir lo que dices: ni en medio de la frase ni justo después de otras palabras (hace falta una
    /// pausa antes). Así no despierta cuando conversas con alguien (decidido por Pol, 14/11/2026).
    /// </param>
    /// <param name="minConfidence">Seguridad mínima de Vosk en cada palabra de la frase (0 = no se mira).</param>
    public WakeWordDetector(Model model, IEnumerable<string> phrases, IEnumerable<string> decoys,
        bool requireAtStart = true, double minConfidence = 0.8)
    {
        _phrases = phrases.Select(Normalize).Where(p => p.Length > 0).Distinct().ToArray();
        _requireAtStart = requireAtStart;
        _minConfidence = Math.Clamp(minConfidence, 0, 1);
        var grammar = JsonSerializer.Serialize(_phrases.Concat(decoys.Select(Normalize)).Append("[unk]").Distinct());
        _recognizer = new VoskRecognizer(model, 16000f, grammar);
        _recognizer.SetWords(true); // con la seguridad y el tiempo de cada palabra
    }

    public WakeDetection? Feed(byte[] pcm, int count)
    {
        if (_utterance.Length + count > MaxUtteranceBytes) _utterance.SetLength(0);
        _utterance.Write(pcm, 0, count);
        for (var i = 0; i + 1 < count; i += 2)
            _recent[(int)(_samples++ % RecentSamples)] = BitConverter.ToInt16(pcm, i);
        if (!_recognizer.AcceptWaveform(pcm, count)) return null;
        var json = _recognizer.Result();
        return Decide(Text(json), Words(json));
    }

    /// <summary>¿Hubo voz en los PauseBeforeSeconds anteriores a ese momento (en segundos del flujo)?</summary>
    internal bool SpokeRightBefore(double startSeconds)
    {
        var end = (long)(startSeconds * 16000) - 800; // 50 ms de margen antes de la palabra
        var begin = end - (long)(PauseBeforeSeconds * 16000);
        if (end <= 0) return false;
        const int frame = 320; // 20 ms
        var voiced = 0;
        for (var f = Math.Max(0, begin); f + frame <= end; f += frame)
        {
            if (_samples - f > RecentSamples) continue; // ya no está en el búfer
            double sum = 0;
            for (var s = f; s < f + frame; s++)
            {
                double v = _recent[(int)(s % RecentSamples)];
                sum += v * v;
            }
            if (Math.Sqrt(sum / frame) >= SpeechRms) voiced++;
        }
        return voiced * 20 >= 150; // más de 150 ms de voz justo antes: venías hablando
    }

    /// <summary>Vacía el estado (tras grabar una orden, para no arrastrar audio viejo).</summary>
    public void Reset()
    {
        _recognizer.Reset();
        _utterance.SetLength(0);
    }

    internal WakeDetection? Decide(string heard, IReadOnlyList<(string Word, double Conf, double Start)> words)
    {
        var audio = _utterance.ToArray();
        _utterance.SetLength(0);
        heard = Normalize(heard);
        if (heard.Length == 0) return null;

        foreach (var phrase in _phrases)
        {
            var at = (" " + heard + " ").IndexOf(" " + phrase + " ", StringComparison.Ordinal);
            if (at < 0) continue;
            // Estricto (14/11/2026): la frase tiene que abrir lo que dijiste, no aparecer en medio de una charla
            if (_requireAtStart && at != 0) continue;
            var first = FindPhrase(phrase, words);
            if (_minConfidence > 0 && words.Count > 0 && (first < 0 || !Confident(phrase, words, first))) continue;
            // …ni justo después de otras palabras: Vosk corta "le dije a mi amigo | oye kroko" en dos frases
            if (_requireAtStart && first >= 0 && SpokeRightBefore(words[first].Start)) continue;
            var after = heard[Math.Min(heard.Length, at + phrase.Length)..].Trim();
            return new WakeDetection(heard, after.Length > 0, audio);
        }
        return null;
    }

    private static int FindPhrase(string phrase, IReadOnlyList<(string Word, double Conf, double Start)> words)
    {
        var parts = phrase.Split(' ');
        for (var i = 0; i + parts.Length <= words.Count; i++)
            if (parts.Select((p, j) => words[i + j].Word == p).All(x => x)) return i;
        return -1;
    }

    /// <summary>Cada palabra de la frase tiene que venir con seguridad suficiente.</summary>
    private bool Confident(string phrase, IReadOnlyList<(string Word, double Conf, double Start)> words, int first) =>
        phrase.Split(' ').Select((_, j) => words[first + j].Conf).All(c => c >= _minConfidence);

    private static List<(string Word, double Conf, double Start)> Words(string json)
    {
        var list = new List<(string, double, double)>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
            foreach (var w in result.EnumerateArray())
                list.Add((Normalize(w.TryGetProperty("word", out var word) ? word.GetString() ?? "" : ""),
                          w.TryGetProperty("conf", out var conf) && conf.TryGetDouble(out var c) ? c : 1.0,
                          w.TryGetProperty("start", out var start) && start.TryGetDouble(out var s) ? s : 0));
        return list;
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
                detector ??= new WakeWordDetector(model, wake.EffectivePhrases, wake.EffectiveDecoys, wake.RequireAtStart, wake.MinConfidence);

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
