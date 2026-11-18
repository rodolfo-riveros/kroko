using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kroko.App.Tools;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace Kroko.App.Services;

/// <summary>Una cosa que hizo Kroko hoy, con su botón: Deshacer, Abrir o Ver.</summary>
public sealed partial class ActivityItem : ObservableObject
{
    public required DateTime Time { get; init; }
    public required string Text { get; init; }

    /// <summary>«Deshacer», «Abrir», «Ver» o vacío (sin botón).</summary>
    [ObservableProperty] public partial string ActionLabel { get; set; } = "";

    /// <summary>Texto corto para la isla: «Ordené 24 archivos».</summary>
    public string Short { get; init; } = "";

    public bool CanUndo => Undo is not null && !Undone;
    public bool Undone { get; private set; }
    public string TimeText => Time.ToString("HH:mm");
    public bool HasAction => ActionLabel.Length > 0;

    internal Func<string>? Undo { get; init; }
    internal Action? Open { get; init; }

    /// <summary>Lo que dijo al deshacer o al fallar (para la isla).</summary>
    public event Action<ActivityItem, string>? Done;

    /// <summary>El botón de la fila: «Deshacer» deshace; «Abrir» y «Ver» abren.</summary>
    [RelayCommand]
    private void Run()
    {
        if (ActionLabel == "Deshacer") UndoNow();
        else Open?.Invoke();
    }

    /// <summary>Deshacer (también desde la isla, aunque la fila diga «Abrir»).</summary>
    [RelayCommand]
    private void UndoNow()
    {
        try
        {
            if (Undo is not null && !Undone)
            {
                var said = Undo();
                Undone = true;
                if (ActionLabel == "Deshacer") ActionLabel = "";
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(HasAction));
                Done?.Invoke(this, said);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or ToolException)
        {
            Done?.Invoke(this, "No pude: " + ex.Message);
        }
    }
}

/// <summary>
/// «Lo que hizo Kroko» (diseño Kroko 2, 18/11/2026): lo que hizo hoy, de lo más nuevo a lo más viejo, y todo lo que se
/// puede deshacer se deshace desde aquí o desde la isla (8 s). Lo de la PC sale de las herramientas de la App; lo del
/// cerebro (Notion, tareas, correo, calendario), de su check verde con detalle. Vive en memoria: solo el día de hoy.
/// </summary>
public sealed class ActivityFeed
{
    public const int UndoSeconds = 8;
    private readonly ILogger<ActivityFeed> _log;

    public ActivityFeed(ToolExecutor tools, BrainConnection brain, ILogger<ActivityFeed> log)
    {
        _log = log;
        tools.ToolDone += (name, args, data) => Dispatch(() => FromTool(name, args, data));
        brain.MessageReceived += m =>
        {
            // Solo lo del cerebro: lo de la PC ya lo cuenta la herramienta (con su Deshacer)
            if (m is BadgeMessage { Detail: { Length: > 0 } detail } b && !IsLocalTool(b.Tool))
                Dispatch(() => Add(new ActivityItem
                {
                    Time = DateTime.Now, Text = detail, Short = b.Text,
                    ActionLabel = b.Url is null ? "" : "Ver",
                    Open = b.Url is null ? null : () => OpenShell(b.Url),
                }));
        };
    }

    public ObservableCollection<ActivityItem> Items { get; } = [];

    /// <summary>Lo último que se puede deshacer (la isla lo ofrece durante 8 s).</summary>
    public ActivityItem? LastUndoable { get; private set; }

    /// <summary>Algo nuevo (la isla lo muestra).</summary>
    public event Action<ActivityItem>? Added;

    /// <summary>Se deshizo algo o falló el botón: la frase para la isla («Listo: devolví 24 archivos»).</summary>
    public event Action<string>? Said;

    public void Add(ActivityItem item)
    {
        // Solo hoy: al cambiar de día se empieza de cero
        if (Items.Count > 0 && Items[0].Time.Date != item.Time.Date) Items.Clear();
        item.Done += (_, said) => Said?.Invoke(said);
        Items.Insert(0, item);
        while (Items.Count > 40) Items.RemoveAt(Items.Count - 1);
        if (item.CanUndo) LastUndoable = item;
        Added?.Invoke(item);
    }

    /// <summary>¿Hay algo que deshacer desde la isla ahora mismo?</summary>
    public ActivityItem? UndoableNow() =>
        LastUndoable is { CanUndo: true } a && (DateTime.Now - a.Time).TotalSeconds <= UndoSeconds ? a : null;

    // ---------------------------------------------------------------- lo que hacen las herramientas de la PC

    private static bool IsLocalTool(string? tool) =>
        tool is not null && Kroko.Shared.Tools.ToolCatalog.All.Any(t => t.Name.Equals(tool, StringComparison.OrdinalIgnoreCase));

    private void FromTool(string name, JsonElement args, JsonElement? data)
    {
        try
        {
            if (Describe(name, args, data) is { } item) Add(item);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            _log.LogDebug("Actividad sin anotar para {Tool}: {Error}", name, ex.Message);
        }
    }

    internal static ActivityItem? Describe(string name, JsonElement args, JsonElement? data)
    {
        var d = data ?? default;
        switch (name)
        {
            case "ordenar_carpeta" when Int(d, "movidos") is > 0 and var n:
            {
                var log = Str(d, "registro");
                var folder = FolderName(Str(args, "ruta"));
                return new ActivityItem
                {
                    Time = DateTime.Now, Text = $"Ordenó {n} archivo{(n == 1 ? "" : "s")} de {folder}", Short = $"Ordené {n} archivo{(n == 1 ? "" : "s")}",
                    ActionLabel = log is null ? "" : "Deshacer",
                    Undo = log is null ? null : () => OrderUndo.Undo(log) is { } r ? $"Listo: devolví {r.Devueltos} archivo(s) a su sitio" : "Ya estaba deshecho",
                };
            }
            case "mover_archivos" or "copiar_archivos" when Int(d, "hechos") is > 0 and var n:
            {
                var dest = Str(d, "destino") ?? "";
                var verb = name == "mover_archivos" ? "Movió" : "Copió";
                return new ActivityItem
                {
                    Time = DateTime.Now, Text = $"{verb} {n} archivo{(n == 1 ? "" : "s")} a {FolderName(dest)}", Short = $"{verb} {n}",
                    ActionLabel = "Abrir", Open = () => OpenShell(dest),
                };
            }
            case "renombrar":
            {
                var path = Str(args, "ruta");
                var before = Str(d, "antes");
                var now = Str(d, "ahora");
                if (path is null || before is null || now is null) return null;
                var folder = Path.GetDirectoryName(PathResolver.Resolve(path).TrimEnd('\\'))!;
                return new ActivityItem
                {
                    Time = DateTime.Now, Text = $"Renombró {before} a {now}", Short = "Renombrado",
                    ActionLabel = "Deshacer",
                    Undo = () =>
                    {
                        var from = Path.Combine(folder, now);
                        var to = Path.Combine(folder, before);
                        if (File.Exists(from)) File.Move(from, to);
                        else Directory.Move(from, to);
                        return $"Listo: vuelve a llamarse {before}";
                    },
                };
            }
            case "crear_carpeta" when Str(d, "creada") is { } made && !Bool(d, "yaExistia"):
                return new ActivityItem
                {
                    Time = DateTime.Now, Text = $"Creó la carpeta {FolderName(made)}", Short = "Carpeta creada",
                    ActionLabel = "Abrir", Open = () => OpenShell(made),
                };
            case "enviar_a_papelera" when Int(d, "enviadosAPapelera") is > 0 and var n:
                return new ActivityItem
                {
                    Time = DateTime.Now, Text = $"Mandó {n} elemento{(n == 1 ? "" : "s")} a la Papelera", Short = "A la Papelera",
                    ActionLabel = "Abrir", Open = () => OpenShell("shell:RecycleBinFolder"),
                };
            case "enviar_archivo":
                return new ActivityItem { Time = DateTime.Now, Text = $"Te envió {Str(d, "archivo") ?? "un archivo"} por Telegram", Short = "Enviado" };
            case "captura_pantalla":
                return null;
            default:
                return null;
        }
    }

    /// <summary>Un archivo que Kroko creó (convertir, juntar en PDF…): Abrir, y Deshacer = a la Papelera.</summary>
    public void AddCreatedFile(string text, string shortText, string path) => Add(new ActivityItem
    {
        Time = DateTime.Now, Text = text, Short = shortText, ActionLabel = "Abrir",
        Open = () => OpenShell(path),
        Undo = () => RecycleBin.Send(path) ? "Listo: lo mandé a la Papelera" : "Ya no estaba",
    });

    public static void OpenShell(string target, string? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Arguments = arguments ?? "" })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) { } // ya no existe o no hay programa para abrirlo
    }

    private static string FolderName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "la carpeta";
        var full = PathResolver.Resolve(path).TrimEnd('\\');
        var name = Path.GetFileName(full);
        return name.Length > 0 ? name : full;
    }

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? Int(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;

    private static bool Bool(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;

    private static void Dispatch(Action a)
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess()) d.BeginInvoke(a);
        else a();
    }
}
