using NAudio.Wave;

namespace Kroko.App.Tests;

public class MicrophoneTests
{
    /// <summary>
    /// "Predeterminado de Windows" usa WAVE_MAPPER (-1): debe entregar audio 16 kHz mono del micrófono que Windows
    /// tenga por defecto. Solo mide que llegan bytes (nada se guarda). Sin micrófono (p. ej. en GitHub) no hace nada.
    /// </summary>
    [Fact]
    public async Task El_microfono_predeterminado_de_Windows_entrega_audio()
    {
        if (WaveIn.DeviceCount == 0) return;

        var bytes = 0;
        var device = int.TryParse(Environment.GetEnvironmentVariable("KROKO_TEST_MIC"), out var d) ? d : -1;
        using var wave = new WaveInEvent { DeviceNumber = device, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 50 };
        wave.DataAvailable += (_, e) => Interlocked.Add(ref bytes, e.BytesRecorded);
        wave.StartRecording();
        var ms = int.TryParse(Environment.GetEnvironmentVariable("KROKO_TEST_MIC_MS"), out var t) ? t : 4000; // los audífonos Bluetooth tardan 1–2 s en pasar a "manos libres"
        await Task.Delay(ms);
        wave.StopRecording();

        Assert.True(bytes > 16000, $"Solo llegaron {bytes} bytes en {ms} ms");
    }
}
