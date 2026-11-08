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
    private CancellationTokenSource? _download;

    public SettingsViewModel(IOptions<KrokoOptions> options, BrainApiClient api, WindowService windows, MicrophoneStream mic,
        VoskModelManager models, WakeWordService wake, DocumentIndexer documents)
    {
        _loading = true;
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
        AliasesText = string.Join(Environment.NewLine, o.ProgramAliases.Select(kv => $"{kv.Key} = {kv.Value}"));
        ScriptsText = string.Join(Environment.NewLine, o.ScriptWhitelist.Select(kv => $"{kv.Key} = {kv.Value}"));
        _loading = false;
    }

    /// <summary>Pestaña abierta: 0 General, 1 Voz, 2 Cerebro, 3 Personalidad, 4 Herramientas, 5 Cuentas y apps.</summary>
    [ObservableProperty] public partial int SelectedSection { get; set; } = 1;

    public static readonly string[] Sections = ["general", "voz", "cerebro", "personalidad", "herramientas", "cuentas"];

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

    [RelayCommand]
    private void Save()
    {
        if (!VoiceController.TryParseHotkey(Hotkey, out _, out _))
        {
            SaveStatus = $"El atajo \"{Hotkey}\" no es válido (ejemplos: Ctrl+Space, Ctrl+Alt+J).";
            return;
        }
        var address = BrainAddress.Normalize(BrainUrl);
        if (!address.Ok)
        {
            SaveStatus = address.Error!;
            return;
        }
        BrainUrl = address.Url; // se ve tal como se guarda

        var k = UserSettings.LoadKrokoSection();
        k["BubbleScale"] = Math.Round(Math.Clamp(BubbleScale, 0.75, 1.5), 2);
        k["SoundsEnabled"] = SoundsEnabled;
        k["SoundVolume"] = Math.Round(Math.Clamp(SoundVolume, 0, 1), 2);
        k["HideOnFullscreen"] = HideOnFullscreen;
        k["BrainUrl"] = BrainUrl.Trim();
        k["BrainToken"] = BrainToken.Trim();
        k["LaunchLocalBrain"] = LaunchLocalBrain;
        k["DistractionsEnabled"] = DistractionsEnabled;
        k["SleepAfterMinutes"] = Math.Max(1, SleepAfterMinutes);
        k["NightSleepAfterMinutes"] = Math.Max(1, NightSleepAfterMinutes);
        k["HideAfterSleepMinutes"] = Math.Max(0, HideAfterSleepMinutes);
        k["NightStartHour"] = Math.Clamp(NightStartHour, 0, 23);
        k["NightEndHour"] = Math.Clamp(NightEndHour, 0, 23);
        k["Voice"] = BuildVoiceJson();
        k["DisabledTools"] = new JsonArray([.. Tools.Where(t => !t.Enabled).Select(t => (JsonNode?)t.Name)]);
        k["ProgramAliases"] = ParsePairs(AliasesText);
        k["ScriptWhitelist"] = ParsePairs(ScriptsText);
        k["Documents"] = new JsonObject
        {
            ["Enabled"] = DocumentsEnabled,
            ["Folders"] = new JsonArray([.. DocumentFoldersText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(f => (JsonNode?)f)]),
        };
        UserSettings.SaveKrokoSection(k);
        SaveStatus = "Guardado. Reinicia Kroko para aplicar todos los cambios.";
    }

    [RelayCommand]
    private void SaveAndRestart()
    {
        Save();
        if (SaveStatus.StartsWith("Guardado", StringComparison.Ordinal)) AppRestart.Now();
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
            : _wake.IsListening ? "✓ Escuchando «Oye Kroko» y «Hola Kroko». Todo se procesa en esta PC."
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
