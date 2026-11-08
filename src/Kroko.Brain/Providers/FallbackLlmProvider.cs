using Microsoft.Extensions.Options;

namespace Kroko.Brain.Providers;

/// <summary>
/// Proveedor "router": prueba los proveedores en el orden de Llm:Order y, si uno falla
/// (sin conexión, cuota agotada, error del servidor…), pasa automáticamente al siguiente.
/// </summary>
public sealed class FallbackLlmProvider(
    IEnumerable<ILlmProvider> providers,
    IOptionsMonitor<LlmOptions> options,
    ILogger<FallbackLlmProvider> log) : IConfiguredLlm
{
    private readonly ILlmProvider[] _providers = [.. providers];

    public IReadOnlyList<string> ConfiguredProviders => Ordered().Select(p => p.Name).ToArray();

    public async Task<LlmResponse> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        var candidates = Ordered().ToList();
        if (candidates.Count == 0) throw new NoLlmConfiguredException();

        LlmProviderException? last = null;
        foreach (var p in candidates)
        {
            try
            {
                return await p.ChatAsync(systemPrompt, history, tools, ct);
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
