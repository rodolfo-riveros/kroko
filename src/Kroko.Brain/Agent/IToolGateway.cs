using Kroko.Brain.Providers;

namespace Kroko.Brain.Agent;

/// <summary>Resultado de una herramienta para la IA (JSON) y, opcionalmente, una imagen (captura).</summary>
public sealed record ToolExecution(string Json, string? ImageBase64 = null, string? ImageMime = null);

/// <summary>
/// Puente hacia las herramientas. Las "manos" viven en la App: el Brain decide,
/// pide confirmación si hace falta y la App ejecuta.
/// </summary>
public interface IToolGateway
{
    IReadOnlyList<ToolDefinition> Tools { get; }

    Task<ToolExecution> ExecuteAsync(ToolCall call, IAgentChannel channel, CancellationToken ct);
}
