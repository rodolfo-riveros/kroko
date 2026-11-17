using Microsoft.Extensions.Options;

namespace Kroko.Brain.Providers;

/// <summary>
/// Proveedor "router": prueba los proveedores en el orden de Llm:Order y, si uno falla
/// (sin conexión, cuota agotada, error del servidor…), pasa automáticamente al siguiente.
/// </summary>
public sealed class FallbackLlmProvider(
    IEnumerable<ILlmProvider> providers,
    IOptionsMonitor<LlmOptions> options,
    ILogger<FallbackLlmProvider> log,
    Kroko.Brain.Usage.UsageTracker? usage = null,
    LocalLlmProvider? local = null) : IConfiguredLlm
{
    private readonly ILlmProvider[] _providers = [.. providers];

    public IReadOnlyList<string> ConfiguredProviders => Ordered().Select(p => p.Name).ToArray();

    public async Task<LlmResponse> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        // IA mixta (17/11/2026): lo simple lo piensa la IA de la PC; lo difícil, o si ella no puede, Gemini
        if (local is { IsConfigured: true } && LocalLlm.IsSimple(history, options.CurrentValue.Local))
        {
            var localTools = tools.Where(t => LocalLlm.Allowed(t.Name, options.CurrentValue.Local)).ToList();
            try
            {
                var r = await local.ChatAsync(systemPrompt, history, localTools, ct);
                var inReach = r.ToolCalls.All(c => localTools.Any(t => t.Name == c.Name));
                if (inReach && (r.ToolCalls.Count > 0 || r.Text.Trim().Length > 0)) return r;
                log.LogInformation("La IA de la PC no pudo con esto; paso a la nube");
            }
            catch (LlmProviderException ex)
            {
                log.LogWarning("La IA de la PC falló ({Message}); paso a la nube", ex.Message);
            }
        }

        var candidates = Ordered().ToList();
        if (candidates.Count == 0) throw new NoLlmConfiguredException();

        LlmProviderException? last = null;
        foreach (var p in candidates)
        {
            try
            {
                var response = await p.ChatAsync(systemPrompt, history, tools, ct);
                usage?.Record(response.Usage); // el medidor de gasto (chat, resumen, automatizaciones…)
                return response;
            }
            catch (LlmProviderException ex)
            {
                last = ex;
                log.LogWarning("Falló {Provider}: {Message}. Probando el siguiente proveedor…", p.Name, ex.Message);
            }
        }
        throw last!;
    }

    private IEnumerable<ILlmProvider> Ordered()
    {
        var order = options.CurrentValue.Order;
        return _providers
            .Where(p => p.IsConfigured)
            .OrderBy(p =>
            {
                var i = order.FindIndex(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase));
                return i < 0 ? int.MaxValue : i;
            });
    }
}

/// <summary>Lo que usa el agente: "la IA", sin importar qué proveedor responda.</summary>
public interface IConfiguredLlm
{
    IReadOnlyList<string> ConfiguredProviders { get; }

    Task<LlmResponse> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ToolDefinition> tools, CancellationToken ct);
}
