using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kroko.App.Audio;
using Kroko.App.Services;
using Kroko.App.Tools;
using Microsoft.Extensions.Options;

namespace Kroko.App.ViewModels;

/// <summary>Una tarjeta de hábito del panel: meta del día, racha, semana y el botón «+1».</summary>
public sealed partial class HabitCard(HabitItemDto h, KrokoPanelViewModel owner) : ObservableObject
{
    public long Id { get; } = h.Id;
    public string Name { get; } = h.Nombre;
    public string Style { get; } = h.Estilo;
    public string Emoji { get; } = KrokoPanelViewModel.EmojiFor(h.Estilo);
    public int Goal { get; } = h.Meta;
    public int Count { get; } = h.Llevas;
    public bool Done { get; } = h.Hecho;
    public int Streak { get; } = h.Racha;
    public bool[] Week { get; } = h.Semana.Length == 7 ? h.Semana : new bool[7];
    public double Progress => Goal <= 0 ? 0 : Math.Clamp((double)Count / Goal, 0, 1);
    public string CountText => Goal > 1 ? $"{Count}/{Goal} {h.Unidad}".Trim() : Done ? "Hecho hoy" : "Pendiente";
    public string StreakText => Streak switch { 0 => "Sin racha aún", 1 => "Racha: 1 día", _ => $"Racha: {Streak} días" };
    public string AddText => Goal > 1 ? "+1" : Done ? "✓" : "Hecho";

    [RelayCommand]
    private Task AddAsync() => owner.AddToHabitAsync(this);

    [RelayCommand]
    private Task ResetAsync() => owner.ResetHabitAsync(this);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteHabitAsync(this);
}

public sealed partial class TaskRow(TaskItemDto t, KrokoPanelViewModel owner) : ObservableObject
{
    public long Id { get; } = t.Id;
    public string Text { get; } = t.Texto;
    public string Detail { get; } = t.Fecha is { } f && DateOnly.TryParse(f, out var d) ? KrokoPanelViewModel.DayLabel(d) : "";

    [ObservableProperty]
    public partial bool Done { get; set; } = t.Hecha;

    partial void OnDoneChanged(bool value) => _ = owner.CompleteTaskAsync(this, value);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteTaskAsync(this);
}

public sealed partial class ReminderRow(ReminderItemDto r, KrokoPanelViewModel owner) : ObservableObject
{
    public long Id { get; } = r.Id;
    public string Text { get; } = r.Texto;
    public string When { get; } = KrokoPanelViewModel.WhenLabel(r.Cuando) + (r.Repetir is "nunca" or "" ? "" : $" · {r.Repetir}");

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteReminderAsync(this);
}

public sealed record StatBar(string Name, string Value, double Fraction);

/// <summary>
/// El panel «Kroko» (decidido por Pol, 14/11/2026, inspirado en el tracker de hábitos de Troko): un solo lugar con tu
/// día (anillo de progreso), tareas y avisos, hábitos con metas y rachas, enfoque (pomodoro), tu PC y unos ajustes
/// simples. Todo lo que aquí se hace con un clic también se puede pedir por voz.
/// </summary>
public sealed partial class KrokoPanelViewModel : ObservableObject, IDisposable
{
    public static readonly string[] Sections = ["inicio", "tareas", "habitos", "enfoque", "mipc", "ajustes"];

    private readonly BrainApiClient _api;
    private readonly CompanionBehavior _behavior;
    private readonly PanelViewModel _chat;
    private readonly VoiceController _voice;
    private readonly WindowService _windows;
    private readonly WakeWordService _wake;
    private readonly KrokoOptions _opt;
    private readonly DispatcherTimer _refresh;
    private bool _loadingSettings;

    public KrokoPanelViewModel(BrainApiClient api, CompanionBehavior behavior, PanelViewModel chat, VoiceController voice,
        WindowService windows, WakeWordService wake, TimerService timers, IOptions<KrokoOptions> options)
    {
        _api = api;
        _behavior = behavior;
        _chat = chat;
        _voice = voice;
        _windows = windows;
        _wake = wake;
        _opt = options.Value;
        Timers = timers;
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refresh.Tick += async (_, _) => await RefreshAsync();
        _refresh.Start();
        LoadSettings();
    }

    public TimerService Timers { get; }

    [ObservableProperty] public partial int SelectedSection { get; set; }

    [ObservableProperty] public partial string Greeting { get; set; } = "Tu resumen de hoy";
    [ObservableProperty] public partial string DateText { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsOffline { get; set; }

    // ---- Inicio
    [ObservableProperty] public partial double DayProgress { get; set; }
    [ObservableProperty] public partial string DayProgressText { get; set; } = "0/0";
    [ObservableProperty] public partial string MainHabitValue { get; set; } = "—";
    [ObservableProperty] public partial string MainHabitLabel { get; set; } = "hábitos de hoy";
    [ObservableProperty] public partial string PendingTasksValue { get; set; } = "0";
    [ObservableProperty] public partial string NextNoticeValue { get; set; } = "—";
    [ObservableProperty] public partial string NextNoticeLabel { get; set; } = "sin avisos";
    [ObservableProperty] public partial string AskText { get; set; } = "";
    [ObservableProperty] public partial string Celebration { get; set; } = "";

    // ---- Tareas, hábitos
    public ObservableCollection<TaskRow> Tasks { get; } = [];
    public ObservableCollection<ReminderRow> Reminders { get; } = [];
    public ObservableCollection<HabitCard> Habits { get; } = [];
    [ObservableProperty] public partial string NewTaskText { get; set; } = "";
    [ObservableProperty] public partial bool NewTaskToday { get; set; }
    [ObservableProperty] public partial string NewHabitName { get; set; } = "";
    [ObservableProperty] public partial double NewHabitGoal { get; set; } = 1;
    [ObservableProperty] public partial string NewHabitUnit { get; set; } = "";
    public bool HasNoHabits => Habits.Count == 0;
    public bool HasNoTasks => Tasks.Count == 0;
    public bool HasNoReminders => Reminders.Count == 0;

    // ---- Mi PC
    public ObservableCollection<StatBar> PcStats { get; } = [];
    [ObservableProperty] public partial string PcSummary { get; set; } = "";

    // ---- Ajustes simples
    [ObservableProperty] public partial bool StartHidden { get; set; }
    [ObservableProperty] public partial bool HideAfterMinute { get; set; }
    [ObservableProperty] public partial bool WakeWordOn { get; set; }
    [ObservableProperty] public partial bool RepliesWithVoice { get; set; }
    [ObservableProperty] public partial bool Realtime { get; set; }
    [ObservableProperty] public partial bool StartWithWindowsOn { get; set; }
    [ObservableProperty] public partial bool SoundsOn { get; set; }
    [ObservableProperty] public partial bool NoticeMeetings { get; set; }
    [ObservableProperty] public partial bool NoticeEmails { get; set; }
    [ObservableProperty] public partial bool NoticeOverdue { get; set; }
    [ObservableProperty] public partial bool NoticeSummary { get; set; }
    [ObservableProperty] public partial string SettingsStatus { get; set; } = "";

    public void OpenSection(string? section)
    {
        var i = Array.FindIndex(Sections, s => s.Equals(section, StringComparison.OrdinalIgnoreCase));
        SelectedSection = i >= 0 ? i : 0;
    }

    partial void OnSelectedSectionChanged(int value)
    {
        if (Sections.ElementAtOrDefault(value) == "mipc") _ = LoadPcAsync();
    }

    // ------------------------------------------------------------------ cargar el día

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var now = DateTime.Now;
        DateText = now.ToString("dddd d 'de' MMMM", System.Globalization.CultureInfo.GetCultureInfo("es-PE"));
        Greeting = now.Hour switch { < 12 => "Buenos días", < 19 => "Buenas tardes", _ => "Buenas noches" };
        try
        {
            if (await _api.GetTodayAsync() is not { } day) return;
            IsOffline = false;
            Status = "";
            Apply(day);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            IsOffline = true;
            Status = "No me puedo conectar con mi cerebro. Lo intento solo en unos segundos.";
        }
    }

    private void Apply(TodayDto day)
    {
        var r = day.Resumen;
        DayProgress = r.HabitosTotal == 0 ? 0 : (double)r.HabitosHechos / r.HabitosTotal;
        DayProgressText = r.HabitosTotal == 0 ? "—" : $"{r.HabitosHechos}/{r.HabitosTotal}";

        // El hábito principal: el primero con meta de varias veces (los vasos de agua), si no el primero
        var main = day.Habitos.FirstOrDefault(h => h.Meta > 1) ?? day.Habitos.FirstOrDefault();
        MainHabitValue = main is null ? "—" : main.Meta > 1 ? $"{main.Llevas}/{main.Meta}" : main.Hecho ? "✓" : "0/1";
        MainHabitLabel = main is null ? "crea tu primer hábito" : main.Meta > 1 && main.Unidad.Length > 0 ? $"{main.Unidad} · {main.Nombre}" : main.Nombre;
        PendingTasksValue = r.TareasPendientes.ToString();
        NextNoticeValue = r.ProximoAviso ?? "—";
        NextNoticeLabel = r.ProximoAvisoTexto ?? "sin avisos pendientes";

        Sync(Habits, day.Habitos.Select(h => new HabitCard(h, this)));
        Sync(Tasks, day.Tareas.Select(t => new TaskRow(t, this)));
        Sync(Reminders, day.Recordatorios.Select(x => new ReminderRow(x, this)));
        OnPropertyChanged(nameof(HasNoHabits));
        OnPropertyChanged(nameof(HasNoTasks));
        OnPropertyChanged(nameof(HasNoReminders));
    }

    /// <summary>Solo para las capturas (--snapshot): un día de ejemplo, sin cerebro.</summary>
    internal void ShowSample()
    {
        _refresh.Stop();
        DateText = "domingo 15 de noviembre";
        Greeting = "Buenos días";
        Apply(new TodayDto("2026-11-15",
            new TodaySummaryDto(1, 3, 2, "13:30", "Llamar al dentista"),
            [
                new(1, "Beber agua", 5, 8, "vasos", "agua", false, 4, [true, true, false, true, true, true, false], ""),
                new(2, "Leer", 1, 1, "", "lectura", true, 12, [true, true, true, true, true, true, true], ""),
                new(3, "Ejercicio", 0, 1, "", "ejercicio", false, 0, [false, true, false, false, true, false, false], ""),
            ],
            [new(1, "Comprar pan", null, false), new(2, "Enviar el informe a Ana", "2026-11-15", false), new(3, "Pagar la luz", "2026-11-14", true)],
            [new(1, "Llamar al dentista", "2026-11-15 13:30", "nunca"), new(2, "Hábito: Beber agua", "2026-11-15 16:00", "diario")]));
        Celebration = "💧  ¡Meta cumplida! Beber agua · 4 días seguidos";
        NoticeMeetings = NoticeEmails = true;
        PcStats.Add(new StatBar("Procesador", "12 %", 0.12));
        PcStats.Add(new StatBar("Memoria", "58 % · 6,7 GB libres", 0.58));
        PcStats.Add(new StatBar("Disco C:", "212 GB libres de 476 GB", 0.55));
        PcSummary = "Encendida desde hace 3 h 12 min · con internet";
    }

    private static void Sync<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    // ------------------------------------------------------------------ hablar o escribir a Kroko

    [RelayCommand]
    private void Ask()
    {
        var text = AskText.Trim();
        if (text.Length == 0) return;
        AskText = "";
        _behavior.WakeUp();
        _chat.InputText = text;
        if (_chat.SendCommand.CanExecute(null)) _chat.SendCommand.Execute(null);
    }

    [RelayCommand]
    private void Talk()
    {
        _behavior.WakeUp();
        _voice.Toggle();
    }

    [RelayCommand]
    private void Example(string text)
    {
        AskText = text;
        Ask();
    }

    // ------------------------------------------------------------------ hábitos

    internal async Task AddToHabitAsync(HabitCard card)
    {
        if (card.Goal <= 1 && card.Done) return;
        await Try(async () =>
        {
            var (habit, reached) = await _api.AddToHabitAsync(card.Id);
            if (reached && habit is not null) Celebrate(habit);
            else _behavior.PlayExpression("happy");
            await RefreshAsync();
        });
    }

    internal Task ResetHabitAsync(HabitCard card) => Try(async () =>
    {
        await _api.ResetHabitAsync(card.Id);
        await RefreshAsync();
    });

    internal Task DeleteHabitAsync(HabitCard card) => Try(async () =>
    {
        await _api.DeleteHabitAsync(card.Id);
        await RefreshAsync();
    });

    [RelayCommand]
    private Task AddHabitAsync() => Try(async () =>
    {
        var name = NewHabitName.Trim();
        if (name.Length == 0) return;
        await _api.AddHabitAsync(name, (int)Math.Clamp(Math.Round(NewHabitGoal), 1, 99), NewHabitUnit.Trim(), null);
        NewHabitName = NewHabitUnit = "";
        NewHabitGoal = 1;
        _behavior.PlayExpression("success");
        await RefreshAsync();
    });

    private void Celebrate(HabitItemDto habit)
    {
        Celebration = $"{EmojiFor(habit.Estilo)}  ¡Meta cumplida! {habit.Nombre}" + (habit.Racha > 1 ? $" · {habit.Racha} días seguidos" : "");
        _behavior.WakeUp();
        _behavior.PlayExpression(habit.Racha >= 3 ? "love" : "success");
        var clear = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        clear.Tick += (_, _) =>
        {
            clear.Stop();
            Celebration = "";
        };
        clear.Start();
    }

    // ------------------------------------------------------------------ tareas y avisos

    [RelayCommand]
    private Task AddTaskAsync() => Try(async () =>
    {
        var text = NewTaskText.Trim();
        if (text.Length == 0) return;
        await _api.AddTaskAsync(text, NewTaskToday ? DateOnly.FromDateTime(DateTime.Today) : null);
        NewTaskText = "";
        _behavior.PlayExpression("happy");
        await RefreshAsync();
    });

    internal Task CompleteTaskAsync(TaskRow row, bool done) => Try(async () =>
    {
        await _api.CompleteTaskAsync(row.Id, done);
        if (done) _behavior.PlayExpression("success");
        await RefreshAsync();
    });

    internal Task DeleteTaskAsync(TaskRow row) => Try(async () =>
    {
        await _api.DeleteTaskAsync(row.Id);
        await RefreshAsync();
    });

    internal Task DeleteReminderAsync(ReminderRow row) => Try(async () =>
    {
        await _api.DeleteReminderAsync(row.Id);
        await RefreshAsync();
    });

    // ------------------------------------------------------------------ enfoque

    [RelayCommand]
    private void StartFocus(string minutes)
    {
        var work = int.TryParse(minutes, out var m) ? m : 25;
        Timers.StartPomodoro(work, Math.Max(5, work / 5));
        _behavior.WakeUp();
    }

    [RelayCommand]
    private void StartBreak() => Timers.StartTimer(TimeSpan.FromMinutes(5), "Descanso");

    [RelayCommand]
    private void StopFocus() => Timers.Cancel();

    // ------------------------------------------------------------------ mi PC

    [RelayCommand]
    private async Task LoadPcAsync()
    {
        try
        {
            var outcome = await new EstadoEquipoTool().RunAsync(new ToolArgs(JsonDocument.Parse("{}").RootElement), default);
            if (!outcome.Ok) return;
            var json = JsonSerializer.SerializeToNode(outcome.Data)!;
            PcStats.Clear();
            PcStats.Add(new StatBar("Procesador", $"{json["cpuPct"]} %", (double)json["cpuPct"]! / 100));
            PcStats.Add(new StatBar("Memoria", $"{json["ramPct"]} % · {json["ramLibreGB"]} GB libres", (double)json["ramPct"]! / 100));
            if (json["bateria"] is JsonObject b)
                PcStats.Add(new StatBar("Batería", $"{b["porcentaje"]} %" + ((bool)b["cargando"]! ? " · cargando" : ""), (double)b["porcentaje"]! / 100));
            foreach (var d in json["discos"]!.AsArray())
                PcStats.Add(new StatBar($"Disco {d!["unidad"]}", $"{d["libreGB"]} GB libres de {d["totalGB"]} GB", (double)d["usadoPct"]! / 100));
            PcSummary = $"Encendida desde hace {json["encendidaDesde"]} · " + ((bool)json["internet"]! ? "con internet" : "sin internet");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or FormatException)
        {
            PcSummary = "No pude leer el estado del equipo.";
        }
    }

    [RelayCommand]
    private void TidyDesktop() => Example("Ordena mi escritorio");

    [RelayCommand]
    private void OpenDownloads() => Example("Abre la carpeta de descargas");

    // ------------------------------------------------------------------ ajustes simples (se aplican al instante)

    private void LoadSettings()
    {
        _loadingSettings = true;
        StartHidden = _opt.StartHidden;
        HideAfterMinute = _opt.HideAfterIdleSeconds > 0;
        WakeWordOn = _opt.Voice.WakeWord.Enabled;
        RepliesWithVoice = _opt.Voice.RepliesWithVoice;
        Realtime = _opt.Voice.Realtime;
        StartWithWindowsOn = StartWithWindows.IsEnabled;
        SoundsOn = _opt.SoundsEnabled;
        _loadingSettings = false;
    }

    [RelayCommand]
    public async Task LoadNoticesAsync()
    {
        try
        {
            if (await _api.GetNoticesAsync() is not { } n) return;
            _loadingSettings = true;
            NoticeMeetings = n.Reuniones;
            NoticeEmails = n.Correos;
            NoticeOverdue = n.TareasVencidas;
            NoticeSummary = n.ResumenDelDia;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SettingsStatus = "No pude leer los avisos: el cerebro no responde.";
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    partial void OnStartHiddenChanged(bool value) => SaveSetting(k => k["StartHidden"] = value, () => _opt.StartHidden = value);
    partial void OnHideAfterMinuteChanged(bool value) =>
        SaveSetting(k => k["HideAfterIdleSeconds"] = value ? 60 : 0, () => _opt.HideAfterIdleSeconds = value ? 60 : 0);
    partial void OnSoundsOnChanged(bool value) => SaveSetting(k => k["SoundsEnabled"] = value, () => _opt.SoundsEnabled = value);

    partial void OnWakeWordOnChanged(bool value) => SaveVoice(v =>
    {
        var wake = v["WakeWord"] as JsonObject ?? new JsonObject();
        wake["Enabled"] = value;
        v["WakeWord"] = wake.DeepClone();
    }, () =>
    {
        _opt.Voice.WakeWord.Enabled = value;
        _wake.Refresh();
    });

    partial void OnRepliesWithVoiceChanged(bool value) =>
        SaveVoice(v => v["RepliesWithVoice"] = value, () => _opt.Voice.RepliesWithVoice = value);

    partial void OnRealtimeChanged(bool value) => SaveVoice(v => v["Realtime"] = value, () => _opt.Voice.Realtime = value);

    partial void OnStartWithWindowsOnChanged(bool value)
    {
        if (!_loadingSettings) StartWithWindows.Set(value);
    }

    partial void OnNoticeMeetingsChanged(bool value) => _ = SaveNoticesAsync();
    partial void OnNoticeEmailsChanged(bool value) => _ = SaveNoticesAsync();
    partial void OnNoticeOverdueChanged(bool value) => _ = SaveNoticesAsync();
    partial void OnNoticeSummaryChanged(bool value) => _ = SaveNoticesAsync();

    private async Task SaveNoticesAsync()
    {
        if (_loadingSettings) return;
        try
        {
            await _api.SetNoticesAsync(new NoticesDto(NoticeMeetings, NoticeEmails, NoticeOverdue, NoticeSummary));
            SettingsStatus = "Avisos guardados.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            SettingsStatus = "No pude guardar los avisos: el cerebro no responde.";
        }
    }

    private void SaveSetting(Action<JsonObject> write, Action apply)
    {
        if (_loadingSettings) return;
        apply();
        var k = UserSettings.LoadKrokoSection();
        write(k);
        UserSettings.SaveKrokoSection(k);
        SettingsStatus = "Guardado.";
    }

    private void SaveVoice(Action<JsonObject> write, Action apply) => SaveSetting(k =>
    {
        var v = k["Voice"] as JsonObject ?? new JsonObject();
        write(v);
        k["Voice"] = v.DeepClone();
    }, apply);

    [RelayCommand] private void OpenOnboarding() => _windows.ShowOnboarding();
    [RelayCommand] private void OpenConnectors() => _windows.ShowConnectors();
    [RelayCommand] private void OpenMemory() => _windows.ShowMemory();
    [RelayCommand] private void OpenAdvanced() => _windows.ShowSettings();
    [RelayCommand] private void OpenAudit() => _windows.ShowAudit();

    // ------------------------------------------------------------------ utilidades

    private async Task Try(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Status = "No me puedo conectar con mi cerebro ahora mismo.";
        }
    }

    public static string EmojiFor(string style) => style switch
    {
        "agua" => "💧",
        "lectura" => "📖",
        "ejercicio" => "🏋",
        "caminar" => "🚶",
        "meditacion" => "🧘",
        "sueno" => "🌙",
        "estudio" => "🎓",
        "comida" => "🍎",
        "medicina" => "💊",
        "orden" => "🧹",
        "pantalla" => "📵",
        "diario" => "✍",
        "musica" => "🎵",
        "dinero" => "🐷",
        _ => "⭐",
    };

    public static string DayLabel(DateOnly d)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return d == today ? "hoy" : d == today.AddDays(1) ? "mañana" : d < today ? $"venció el {d:dd/MM}" : d.ToString("dd/MM");
    }

    public static string WhenLabel(string when)
    {
        if (!DateTime.TryParse(when, out var t)) return when;
        var day = DateOnly.FromDateTime(t);
        var today = DateOnly.FromDateTime(DateTime.Today);
        return (day == today ? "hoy" : day == today.AddDays(1) ? "mañana" : t.ToString("dd/MM")) + $" · {t:HH:mm}";
    }

    public void Dispose() => _refresh.Stop();
}
