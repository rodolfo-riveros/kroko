using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Kroko.App.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Kroko.App.Services;

/// <summary>
/// Temporizador y pomodoro visibles en el panel (tarjeta con cuenta regresiva circular, sección 4).
/// Uno a la vez: empezar otro reemplaza al anterior.
/// </summary>
public sealed partial class TimerService : ObservableObject
{
    private readonly DispatcherTimer _tick;
    private readonly ISoundService _sounds;
    private readonly ILogger<TimerService> _log;
    private DateTime _endsAt;
    private TimeSpan _total;
    private int _workMinutes = 25;
    private int _breakMinutes = 5;
    private int _completedBlocks;

    public TimerService(ISoundService sounds, ILogger<TimerService> log)
    {
        _sounds = sounds;
        _log = log;
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => Tick();
    }

    /// <summary>Terminó un tiempo: (título, texto, expresión).</summary>
    public event Action<string, string, string>? Finished;

    /// <summary>Cambia la fase del pomodoro: true = trabajo, false = descanso, null = sin pomodoro.</summary>
    public event Action<bool?>? PomodoroPhaseChanged;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial string Label { get; set; } = "";

    [ObservableProperty]
    public partial string RemainingText { get; set; } = "";

    /// <summary>Lo que falta (1 → 0), para el anillo.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    public bool IsPomodoro { get; private set; }
    public bool IsWorkPhase { get; private set; }

    public void StartTimer(TimeSpan duration, string label)
    {
        IsPomodoro = false;
        PomodoroPhaseChanged?.Invoke(null);
        Begin(duration, string.IsNullOrWhiteSpace(label) ? "Temporizador" : label);
    }

    public void StartPomodoro(int workMinutes, int breakMinutes)
    {
        _workMinutes = Math.Clamp(workMinutes, 1, 180);
        _breakMinutes = Math.Clamp(breakMinutes, 1, 60);
        _completedBlocks = 0;
        IsPomodoro = true;
        StartWork();
    }

    public void Cancel()
    {
        if (!IsActive) return;
        _tick.Stop();
        IsActive = false;
        if (IsPomodoro) PomodoroPhaseChanged?.Invoke(null);
        IsPomodoro = false;
        Label = RemainingText = "";
        Progress = 0;
    }

    private void StartWork()
    {
        IsWorkPhase = true;
        PomodoroPhaseChanged?.Invoke(true);
        Begin(TimeSpan.FromMinutes(_workMinutes), $"Pomodoro · trabajo {_completedBlocks + 1}");
    }

    private void StartBreak()
    {
        IsWorkPhase = false;
        PomodoroPhaseChanged?.Invoke(false);
        var longBreak = _completedBlocks % 4 == 0;
        Begin(TimeSpan.FromMinutes(longBreak ? Math.Max(15, _breakMinutes) : _breakMinutes), longBreak ? "Pomodoro · descanso largo" : "Pomodoro · descanso");
    }

    private void Begin(TimeSpan duration, string label)
    {
        _total = duration;
        _endsAt = DateTime.Now + duration;
        Label = label;
        IsActive = true;
        Tick();
        _tick.Start();
        _log.LogInformation("{Label}: {Minutes:0.#} min", label, duration.TotalMinutes);
    }

    private void Tick()
    {
        var left = _endsAt - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            _tick.Stop();
            Complete();
            return;
        }
        RemainingText = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"mm\:ss");
        Progress = Math.Clamp(left.TotalSeconds / Math.Max(1, _total.TotalSeconds), 0, 1);
    }

    private void Complete()
    {
        _sounds.Play("success");
        if (!IsPomodoro)
        {
            var label = Label;
            IsActive = false;
            Progress = 0;
            Finished?.Invoke("¡Tiempo!", $"Se acabó el tiempo: {label}.", "surprised");
            return;
        }

        if (IsWorkPhase)
        {
            _completedBlocks++;
            Finished?.Invoke("Pomodoro", $"¡Buen trabajo! Bloque {_completedBlocks} terminado. Toca descansar.", "success");
            StartBreak();
        }
        else
        {
            Finished?.Invoke("Pomodoro", "Se acabó el descanso. ¡A seguir!", "confident");
            StartWork();
        }
    }
}

/// <summary>
/// Modo concentración: apaga las notificaciones emergentes de Windows (y las restaura al salir)
/// y pone a Kroko en modo foco.
/// </summary>
public sealed class FocusService(ILogger<FocusService> log) : IDisposable
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
    private const string Value = "ToastEnabled";
    private object? _previous;
    private DispatcherTimer? _autoOff;

    public bool IsActive { get; private set; }

    public event Action<bool>? Changed;

    public void Activate(int? minutes)
    {
        if (!IsActive)
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            _previous = k.GetValue(Value);
            k.SetValue(Value, 0, RegistryValueKind.DWord);
            IsActive = true;
            log.LogInformation("Modo concentración activado (notificaciones de Windows en silencio)");
            Changed?.Invoke(true);
        }

        _autoOff?.Stop();
        if (minutes is > 0)
        {
            _autoOff = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes.Value) };
            _autoOff.Tick += (_, _) => Deactivate();
            _autoOff.Start();
        }
    }

    public void Deactivate()
    {
        _autoOff?.Stop();
        if (!IsActive) return;
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        if (_previous is null) k.DeleteValue(Value, throwOnMissingValue: false);
        else k.SetValue(Value, _previous, RegistryValueKind.DWord);
        IsActive = false;
        log.LogInformation("Modo concentración desactivado (notificaciones restauradas)");
        Changed?.Invoke(false);
    }

    /// <summary>Al cerrar Kroko, nunca dejar las notificaciones apagadas.</summary>
    public void Dispose() => Deactivate();
}
