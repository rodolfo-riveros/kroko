using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kroko.App.Face;
using Kroko.App.Services;
using Kroko.Shared.Protocol;

namespace Kroko.App.ViewModels;

/// <summary>
/// La isla «Kroko 2» (diseño del 18/11/2026): diez estados en una sola pieza que cuelga del borde de arriba, crece solo lo
/// necesario y vuelve a su tamaño mínimo. Cada estado tiene una acción a un clic. Este modelo decide qué estado toca
/// (por prioridad) y da los datos de cada uno; IslandView solo dibuja.
/// </summary>
public sealed partial class IslandViewModel : ObservableObject
{
    public const string Idle = "reposo", Listening = "escuchando", Thinking = "pensando", Done = "listo", Focus = "enfoque",
        Music = "musica", Reminder = "recordatorio", Near = "cerca", Clip = "portapapeles", Offline = "sin-conexion",
        Working = "trabajando", Result = "resultado";

    private readonly CompanionBehavior _behavior;
    private readonly PanelViewModel _panel;
    private readonly WindowService _windows;
    private readonly BadgeViewModel _badge;
    private readonly MediaSessionService _media;
    private readonly ActivityFeed _activity;
    private readonly QuickFiles _quick;
    private readonly BrainApiClient _api;
    private readonly BrainConnection _brain;
    private readonly ClipboardWatcher _clipboard;
    private readonly VoiceController _voice;
    private CancellationTokenSource? _work;
    private DateTime _doneUntil, _clipUntil, _resultUntil, _musicPulseUntil, _reminderUntil;

    public IslandViewModel(CompanionBehavior behavior, PanelViewModel panel, WindowService windows, BadgeViewModel badge,
        MediaSessionService media, ActivityFeed activity, QuickFiles quick, BrainApiClient api, BrainConnection brain,
        ClipboardWatcher clipboard, VoiceController voice)
    {
        _voice = voice;
        _behavior = behavior;
        _panel = panel;
        _windows = windows;
        _badge = badge;
        _media = media;
        _activity = activity;
        _quick = quick;
        _api = api;
        _brain = brain;
        _clipboard = clipboard;
        Timers = badge.Timers;

        activity.Added += a =>
        {
            DoneItem = a;
            DoneText = a.Short.Length > 0 ? a.Short : a.Text;
            _doneUntil = DateTime.Now.AddSeconds(ActivityFeed.UndoSeconds);
        };
        activity.Said += said =>
        {
            DoneItem = null;
            DoneText = said;
            _doneUntil = DateTime.Now.AddSeconds(3);
        };
        media.TrackChanged += () => _musicPulseUntil = DateTime.Now.AddSeconds(3);
        media.Changed += () => Application.Current?.Dispatcher.BeginInvoke(() =>
            behavior.SetMusicPlaying(media.Now.HasMedia && media.Now.Playing));
        brain.MessageReceived += m =>
        {
            if (m is NotifyMessage { Kind: "recordatorio" } n)
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    ReminderText = n.Text.Length > 0 ? n.Text : n.Title;
                    _reminderUntil = DateTime.Now.AddMinutes(2);
                });
        };
        clipboard.Copied += c =>
        {
            if (_panel.IsListening || _panel.IsThinking) return;
            ClipItem = c;
            _clipUntil = DateTime.Now.AddSeconds(7);
        };
    }

    public FaceAnimator Face => _behavior.Face;
    public TimerService Timers { get; }
    public PanelViewModel Panel => _panel;

    /// <summary>Uno de los estados (reposo, escuchando, pensando, listo, enfoque, musica, recordatorio, cerca, portapapeles, sin-conexion, trabajando, resultado).</summary>
    [ObservableProperty] public partial string State { get; set; } = Idle;

    /// <summary>La isla tiene algo que mostrar (sale aunque esté escondida).</summary>
    public bool Wanted { get; private set; }

    [ObservableProperty] public partial bool Hover { get; set; }
    [ObservableProperty] public partial string Clock { get; set; } = "";
    [ObservableProperty] public partial bool Online { get; set; } = true;

    // ---- pensando
    [ObservableProperty] public partial string ThinkingText { get; set; } = "Pensando…";
    [ObservableProperty] public partial string ToolChip { get; set; } = "";

    // ---- listo + deshacer
    [ObservableProperty] public partial string DoneText { get; set; } = "";
    [ObservableProperty] public partial ActivityItem? DoneItem { get; set; }
    public bool DoneCanUndo => DoneItem?.CanUndo == true;
    partial void OnDoneItemChanged(ActivityItem? value) => OnPropertyChanged(nameof(DoneCanUndo));

    // ---- música
    [ObservableProperty] public partial string MusicTitle { get; set; } = "";
    [ObservableProperty] public partial string MusicArtist { get; set; } = "";
    [ObservableProperty] public partial bool MusicPlaying { get; set; }
    [ObservableProperty] public partial System.Windows.Media.ImageSource? MusicCover { get; set; }

    // ---- recordatorio
    [ObservableProperty] public partial string ReminderText { get; set; } = "";

    // ---- archivo cerca (bolsillos)
    public ObservableCollection<Pocket> Pockets { get; } = [];
    [ObservableProperty] public partial string NearDetail { get; set; } = "";
    [ObservableProperty] public partial bool NearOpen { get; set; }
    public IReadOnlyList<string> NearFiles { get; private set; } = [];

    // ---- portapapeles
    [ObservableProperty] public partial ClipItem? ClipItem { get; set; }
    public string ClipText => ClipItem?.Kind switch { "enlace" => "Copiaste un enlace", "imagen" => "Copiaste una imagen", _ => "Copiaste un texto" };
    partial void OnClipItemChanged(ClipItem? value) => OnPropertyChanged(nameof(ClipText));

    // ---- trabajando / resultado (soltar archivos, pasos 3 y 4)
    [ObservableProperty] public partial string WorkTitle { get; set; } = "";
    [ObservableProperty] public partial string WorkDetail { get; set; } = "";
    [ObservableProperty] public partial double WorkProgress { get; set; }
    [ObservableProperty] public partial string WorkPercent { get; set; } = "";
    [ObservableProperty] public partial QuickResult? ResultData { get; set; }
    [ObservableProperty] public partial string ResultError { get; set; } = "";
    public string ResultFileName => ResultData?.Path is { } p ? Path.GetFileName(p) : "";
    public string ResultKind => ResultData?.Path is { } p ? Path.GetExtension(p).TrimStart('.').ToUpperInvariant() : "";
    public bool ResultHasFile => ResultData?.Path is { } p && File.Exists(p);
    partial void OnResultDataChanged(QuickResult? value)
    {
        OnPropertyChanged(nameof(ResultFileName));
        OnPropertyChanged(nameof(ResultKind));
        OnPropertyChanged(nameof(ResultHasFile));
    }

    /// <summary>Se llama varias veces por segundo (BubbleWindow).</summary>
    public void Refresh()
    {
        var now = DateTime.Now;
        Clock = now.ToString("HH:mm");
        Online = _behavior.IsBrainAvailable;
        RefreshMusic();

        var state = Pick(now);
        if (State != state) State = state;
        Wanted = state is not (Idle or Music) || _panel.IsActiveForIsland || _windows.IsIslandPanelOpen || _windows.IsDropOpen
                 || _windows.IsPlayerOpen || now < _musicPulseUntil || (state == Music && Hover);
    }

    private string Pick(DateTime now)
    {
        if (_work is not null) return Working;
        if (ResultData is not null || ResultError.Length > 0)
        {
            if (now < _resultUntil || Hover) return Result;
            ResultData = null;
            ResultError = "";
        }
        if (NearOpen) return Near;
        if (ReminderText.Length > 0 && now < _reminderUntil) return Reminder;
        if (_panel.IsListening) return Listening;
        if (_panel.IsThinking)
        {
            ToolChip = _panel.CurrentTool;
            ThinkingText = ToolChip.Length > 0 ? FriendlyTool(ToolChip) : "Pensando…";
            return Thinking;
        }
        if (now < _doneUntil && DoneText.Length > 0) return Done;
        if (_badge.BadgeText.Length > 0)
        {
            DoneItem = null;
            DoneText = _badge.BadgeText;
            return Done;
        }
        if (Timers.IsActive) return Focus;
        if (ClipItem is not null && now < _clipUntil) return Clip;
        if (MusicTitle.Length > 0 && (MusicPlaying || Hover)) return Music;
        if (!Online) return Offline;
        return Idle;
    }

    private void RefreshMusic()
    {
        var n = _media.Now;
        if (MusicTitle != n.Title) MusicTitle = n.Title;
        var artist = n.Artist.Length > 0 && n.App.Length > 0 ? $"{n.Artist} · {n.App}" : n.Artist.Length > 0 ? n.Artist : n.App;
        if (MusicArtist != artist) MusicArtist = artist;
        if (MusicPlaying != n.Playing) MusicPlaying = n.Playing;
        if (!ReferenceEquals(MusicCover, n.Cover)) MusicCover = n.Cover;
    }

    /// <summary>«Abriendo Notion», «Buscando en internet»… a partir del nombre de la herramienta.</summary>
    internal static string FriendlyTool(string tool)
    {
        var t = tool.ToLowerInvariant();
        return t switch
        {
            _ when t.StartsWith("notion", StringComparison.Ordinal) || t == "guardar_en_notion" => "Abriendo Notion",
            _ when t.StartsWith("todoist", StringComparison.Ordinal) => "Abriendo Todoist",
            _ when t.StartsWith("trello", StringComparison.Ordinal) => "Abriendo Trello",
            _ when t.StartsWith("navegador", StringComparison.Ordinal) => "Usando el navegador",
            _ when t.StartsWith("google_calendario", StringComparison.Ordinal) => "Revisando tu calendario",
            _ when t.StartsWith("google_", StringComparison.Ordinal) => "Abriendo Google",
            _ when t.StartsWith("correo", StringComparison.Ordinal) => "Revisando tu correo",
            "buscar_en_internet" => "Buscando en internet",
            "leer_pagina" => "Leyendo la página",
            "abrir_programa" => "Abriendo el programa",
            "abrir_url" => "Abriendo la página",
            "abrir_carpeta" or "abrir_archivo" => "Abriendo",
            "ordenar_carpeta" => "Ordenando archivos",
            "mover_archivos" => "Moviendo archivos",
            "copiar_archivos" => "Copiando archivos",
            "captura_pantalla" or "ver_pantalla" or "leer_ventana_activa" => "Mirando tu pantalla",
            "ver_imagen" => "Mirando la imagen",
            "leer_documento" or "buscar_en_documentos" => "Leyendo el documento",
            "musica" => "Poniendo música",
            "whatsapp_enviar" => "Escribiendo en WhatsApp",
            "tarea_crear" or "tarea_completar" => "Anotando",
            "recordatorio_crear" => "Programando el aviso",
            "estado_equipo" => "Revisando tu PC",
            "enviar_archivo" => "Enviando el archivo",
            _ => "Trabajando",
        };
    }

    // ---------------------------------------------------------------- acciones de cada estado

    [RelayCommand]
    private void CancelListening() => _voice.CancelListening();

    [RelayCommand]
    private void Undo() => DoneItem?.UndoNowCommand.Execute(null);

    [RelayCommand]
    private void PauseFocus() => Timers.PauseOrResume();

    [RelayCommand]
    private async Task MusicPlayPauseAsync()
    {
        if (!await _media.PlayPauseAsync()) Tools.MusicKeys.PlayPause();
    }

    [RelayCommand]
    private async Task MusicNextAsync()
    {
        if (!await _media.NextAsync()) Tools.MusicKeys.Next();
    }

    [RelayCommand]
    private void OpenPlayer() => _windows.ShowPlayer();

    /// <summary>Rueda del ratón sobre la isla con música: volumen (diseño Kroko 2).</summary>
    public void Wheel(int delta)
    {
        if (State != Music) return;
        MediaSessionService.Volume += delta > 0 ? 4 : -4;
    }

    [RelayCommand]
    private async Task SnoozeAsync()
    {
        var text = ReminderText;
        ReminderText = "";
        try
        {
            await _api.SnoozeReminderAsync(text, 10);
            ShowSaid("Te lo recuerdo en 10 min");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            ShowSaid("No pude posponerlo: sin conexión con mi cerebro");
        }
    }

    [RelayCommand]
    private void ReminderDone()
    {
        ReminderText = "";
        ShowSaid("Hecho");
    }

    [RelayCommand]
    private async Task RetryAsync() => await _brain.ReconnectNowAsync();

    // ---- portapapeles

    [RelayCommand]
    private void ClipToPhone()
    {
        if (ClipItem is not { } c) return;
        _clipUntil = DateTime.MinValue;
        _windows.ShowClipboardPhone(c);
    }

    [RelayCommand]
    private void ClipSummarize()
    {
        if (ClipItem is not { } c) return;
        _clipUntil = DateTime.MinValue;
        Ask(c.Kind == "enlace" ? $"Resúmeme esta página: {c.Text}" : $"Resúmeme este texto que copié:\n\n{c.Text}");
    }

    [RelayCommand]
    private void ClipExtractText()
    {
        if (ClipItem is not { Image: { } img }) return;
        _clipUntil = DateTime.MinValue;
        var path = ClipboardWatcher.SaveImage(img);
        Ask($"Extrae el texto de la imagen «{path}» (míralo con ver_imagen) y dámelo tal cual.");
    }

    [RelayCommand]
    private void ClipTranslate()
    {
        if (ClipItem is not { } c) return;
        _clipUntil = DateTime.MinValue;
        Ask($"Tradúceme este texto al español (o al inglés si ya está en español):\n\n{c.Text}");
    }

    private void Ask(string text)
    {
        _panel.InputText = text;
        if (_panel.SendCommand.CanExecute(null)) _panel.SendCommand.Execute(null);
    }

    private void ShowSaid(string text)
    {
        DoneItem = null;
        DoneText = text;
        _doneUntil = DateTime.Now.AddSeconds(3);
    }

    // ---------------------------------------------------------------- archivo cerca y bolsillos (soltar, pasos 1–4)

    /// <summary>Un archivo se acerca a la isla: se abre como imán con sus bolsillos.</summary>
    public void DragNear(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        _behavior.WakeUp();
        NearFiles = files;
        NearDetail = QuickFiles.Describe(files);
        Pockets.Clear();
        foreach (var p in QuickFiles.PocketsFor(files)) Pockets.Add(p);
        NearOpen = true;
    }

    public void DragAway() => NearOpen = false;

    /// <summary>Soltó sobre un bolsillo (key) o en cualquier otra parte de la isla (null = elegir en el panel).</summary>
    public async Task DropAsync(IReadOnlyList<string> files, string? key)
    {
        NearOpen = false;
        if (files.Count == 0) return;
        switch (key)
        {
            case null:
                _windows.ShowDrop(files);
                return;
            case "celular":
                _windows.ShowDrop(files, "celular");
                return;
            case "telegram":
                Ask($"Mándame por Telegram {(files.Count == 1 ? "el archivo" : "los archivos")} {string.Join(", ", files.Select(f => $"«{f}»"))}.");
                return;
            case "resumir":
                Ask($"Resúmeme el archivo «{files[0]}» (léelo con leer_documento).");
                return;
        }

        _work = new CancellationTokenSource();
        var label = key switch { "pdf" => "Convirtiendo", "mp3" => "Convirtiendo", "transcribir" => "Transcribiendo", _ => "Guardando" };
        WorkTitle = label + "…";
        WorkDetail = "";
        WorkProgress = 0;
        WorkPercent = "";
        var progress = new Progress<(int Done, int Total, string Current)>(p =>
        {
            WorkTitle = p.Total > 1 ? $"{label} {Math.Min(p.Done + 1, p.Total)} de {p.Total}" : label + "…";
            WorkDetail = key == "pdf" && p.Current.Length > 0 ? $"{p.Current} → PDF" : p.Current;
            WorkProgress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            WorkPercent = $"{(int)(WorkProgress * 100)} %";
        });
        try
        {
            ResultError = "";
            ResultData = await _quick.RunAsync(key, files, progress, _work.Token);
            _behavior.PlayExpression("success");
        }
        catch (OperationCanceledException)
        {
            ResultError = "Cancelado. No cambié nada.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                                       or NotSupportedException or Tools.ToolException or System.Net.Http.HttpRequestException)
        {
            ResultError = "No pude: " + ex.Message;
            _behavior.PlayExpression("oops");
        }
        finally
        {
            _work.Dispose();
            _work = null;
            _resultUntil = DateTime.Now.AddSeconds(12);
        }
    }

    [RelayCommand]
    private void CancelWork() => _work?.Cancel();

    [RelayCommand]
    private void ResultOpen()
    {
        if (ResultData?.Path is { } p) ActivityFeed.OpenShell(p);
        CloseResult();
    }

    [RelayCommand]
    private void ResultShowInFolder()
    {
        if (ResultData?.Path is { } p) ActivityFeed.OpenShell("explorer.exe", $"/select,\"{p}\"");
        CloseResult();
    }

    [RelayCommand]
    private void ResultToPhone()
    {
        if (ResultData?.Path is { } p) _windows.ShowDrop([p], "celular");
        CloseResult();
    }

    [RelayCommand]
    private void ResultToTray()
    {
        if (ResultData?.Path is { } p) _ = _quick.RunAsync("bandeja", [p], new Progress<(int, int, string)>(), CancellationToken.None);
        ShowSaid("En la bandeja");
        CloseResult();
    }

    [RelayCommand]
    private void ResultUndo()
    {
        if (ResultData?.Undo is { } undo)
        {
            try
            {
                ShowSaid(undo());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Tools.ToolException)
            {
                ShowSaid("No pude: " + ex.Message);
            }
        }
        CloseResult();
    }

    [RelayCommand]
    private void CloseResult()
    {
        ResultData = null;
        ResultError = "";
    }
}
