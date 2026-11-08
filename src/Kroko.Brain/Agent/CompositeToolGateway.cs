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
    ILogger<CompositeToolGateway> log) : IToolGateway
{
    private readonly IBrainTool[] _static = [.. brainTools];
    private readonly IToolSource[] _sources = [.. sources];

    private Dictionary<string, IBrainTool> Current()
    {
        var map = new Dictionary<string, IBrainTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _static.Concat(_sources.SelectMany(s => s.CurrentTools)))
            map.TryAdd(t.Definition.Name, t);
        return map;
    }

    public IReadOnlyList<ToolDefinition> Tools => [.. Current().Values.Select(t => t.Definition), .. app.Tools];

    public async Task<ToolExecution> ExecuteAsync(ToolCall call, IAgentChannel channel, CancellationToken ct)
    {
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
