using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Kroko.App.Audio;

/// <summary>
/// Graba lo que dices (WAV 16 kHz mono 16 bits, lo que espera el Brain) desde el micrófono compartido
/// y da el nivel de voz (0–1) para el anillo y el detector de silencio del modo "toque".
/// </summary>
public sealed class VoiceRecorder(MicrophoneStream mic, ILogger<VoiceRecorder> log) : IDisposable
{
    private const string User = "grabacion";
    public static readonly WaveFormat Format = MicrophoneStream.Format;

    private MemoryStream? _pcm;
    private readonly Lock _lock = new();

    public bool IsRecording { get; private set; }

    /// <summary>Nivel actual (0–1), para el anillo de escucha.</summary>
    public event Action<double>? Level;

    /// <summary>Cada bloque grabado (para los subtítulos en vivo).</summary>
    public event Action<byte[], int>? Chunk;

    public static bool HasMicrophone => MicrophoneStream.HasMicrophone;

    /// <summary>Empieza a grabar. "preroll" = audio ya capturado que debe ir al principio (lo dicho junto a «Oye Kroko»).</summary>
    public void Start(int deviceNumber = -1, byte[]? preroll = null)
    {
        if (IsRecording) return;
        _pcm = new MemoryStream();
        if (preroll is { Length: > 0 }) _pcm.Write(preroll);
        mic.Data += OnData;
        try
        {
            mic.Acquire(User, deviceNumber);
        }
        catch
        {
            mic.Data -= OnData;
            _pcm = null;
            throw;
        }
        IsRecording = true;
        log.LogInformation("Grabando lo que dices");
    }

    /// <summary>Para y devuelve el WAV grabado (o null si no había nada).</summary>
    public byte[]? Stop()
    {
        if (!IsRecording) return null;
        IsRecording = false;
        mic.Data -= OnData;
        mic.Release(User);
        byte[] pcm;
        lock (_lock) pcm = _pcm?.ToArray() ?? [];
        _pcm = null;
        Level?.Invoke(0);
        return pcm.Length == 0 ? null : Wav.FromPcm16(pcm, Format.SampleRate);
    }

    private void OnData(byte[] buffer, int count)
    {
        lock (_lock) _pcm?.Write(buffer, 0, count);
        Level?.Invoke(MicrophoneStream.LevelOf(buffer, count));
        Chunk?.Invoke(buffer, count);
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Reproduce la voz de Kroko y publica la amplitud (0–1) para mover la boca y la antena.
/// </summary>
public sealed class SpeechPlayer(ILogger<SpeechPlayer> log) : IDisposable
{
    private WaveOut? _out;
    private WaveFileReader? _reader;
    private int _generation;

    public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;

    public event Action<double>? Level;
    public event Action? Finished;

    /// <summary>Empieza a reproducir un WAV. Devuelve su duración (o null si no se pudo).</summary>
    public TimeSpan? Play(byte[] wav)
    {
        Stop();
        try
        {
            _reader = new WaveFileReader(new MemoryStream(wav));
            var meter = new MeteringSampleProvider(_reader.ToSampleProvider(), _reader.WaveFormat.SampleRate / 30);
            meter.StreamVolume += (_, e) => Level?.Invoke(Math.Clamp((e.MaxSampleValues?.Max() ?? 0) * 1.6, 0, 1));
            _out = new WaveOut { BufferMilliseconds = 60 };
            _out.Init(meter);
            var generation = ++_generation;
            _out.PlaybackStopped += (_, _) =>
            {
                // Si ya empezó otra voz (o se cortó a mano), este aviso llega tarde y se ignora
                if (generation != _generation) return;
                Level?.Invoke(0);
                Finished?.Invoke();
            };
            _out.Play();
            return _reader.TotalTime;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or NAudio.MmException or ArgumentException)
        {
            log.LogWarning(ex, "No se pudo reproducir la voz");
            Stop();
            return null;
        }
    }

    /// <summary>Corta la voz (interrupción con el atajo o con Esc).</summary>
    public void Stop()
    {
        _generation++;
        var o = _out;
        _out = null;
        if (o is not null)
        {
            try { o.Stop(); } catch (NAudio.MmException) { }
            o.Dispose();
        }
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Voz de respaldo de Windows (System.Speech) si el Brain no manda audio: prefiere una voz en español.
/// </summary>
public sealed class LocalTts(ILogger<LocalTts> log)
{
    public byte[]? Synthesize(string text)
    {
        try
        {
            using var synth = new SpeechSynthesizer();
            var voice = synth.GetInstalledVoices()
                .Where(v => v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("es", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => v.VoiceInfo.Culture.Name is "es-MX" or "es-US" or "es-PE")
                .FirstOrDefault();
            if (voice is not null) synth.SelectVoice(voice.VoiceInfo.Name);
            synth.Rate = 1;
            using var ms = new MemoryStream();
            synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(22050, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak(text);
            synth.SetOutputToNull();
            return Wav.FromPcm16(ms.ToArray(), 22050);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or ArgumentException)
        {
            log.LogWarning(ex, "La voz de Windows no está disponible");
            return null;
        }
    }
}

/// <summary>Recuerda si la última orden fue por voz (para responder hablando).</summary>
public sealed class VoiceSession
{
    public bool LastRequestWasVoice { get; set; }
}

/// <summary>Cabecera WAV para PCM 16 bits mono.</summary>
public static class Wav
{
    public static byte[] FromPcm16(byte[] pcm, int sampleRate)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(sampleRate);
        w.Write(sampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }
}
