using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Kroko.App.Audio;

/// <summary>
/// Una sola captura del micrófono (16 kHz mono 16 bits) compartida por quien la necesite: la palabra de
/// activación, la grabación de órdenes y los subtítulos en vivo. Se abre cuando alguien la pide y se cierra
/// cuando ya nadie la usa. Si el micrófono se desconecta, reintenta cada pocos segundos.
/// Debe crearse en el hilo de la interfaz (WaveIn usa mensajes de ventana).
/// </summary>
public sealed class MicrophoneStream : IDisposable
{
    public static readonly WaveFormat Format = new(16000, 16, 1);

    private readonly ILogger<MicrophoneStream> _log;
    private readonly HashSet<string> _users = [];
    private readonly DispatcherTimer _retry;
    private readonly DispatcherTimer _watch;
    private WaveIn? _wave;
    private int _device = -1;
    private int _knownDevices;

    public MicrophoneStream(ILogger<MicrophoneStream> log)
    {
        _log = log;
        _retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _retry.Tick += (_, _) => TryOpen();

        // Conectar o quitar un micrófono con Kroko abierto: se nota en 2 s (antes solo se miraba al arrancar)
        _knownDevices = WaveIn.DeviceCount;
        _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _watch.Tick += (_, _) => CheckDevices();
        _watch.Start();
        _log.LogInformation("Micrófonos al arrancar: {Count}", _knownDevices);
    }

    /// <summary>Se conectó o se quitó un micrófono (true = hay al menos uno). En el hilo de la interfaz.</summary>
    public event Action<bool>? DevicesChanged;

    private void CheckDevices()
    {
        var count = WaveIn.DeviceCount;
        if (count == _knownDevices) return;
        _log.LogInformation("Cambiaron los micrófonos: {Before} → {After}", _knownDevices, count);
        _knownDevices = count;

        // Con el micrófono abierto, se reabre para seguir al predeterminado de Windows (o soltar el que se quitó)
        if (_users.Count > 0)
        {
            Close();
            TryOpen();
            if (_wave is null && count > 0) _retry.Start();
        }
        DevicesChanged?.Invoke(count > 0);
    }

    /// <summary>Audio PCM recién capturado (bloques de 50 ms). Se dispara en el hilo de la interfaz.</summary>
    public event Action<byte[], int>? Data;

    /// <summary>true = el micrófono está abierto y entregando audio.</summary>
    public bool IsOpen => _wave is not null;

    public static bool HasMicrophone => WaveIn.DeviceCount > 0;

    /// <summary>Pide el micrófono para "user" (p. ej. "activacion", "grabacion"). Lanza si no se puede abrir.</summary>
    public void Acquire(string user, int deviceNumber = -1)
    {
        _users.Add(user);
        if (_wave is not null && deviceNumber == _device) return;
        if (_wave is not null) Close(); // cambió el micrófono elegido
        _device = deviceNumber;
        Open();
    }

    /// <summary>Como Acquire, pero sin lanzar: si falla, reintenta en segundo plano.</summary>
    public bool TryAcquire(string user, int deviceNumber = -1)
    {
        try
        {
            Acquire(user, deviceNumber);
            return true;
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            _log.LogInformation("Micrófono no disponible para {User} ({Message}); reintento cada 5 s", user, ex.Message);
            _retry.Start();
            return false;
        }
    }

    public void Release(string user)
    {
        if (!_users.Remove(user) || _users.Count > 0) return;
        _retry.Stop();
        Close();
    }

    public bool IsUsedBy(string user) => _users.Contains(user);

    private void Open()
    {
        if (!HasMicrophone) throw new InvalidOperationException("No hay ningún micrófono.");
        var wave = new WaveIn
        {
            WaveFormat = Format,
            BufferMilliseconds = 50,
            // -1 = el micrófono predeterminado de Windows (WAVE_MAPPER), no "el primero de la lista"
            DeviceNumber = _device >= 0 && _device < WaveIn.DeviceCount ? _device : -1,
        };
        wave.DataAvailable += OnData;
        wave.RecordingStopped += OnStopped;
        wave.StartRecording();
        _wave = wave;
        _retry.Stop();
        _log.LogInformation("Micrófono {Device} abierto ({Users})",
            wave.DeviceNumber < 0 ? "predeterminado de Windows" : WaveIn.GetCapabilities(wave.DeviceNumber).ProductName, string.Join(", ", _users));
    }

    private void TryOpen()
    {
        if (_users.Count == 0 || _wave is not null)
        {
            _retry.Stop();
            return;
        }
        try
        {
            Open();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            // sigue sin micrófono; se vuelve a intentar en el próximo tic
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded > 0) Data?.Invoke(e.Buffer, e.BytesRecorded);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null || !ReferenceEquals(sender, _wave)) return;
        // Se desconectó el micrófono (o Windows lo quitó): se cierra y se reintenta mientras alguien lo quiera
        _log.LogWarning("El micrófono se detuvo: {Message}", e.Exception.Message);
        Close();
        if (_users.Count > 0) _retry.Start();
    }

    private void Close()
    {
        var wave = _wave;
        _wave = null;
        if (wave is null) return;
        wave.DataAvailable -= OnData;
        wave.RecordingStopped -= OnStopped;
        try
        {
            wave.StopRecording();
        }
        catch (NAudio.MmException) { }
        wave.Dispose();
    }

    /// <summary>Nivel 0–1 (RMS con curva) de un bloque PCM, para el anillo y el detector de silencio.</summary>
    public static double LevelOf(byte[] buffer, int count)
    {
        double sum = 0;
        var samples = count / 2;
        for (var i = 0; i + 1 < count; i += 2)
        {
            var s = BitConverter.ToInt16(buffer, i) / 32768.0;
            sum += s * s;
        }
        var rms = samples > 0 ? Math.Sqrt(sum / samples) : 0;
        return Math.Clamp(Math.Sqrt(rms) * 2.2, 0, 1);
    }

    /// <summary>Solo pruebas y "--fake-mic": inyecta audio como si viniera del micrófono.</summary>
    internal void Inject(byte[] pcm) => Data?.Invoke(pcm, pcm.Length);

    public void Dispose()
    {
        _watch.Stop();
        _retry.Stop();
        _users.Clear();
        Close();
    }
}
