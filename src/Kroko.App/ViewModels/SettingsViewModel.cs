using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kroko.App.Audio;
using Kroko.App.Services;
using Kroko.Shared.Protocol;
using Kroko.Shared.Tools;
using Microsoft.Extensions.Options;
using NAudio.Wave;

namespace Kroko.App.ViewModels;

public sealed partial class ToolToggle(string name, string description, string risk, bool enabled) : ObservableObject
{
    public string Name => name;
    public string Description => description;
    public string Risk => risk;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = enabled;
}


/// <summary>Una entrada de la barra lateral de Configuración (estilo macOS): icono en un cuadrito de color.</summary>
public sealed record SettingsNavItem(int Index, string Key, string Title, string Glyph, string Color, string? Group, string Keywords)
{
    /// <summary>Glifo oscuro sobre colores claros (amarillo), blanco sobre el resto.</summary>
    public string GlyphColor => Color is "#FFD60A" ? "#1C1C1E" : "#FFFFFF";

    /// <summary>Glyph es el código del icono de Segoe Fluent (p. ej. "E713").</summary>
    public string GlyphText => ((char)Convert.ToInt32(Glyph, 16)).ToString();
}
/// <summary>
/// Ventana de Ajustes (sección 10). Guarda en %LOCALAPPDATA%\Kroko\settings.json, que se aplica
/// por encima de appsettings.json. Algunos cambios necesitan reiniciar Kroko (botón incluido).
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private const string MicUser = "ajustes";
    private readonly BrainApiClient _api;
    private readonly WindowService _windows;
    private readonly MicrophoneStream _mic;
    private readonly VoskModelManager _models;
    private readonly WakeWordService _wake;
    private readonly VoiceSettings _voice; // las opciones vivas: los interruptores de voz se aplican al instante
    private readonly bool _loading;
    private readonly KrokoOptions _options;
    private CancellationTokenSource? _download;

    public SettingsViewModel(IOptions<KrokoOptions> options, BrainApiClient api, WindowService windows, MicrophoneStream mic,
        VoskModelManager models, WakeWordService wake, DocumentIndexer documents, OnboardingViewModel account)
    {
        _loading = true;
        Account = account;
        SelectedNav = Nav[0];
        _options = options.Value;
        _api = api;
        _windows = windows;
        _mic = mic;
        _models = models;
        _wake = wake;
        DocumentsStatus = documents.Status;
        var o = options.Value;
        _voice = o.Voice;

        StartWithWindowsEnabled = StartWithWindows.IsEnabled;
        BubbleScale = o.BubbleScale;
        SoundsEnabled = o.SoundsEnabled;
        SoundVolume = o.SoundVolume;
        HideOnFullscreen = o.HideOnFullscreen;
        IslandLook = o.IsIsland;
        IslandAutoHide = o.IslandAutoHide;
        StartHidden = o.StartHidden;
        HideAfterIdle = o.HideAfterIdleSeconds > 0;

        Hotkey = o.Voice.Hotkey;
        PushToTalk = string.Equals(o.Voice.Mode, "PushToTalk", StringComparison.OrdinalIgnoreCase);
        RepliesWithVoice = o.Voice.RepliesWithVoice;
        SpeakTypedReplies = o.Voice.SpeakTypedReplies;
        LocalTtsFallback = o.Voice.LocalTtsFallback;
        Microphones.Add("Predeterminado de Windows");
        for (var i = 0; i < WaveIn.DeviceCount; i++) Microphones.Add(WaveIn.GetCapabilities(i).ProductName);
        MicrophoneIndex = Math.Clamp(o.Voice.MicrophoneDevice + 1, 0, Microphones.Count - 1);
        WakeWordEnabled = o.Voice.WakeWord.Enabled;
        ListenInFullscreen = o.Voice.WakeWord.ListenInFullscreen;
        Conversation = o.Voice.Conversation;
        Realtime = o.Voice.Realtime;
        FollowUpSeconds = o.Voice.FollowUpSeconds;
        LiveCaptions = o.Voice.LiveCaptions;
        DocumentsEnabled = o.Documents.Enabled;
        DocumentFoldersText = string.Join(Environment.NewLine, o.Documents.EffectiveFolders);
        RefreshModelStatus();

        BrainUrl = o.BrainUrl;
        BrainToken = o.BrainToken ?? "";
        LaunchLocalBrain = o.LaunchLocalBrain;

        DistractionsEnabled = o.DistractionsEnabled;
        SleepAfterMinutes = o.SleepAfterMinutes;
        NightSleepAfterMinutes = o.NightSleepAfterMinutes;
        HideAfterSleepMinutes = o.HideAfterSleepMinutes;
        NightStartHour = o.NightStartHour;
        NightEndHour = o.NightEndHour;

        foreach (var t in ToolCatalog.All)
            Tools.Add(new ToolToggle(t.Name, t.Description, RiskLabel(t.Risk), !o.DisabledTools.Contains(t.Name, StringComparer.OrdinalIgnoreCase)));
        foreach (var tool in Tools)
            tool.PropertyChanged += (_, _) =>
            {
                _restartPending = true;
                SaveSoon();
            };
        AliasesText = string.Join(Environment.NewLine, o.ProgramAliases.Select(kv => $"{kv.Key} = {kv.Value}"));
        ScriptsText = string.Join(Environment.NewLine, o.ScriptWhitelist.Select(kv => $"{kv.Key} = {kv.Value}"));
        _loading = false;
    }

    /// <summary>
    /// Sección abierta, en el orden de la barra lateral (17/11/2026, estilo macOS y en orden de uso): Kroko (general,
    /// voz, personalidad, avisos) · Tú (perfil, cuentas) · Inteligencia (claves, gasto, cerebro) · Seguridad (permisos,
    /// privacidad).
    /// </summary>
    [ObservableProperty] public partial int SelectedSection { get; set; }

    public static readonly string[] Sections =
        ["general", "voz", "personalidad", "avisos", "perfil", "cuentas", "claves", "gasto", "cerebro", "permisos", "privacidad"];

    /// <summary>Nombres viejos de secciones (la IA o enlaces antiguos) → los nuevos.</summary>
    public static int? FindSection(string? name)
    {
        var key = name?.Trim().ToLowerInvariant() switch
        {
            "herramientas" => "permisos",
            "apps" or "cuentas y apps" => "cuentas",
            "ia" or "claves de ia" => "claves",
            "memoria" or "datos" => "privacidad",
            var k => k,
        };
        var i = Array.IndexOf(Sections, key);
        return i >= 0 ? i : null;
    }

    /// <summary>Perfil, claves de IA y cuentas: los mismos datos del asistente «Configurar Kroko».</summary>
    public OnboardingViewModel Account { get; }

    /// <summary>La barra lateral, en orden de uso.</summary>
    public static readonly SettingsNavItem[] AllNav =
    [
        new(0, "general", "General", "E713", "#8E8E93", "Kroko", "isla burbuja escondido inicio windows sonidos tamaño pantalla completa"),
        new(1, "voz", "Voz y conversación", "E720", "#FF453A", null, "oye kroko micrófono atajo tiempo real subtítulos responder"),
        new(2, "personalidad", "Personalidad", "E76E", "#BF5AF2", null, "sueño noche distracciones dormir"),
        new(3, "avisos", "Avisos", "EA8F", "#FF9F0A", null, "reuniones correos tareas vencidas resumen notificaciones"),
        new(4, "perfil", "Tu perfil", "E77B", "#0A84FF", "Tú", "nombre ciudad país zona horaria acento sobre mí"),
        new(5, "cuentas", "Cuentas y apps", "E71B", "#30B0C7", null, "gmail correo telegram google calendar trello notion todoist conectores"),
        new(6, "claves", "Claves de IA", "E8D7", "#FFD60A", "Inteligencia", "gemini deepseek tavily api key clave"),
        new(7, "gasto", "Gasto de IA", "E9D2", "#30D158", null, "tokens tope consumo ahorro"),
        new(8, "cerebro", "Conexión al cerebro", "E968", "#5E5CE6", null, "servidor dirección token localhost conexión"),
        new(9, "permisos", "Permisos", "EA18", "#636366", "Seguridad", "herramientas alias scripts documentos permisos"),
        new(10, "privacidad", "Privacidad y datos", "E72E", "#0A84FF", null, "memoria recuerdos registro auditoría galería datos"),
    ];

    public ObservableCollection<SettingsNavItem> Nav { get; } = new(AllNav);

    [ObservableProperty] public partial string SearchText { get; set; } = "";

    [ObservableProperty] public partial SettingsNavItem? SelectedNav { get; set; }

    /// <summary>Título de la página abierta (arriba, como en macOS).</summary>
    public string PageTitle => AllNav.ElementAtOrDefault(SelectedSection)?.Title ?? "Configuración";

    partial void OnSearchTextChanged(string value)
    {
        var q = value.Trim();
        var match = AllNav.Where(n => q.Length == 0
            || n.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || n.Keywords.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        Nav.Clear();
        foreach (var n in match) Nav.Add(q.Length == 0 ? n : n with { Group = null });
        if (Nav.FirstOrDefault(n => n.Index == SelectedSection) is { } keep) SelectedNav = keep;
        else if (Nav.Count > 0) SelectedNav = Nav[0];
    }

    partial void OnSelectedNavChanged(SettingsNavItem? value)
    {
        if (value is not null && value.Index != SelectedSection) SelectedSection = value.Index;
    }

    partial void OnSelectedSectionChanged(int value)
    {
        if (SelectedNav?.Index != value) SelectedNav = Nav.FirstOrDefault(n => n.Index == value);
        OnPropertyChanged(nameof(PageTitle));
    }

    // ---- Dónde vive y cuándo aparece
    [ObservableProperty] public partial bool IslandLook { get; set; }

    /// <summary>El tamaño de la burbuja solo importa si Kroko vive en la esquina.</summary>
    public bool BubbleOptionsEnabled => !IslandLook;

    partial void OnIslandLookChanged(bool value) => OnPropertyChanged(nameof(BubbleOptionsEnabled));
    [ObservableProperty] public partial bool IslandAutoHide { get; set; }
    [ObservableProperty] public partial bool StartHidden { get; set; }
    [ObservableProperty] public partial bool HideAfterIdle { get; set; }

    // ---- Avisos (viven en el cerebro)
    [ObservableProperty] public partial bool NoticeMeetings { get; set; }
    [ObservableProperty] public partial bool NoticeEmails { get; set; }
    [ObservableProperty] public partial bool NoticeOverdue { get; set; }
    [ObservableProperty] public partial bool NoticeSummary { get; set; }
    [ObservableProperty] public partial string NoticesStatus { get; set; } = "";

    /// <summary>Algún cambio guardado necesita reiniciar Kroko para notarse.</summary>
    [ObservableProperty] public partial bool NeedsRestart { get; set; }

    // ---- General
    [ObservableProperty] public partial bool StartWithWindowsEnabled { get; set; }
    [ObservableProperty] public partial double BubbleScale { get; set; }
    [ObservableProperty] public partial bool SoundsEnabled { get; set; }
    [ObservableProperty] public partial double SoundVolume { get; set; }
    [ObservableProperty] public partial bool HideOnFullscreen { get; set; }

    // ---- Voz
    [ObservableProperty] public partial string Hotkey { get; set; } = "";
    [ObservableProperty] public partial bool PushToTalk { get; set; }
    [ObservableProperty] public partial bool RepliesWithVoice { get; set; }
    [ObservableProperty] public partial bool SpeakTypedReplies { get; set; }
    [ObservableProperty] public partial bool LocalTtsFallback { get; set; }
    [ObservableProperty] public partial int MicrophoneIndex { get; set; }
    public ObservableCollection<string> Microphones { get; } = [];
    [ObservableProperty] public partial bool WakeWordEnabled { get; set; }
    [ObservableProperty] public partial bool ListenInFullscreen { get; set; }
    [ObservableProperty] public partial bool Conversation { get; set; }
    [ObservableProperty] public partial bool Realtime { get; set; }
    [ObservableProperty] public partial double FollowUpSeconds { get; set; }
    [ObservableProperty] public partial bool LiveCaptions { get; set; }
    [ObservableProperty] public partial bool DocumentsEnabled { get; set; }
    [ObservableProperty] public partial string DocumentFoldersText { get; set; } = "";
    [ObservableProperty] public partial string DocumentsStatus { get; set; } = "";
    [ObservableProperty] public partial bool ModelInstalled { get; set; }
    [ObservableProperty] public partial string ModelStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial double DownloadProgress { get; set; }
    [ObservableProperty] public partial bool MicTesting { get; set; }
    [ObservableProperty] public partial double MicLevel { get; set; }

    // ---- Cerebro
    [ObservableProperty] public partial string BrainUrl { get; set; } = "";
    [ObservableProperty] public partial string BrainToken { get; set; } = "";
    [ObservableProperty] public partial bool LaunchLocalBrain { get; set; }
    [ObservableProperty] public partial string BrainStatus { get; set; } = "";

    // ---- Gasto de IA (medidor y tope diario del cerebro)
    [ObservableProperty] public partial string UsageToday { get; set; } = "Cargando…";
    [ObservableProperty] public partial string UsageMonth { get; set; } = "";
    [ObservableProperty] public partial string UsageDetail { get; set; } = "";
    /// <summary>Tope diario en miles de tokens (0 = sin tope).</summary>
    [ObservableProperty] public partial double DailyLimitThousands { get; set; }
    [ObservableProperty] public partial string UsageStatus { get; set; } = "";

    [RelayCommand]
    public async Task LoadUsageAsync()
    {
        try
        {
            var u = await _api.GetUsageAsync();
            if (u is null) return;
            UsageToday = $"Hoy: {Tokens(u.Hoy.Total)} tokens en {u.Hoy.Llamadas} llamadas" +
                         (u.Hoy.EnCache > 0 ? $" ({Tokens(u.Hoy.EnCache)} con descuento de caché)" : "");
            UsageMonth = $"Este mes: {Tokens(u.Mes.Total)} tokens";
            UsageDetail = u.Hoy.PorTipo.Count == 0 ? "" : "Hoy por tipo: " + string.Join(" · ",
                u.Hoy.PorTipo.OrderByDescending(p => p.Value).Select(p => $"{KindName(p.Key)} {Tokens(p.Value)}"));
            DailyLimitThousands = Math.Round(u.LimiteDiario / 1000.0);
            UsageStatus = u.ModoAhorro ? "⚠ Modo ahorro: pasaste el tope de hoy (sin voz en tiempo real ni voz de Gemini)." : "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            UsageToday = "No pude leer el gasto (¿el cerebro está conectado y actualizado?).";
            UsageMonth = UsageDetail = "";
        }
    }

    [RelayCommand]
    private async Task SaveDailyLimitAsync()
    {
        try
        {
            await _api.SetDailyLimitAsync((long)Math.Max(0, Math.Round(DailyLimitThousands)) * 1000);
            await LoadUsageAsync();
            UsageStatus = DailyLimitThousands > 0
                ? $"Tope guardado: {DailyLimitThousands:N0} mil tokens por día." + (UsageStatus.Length > 0 ? " " + UsageStatus : "")
                : "Sin tope diario.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            UsageStatus = "No pude guardar el tope: el cerebro no respondió.";
        }
    }

    internal static string Tokens(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#} M",
        >= 1_000 => $"{n / 1_000.0:0.#} mil",
        _ => n.ToString(),
    };

    private static string KindName(string kind) => kind switch
    {
        "chat" => "chat",
        "tiempo_real" => "voz en tiempo real",
        "resumen" => "resúmenes",
        "voz" => "voz de Gemini",
        "transcripcion" => "entender tu voz",
        _ => kind,
    };

    // ---- Personalidad
    [ObservableProperty] public partial bool DistractionsEnabled { get; set; }
    [ObservableProperty] public partial double SleepAfterMinutes { get; set; }
    [ObservableProperty] public partial double NightSleepAfterMinutes { get; set; }
    [ObservableProperty] public partial double HideAfterSleepMinutes { get; set; }
    [ObservableProperty] public partial int NightStartHour { get; set; }
    [ObservableProperty] public partial int NightEndHour { get; set; }

    // ---- Herramientas
    public ObservableCollection<ToolToggle> Tools { get; } = [];
    [ObservableProperty] public partial string AliasesText { get; set; } = "";
    [ObservableProperty] public partial string ScriptsText { get; set; } = "";

    [ObservableProperty] public partial string SaveStatus { get; set; } = "";

    partial void OnStartWithWindowsEnabledChanged(bool value) => StartWithWindows.Set(value); // se aplica al instante

    [RelayCommand]
    public async Task CheckBrainAsync()
    {
        // Prueba lo que está escrito ahora (aunque no se haya guardado): dirección y token
        BrainStatus = "Comprobando…";
        var (_, message) = await BrainAddress.ProbeAsync(BrainUrl, BrainToken);
        BrainStatus = message;
    }

    // ---------------------------------------------------------------- se guarda solo (17/11/2026, como en macOS)

    /// <summary>Lo que se guarda en settings.json al cambiar.</summary>
    private static readonly HashSet<string> Persisted =
    [
        nameof(BubbleScale), nameof(SoundsEnabled), nameof(SoundVolume), nameof(HideOnFullscreen),
        nameof(IslandLook), nameof(StartHidden), nameof(HideAfterIdle), nameof(IslandAutoHide),
        nameof(BrainUrl), nameof(BrainToken), nameof(LaunchLocalBrain),
        nameof(DistractionsEnabled), nameof(SleepAfterMinutes), nameof(NightSleepAfterMinutes), nameof(HideAfterSleepMinutes),
        nameof(NightStartHour), nameof(NightEndHour),
        nameof(Hotkey), nameof(PushToTalk), nameof(LocalTtsFallback), nameof(MicrophoneIndex),
        nameof(AliasesText), nameof(ScriptsText), nameof(DocumentsEnabled), nameof(DocumentFoldersText),
    ];

    /// <summary>Lo que solo se nota al reiniciar Kroko.</summary>
    private static readonly HashSet<string> NeedRestart =
    [
        nameof(BubbleScale), nameof(IslandLook), nameof(BrainUrl), nameof(BrainToken), nameof(LaunchLocalBrain),
        nameof(DistractionsEnabled), nameof(SleepAfterMinutes), nameof(NightSleepAfterMinutes), nameof(HideAfterSleepMinutes),
        nameof(NightStartHour), nameof(NightEndHour), nameof(Hotkey), nameof(PushToTalk), nameof(MicrophoneIndex),
        nameof(AliasesText), nameof(ScriptsText), nameof(DocumentsEnabled), nameof(DocumentFoldersText), "Tools",
    ];

    private System.Windows.Threading.DispatcherTimer? _saveSoon;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null || !Persisted.Contains(e.PropertyName)) return;
        if (NeedRestart.Contains(e.PropertyName)) _restartPending = true;
        SaveSoon();
    }

    private bool _restartPending;

    /// <summary>Espera un momento (por si sigues escribiendo) y guarda.</summary>
    private void SaveSoon()
    {
        if (_saveSoon is null)
        {
            _saveSoon = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _saveSoon.Tick += (_, _) =>
            {
                _saveSoon.Stop();
                SaveNow();
            };
        }
        _saveSoon.Stop();
        _saveSoon.Start();
    }

    /// <summary>
    /// Guarda todo en settings.json. Lo que está mal escrito (un atajo o una dirección inválidos) no se guarda y se
    /// avisa; lo demás sí. Lo que se puede aplicar en vivo se aplica ya.
    /// </summary>
    public void SaveNow()
    {
        _saveSoon?.Stop();
        var problems = new List<string>();
        var k = UserSettings.LoadKrokoSection();

        k["Look"] = IslandLook ? "isla" : "burbuja";
        k["IslandAutoHide"] = IslandAutoHide;
        k["StartHidden"] = StartHidden;
        k["HideAfterIdleSeconds"] = HideAfterIdle ? 60 : 0;
        k["BubbleScale"] = Math.Round(Math.Clamp(BubbleScale, 0.75, 1.5), 2);
        k["SoundsEnabled"] = SoundsEnabled;
        k["SoundVolume"] = Math.Round(Math.Clamp(SoundVolume, 0, 1), 2);
        k["HideOnFullscreen"] = HideOnFullscreen;

        var address = BrainAddress.Normalize(BrainUrl);
        if (address.Ok) k["BrainUrl"] = address.Url;
        else problems.Add(address.Error!);
        k["BrainToken"] = BrainToken.Trim();
        k["LaunchLocalBrain"] = LaunchLocalBrain;

        k["DistractionsEnabled"] = DistractionsEnabled;
        k["SleepAfterMinutes"] = Math.Max(1, SleepAfterMinutes);
        k["NightSleepAfterMinutes"] = Math.Max(1, NightSleepAfterMinutes);
        k["HideAfterSleepMinutes"] = Math.Max(0, HideAfterSleepMinutes);
        k["NightStartHour"] = Math.Clamp(NightStartHour, 0, 23);
        k["NightEndHour"] = Math.Clamp(NightEndHour, 0, 23);

        var voice = BuildVoiceJson();
        if (!VoiceController.TryParseHotkey(Hotkey, out _, out _))
        {
            problems.Add($"El atajo «{Hotkey}» no es válido (ejemplos: Ctrl+Space, Ctrl+Alt+J); dejo el anterior.");
            voice["Hotkey"] = (k["Voice"] as JsonObject)?["Hotkey"]?.DeepClone() ?? _options.Voice.Hotkey;
        }
        k["Voice"] = voice;
        k["DisabledTools"] = new JsonArray([.. Tools.Where(t => !t.Enabled).Select(t => (JsonNode?)t.Name)]);
        k["ProgramAliases"] = ParsePairs(AliasesText);
        k["ScriptWhitelist"] = ParsePairs(ScriptsText);
        k["Documents"] = new JsonObject
        {
            ["Enabled"] = DocumentsEnabled,
            ["Folders"] = new JsonArray([.. DocumentFoldersText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(f => (JsonNode?)f)]),
        };
        UserSettings.SaveKrokoSection(k);

        // En vivo (sin reiniciar)
        _options.IslandAutoHide = IslandAutoHide; // la isla lo nota al instante
        _options.StartHidden = StartHidden;
        _options.HideAfterIdleSeconds = HideAfterIdle ? 60 : 0;
        _options.SoundsEnabled = SoundsEnabled;
        _options.SoundVolume = Math.Clamp(SoundVolume, 0, 1);
        _options.HideOnFullscreen = HideOnFullscreen;
        _voice.LocalTtsFallback = LocalTtsFallback;

        if (_restartPending) NeedsRestart = true;
        SaveStatus = problems.Count > 0 ? string.Join(" ", problems) : "";
    }

    [RelayCommand]
    private void Restart()
    {
        SaveNow();
        AppRestart.Now();
    }

    // ---- Avisos

    [RelayCommand]
    public async Task LoadNoticesAsync()
    {
        try
        {
            if (await _api.GetNoticesAsync() is not { } n) return;
            _loadingNotices = true;
            NoticeMeetings = n.Reuniones;
            NoticeEmails = n.Correos;
            NoticeOverdue = n.TareasVencidas;
            NoticeSummary = n.ResumenDelDia;
            NoticesStatus = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            NoticesStatus = "No pude leer los avisos: el cerebro no responde.";
        }
        finally
        {
            _loadingNotices = false;
        }
    }

    private bool _loadingNotices;

    partial void OnNoticeMeetingsChanged(bool value) => _ = SaveNoticesAsync();
    partial void OnNoticeEmailsChanged(bool value) => _ = SaveNoticesAsync();
    partial void OnNoticeOverdueChanged(bool value) => _ = SaveNoticesAsync();
    partial void OnNoticeSummaryChanged(bool value) => _ = SaveNoticesAsync();

    private async Task SaveNoticesAsync()
    {
        if (_loading || _loadingNotices) return;
        try
        {
            await _api.SetNoticesAsync(new NoticesDto(NoticeMeetings, NoticeEmails, NoticeOverdue, NoticeSummary));
            NoticesStatus = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            NoticesStatus = "No pude guardar los avisos: el cerebro no responde.";
        }
    }

    [RelayCommand] private void OpenConnectors() => _windows.ShowConnectors();
    [RelayCommand] private void OpenOnboarding() => _windows.ShowOnboarding();
    [RelayCommand] private void OpenAudit() => _windows.ShowAudit();
    [RelayCommand] private void OpenGallery() => _windows.ShowGallery();
    [RelayCommand] private void OpenMemory() => _windows.ShowMemory();

    /// <summary>"nombre = ruta" por línea → objeto JSON.</summary>
    internal static JsonObject ParsePairs(string text)
    {
        var obj = new JsonObject();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = line.IndexOf('=');
            if (i <= 0) continue;
            var key = line[..i].Trim();
            var value = line[(i + 1)..].Trim().Trim('"');
            if (key.Length > 0 && value.Length > 0) obj[key] = value;
        }
        return obj;
    }

    // ---------------------------------------------------------------- voz y conversación (se aplican al instante)

    private JsonObject BuildVoiceJson() => new()
    {
        ["Hotkey"] = Hotkey.Trim(),
        ["Mode"] = PushToTalk ? "PushToTalk" : "Tap",
        ["RepliesWithVoice"] = RepliesWithVoice,
        ["SpeakTypedReplies"] = SpeakTypedReplies,
        ["LocalTtsFallback"] = LocalTtsFallback,
        ["MicrophoneDevice"] = MicrophoneIndex - 1,
        ["Conversation"] = Conversation,
        ["Realtime"] = Realtime,
        ["FollowUpSeconds"] = Math.Round(Math.Clamp(FollowUpSeconds, 3, 20)),
        ["LiveCaptions"] = LiveCaptions,
        ["WakeWord"] = new JsonObject { ["Enabled"] = WakeWordEnabled, ["ListenInFullscreen"] = ListenInFullscreen },
    };

    /// <summary>Guarda solo la sección de voz (los interruptores de voz no esperan al botón Guardar).</summary>
    private void ApplyVoiceNow()
    {
        if (_loading) return;
        _voice.WakeWord.Enabled = WakeWordEnabled;
        _voice.WakeWord.ListenInFullscreen = ListenInFullscreen;
        _voice.RepliesWithVoice = RepliesWithVoice;
        _voice.SpeakTypedReplies = SpeakTypedReplies;
        _voice.Conversation = Conversation;
        _voice.Realtime = Realtime;
        _voice.FollowUpSeconds = Math.Clamp(FollowUpSeconds, 3, 20);
        _voice.LiveCaptions = LiveCaptions;
        _wake.Refresh();

        var k = UserSettings.LoadKrokoSection();
        var voice = k["Voice"] as JsonObject ?? new JsonObject();
        voice["RepliesWithVoice"] = RepliesWithVoice;
        voice["SpeakTypedReplies"] = SpeakTypedReplies;
        voice["Conversation"] = Conversation;
        voice["Realtime"] = Realtime;
        voice["FollowUpSeconds"] = Math.Round(Math.Clamp(FollowUpSeconds, 3, 20));
        voice["LiveCaptions"] = LiveCaptions;
        voice["WakeWord"] = new JsonObject { ["Enabled"] = WakeWordEnabled, ["ListenInFullscreen"] = ListenInFullscreen };
        k["Voice"] = voice.DeepClone();
        UserSettings.SaveKrokoSection(k);
        RefreshModelStatus();
    }

    partial void OnWakeWordEnabledChanged(bool value) => ApplyVoiceNow();
    partial void OnListenInFullscreenChanged(bool value) => ApplyVoiceNow();
    partial void OnRepliesWithVoiceChanged(bool value) => ApplyVoiceNow();
    partial void OnSpeakTypedRepliesChanged(bool value) => ApplyVoiceNow();
    partial void OnConversationChanged(bool value) => ApplyVoiceNow();
    partial void OnRealtimeChanged(bool value) => ApplyVoiceNow();
    partial void OnFollowUpSecondsChanged(double value) => ApplyVoiceNow();
    partial void OnLiveCaptionsChanged(bool value) => ApplyVoiceNow();

    private void RefreshModelStatus()
    {
        ModelInstalled = _models.IsInstalled;
        ModelStatus = !ModelInstalled
            ? "Falta el modelo de voz en español (40 MB, se descarga una sola vez del sitio oficial de Vosk)."
            : !WakeWordEnabled ? "Modelo instalado. La frase de activación está apagada."
            : !MicrophoneStream.HasMicrophone ? "Modelo instalado. Conecta un micrófono y empezaré a escuchar."
            : _wake.IsListening ? "✓ Escuchando «Oye Kroko». Todo se procesa en esta PC."
            : _wake.UserPaused ? "En pausa desde la bandeja."
            : "Modelo instalado. Esperando el micrófono…";
    }

    [RelayCommand]
    private async Task DownloadModelAsync()
    {
        if (IsDownloading)
        {
            _download?.Cancel();
            return;
        }
        IsDownloading = true;
        DownloadProgress = 0;
        _download = new CancellationTokenSource();
        try
        {
            ModelStatus = "Descargando el modelo de voz…";
            await _models.DownloadAsync(new Progress<double>(p => DownloadProgress = p), _download.Token);
            _wake.Refresh();
            RefreshModelStatus();
        }
        catch (OperationCanceledException)
        {
            ModelStatus = "Descarga cancelada.";
        }
        catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or System.IO.InvalidDataException or InvalidOperationException)
        {
            ModelStatus = "No pude descargar el modelo: " + ex.Message;
        }
        finally
        {
            IsDownloading = false;
            _download.Dispose();
            _download = null;
        }
    }

    /// <summary>Medidor del micrófono: abre el micrófono mientras está activado.</summary>
    partial void OnMicTestingChanged(bool value)
    {
        if (value)
        {
            _mic.Data += OnMicData;
            if (!_mic.TryAcquire(MicUser, MicrophoneIndex - 1))
            {
                _mic.Data -= OnMicData;
                MicTesting = false;
                ModelStatus = "No pude abrir el micrófono.";
            }
        }
        else
        {
            _mic.Data -= OnMicData;
            _mic.Release(MicUser);
            MicLevel = 0;
        }
    }

    private void OnMicData(byte[] buffer, int count) =>
        MicLevel = Math.Max(MicrophoneStream.LevelOf(buffer, count), MicLevel * 0.8); // caída suave

    public void Dispose()
    {
        if (_saveSoon?.IsEnabled == true) SaveNow(); // lo último que escribiste no se pierde al cerrar
        _download?.Cancel();
        if (MicTesting) MicTesting = false;
    }

    private static string RiskLabel(ToolRisk risk) => risk switch
    {
        ToolRisk.Read => "Lectura",
        ToolRisk.Write => "Cambio",
        _ => "Pide permiso",
    };

    public static string Percent(double v) => v.ToString("P0", CultureInfo.GetCultureInfo("es-PE"));
}
