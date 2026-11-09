using System.Diagnostics;
using Kroko.Brain.Providers;
using Kroko.Shared.Protocol;

namespace Kroko.Brain.Agent;

/// <summary>
/// Reúne en una sola lista para la IA:
/// - herramientas del Brain (memoria, internet, correo…),
/// - herramientas dinámicas de los conectores MCP (navegador, Notion, Google…),
/// - herramientas de la App (las "manos" en la PC).
/// Las del Brain que son peligrosas pasan por la tarjeta de confirmación y todas quedan auditadas.
/// </summary>
public sealed class CompositeToolGateway(
    IEnumerable<IBrainTool> brainTools,
    IEnumerable<IToolSource> sources,
    AppToolGateway app,
    BrainAuditLog audit,
    ILogger<CompositeToolGateway> log,
    Microsoft.Extensions.Options.IOptionsMonitor<AgentOptions>? options = null) : IToolGateway
{
    public const string ListTool = "ver_herramientas_conector";
    public const string UseTool = "usar_conector";

    private readonly IBrainTool[] _static = [.. brainTools];
    private readonly IToolSource[] _sources = [.. sources];

    private bool OnDemand => options?.CurrentValue.ConnectorToolsOnDemand ?? true;

    private Dictionary<string, IBrainTool> Current()
    {
        var map = new Dictionary<string, IBrainTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _static.Concat(_sources.SelectMany(s => s.CurrentTools)))
            map.TryAdd(t.Definition.Name, t);
        return map;
    }

    private List<IBrainTool> ConnectorTools() => [.. _sources.SelectMany(s => s.CurrentTools)];

    /// <summary>
    /// Lo que se manda a la IA en cada llamada. Con el ahorro activado, las herramientas de los conectores (decenas de
    /// descripciones largas) se cambian por dos cortas que las piden y las usan cuando hacen falta.
    /// </summary>
    public IReadOnlyList<ToolDefinition> Tools
    {
        get
        {
            var connectors = ConnectorTools();
            if (!OnDemand || connectors.Count == 0)
                return [.. Current().Values.Select(t => t.Definition), .. app.Tools];
            return [.. _static.Select(t => t.Definition), ListDefinition, UseDefinition, .. app.Tools];
        }
    }

    /// <summary>Conector de una herramienta MCP ("notion_notion-search" → "notion").</summary>
    internal static string ConnectorOf(string toolName) => toolName.Split('_', 2)[0];

    /// <summary>Conectores conectados con cuántas herramientas tiene cada uno (para el prompt).</summary>
    public IReadOnlyList<(string Connector, int Tools)> Connectors =>
        [.. ConnectorTools().GroupBy(t => ConnectorOf(t.Definition.Name), StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, g.Count()))];

    private static readonly ToolDefinition ListDefinition = BrainToolResult.Define(ListTool,
        "Muestra las herramientas de un conector (Notion, Todoist, navegador…). Sin 'herramientas' devuelve la lista con " +
        "una línea de cada una; con 'herramientas' devuelve la descripción completa y los argumentos de esas (máx. 5). " +
        "Úsala antes de usar_conector.",
        """{"type":"object","properties":{"conector":{"type":"string","description":"notion, todoist, navegador…"},"herramientas":{"type":"array","items":{"type":"string"},"description":"Nombres exactos para ver sus argumentos"}},"required":["conector"]}""",
        ToolRisk.Read);

    private static readonly ToolDefinition UseDefinition = BrainToolResult.Define(UseTool,
        "Ejecuta una herramienta de un conector con sus argumentos (míralos antes con ver_herramientas_conector). " +
        "Lo que pide permiso sigue pidiéndolo.",
        """{"type":"object","properties":{"conector":{"type":"string"},"herramienta":{"type":"string","description":"Nombre exacto de la herramienta"},"argumentos":{"type":"string","description":"Los argumentos como JSON, p. ej. {\"query\":\"reunión\"}"}},"required":["conector","herramienta"]}""",
        ToolRisk.Read);

    public async Task<ToolExecution> ExecuteAsync(ToolCall call, IAgentChannel channel, CancellationToken ct)
    {
        if (call.Name.Equals(ListTool, StringComparison.OrdinalIgnoreCase)) return ListConnectorTools(call.Arguments);
        if (call.Name.Equals(UseTool, StringComparison.OrdinalIgnoreCase))
        {
            // Se ejecuta como si la IA la hubiera llamado por su nombre: mismo riesgo, tarjeta y auditoría
            if (ResolveConnectorCall(call) is not { } inner) return ToolConfirmation.Fail(UnknownConnectorTool(call.Arguments));
            return await ExecuteAsync(inner, channel, ct);
        }
        if (!Current().TryGetValue(call.Name, out var tool)) return await app.ExecuteAsync(call, channel, ct);

        var risk = tool.RiskFor(call.Arguments);
        var args = BrainToolResult.Summarize(call.Arguments);
        var decision = "automática";
        if (risk == ToolRisk.Dangerous)
        {
            var rejected = await ToolConfirmation.RequestAsync(channel, call.Id, call.Name, tool.Describe(call.Arguments), log, ct);
            if (rejected is not null)
            {
                audit.Record(call.Name, risk, args, "rechazada o sin respuesta", false, "no se ejecutó", 0);
                return rejected;
            }
            decision = "aprobada";
        }

        var watch = Stopwatch.StartNew();
        ToolExecution result;
        try
        {
            log.LogInformation("Herramienta del Brain {Tool}", call.Name);
            result = await tool.ExecuteAsync(call.Arguments, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Nunca excepciones crudas hacia la IA ni hacia Pol: se registra y se informa con sencillez
            log.LogError(ex, "Falló la herramienta del Brain {Tool}", call.Name);
            result = ToolConfirmation.Fail("Algo falló al hacerlo. Quedó anotado en el registro.");
        }

        var ok = result.Json.StartsWith("{\"ok\":true", StringComparison.Ordinal);
        audit.Record(call.Name, risk, args, decision, ok, result.Json, watch.ElapsedMilliseconds);
        if (ok) await SendCardAsync(call.Name, result, channel, ct);
        return result;
    }

    // ------------------------------------------------------------------ conectores bajo demanda

    private ToolExecution ListConnectorTools(System.Text.Json.JsonElement args)
    {
        var connector = Str(args, "conector");
        var tools = ConnectorTools().Where(t => ConnectorOf(t.Definition.Name).Equals(connector, StringComparison.OrdinalIgnoreCase)).ToList();
        if (tools.Count == 0)
            return ToolConfirmation.Fail($"No hay un conector «{connector}» conectado. Conectados: " +
                                         string.Join(", ", Connectors.Select(c => c.Connector)) + ".");

        var wanted = args.TryGetProperty("herramientas", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.Array
            ? w.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).Take(5).ToList()
            : [];
        if (wanted.Count == 0)
        {
            return BrainToolResult.Ok(new
            {
                conector = connector,
                herramientas = tools.Select(t => new { nombre = ShortName(t.Definition.Name), resumen = FirstSentence(t.Definition.Description) }),
                siguiente = "Pide los argumentos de las que vayas a usar con 'herramientas' y luego usa usar_conector.",
            });
        }
        return BrainToolResult.Ok(new
        {
            conector = connector,
            herramientas = tools
                .Where(t => wanted.Any(n => Matches(t.Definition.Name, connector, n)))
                .Select(t => new
                {
                    nombre = ShortName(t.Definition.Name),
                    descripcion = t.Definition.Description,
                    argumentos = System.Text.Json.Nodes.JsonNode.Parse(t.Definition.Parameters.GetRawText()),
                }),
        });
    }

    private ToolCall? ResolveConnectorCall(ToolCall call)
    {
        var connector = Str(call.Arguments, "conector");
        var name = Str(call.Arguments, "herramienta");
        var tool = ConnectorTools().FirstOrDefault(t => Matches(t.Definition.Name, connector, name));
        if (tool is null) return null;

        var raw = call.Arguments.TryGetProperty("argumentos", out var a) ? a : default;
        System.Text.Json.JsonElement args;
        try
        {
            args = raw.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String when raw.GetString() is { Length: > 0 } json => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone(),
                System.Text.Json.JsonValueKind.Object => raw.Clone(),
                _ => System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone(),
            };
        }
        catch (System.Text.Json.JsonException)
        {
            args = System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone();
        }
        return new ToolCall(call.Id, tool.Definition.Name, args);
    }

    private string UnknownConnectorTool(System.Text.Json.JsonElement args) =>
        $"No encontré la herramienta «{Str(args, "herramienta")}» en «{Str(args, "conector")}». Mira la lista con {ListTool}.";

    /// <summary>"notion-search", "notion_notion-search" o "notion-search" con conector "notion" → la misma herramienta.</summary>
    private static bool Matches(string fullName, string connector, string name) =>
        ConnectorOf(fullName).Equals(connector, StringComparison.OrdinalIgnoreCase)
        && (fullName.Equals(name, StringComparison.OrdinalIgnoreCase) || ShortName(fullName).Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string ShortName(string fullName) => fullName.Split('_', 2) is [_, var rest] ? rest : fullName;

    private static string FirstSentence(string text)
    {
        var t = text.ReplaceLineEndings(" ").Trim();
        var end = t.IndexOfAny(['.', '\n']);
        if (end > 20) t = t[..(end + 1)];
        return t.Length > 140 ? t[..140] + "…" : t;
    }

    private static string Str(System.Text.Json.JsonElement args, string name) =>
        args.ValueKind == System.Text.Json.JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString()!.Trim()
            : "";

    /// <summary>Tareas y hábitos también se muestran como tarjeta con checks en el panel.</summary>
    private static async Task SendCardAsync(string tool, ToolExecution result, IAgentChannel channel, CancellationToken ct)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(result.Json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return;
            if (Cards.FromToolResult(tool, data) is { } card)
                await channel.SendAsync(card with { SessionId = channel.SessionId }, ct);
        }
        catch (System.Text.Json.JsonException) { }
    }
}
