using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kroko.Brain.Agent;
using Kroko.Brain.Mcp;
using Kroko.Brain.Memory;
using Kroko.Brain.Providers;
using Kroko.Shared.Protocol;

namespace Kroko.Brain.Apps;

/// <summary>Las llamadas a Notion que necesita el cuaderno (en producción, el conector MCP oficial; en pruebas, uno simulado).</summary>
public interface INotionCalls
{
    bool IsConnected { get; }
    Task<string> CallAsync(string tool, JsonObject args, CancellationToken ct);
}

public sealed class McpNotionCalls(McpHub hub) : INotionCalls
{
    public const string Server = "notion";

    public bool IsConnected => hub.IsConnected(Server);

    public Task<string> CallAsync(string tool, JsonObject args, CancellationToken ct) =>
        hub.CallAsync(Server, tool, args.ToDictionary(p => p.Key, p => (object?)JsonSerializer.SerializeToElement(p.Value)), ct);
}

/// <summary>Una nota o entrada de agenda para Notion.</summary>
public sealed record NotionNote(string Title, string? Content, string Type, string? Date, IReadOnlyList<string> Tags);

/// <summary>
/// Las notas y la agenda del dueño en Notion (decidido por Pol, 08/11/2026): todo va a una base de datos
/// «Kroko · Notas y agenda» que Kroko crea UNA vez (Nombre, Tipo, Fecha, Etiquetas + vistas Calendario y Por tipo)
/// y cuya dirección guarda en su memoria. Lo hace el código, no la IA paso a paso: así funciona igual por voz en
/// tiempo real, por Telegram o escribiendo, y no se pierde a medio camino.
/// </summary>
public sealed partial class NotionNotebook(INotionCalls notion, KrokoDatabase db, TimeProvider time, ILogger<NotionNotebook> log)
{
    public const string DatabaseTitle = AppRouting.NotionDatabase;
    public static readonly string[] Types = ["Nota", "Idea", "Agenda", "Reunión", "Lista"];

    internal const string KeyDataSource = "notion.notas.data_source";
    internal const string KeyDatabase = "notion.notas.database";

    private const string Schema =
        """CREATE TABLE ("Nombre" TITLE, "Tipo" SELECT('Nota':gray, 'Idea':yellow, 'Agenda':blue, 'Reunión':purple, 'Lista':green), "Fecha" DATE, "Etiquetas" MULTI_SELECT('personal':blue, 'trabajo':orange))""";

    private readonly SemaphoreSlim _setup = new(1, 1);

    public bool IsConnected => notion.IsConnected;

    /// <summary>Guarda la nota y devuelve el enlace de la página creada (o null si Notion no lo dio).</summary>
    public async Task<string?> SaveAsync(NotionNote note, CancellationToken ct)
    {
        if (!notion.IsConnected)
            throw new InvalidOperationException("Notion no está conectado: clic derecho en Kroko → Conectores → Conectar Notion.");

        var dataSource = await EnsureDatabaseAsync(ct);
        try
        {
            return await CreatePageAsync(dataSource, note, withTags: true, ct);
        }
        catch (McpCallException ex) when (note.Tags.Count > 0 && Mentions(ex, "Etiquetas", "multi"))
        {
            log.LogInformation("Notion no aceptó las etiquetas; guardo la nota sin ellas ({Message})", ex.Message);
            return await CreatePageAsync(dataSource, note, withTags: false, ct);
        }
        catch (McpCallException ex) when (Mentions(ex, "not found", "no encontr", "could not find", "archived", "does not exist"))
        {
            // La base se borró o se movió en Notion: se olvida y se crea de nuevo (una sola vez)
            log.LogWarning("La base de notas de Notion ya no está ({Message}); la creo de nuevo", ex.Message);
            db.SetValue(KeyDataSource, "");
            db.SetValue(KeyDatabase, "");
            dataSource = await EnsureDatabaseAsync(ct);
            return await CreatePageAsync(dataSource, note, withTags: true, ct);
        }
    }

    /// <summary>El data source (collection://…) de la base: el guardado, el que ya exista con ese nombre o uno nuevo.</summary>
    internal async Task<string> EnsureDatabaseAsync(CancellationToken ct)
    {
        if (db.GetValue(KeyDataSource) is { Length: > 0 } known) return known;
        await _setup.WaitAsync(ct);
        try
        {
            if (db.GetValue(KeyDataSource) is { Length: > 0 } again) return again;
            var (dataSource, database) = await FindExistingAsync(ct) ?? await CreateDatabaseAsync(ct);
            db.SetValue(KeyDataSource, dataSource);
            db.SetValue(KeyDatabase, database ?? "");
            return dataSource;
        }
        finally
        {
            _setup.Release();
        }
    }

    private async Task<(string DataSource, string? Database)?> FindExistingAsync(CancellationToken ct)
    {
        string text;
        try
        {
            text = await notion.CallAsync("notion-search", new JsonObject { ["query"] = DatabaseTitle, ["page_size"] = 10 }, ct);
        }
        catch (McpCallException)
        {
            return null;
        }
        foreach (var id in DatabaseIdsTitled(text, DatabaseTitle))
        {
            try
            {
                var fetched = await notion.CallAsync("notion-fetch", new JsonObject { ["id"] = id }, ct);
                if (DataSourceId(fetched) is { } ds)
                {
                    log.LogInformation("Uso la base de Notion que ya existía: {Title}", DatabaseTitle);
                    return (ds, id);
                }
            }
            catch (McpCallException) { }
        }
        return null;
    }

    private async Task<(string DataSource, string? Database)> CreateDatabaseAsync(CancellationToken ct)
    {
        var created = await notion.CallAsync("notion-create-database", new JsonObject
        {
            ["title"] = DatabaseTitle,
            ["description"] = "Notas, ideas y agenda que guarda Kroko. Las citas con fecha también van a tu Google Calendar.",
            ["schema"] = Schema,
        }, ct);
        var dataSource = DataSourceId(created)
            ?? throw new McpCallException("Notion creó la base pero no devolvió su identificador. Inténtalo otra vez.");
        var database = NotionIds(created).FirstOrDefault();
        log.LogInformation("Base de Notion creada: {Title}", DatabaseTitle);

        // Vistas útiles (si fallan, la base sirve igual)
        if (database is not null)
        {
            foreach (var (name, type, configure) in new[]
                     {
                         ("Calendario", "calendar", "CALENDAR BY \"Fecha\""),
                         ("Por tipo", "board", "GROUP BY \"Tipo\""),
                     })
            {
                try
                {
                    await notion.CallAsync("notion-create-view", new JsonObject
                    {
                        ["database_id"] = database, ["data_source_id"] = dataSource, ["name"] = name, ["type"] = type, ["configure"] = configure,
                    }, ct);
                }
                catch (McpCallException ex)
                {
                    log.LogInformation("No se pudo crear la vista {View} en Notion ({Message})", name, ex.Message);
                }
            }
        }
        return (dataSource, database);
    }

    private async Task<string?> CreatePageAsync(string dataSource, NotionNote note, bool withTags, CancellationToken ct)
    {
        var properties = new JsonObject
        {
            ["Nombre"] = note.Title.Trim(),
            ["Tipo"] = NormalizeType(note.Type),
        };
        if (ParseDate(note.Date) is { } date)
        {
            properties["date:Fecha:start"] = date.Value;
            properties["date:Fecha:is_datetime"] = date.HasTime ? 1 : 0;
        }
        if (withTags && note.Tags.Count > 0)
            properties["Etiquetas"] = new JsonArray([.. note.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => (JsonNode?)t.Trim())]);

        var page = new JsonObject { ["properties"] = properties };
        if (!string.IsNullOrWhiteSpace(note.Content)) page["content"] = note.Content.Trim();

        var result = await notion.CallAsync("notion-create-pages", new JsonObject
        {
            ["parent"] = new JsonObject { ["data_source_id"] = dataSource.Replace("collection://", "", StringComparison.Ordinal) },
            ["pages"] = new JsonArray(page),
            ["allow_async"] = false,
        }, ct);
        return NotionUrl(result);
    }

    // ------------------------------------------------------------------ ayudas (públicas para las pruebas)

    public static string NormalizeType(string? type)
    {
        var t = (type ?? "").Trim();
        var match = Types.FirstOrDefault(x => string.Equals(Strip(x), Strip(t), StringComparison.OrdinalIgnoreCase));
        return match ?? "Nota";

        static string Strip(string s) => s.Replace("ó", "o", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"2026-11-13" (día) o "2026-11-13T15:00" (con hora: se le pone la zona horaria del dueño).</summary>
    public (string Value, bool HasTime)? ParseDate(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return null;
        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return (day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), false);
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withZone) && HasOffset(s))
            return (withZone.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), true);
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            var offset = time.LocalTimeZone.GetUtcOffset(local);
            return (new DateTimeOffset(local, offset).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), true);
        }
        return null;
    }

    private static bool HasOffset(string s) => s.EndsWith('Z') || OffsetRegex().IsMatch(s);

    internal static string? DataSourceId(string text) =>
        DataSourceRegex().Match(text) is { Success: true } m ? "collection://" + m.Groups[1].Value : null;

    /// <summary>Ids de Notion (32 hex, con o sin guiones) en las direcciones notion.so / notion.com de un texto.</summary>
    internal static IEnumerable<string> NotionIds(string text) =>
        NotionUrlRegex().Matches(text).Select(m => m.Groups["id"].Value.Replace("-", "", StringComparison.Ordinal)).Distinct();

    internal static string? NotionUrl(string text)
    {
        var urls = NotionUrlRegex().Matches(text).Select(m => m.Value).ToList();
        return urls.Count > 0 ? urls[^1] : null; // la página nueva suele ser la última dirección de la respuesta
    }

    /// <summary>De la respuesta de notion-search, los ids de las bases cuyo título es exactamente ese.</summary>
    internal static IEnumerable<string> DatabaseIdsTitled(string searchText, string title)
    {
        var ids = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(searchText);
            if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in results.EnumerateArray())
                {
                    var type = r.TryGetProperty("type", out var t) ? t.GetString() : null;
                    var name = r.TryGetProperty("title", out var n) ? n.GetString() : null;
                    if (type is "database" or "data_source" && string.Equals(name?.Trim(), title, StringComparison.OrdinalIgnoreCase)
                        && r.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value)
                        ids.Add(value);
                }
            }
        }
        catch (JsonException) { }
        return ids;
    }

    private static bool Mentions(Exception ex, params string[] words) =>
        words.Any(w => ex.Message.Contains(w, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"collection://([0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12})")]
    private static partial Regex DataSourceRegex();

    [GeneratedRegex(@"https://(?:www\.|app\.)?notion\.(?:so|com)/[^\s""'<>)]*?(?<id>[0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(?:\?[^\s""'<>)]*)?")]
    private static partial Regex NotionUrlRegex();

    [GeneratedRegex(@"[+-]\d{2}:?\d{2}$")]
    private static partial Regex OffsetRegex();
}

/// <summary>«Anota en Notion…», «agenda en Notion la reunión…»: una sola herramienta que hace todo el trabajo.</summary>
public sealed class GuardarEnNotionTool(NotionNotebook notebook) : IBrainTool
{
    public ToolDefinition Definition { get; } = BrainToolResult.Define("guardar_en_notion",
        $"Guarda una nota, idea, lista o entrada de agenda en el Notion de tu dueño, en su base «{NotionNotebook.DatabaseTitle}» " +
        "(si no existe, la crea sola la primera vez). Úsala SIEMPRE para notas y agenda en Notion, en vez de las herramientas " +
        "notion_… sueltas. Para citas con hora, crea además el evento en Google Calendar si está vinculado.",
        """
        {"type":"object","properties":{
          "titulo":{"type":"string","description":"Título corto y claro"},
          "contenido":{"type":"string","description":"El texto completo de la nota (Markdown: viñetas con -, casillas con - [ ])"},
          "tipo":{"type":"string","enum":["Nota","Idea","Agenda","Reunión","Lista"]},
          "fecha":{"type":"string","description":"Solo si tiene fecha: 2026-11-13 (día) o 2026-11-13T15:00 (con hora local del dueño)"},
          "etiquetas":{"type":"array","items":{"type":"string"},"description":"Etiquetas útiles, p. ej. trabajo, personal, clientes"}
        },"required":["titulo","tipo"]}
        """, ToolRisk.Write);

    public string Describe(JsonElement args) => $"Guardar en Notion: {BrainToolResult.Summarize(args)}";

    public async Task<ToolExecution> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var title = args.TryGetProperty("titulo", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(title)) return BrainToolResult.Fail("Falta el título de la nota.");
        var note = new NotionNote(
            title,
            args.TryGetProperty("contenido", out var c) ? c.GetString() : null,
            args.TryGetProperty("tipo", out var ty) ? ty.GetString() ?? "Nota" : "Nota",
            args.TryGetProperty("fecha", out var f) ? f.GetString() : null,
            args.TryGetProperty("etiquetas", out var e) && e.ValueKind == JsonValueKind.Array
                ? [.. e.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0)]
                : []);
        try
        {
            var url = await notebook.SaveAsync(note, ct);
            return BrainToolResult.Ok(new
            {
                guardado = $"«{note.Title.Trim()}» en {NotionNotebook.DatabaseTitle}",
                tipo = NotionNotebook.NormalizeType(note.Type),
                enlace = url,
            });
        }
        catch (Exception ex) when (ex is McpCallException or InvalidOperationException)
        {
            return BrainToolResult.Fail(ex.Message);
        }
    }
}
