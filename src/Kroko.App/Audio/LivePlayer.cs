using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Kroko.App.Audio;

/// <summary>
/// Reproduce la voz de Kroko a medida que llega (PCM 16 bits mono 24 kHz de Gemini Live) y publica la
/// amplitud para mover la boca. Clear() la corta al instante (cuando lo interrumpes).
/// </summary>
public sealed class LivePlayer(ILogger<LivePlayer> log) : IDisposable
{
    public static readonly WaveFormat Format = new(24000, 16, 1);

    private readonly Lock _lock = new();
    private BufferedWaveProvider? _buffer;
    private WaveOut? _out;
    private DateTime _lastAudio = DateTime.MinValue;

    /// <summary>Amplitud (0–1) de lo que suena ahora.</summary>
    public event Action<double>? Level;

    /// <summary>Hay voz sonando (o acaba de sonar: margen de 300 ms para que el eco no vuelva al micrófono).</summary>
    public bool IsSpeaking
    {
        get
        {
            lock (_lock)
                return (_buffer?.BufferedBytes ?? 0) > 0 || DateTime.Now - _lastAudio < TimeSpan.FromMilliseconds(300);
        }
    }

    public DateTime LastAudio => _lastAudio;

    public void Enqueue(byte[] pcm)
    {
        lock (_lock)
        {
            if (_out is null) Open();
            _buffer!.AddSamples(pcm, 0, pcm.Length);
            _lastAudio = DateTime.Now + _buffer.BufferedDuration;
            if (_out!.PlaybackState != PlaybackState.Playing) _out.Play();
        }
    }

    /// <summary>Corta la voz en seco (interrupción).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _buffer?.ClearBuffer();
            _lastAudio = DateTime.MinValue;
        }
        Level?.Invoke(0);
    }

    private void Open()
    {
        _buffer = new BufferedWaveProvider(Format, TimeSpan.FromMinutes(2))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true, // con silencio cuando no hay audio: el dispositivo queda abierto y sin cortes
        };
        var meter = new MeteringSampleProvider(_buffer.ToSampleProvider(), samplesPerNotification: 1200); // 50 ms
        meter.StreamVolume += (_, e) => Level?.Invoke(Math.Clamp(Math.Sqrt(e.MaxSampleValues is { Length: > 0 } v ? v.Max() : 0) * 1.4, 0, 1));
        _out = new WaveOut();
        _out.Init(meter);
        log.LogDebug("Reproductor de voz en tiempo real abierto");
    }

    public void Close()
    {
        lock (_lock)
        {
            _out?.Stop();
            _out?.Dispose();
            _out = null;
            _buffer = null;
            _lastAudio = DateTime.MinValue;
        }
        Level?.Invoke(0);
    }

    public void Dispose() => Close();
}
