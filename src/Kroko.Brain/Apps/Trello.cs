using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kroko.Brain.Agent;
using Kroko.Brain.Providers;
using Kroko.Shared.Protocol;
using Kroko.Shared.Tools;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Apps;

/// <summary>
/// Sección "Trello". La clave y el token se generan en https://trello.com/power-ups/admin y se guardan
/// en "Configurar Kroko" (cifrados) o con TRELLO_API_KEY / TRELLO_TOKEN.
/// </summary>
public sealed class TrelloOptions
{
    public const string Section = "Trello";

    public string? ApiKey { get; set; }
    public string? Token { get; set; }
    public string BaseUrl { get; set; } = "https://api.trello.com/1";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Token);
}

public sealed class TrelloNotConfiguredException()
    : Exception("Trello todavía no está conectado: falta la clave y el token (Configurar Kroko → Tus apps).");

/// <summary>Cliente REST de Trello. La clave va en la cabecera Authorization (nunca en la URL, para que no salga en los registros).</summary>
public sealed class TrelloClient(HttpClient http, IOptionsMonitor<TrelloOptions> options)
{
    public bool IsConfigured => options.CurrentValue.IsConfigured;

    public async Task<JsonElement> SendAsync(HttpMethod method, string path, Dictionary<string, string?>? query, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.IsConfigured) throw new TrelloNotConfiguredException();
        var qs = query is null ? "" : "?" + string.Join('&', query.Where(kv => kv.Value is not null)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        using var req = new HttpRequestMessage(method, $"{o.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}{qs}");
        req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", $"oauth_consumer_key=\"{o.ApiKey}\", oauth_token=\"{o.Token}\"");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Trello rechazó la clave o el token. Hay que generarlos de nuevo.");
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Trello respondió {(int)res.StatusCode}: {Trim(await res.Content.ReadAsStringAsync(ct))}");
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public Task<JsonElement> GetAsync(string path, Dictionary<string, string?>? query, CancellationToken ct) => SendAsync(HttpMethod.Get, path, query, ct);

    /// <summary>Tablero por id o por nombre (sin distinguir mayúsculas, acepta parte del nombre).</summary>
    public async Task<(string Id, string Name)> FindBoardAsync(string boardIdOrName, CancellationToken ct)
    {
        var boards = await GetAsync("members/me/boards", new() { ["fields"] = "name", ["filter"] = "open" }, ct);
        return Pick(boards, boardIdOrName, "tablero");
    }

    public async Task<(string Id, string Name)> FindListAsync(string boardId, string listIdOrName, CancellationToken ct)
    {
        var lists = await GetAsync($"boards/{boardId}/lists", new() { ["fields"] = "name", ["filter"] = "open" }, ct);
        return Pick(lists, listIdOrName, "lista");
    }

    private static (string, string) Pick(JsonElement items, string wanted, string what)
    {
        var all = items.EnumerateArray().Select(e => (Id: e.GetProperty("id").GetString()!, Name: e.GetProperty("name").GetString() ?? "")).ToList();
        var w = wanted.Trim();
        var hit = all.FirstOrDefault(x => x.Id == w || x.Name.Equals(w, StringComparison.OrdinalIgnoreCase));
        if (hit.Id is null) hit = all.FirstOrDefault(x => x.Name.Contains(w, StringComparison.OrdinalIgnoreCase));
        return hit.Id is null
            ? throw new ArgumentException($"No encontré el {what} \"{wanted}\". Hay: {string.Join(", ", all.Select(x => x.Name))}.")
            : hit;
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200];
}

/// <summary>Base de las herramientas de Trello: errores claros y nunca excepciones crudas.</summary>
public abstract class TrelloTool(TrelloClient trello) : IBrainTool
{
    protected TrelloClient Trello => trello;

    public abstract ToolDefinition Definition { get; }

    protected abstract Task<object> RunAsync(JsonElement args, CancellationToken ct);

    public async Task<ToolExecution> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            return BrainToolResult.Ok(await RunAsync(args, ct));
        }
        catch (Exception ex) when (ex is TrelloNotConfiguredException or ArgumentException or InvalidOperationException or HttpRequestException)
        {
            return ToolConfirmation.Fail(ex.Message);
        }
    }

    protected static object Card(JsonElement c) => new
    {
        id = c.GetProperty("id").GetString(),
        nombre = c.GetProperty("name").GetString(),
        vence = c.TryGetProperty("due", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
        hecha = c.TryGetProperty("dueComplete", out var dc) && dc.ValueKind == JsonValueKind.True,
        lista = c.TryGetProperty("idList", out var l) ? l.GetString() : null,
        descripcion = c.TryGetProperty("desc", out var desc) && desc.GetString() is { Length: > 0 } t ? (t.Length > 300 ? t[..300] + "…" : t) : null,
    };
}

public sealed class TrelloTablerosTool(TrelloClient trello) : TrelloTool(trello)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("trello_tableros",
        "Lista los tableros de Trello de tu dueño con sus listas (columnas).",
        """{"type":"object","properties":{}}""", ToolRisk.Read);

    protected override async Task<object> RunAsync(JsonElement args, CancellationToken ct)
    {
        var boards = await Trello.GetAsync("members/me/boards",
            new() { ["fields"] = "name", ["filter"] = "open", ["lists"] = "open", ["list_fields"] = "name" }, ct);
        return new
        {
            tableros = boards.EnumerateArray().Select(b => new
            {
                id = b.GetProperty("id").GetString(),
                nombre = b.GetProperty("name").GetString(),
                listas = b.TryGetProperty("lists", out var ls) ? ls.EnumerateArray().Select(l => l.GetProperty("name").GetString()).ToList() : [],
            }),
        };
    }
}

public sealed class TrelloTarjetasTool(TrelloClient trello) : TrelloTool(trello)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("trello_tarjetas",
        "Tarjetas abiertas de un tablero de Trello (opcional: solo de una lista).",
        """{"type":"object","properties":{"tablero":{"type":"string","description":"Nombre o id"},"lista":{"type":"string"}},"required":["tablero"]}""",
        ToolRisk.Read);

    protected override async Task<object> RunAsync(JsonElement args, CancellationToken ct)
    {
        var board = await Trello.FindBoardAsync(ToolCatalog.Str(args, "tablero"), ct);
        var lists = await Trello.GetAsync($"boards/{board.Id}/lists", new() { ["fields"] = "name", ["filter"] = "open" }, ct);
        var listNames = lists.EnumerateArray().ToDictionary(l => l.GetProperty("id").GetString()!, l => l.GetProperty("name").GetString());
        string path = $"boards/{board.Id}/cards";
        if (ToolCatalog.Str(args, "lista") is { Length: > 0 } wanted)
            path = $"lists/{(await Trello.FindListAsync(board.Id, wanted, ct)).Id}/cards";
        var cards = await Trello.GetAsync(path, new() { ["fields"] = "name,due,dueComplete,idList,desc" }, ct);
        return new
        {
            tablero = board.Name,
            tarjetas = cards.EnumerateArray().Take(60).Select(c => new
            {
                id = c.GetProperty("id").GetString(),
                nombre = c.GetProperty("name").GetString(),
                lista = listNames.GetValueOrDefault(c.GetProperty("idList").GetString() ?? ""),
                vence = c.TryGetProperty("due", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                hecha = c.TryGetProperty("dueComplete", out var dc) && dc.ValueKind == JsonValueKind.True,
            }),
        };
    }
}

public sealed class TrelloCrearTarjetaTool(TrelloClient trello) : TrelloTool(trello)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("trello_crear_tarjeta",
        "Crea una tarjeta en una lista de Trello.",
        """
        {"type":"object","properties":{"tablero":{"type":"string"},"lista":{"type":"string"},"nombre":{"type":"string"},
          "descripcion":{"type":"string"},"vence":{"type":"string","description":"Fecha y hora ISO 8601 (opcional)"}},
         "required":["tablero","lista","nombre"]}
        """);

    protected override async Task<object> RunAsync(JsonElement args, CancellationToken ct)
    {
        var board = await Trello.FindBoardAsync(ToolCatalog.Str(args, "tablero"), ct);
        var list = await Trello.FindListAsync(board.Id, ToolCatalog.Str(args, "lista"), ct);
        var card = await Trello.SendAsync(HttpMethod.Post, "cards", new()
        {
            ["idList"] = list.Id,
            ["name"] = ToolCatalog.Str(args, "nombre"),
            ["desc"] = ToolCatalog.Str(args, "descripcion") is { Length: > 0 } d ? d : null,
            ["due"] = ToolCatalog.Str(args, "vence") is { Length: > 0 } v ? v : null,
        }, ct);
        return new { creada = Card(card), tablero = board.Name, lista = list.Name };
    }
}

public sealed class TrelloMoverTarjetaTool(TrelloClient trello) : TrelloTool(trello)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("trello_mover_tarjeta",
        "Mueve una tarjeta (por su id) a otra lista del mismo tablero, o la marca como hecha.",
        """{"type":"object","properties":{"id":{"type":"string"},"tablero":{"type":"string"},"lista":{"type":"string"},"hecha":{"type":"boolean"}},"required":["id"]}""");

    protected override async Task<object> RunAsync(JsonElement args, CancellationToken ct)
    {
        var id = ToolCatalog.Str(args, "id");
        var query = new Dictionary<string, string?>();
        if (ToolCatalog.Str(args, "lista") is { Length: > 0 } wanted)
        {
            var board = await Trello.FindBoardAsync(ToolCatalog.Str(args, "tablero") is { Length: > 0 } b ? b
                : throw new ArgumentException("Para moverla dime también el tablero."), ct);
            query["idList"] = (await Trello.FindListAsync(board.Id, wanted, ct)).Id;
        }
        if (args.TryGetProperty("hecha", out var done) && done.ValueKind is JsonValueKind.True or JsonValueKind.False)
            query["dueComplete"] = done.GetBoolean() ? "true" : "false";
        if (query.Count == 0) throw new ArgumentException("Dime a qué lista moverla o si está hecha.");
        return new { tarjeta = Card(await Trello.SendAsync(HttpMethod.Put, $"cards/{id}", query, ct)) };
    }
}

public sealed class TrelloComentarTool(TrelloClient trello) : TrelloTool(trello)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("trello_comentar",
        "Añade un comentario a una tarjeta de Trello.",
        """{"type":"object","properties":{"id":{"type":"string"},"texto":{"type":"string"}},"required":["id","texto"]}""");

    protected override async Task<object> RunAsync(JsonElement args, CancellationToken ct)
    {
        await Trello.SendAsync(HttpMethod.Post, $"cards/{ToolCatalog.Str(args, "id")}/actions/comments",
            new() { ["text"] = ToolCatalog.Str(args, "texto") }, ct);
        return new { comentado = true };
    }
}

public sealed class TrelloArchivarTool(TrelloClient trello) : TrelloTool(trello), IBrainTool
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("trello_archivar",
        "Archiva una tarjeta de Trello (se puede recuperar desde el archivo del tablero).",
        """{"type":"object","properties":{"id":{"type":"string"},"nombre":{"type":"string","description":"Nombre de la tarjeta, para la confirmación"}},"required":["id"]}""",
        ToolRisk.Dangerous);

    public string Describe(JsonElement args)
    {
        var name = ToolCatalog.Str(args, "nombre") is { Length: > 0 } n ? n : ToolCatalog.Str(args, "id");
        return $"Archivar en Trello la tarjeta «{name}»";
    }

    protected override async Task<object> RunAsync(JsonElement args, CancellationToken ct)
    {
        await Trello.SendAsync(HttpMethod.Put, $"cards/{ToolCatalog.Str(args, "id")}", new() { ["closed"] = "true" }, ct);
        return new { archivada = true };
    }
}
