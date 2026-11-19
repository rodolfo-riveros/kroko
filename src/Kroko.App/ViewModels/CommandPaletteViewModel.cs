using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kroko.App.Services;
using Kroko.App.Tools.FileActions;

namespace Kroko.App.ViewModels;

/// <summary>Una fila de la barra de comandos: título de sección o algo que se puede ejecutar.</summary>
public sealed partial class CommandRow : ObservableObject
{
    public bool IsHeader { get; init; }
    public string Section { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Hint { get; init; } = "";

    /// <summary>El título partido para resaltar lo que escribiste: antes, coincidencia (negrita) y después.</summary>
    public string Before { get; init; } = "";
    public string Match { get; init; } = "";
    public string After { get; init; } = "";

    public bool IsFile { get; init; }
    internal Action? Run { get; init; }

    [ObservableProperty] public partial bool IsSelected { get; set; }
    public string ShownHint => IsSelected && Hint.Length == 0 ? "Enter" : Hint;
    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(ShownHint));
}

/// <summary>
/// Barra de comandos (diseño Kroko 2, 18/11/2026): «Buscar o pedir algo». Escribes y aparecen ACCIONES que coinciden,
/// ARCHIVOS RECIENTES (Escritorio, Documentos, Descargas) y PREGUNTAR A KROKO. ↑↓ para moverse, Enter ejecuta,
/// Tab manda lo escrito como pregunta. Se abre con Ctrl+Shift+Espacio (Ctrl+Espacio ya es para hablarle).
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly PanelViewModel _chat;
    private readonly WindowService _windows;
    private readonly IslandViewModel _island;
    private readonly TimerService _timers;
    private readonly List<(string Title, string Glyph, string Keywords, Action Run)> _actions;
    private List<FileInfo> _recent = [];

    public CommandPaletteViewModel(PanelViewModel chat, WindowService windows, IslandViewModel island, TimerService timers)
    {
        _chat = chat;
        _windows = windows;
        _island = island;
        _timers = timers;
        _actions =
        [
            ("Convertir la última descarga a PDF", G("E8AB"), "pdf foto imagen convertir descarga", () => _ = ConvertLatestAsync("pdf")),
            ("Convertir audio a MP3…", G("E8AB"), "mp3 audio convertir", () => _ = PickAndRunAsync("mp3", "Audio|*.wav;*.m4a;*.aac;*.ogg;*.flac;*.wma;*.opus")),
            ("Transcribir un audio…", G("E8D2"), "transcribir audio texto nota de voz", () => _ = PickAndRunAsync("transcribir", "Audio|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac;*.opus")),
            ("Ordenar mis Descargas", G("E8B7"), "ordenar descargas carpeta limpiar", () => Ask("Ordena mis Descargas")),
            ("Enfocarme 25 minutos", G("E916"), "enfoque pomodoro concentrar estudiar", () => _timers.StartPomodoro(25, 5)),
            ("Resumir mi correo importante", G("E715"), "correo gmail resumir email", () => Ask("Resume mi correo importante")),
            ("¿Qué hay en mi pantalla?", G("E7F4"), "pantalla error explicar captura ver", () => Ask("¿Qué hay en mi pantalla? Mírala con captura_pantalla y explícamelo.")),
            ("¿Por qué está lenta mi PC?", G("E9D9"), "lenta salud pc memoria disco batería", () => Ask("¿Por qué está lenta mi PC? Revisa estado_equipo y los procesos.")),
            ("¿Qué tengo hoy?", G("E787"), "hoy agenda resumen día", () => Ask("¿Qué tengo hoy?")),
            ("Abrir la bandeja", G("E7B8"), "bandeja archivos estacionados", () => _windows.ShowPanel("bandeja")),
            ("Abrir el portapapeles", G("E77F"), "portapapeles copiado pegar historial", () => _windows.ShowClipboard()),
            ("Mi día", G("E706"), "mi dia agenda clima", _windows.ShowMyDay),
            ("Configuración", G("E713"), "configuracion ajustes opciones", () => _windows.ShowSettings()),
        ];
    }

    public ObservableCollection<CommandRow> Rows { get; } = [];

    [ObservableProperty] public partial string Query { get; set; } = "";

    /// <summary>Se cerró o se ejecutó algo: la ventana se va.</summary>
    public event Action? CloseRequested;

    public void Open()
    {
        Query = "";
        _recent = RecentFiles();
        Refresh();
    }

    partial void OnQueryChanged(string value) => Refresh();

    private void Refresh()
    {
        Rows.Clear();
        var q = Query.Trim();

        var actions = _actions.Where(a => q.Length == 0 || Contains(a.Title, q) || Contains(a.Keywords, q)).Take(q.Length == 0 ? 6 : 5).ToList();
        if (actions.Count > 0)
        {
            Rows.Add(new CommandRow { IsHeader = true, Section = "ACCIONES" });
            foreach (var a in actions) Rows.Add(Row(a.Title, q, a.Glyph, "", "", a.Run));
        }

        var files = (q.Length == 0 ? _recent.Take(3) : _recent.Where(f => Contains(f.Name, q)).Take(4)).ToList();
        if (files.Count > 0)
        {
            Rows.Add(new CommandRow { IsHeader = true, Section = "ARCHIVOS RECIENTES" });
            foreach (var f in files)
                Rows.Add(Row(f.Name, q, G("E8A5"), Path.GetFileName(f.DirectoryName) ?? "", "", () => ActivityFeed.OpenShell(f.FullName), isFile: true));
        }

        if (q.Length > 0)
        {
            Rows.Add(new CommandRow { IsHeader = true, Section = "PREGUNTAR A KROKO" });
            Rows.Add(new CommandRow { Glyph = G("E8BD"), Before = $"«{Shorten(q)}» → enviar como pregunta", Hint = "Tab", Run = () => Ask(q) });
        }
        Select(Rows.FirstOrDefault(r => !r.IsHeader));
    }

    private static CommandRow Row(string title, string q, string glyph, string detail, string hint, Action run, bool isFile = false)
    {
        var i = q.Length == 0 ? -1 : title.IndexOf(q, StringComparison.CurrentCultureIgnoreCase);
        return new CommandRow
        {
            Glyph = glyph, Detail = detail, Hint = hint, Run = run, IsFile = isFile,
            Before = i < 0 ? title : title[..i],
            Match = i < 0 ? "" : title.Substring(i, q.Length),
            After = i < 0 ? "" : title[(i + q.Length)..],
        };
    }

    private static bool Contains(string text, string q) => text.Contains(q, StringComparison.CurrentCultureIgnoreCase)
        || RemoveAccents(text).Contains(RemoveAccents(q), StringComparison.OrdinalIgnoreCase);

    private static string RemoveAccents(string s) => new([.. s.Normalize(System.Text.NormalizationForm.FormD)
        .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)]);

    /// <summary>Icono de Segoe Fluent Icons a partir de su código hex.</summary>
    private static string G(string hex) => char.ConvertFromUtf32(Convert.ToInt32(hex, 16));

    private static string Shorten(string s) => s.Length > 40 ? s[..37] + "…" : s;

    // ---------------------------------------------------------------- teclado

    public CommandRow? Selected => Rows.FirstOrDefault(r => r.IsSelected);

    private void Select(CommandRow? row)
    {
        foreach (var r in Rows) r.IsSelected = ReferenceEquals(r, row);
    }

    public void Move(int delta)
    {
        var items = Rows.Where(r => !r.IsHeader).ToList();
        if (items.Count == 0) return;
        var i = Selected is { } s ? items.IndexOf(s) : -1;
        Select(items[(i + delta + items.Count) % items.Count]);
    }

    [RelayCommand]
    public void Execute(CommandRow? row = null)
    {
        row ??= Selected;
        if (row?.Run is not { } run) return;
        CloseRequested?.Invoke();
        run();
    }

    /// <summary>Tab: lo escrito va como pregunta a Kroko.</summary>
    public void AskTyped()
    {
        if (Query.Trim().Length == 0) return;
        CloseRequested?.Invoke();
        Ask(Query.Trim());
    }

    private void Ask(string text)
    {
        _chat.InputText = text;
        if (_chat.SendCommand.CanExecute(null)) _chat.SendCommand.Execute(null);
    }

    // ---------------------------------------------------------------- acciones con archivos

    private static readonly string Downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private async Task ConvertLatestAsync(string key)
    {
        var latest = Directory.Exists(Downloads)
            ? new DirectoryInfo(Downloads).EnumerateFiles().Where(f => FileConvert.IsImage(f.FullName)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault()
            : null;
        if (latest is null)
        {
            Ask("Busca la última foto en mis Descargas y conviértela a PDF.");
            return;
        }
        await _island.DropAsync([latest.FullName], key);
    }

    private async Task PickAndRunAsync(string key, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter, Multiselect = key != "transcribir", InitialDirectory = Downloads };
        if (dialog.ShowDialog() != true) return;
        await _island.DropAsync(dialog.FileNames, key);
    }

    /// <summary>Lo último que tocaste en el Escritorio, Documentos y Descargas (sin entrar a subcarpetas profundas).</summary>
    internal static List<FileInfo> RecentFiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots = [Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.Combine(home, "Downloads")];
        var files = new List<FileInfo>();
        foreach (var root in roots.Where(Directory.Exists).Distinct())
        {
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true };
                files.AddRange(new DirectoryInfo(root).EnumerateFiles("*", options)
                    .Where(f => !f.Name.StartsWith('~') && !f.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && !f.Name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                    .Take(800));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return [.. files.OrderByDescending(f => f.LastWriteTime).Take(300)];
    }
}
