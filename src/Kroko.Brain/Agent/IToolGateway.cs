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

/// <summary>Un archivo de la PC que va al Telegram del dueño (enviar_archivo).</summary>
public sealed record FileAttachment(string Name, string Mime, byte[] Data, string? Caption);

/// <summary>
/// Entrega a Telegram lo que la App manda: archivos (siempre al chat del dueño) y, si la orden vino de Telegram,
/// también las capturas de pantalla. Lo implementa el bot de Telegram.
/// </summary>
public interface ITelegramDelivery
{
    /// <summary>Envía el archivo. Devuelve null si salió bien o un texto con lo que falló.</summary>
    Task<string?> SendFileAsync(IAgentChannel channel, FileAttachment file, CancellationToken ct);

    /// <summary>Si la orden vino de un chat de Telegram, le manda la imagen (p. ej. una captura). Si no, no hace nada.</summary>
    Task SendImageIfTelegramAsync(IAgentChannel channel, byte[] image, string? caption, CancellationToken ct);
}
