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

    /// <summary>En pausa (el tiempo no corre).</summary>
    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    private TimeSpan _pausedLeft;
    private DateTime _workStarted;

    /// <summary>Minutos de enfoque por día (para «HOY» y las barras de la semana).</summary>
    public FocusHistory History { get; } = new();

    public void PauseOrResume()
    {
        if (!IsActive) return;
        if (IsPaused)
        {
            _endsAt = DateTime.Now + _pausedLeft;
            IsPaused = false;
            _tick.Start();
        }
        else
        {
            _pausedLeft = _endsAt - DateTime.Now;
            IsPaused = true;
            _tick.Stop();
        }
    }

    /// <summary>«Terminar»: da por hecho el bloque de enfoque (cuenta como bloque) y para.</summary>
    public void FinishNow()
    {
        if (!IsActive) return;
        if (IsPomodoro && IsWorkPhase) History.Add(_total - (IsPaused ? _pausedLeft : _endsAt - DateTime.Now), block: true);
        IsPomodoro = false; // así Cancel no lo vuelve a contar
        Cancel();
        PomodoroPhaseChanged?.Invoke(null);
    }

    /// <summary>«+5 min»: alarga lo que está corriendo.</summary>
    public void AddMinutes(int minutes)
    {
        if (!IsActive) return;
        var extra = TimeSpan.FromMinutes(minutes);
        _total += extra;
        if (IsPaused) _pausedLeft += extra;
        else _endsAt += extra;
        Tick();
    }

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
        if (IsPomodoro && IsWorkPhase) History.Add(_total - (IsPaused ? _pausedLeft : _endsAt - DateTime.Now), block: false); // lo que alcanzó a enfocarse
        IsPaused = false;
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
        _workStarted = DateTime.Now;
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
            History.Add(_total, block: true);
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

/// <summary>
/// Minutos de enfoque de cada día (17/11/2026, pantalla Enfoque estilo Troko: «HOY · 3 bloques · 75 min» y las barras de
/// la semana). Se guarda en %LOCALAPPDATA%\Kroko\enfoque.json; solo se conservan los últimos 60 días.
/// </summary>
public sealed class FocusHistory
{
    private readonly string _path;
    private readonly Dictionary<string, (int Minutes, int Blocks)> _days;

    public FocusHistory(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", "enfoque.json");
        _days = Load();
    }

    public event Action? Changed;

    public void Add(TimeSpan worked, bool block)
    {
        if (worked < TimeSpan.FromMinutes(1)) return;
        var key = DateTime.Today.ToString("yyyy-MM-dd");
        var (m, b) = _days.GetValueOrDefault(key);
        _days[key] = (m + (int)Math.Round(worked.TotalMinutes), b + (block ? 1 : 0));
        foreach (var old in _days.Keys.Where(k => string.CompareOrdinal(k, DateTime.Today.AddDays(-60).ToString("yyyy-MM-dd")) < 0).ToList())
            _days.Remove(old);
        Save();
        Changed?.Invoke();
    }

    public (int Minutes, int Blocks) On(DateTime day) => _days.GetValueOrDefault(day.ToString("yyyy-MM-dd"));

    private Dictionary<string, (int, int)> Load()
    {
        try
        {
            if (!System.IO.File.Exists(_path)) return [];
            var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int[]>>(System.IO.File.ReadAllText(_path)) ?? [];
            return raw.Where(p => p.Value.Length == 2).ToDictionary(p => p.Key, p => (p.Value[0], p.Value[1]));
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            System.IO.File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(_days.ToDictionary(p => p.Key, p => new[] { p.Value.Minutes, p.Value.Blocks })));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
    }
}
