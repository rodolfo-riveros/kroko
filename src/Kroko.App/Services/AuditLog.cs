using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace Kroko.App.Services;

/// <summary>Una herramienta ejecutada (o rechazada).</summary>
public sealed record AuditEntry(
    DateTime Time,
    string Tool,
    string Risk,
    string Args,
    string Decision,
    bool Ok,
    string Result,
    long DurationMs)
{
    public string TimeText => Time.ToString("dd/MM HH:mm:ss");
    public string Status => Ok ? "✓" : "✗";
}

/// <summary>
/// Registro de auditoría (sección 8): cada herramienta con fecha, argumentos y resultado.
/// Se guarda en %LOCALAPPDATA%\Kroko\audit\audit-AAAA-MM.jsonl (una línea JSON por acción).
/// </summary>
public sealed class AuditLog
{
    private const int MaxText = 400;

    private static readonly JsonSerializerOptions FileJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly string DefaultDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", "audit");

    private readonly Lock _fileLock = new();
    private readonly ILogger<AuditLog> _log;
    private readonly string _dir;

    public AuditLog(ILogger<AuditLog> log) : this(log, DefaultDir) { }

    /// <summary>Para pruebas: guardar el registro en otra carpeta.</summary>
    internal AuditLog(ILogger<AuditLog> log, string dir)
    {
        _log = log;
        _dir = dir;
    }

    /// <summary>Lo más reciente primero (para la ventana de registro).</summary>
    public ObservableCollection<AuditEntry> Recent { get; } = [];

    public void Record(AuditEntry entry)
    {
        entry = entry with { Args = Trim(entry.Args), Result = Trim(entry.Result) };
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(Path.Combine(_dir, $"audit-{entry.Time:yyyy-MM}.jsonl"),
                    JsonSerializer.Serialize(entry, FileJson) + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "No se pudo escribir el registro de auditoría");
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Recent.Insert(0, entry);
            while (Recent.Count > 300) Recent.RemoveAt(Recent.Count - 1);
        });
    }

    /// <summary>
    /// Carga las últimas entradas del mes: las de la App (manos) y las del Brain (correo, internet,
    /// conectores), mezcladas por fecha. Se llama al arrancar y al abrir la ventana de registro.
    /// </summary>
    public void LoadRecent()
    {
        try
        {
            var entries = new List<AuditEntry>();
            foreach (var name in new[] { $"audit-{DateTime.Now:yyyy-MM}.jsonl", $"audit-brain-{DateTime.Now:yyyy-MM}.jsonl" })
            {
                var file = Path.Combine(_dir, name);
                if (!File.Exists(file)) continue;
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var lines = new List<string>();
                while (reader.ReadLine() is { } l) lines.Add(l);
                foreach (var line in lines.TakeLast(300))
                    if (JsonSerializer.Deserialize<AuditEntry>(line) is { } e) entries.Add(e);
            }
            Recent.Clear();
            foreach (var e in entries.OrderByDescending(e => e.Time).Take(300)) Recent.Add(e);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "No se pudo leer el registro de auditoría");
        }
    }

    public static string FolderPath => DefaultDir;

    private static string Trim(string s) => s.Length <= MaxText ? s : s[..MaxText] + "…";
}
