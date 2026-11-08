using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using Kroko.App.Audio;
using Kroko.App.Face;
using Kroko.App.Native;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kroko.App.Services;

/// <summary>
/// La "vida" de Kroko en la burbuja: se duerme si no lo usas, se despierta al tocarlo,
/// se distrae de vez en cuando, reacciona a clics y arrastres, y al estado del equipo.
/// </summary>
public sealed class CompanionBehavior : IDisposable
{
    private static readonly string[] PartialDownloadExtensions = [".crdownload", ".part", ".partial", ".download", ".opdownload"];

    private readonly KrokoOptions _opt;
    private readonly ISoundService _sounds;
    private readonly ILogger<CompanionBehavior> _log;
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private readonly List<DateTime> _recentClicks = [];
    private FileSystemWatcher? _downloads;

    private DateTime _lastInteraction = DateTime.Now;
    private DateTime _sleepyStart;
    private DateTime _sleepingSince;
    private bool _dozed;

    /// <summary>Lleva HideAfterSleepMinutes dormido: la burbuja puede desaparecer.</summary>
    public event Action? LongSleep;

    /// <summary>Se despertó (por voz, atajo, clic o un aviso): la burbuja vuelve.</summary>
    public event Action? Woke;
    private DateTime _nextDistraction;
    private DateTime _lastDizzy = DateTime.MinValue;
    private DateTime _nextBatteryCheck = DateTime.MinValue;
    private bool _batteryWarned;
    private bool _online = true;
    private bool _paused;
    private bool _brainOk = true;
    private bool _emergencyPaused;
    private bool _focusMode;
    private DateTime? _brainDownSince;
    private readonly ExpressionLibrary _library;

    public CompanionBehavior(ExpressionLibrary library, ISoundService sounds,
        IOptions<KrokoOptions> options, ILogger<CompanionBehavior> log)
    {
        _opt = options.Value;
        _library = library;
        _sounds = sounds;
        _log = log;
        Face = new FaceAnimator(library);
        Face.ExpressionEntered += e => { if (e.Sound is { } s) _sounds.Play(s); };
        Face.ExpressionExited += e => { if (e.ExitSound is { } s) _sounds.Play(s); };

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();
        ScheduleDistraction();
    }

    /// <summary>La cara de la burbuja.</summary>
    public FaceAnimator Face { get; }

    private string RestingKey => _emergencyPaused ? "paused" : _focusMode ? "focus" : _online && _brainOk ? "idle" : "offline";

    private bool IsAsleep => Face.BaseKey is "sleepy" or "sleeping";

    /// <summary>Dormido de verdad (nadie lo usa): buen momento para actualizarse.</summary>
    public bool IsSleeping => Face.BaseKey == "sleeping";

    public void Start()
    {
        Face.SetBase(RestingKey);
        Face.Play("waking");
        _timer.Start();

        if (_opt.SystemReactionsEnabled)
        {
            _online = NetworkInterface.GetIsNetworkAvailable();
            NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
            WatchDownloads();
        }
        _log.LogInformation("Kroko despierto");
    }

    /// <summary>Pausa la burbuja mientras está escondida (pantalla completa).</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused) Face.StopDistraction();
    }

    // ---------------------------------------------------------------- interacción física

    public void OnClick()
    {
        var now = DateTime.Now;
        if (WakeIfAsleep()) return;
        Touch();

        _recentClicks.Add(now);
        _recentClicks.RemoveAll(t => (now - t).TotalSeconds > 3);
        if (_recentClicks.Count >= 6)
        {
            // Muchos clics seguidos: se enfada (de forma graciosa)
            _recentClicks.Clear();
            Face.Play("angry");
            return;
        }

        if (CanReact()) Face.Play(_recentClicks.Count >= 3 ? "laugh" : "happy");
    }

    public void OnHoverChanged(bool hover)
    {
        Face.Hover = hover;
        if (!hover) return;
        if (Face.BaseKey == "sleepy") WakeIfAsleep();
        else if (!IsAsleep) Touch();
    }

    public void OnDragStarted()
    {
        WakeIfAsleep();
        Touch();
    }

    public void OnDragEnded() => Touch();

    /// <summary>Lo arrastraste rápido: se marea (como mucho una vez cada 3 s).</summary>
    public void OnFastDrag()
    {
        var now = DateTime.Now;
        if ((now - _lastDizzy).TotalSeconds < 3) return;
        _lastDizzy = now;
        Face.Play("dizzy");
    }

    /// <summary>Muestra una expresión en la burbuja (desde la galería).</summary>
    public void Preview(string key, bool isLoop)
    {
        Touch();
        Face.StopDistraction();
        if (isLoop) Face.SetBase(key);
        else Face.Play(key);
    }

    public void SleepNow()
    {
        Face.StopDistraction();
        Face.SetBase("sleeping");
        _sleepingSince = DateTime.Now;
    }

    public void WakeUp() => WakeIfAsleep();


    // ---------------------------------------------------------------- cerebro (Brain)

    /// <summary>True mientras haya conexión con el Brain (con unos segundos de margen).</summary>
    public bool IsBrainAvailable => _brainOk;

    /// <summary>
    /// Estado de la conexión con el Brain. La desconexión se aplica tras 6 s de margen,
    /// para que Kroko no ponga cara triste mientras el Brain arranca o se reconecta.
    /// </summary>
    public void SetBrainConnected(bool connected)
    {
        if (connected)
        {
            _brainDownSince = null;
            if (!_brainOk)
            {
                _brainOk = true;
                _log.LogInformation("Cerebro conectado");
                if (Face.BaseKey == "offline") Face.SetBase(RestingKey);
            }
        }
        else
        {
            _brainDownSince ??= DateTime.Now;
            if (Face.BaseKey is FaceStates.Thinking or FaceStates.Listening or FaceStates.Speaking)
                Face.SetBase(RestingKey);
        }
    }

    /// <summary>El Brain pide un estado de cara: thinking, listening, speaking o idle.</summary>
    public void SetBrainState(string state)
    {
        Touch();
        Face.StopDistraction();
        switch (state)
        {
            case FaceStates.Thinking or FaceStates.Listening or FaceStates.Speaking:
                Face.SetBase(state);
                break;
            default:
                if (Face.BaseKey is FaceStates.Thinking or FaceStates.Listening or FaceStates.Speaking or "alert" || IsAsleep)
                    Face.SetBase(RestingKey);
                break;
        }
    }

    /// <summary>Tarjeta de confirmación visible: ojos ámbar y antena pulsando (expresión "alert").</summary>
    public void SetConfirming(bool confirming)
    {
        Touch();
        Face.StopDistraction();
        if (confirming) Face.SetBase("alert");
        else if (Face.BaseKey == "alert") Face.SetBase(FaceStates.Thinking);
    }

    /// <summary>Pomodoro (trabajo) o modo concentración: Kroko se queda con cara de foco y no se distrae.</summary>
    public void SetFocusMode(bool on)
    {
        if (_focusMode == on) return;
        _focusMode = on;
        Face.StopDistraction();
        if (Face.BaseKey is "idle" or "offline" or "focus") Face.SetBase(RestingKey);
    }

    /// <summary>Interruptor de emergencia: Kroko queda en pausa hasta que se reanude.</summary>
    public void SetEmergencyPaused(bool paused)
    {
        _emergencyPaused = paused;
        Touch();
        Face.StopDistraction();
        Face.StopOneShot();
        Face.SetBase(RestingKey);
        if (!paused) Face.Play("waking");
    }

    /// <summary>Expresión puntual pedida por el Brain (happy, oops…). Se ignoran las desconocidas.</summary>
    public void PlayExpression(string name)
    {
        if (!_library.Contains(name))
        {
            _log.LogDebug("Expresión desconocida pedida por el Brain: {Name}", name);
            return;
        }
        Touch();
        Face.Play(name);
    }

    // ---------------------------------------------------------------- ciclo de vida

    private void OnTick()
    {
        if (_paused) return;
        var now = DateTime.Now;

        // Brain caído más de 6 s: Kroko lo muestra con la antena gris
        if (_brainOk && _brainDownSince is { } down && (now - down).TotalSeconds >= 6)
        {
            _brainOk = false;
            _log.LogInformation("Cerebro desconectado");
        }

        // Sueño: reposo → sueño → dormido
        var idleMin = (now - _lastInteraction).TotalMinutes;
        var threshold = IsNight(now) ? _opt.NightSleepAfterMinutes : _opt.SleepAfterMinutes;
        if (Face.BaseKey == RestingKey && !_focusMode && !Face.IsPlayingOneShot && idleMin >= threshold)
        {
            Face.StopDistraction();
            Face.SetBase("sleepy");
            _sleepyStart = now;
        }
        else if (Face.BaseKey == "sleepy" && (now - _sleepyStart).TotalSeconds >= 20)
        {
            Face.SetBase("sleeping");
            _sleepingSince = now;
        }

        // Dormido un buen rato: desaparece de la pantalla (sigue escuchando «Oye Kroko»)
        if (Face.BaseKey == "sleeping" && !_dozed && _opt.HideAfterSleepMinutes > 0
            && (now - _sleepingSince).TotalMinutes >= _opt.HideAfterSleepMinutes)
        {
            _dozed = true;
            LongSleep?.Invoke();
        }

        // Si el usuario volvió al estado normal desde la galería, alineamos el reposo con la conexión
        if (Face.BaseKey is "idle" or "offline" or "paused" or "focus" && Face.BaseKey != RestingKey)
            Face.SetBase(RestingKey);

        // Distracciones: solo en reposo, cada 10–20 min, nunca interrumpen
        if (_opt.DistractionsEnabled && now >= _nextDistraction)
        {
            if (Face.BaseKey == RestingKey && CanReact() && !Face.Hover)
                StartRandomDistraction(now);
            ScheduleDistraction();
        }

        if (_opt.SystemReactionsEnabled && now >= _nextBatteryCheck)
        {
            _nextBatteryCheck = now.AddSeconds(60);
            CheckBattery();
        }
    }

    private void StartRandomDistraction(DateTime now)
    {
        var pick = _rng.Next(4);
        _log.LogDebug("Distracción {Pick}", pick);
        switch (pick)
        {
            case 0: Face.StartDistraction(DistractionKind.Fly); break;
            case 1: Face.StartDistraction(DistractionKind.Ball); break;
            case 2: Face.Play("yawn"); break;
            default: Face.StartDistraction(DistractionKind.Clock); break;
        }
    }

    private void ScheduleDistraction()
    {
        var min = Math.Max(0.1, _opt.DistractionMinMinutes);
        var max = Math.Max(min, _opt.DistractionMaxMinutes);
        _nextDistraction = DateTime.Now.AddMinutes(min + _rng.NextDouble() * (max - min));
    }

    private bool IsNight(DateTime now)
    {
        var h = now.Hour;
        return _opt.NightStartHour > _opt.NightEndHour
            ? h >= _opt.NightStartHour || h < _opt.NightEndHour
            : h >= _opt.NightStartHour && h < _opt.NightEndHour;
    }

    private bool CanReact() => !Face.IsPlayingOneShot && !Face.IsDistracted && !IsAsleep
                               && Face.BaseKey is "idle" or "offline";

    private void Touch()
    {
        _lastInteraction = DateTime.Now;
        if (Face.IsDistracted) Face.StopDistraction();
    }

    private bool WakeIfAsleep()
    {
        if (!IsAsleep) return false;
        Touch();
        _dozed = false;
        Woke?.Invoke();
        Face.SetBase(RestingKey);
        Face.Play("waking");
        return true;
    }

    // ---------------------------------------------------------------- reacciones del sistema

    private void CheckBattery()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var s) || s.BatteryLifePercent == 255) return;
        var onBattery = s.ACLineStatus == 0;
        var pct = s.BatteryLifePercent;

        if (onBattery && pct < 15 && !_batteryWarned)
        {
            _batteryWarned = true;
            _log.LogInformation("Batería baja: {Pct}%", pct);
            if (IsAsleep) WakeIfAsleep();
            Face.Play("shocked");
        }
        else if (!onBattery || pct >= 20)
        {
            _batteryWarned = false;
        }
    }

    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_online == e.IsAvailable) return;
            var wasResting = Face.BaseKey == RestingKey;
            _online = e.IsAvailable;
            _log.LogInformation("Internet {State}", _online ? "disponible" : "perdido");
            if (wasResting) Face.SetBase(RestingKey);
            if (_online && wasResting) Face.Play("happy");
        });
    }

    private void WatchDownloads()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(dir)) return;
            _downloads = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
            _downloads.Renamed += (_, e) =>
            {
                // Los navegadores descargan como .crdownload/.part y al terminar renombran el archivo
                var oldExt = Path.GetExtension(e.OldFullPath);
                if (!PartialDownloadExtensions.Contains(oldExt, StringComparer.OrdinalIgnoreCase)) return;
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    _log.LogInformation("Descarga terminada: {File}", e.Name);
                    if (!IsAsleep) Face.Play("success");
                });
            };
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "No se pudo vigilar la carpeta de descargas");
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _downloads?.Dispose();
    }
}
