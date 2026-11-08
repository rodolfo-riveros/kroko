using System.IO;
using System.Media;
using Kroko.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kroko.App.Audio;

public interface ISoundService
{
    bool Enabled { get; set; }
    void Play(string name);
}

/// <summary>
/// Sonidos cortos (≤ 300 ms) y suaves. Si existe assets/sounds/&lt;nombre&gt;.wav se usa ese archivo;
/// si no, se sintetiza un tono equivalente en memoria (no hacen falta archivos para que funcione).
/// </summary>
public sealed class SoundService : ISoundService, IDisposable
{
    private const int SampleRate = 22050;
    private readonly Dictionary<string, SoundPlayer> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<SoundService> _log;

    public SoundService(IOptions<KrokoOptions> options, ILogger<SoundService> log)
    {
        _log = log;
        Enabled = options.Value.SoundsEnabled;
        var volume = Math.Clamp(options.Value.SoundVolume, 0, 1);

        // Cada sonido: lista de (frecuencia inicial Hz, frecuencia final Hz, duración ms)
        var synth = new Dictionary<string, (double From, double To, int Ms)[]>
        {
            ["listen"] = [(660, 660, 60), (990, 990, 90)],
            ["listenEnd"] = [(990, 990, 60), (660, 660, 90)],
            ["success"] = [(784, 784, 60), (988, 988, 60), (1319, 1319, 150)],
            ["error"] = [(311, 311, 110), (233, 233, 170)],
            ["confirm"] = [(880, 880, 70), (1175, 1175, 160)],
        };

        foreach (var (name, notes) in synth)
        {
            try
            {
                var file = Path.Combine(AppContext.BaseDirectory, "assets", "sounds", name + ".wav");
                Stream stream = File.Exists(file)
                    ? new MemoryStream(File.ReadAllBytes(file))
                    : Synthesize(notes, volume);
                var player = new SoundPlayer(stream);
                player.Load();
                _players[name] = player;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                _log.LogWarning(ex, "No se pudo preparar el sonido {Name}", name);
            }
        }
    }

    public bool Enabled { get; set; }

    public void Play(string name)
    {
        if (!Enabled || !_players.TryGetValue(name, out var player)) return;
        try
        {
            player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _log.LogDebug(ex, "No se pudo reproducir {Name}", name);
        }
    }

    /// <summary>Genera un WAV PCM 16 bits mono con tonos suaves (seno + armónico) y envolvente.</summary>
    private static MemoryStream Synthesize((double From, double To, int Ms)[] notes, double volume)
    {
        var samples = new List<short>();
        double phase = 0;
        foreach (var (from, to, ms) in notes)
        {
            var n = SampleRate * ms / 1000;
            for (int i = 0; i < n; i++)
            {
                var p = i / (double)n;
                var freq = from + (to - from) * p;
                phase += 2 * Math.PI * freq / SampleRate;
                var attack = Math.Min(1, i / (SampleRate * 0.006));
                var release = Math.Exp(-4.0 * p);
                var v = (Math.Sin(phase) + 0.18 * Math.Sin(2 * phase)) * attack * release;
                samples.Add((short)(v * volume * short.MaxValue * 0.8));
            }
        }

        var ms2 = new MemoryStream();
        using (var w = new BinaryWriter(ms2, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            var dataBytes = samples.Count * 2;
            w.Write("RIFF"u8);
            w.Write(36 + dataBytes);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(SampleRate);
            w.Write(SampleRate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(dataBytes);
            foreach (var s in samples) w.Write(s);
        }
        ms2.Position = 0;
        return ms2;
    }

    public void Dispose()
    {
        foreach (var p in _players.Values) p.Dispose();
    }
}
