using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlaUI.Core.WindowsAPI;
using Kroko.App.Audio;
using Kroko.App.Tools;
using Kroko.App.ViewModels;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Kroko.App.Services;

/// <summary>
/// Dictado en cualquier app (idea «Kroko 2», 18/11/2026): Ctrl+Shift+D y hablas; al volver a pulsarlo (o tras una pausa)
/// Gemini lo pasa a texto con tildes y puntuación y Kroko lo escribe donde está tu cursor. Si dices solo «borra eso»,
/// borra lo último que escribió. Nunca escribe en campos de contraseña (Kroko no puede ver cuál es: tú decides dónde dictas).
/// </summary>
public sealed class DictationService
{
    private readonly VoiceRecorder _recorder;
    private readonly BrainApiClient _api;
    private readonly IslandViewModel _island;
    private readonly ForegroundTracker _foreground;
    private readonly ISoundService _sounds;
    private readonly ILogger<DictationService> _log;
    private readonly DispatcherTimer _silence = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private DateTime _lastVoice;
    private bool _heard;
    private int _lastTyped;

    public DictationService(MicrophoneStream mic, BrainApiClient api, IslandViewModel island, ForegroundTracker foreground,
        ISoundService sounds, ILogger<VoiceRecorder> recorderLog, ILogger<DictationService> log)
    {
        _recorder = new VoiceRecorder(mic, recorderLog) { User = "dictado" };
        _api = api;
        _island = island;
        _foreground = foreground;
        _sounds = sounds;
        _log = log;
        _recorder.Level += level =>
        {
            if (level <= 0.08) return;
            _lastVoice = DateTime.Now;
            _heard = true;
        };
        _silence.Tick += (_, _) =>
        {
            // Una pausa de 1,8 s después de haber hablado termina el dictado
            if (_heard && (DateTime.Now - _lastVoice).TotalSeconds > 1.8) _ = FinishAsync();
        };
    }

    public bool IsActive => _recorder.IsRecording;

    public void Toggle()
    {
        if (IsActive) _ = FinishAsync();
        else Start();
    }

    private void Start()
    {
        if (!VoiceRecorder.HasMicrophone)
        {
            _island.Say("No encuentro un micrófono");
            return;
        }
        try
        {
            _recorder.Start();
        }
        catch (Exception ex)
        {
            _log.LogWarning("No pude empezar el dictado: {Error}", ex.Message);
            _island.Say("No pude usar el micrófono");
            return;
        }
        _heard = false;
        _lastVoice = DateTime.Now;
        _sounds.Play("waking");
        _island.DictationApp = AppName(_foreground.LastExternalWindow);
        _island.DictationText = "";
        _silence.Start();
    }

    private async Task FinishAsync()
    {
        _silence.Stop();
        var wav = _recorder.Stop();
        if (wav is null || !_heard)
        {
            _island.DictationApp = "";
            _island.Say("No te escuché");
            return;
        }
        _island.DictationText = "Escribiendo…";
        try
        {
            var text = (await _api.TranscribeAsync(wav, "audio/wav", CancellationToken.None)).Trim();
            _island.DictationApp = "";
            if (text.Length == 0)
            {
                _island.Say("No te entendí");
                return;
            }
            var plain = text.ToLowerInvariant().Trim('.', '!', ' ', '¡');
            if (plain is "borra eso" or "bórralo" or "borra lo último")
            {
                Press(VirtualKeyShort.BACK, _lastTyped);
                _island.Say("Borrado");
                _lastTyped = 0;
                return;
            }
            var target = _foreground.LastExternalWindow;
            if (target != 0) ForegroundTracker.SetForegroundWindow(target);
            await Task.Delay(120);
            await Task.Run(() => FlaUI.Core.Input.Keyboard.Type(text + " "));
            _lastTyped = text.Length + 1;
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            _island.Say(words == 1 ? "Escribí 1 palabra" : $"Escribí {words} palabras");
            _log.LogInformation("Dictado: {Words} palabras", words);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            _island.DictationApp = "";
            _island.Say("No pude: " + ex.Message);
        }
    }

    private static void Press(VirtualKeyShort key, int times)
    {
        for (var i = 0; i < Math.Min(times, 2000); i++) FlaUI.Core.Input.Keyboard.Type(key);
    }

    internal static string AppName(nint hwnd)
    {
        if (hwnd == 0) return "tu app";
        try
        {
            _ = ForegroundTracker.GetWindowThreadProcessId(hwnd, out var pid);
            using var p = Process.GetProcessById(pid);
            return p.ProcessName switch
            {
                "WINWORD" => "Word",
                "EXCEL" => "Excel",
                "POWERPNT" => "PowerPoint",
                "chrome" => "Chrome",
                "msedge" => "Edge",
                "firefox" => "Firefox",
                "notepad" => "el Bloc de notas",
                "WhatsApp" or "WhatsApp.Root" => "WhatsApp",
                var n => n,
            };
        }
        catch (Exception) { return "tu app"; }
    }
}

/// <summary>
/// Burbuja al seleccionar texto (idea «Kroko 2»): seleccionas en cualquier programa, pulsas Ctrl+Shift+K y aparece una
/// mini barra con Traducir, Explicar, Corregir y Responder. Kroko copia la selección un instante (y devuelve tu
/// portapapeles como estaba) y la respuesta sale en su panel.
/// </summary>
public sealed class SelectionHelper(WindowService windows, ClipboardWatcher clipboard, IslandViewModel island, ILogger<SelectionHelper> log)
{
    public async void Show()
    {
        try
        {
            var before = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            clipboard.IgnoreNext();
            Clipboard.Clear();
            FlaUI.Core.Input.Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
            await Task.Delay(180);
            var selected = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            clipboard.IgnoreNext();
            if (before is not null) Clipboard.SetText(before);
            if (selected.Length == 0)
            {
                island.Say("Selecciona un texto primero");
                return;
            }
            windows.ShowSelection(selected);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
        {
            log.LogDebug("No pude leer la selección: {Error}", ex.Message);
        }
    }
}

/// <summary>
/// Modo reunión (idea «Kroko 2»): «Kroko, toma nota de esta reunión». Graba tu micrófono y lo que suena en la PC (Meet,
/// Zoom, Teams…) mientras la isla muestra un punto rojo; al terminar lo transcribe con Gemini por partes, guarda la
/// transcripción en Documentos\Kroko\Reuniones y le pide a Kroko el resumen, los acuerdos y las tareas (que anota).
/// Avisa a los demás que estás tomando notas: grabar sin consentimiento puede no ser legal donde vives.
/// </summary>
public sealed class MeetingRecorder
{
    private const int ChunkMinutes = 8;
    private readonly VoiceRecorder _mic;
    private readonly BrainApiClient _api;
    private readonly IslandViewModel _island;
    private readonly PanelViewModel _chat;
    private readonly ILogger<MeetingRecorder> _log;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private WasapiLoopbackCapture? _loopback;
    private MemoryStream? _system;
    private WaveFormat? _systemFormat;
    private DateTime _started;

    public MeetingRecorder(MicrophoneStream mic, BrainApiClient api, IslandViewModel island, PanelViewModel chat,
        ILogger<VoiceRecorder> recorderLog, ILogger<MeetingRecorder> log)
    {
        _mic = new VoiceRecorder(mic, recorderLog) { User = "reunion" };
        _api = api;
        _island = island;
        _chat = chat;
        _log = log;
        _clock.Tick += (_, _) => _island.MeetingClock = (DateTime.Now - _started).ToString(@"h\:mm\:ss").TrimStart('0', ':');
        island.StopMeeting = () => _ = StopAsync();
    }

    public bool IsActive => _mic.IsRecording;

    public string Start()
    {
        if (IsActive) return "Ya estoy tomando nota de la reunión.";
        _mic.Start();
        try
        {
            _loopback = new WasapiLoopbackCapture();
            _systemFormat = _loopback.WaveFormat;
            _system = new MemoryStream();
            _loopback.DataAvailable += (_, e) => { lock (_system) _system.Write(e.Buffer, 0, e.BytesRecorded); };
            _loopback.StartRecording();
        }
        catch (Exception ex)
        {
            _log.LogWarning("Sin audio del sistema (solo micrófono): {Error}", ex.Message);
            _loopback = null;
        }
        _started = DateTime.Now;
        _island.MeetingNote = "tomando nota";
        _island.MeetingClock = "0:00";
        _clock.Start();
        _log.LogInformation("Modo reunión: grabando");
        return "Listo, estoy tomando nota. Avisa a los demás que estás tomando notas. Di «termina la reunión» o pulsa Terminar en la isla.";
    }

    public async Task<string> StopAsync()
    {
        if (!IsActive) return "No estaba tomando nota de ninguna reunión.";
        _clock.Stop();
        var mic = _mic.Stop();
        byte[]? system = null;
        if (_loopback is { } lb)
        {
            lb.StopRecording();
            lb.Dispose();
            lock (_system!) system = _system.ToArray();
        }
        _loopback = null;
        _island.MeetingNote = "transcribiendo…";
        try
        {
            var mixed = await Task.Run(() => Mix(mic, system, _systemFormat));
            var parts = Split(mixed, ChunkMinutes);
            var text = new System.Text.StringBuilder();
            for (var i = 0; i < parts.Count; i++)
            {
                _island.MeetingNote = parts.Count == 1 ? "transcribiendo…" : $"transcribiendo {i + 1} de {parts.Count}…";
                text.AppendLine(await _api.TranscribeAsync(parts[i], "audio/wav", CancellationToken.None));
            }
            var transcript = text.ToString().Trim();
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Kroko", "Reuniones");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"Reunión {_started:yyyy-MM-dd HH.mm}.txt");
            await File.WriteAllTextAsync(file, transcript);
            _island.MeetingClock = "";
            if (transcript.Length == 0) return "No escuché palabras en la reunión.";
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _chat.InputText = $"Terminó una reunión (transcripción guardada en «{file}»). Dame un resumen breve, los acuerdos y las tareas; " +
                                  $"anota cada tarea con tarea_crear (con fecha si se dijo).\n\nTranscripción:\n{transcript}";
                if (_chat.SendCommand.CanExecute(null)) _chat.SendCommand.Execute(null);
            });
            return $"Listo: transcribí la reunión y la guardé en {file}.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
        {
            _island.MeetingClock = "";
            _log.LogWarning("Modo reunión: {Error}", ex.Message);
            return "No pude transcribir la reunión: " + ex.Message;
        }
    }

    /// <summary>Mezcla el micrófono (16 kHz mono) con el audio del sistema (convertido a 16 kHz mono) en un WAV.</summary>
    internal static byte[] Mix(byte[]? micWav, byte[]? systemRaw, WaveFormat? systemFormat)
    {
        var target = new WaveFormat(16000, 16, 1);
        float[] a = micWav is null ? [] : ToFloats(new WaveFileReader(new MemoryStream(micWav)));
        float[] b = [];
        if (systemRaw is { Length: > 0 } && systemFormat is not null)
        {
            var raw = new RawSourceWaveStream(new MemoryStream(systemRaw), systemFormat);
            ISampleProvider sp = raw.ToSampleProvider();
            if (sp.WaveFormat.Channels == 2) sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
            else if (sp.WaveFormat.Channels > 2) sp = new MultiplexingSampleProvider([sp], 1); // el canal izquierdo basta para entender
            sp = new WdlResamplingSampleProvider(sp, 16000);
            b = ReadAll(sp);
        }
        var n = Math.Max(a.Length, b.Length);
        var pcm = new byte[n * 2];
        for (var i = 0; i < n; i++)
        {
            var s = (i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] * 0.8f : 0);
            var v = (short)Math.Clamp(s * 32767, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)v;
            pcm[i * 2 + 1] = (byte)(v >> 8);
        }
        return Wav.FromPcm16(pcm, target.SampleRate);
    }

    private static float[] ToFloats(WaveFileReader reader)
    {
        using (reader) return ReadAll(reader.ToSampleProvider());
    }

    private static float[] ReadAll(ISampleProvider sp)
    {
        var all = new List<float>();
        var buffer = new float[16000];
        int read;
        while ((read = sp.Read(buffer.AsSpan())) > 0) all.AddRange(buffer.AsSpan(0, read).ToArray());
        return [.. all];
    }

    /// <summary>Parte un WAV de 16 kHz mono en trozos de N minutos (Gemini acepta ~18 MB por vez).</summary>
    internal static List<byte[]> Split(byte[] wav, int minutes)
    {
        const int headerSize = 44;
        var pcm = wav.AsSpan(headerSize);
        var chunk = 16000 * 2 * 60 * minutes;
        var parts = new List<byte[]>();
        for (var i = 0; i < pcm.Length; i += chunk)
            parts.Add(Wav.FromPcm16(pcm.Slice(i, Math.Min(chunk, pcm.Length - i)).ToArray(), 16000));
        return parts;
    }
}

/// <summary>«Kroko, toma nota de esta reunión» / «termina la reunión».</summary>
public sealed class ModoReunionTool(IServiceProvider services) : ILocalTool
{
    public string Name => "modo_reunion";

    private MeetingRecorder Meeting => Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<MeetingRecorder>(services);

    public async Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct) => args.String("accion") switch
    {
        "iniciar" => ToolOutcome.Success(new { mensaje = await Application.Current.Dispatcher.InvokeAsync(Meeting.Start) }),
        "terminar" => ToolOutcome.Success(new { mensaje = await await Application.Current.Dispatcher.InvokeAsync(Meeting.StopAsync) }),
        _ => throw new ToolException("accion debe ser iniciar o terminar."),
    };
}
