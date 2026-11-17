using System.Text.Json;
using Kroko.Shared.Protocol;

namespace Kroko.Brain.Providers;

public enum ChatRole { User, Assistant, Tool }

public sealed record ToolCall(string Id, string Name, JsonElement Arguments);

/// <summary>Herramienta que la IA puede pedir. Parameters es un JSON Schema.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonElement Parameters, ToolRisk Risk);

/// <summary>
/// Mensaje del historial, independiente del proveedor.
/// Raw guarda la respuesta original del proveedor para reenviarla tal cual en el siguiente turno
/// (Gemini exige devolver sus "thought signatures" y DeepSeek su "reasoning_content").
/// </summary>
public sealed class ChatMessage
{
    public ChatRole Role { get; init; }
    public string Text { get; init; } = "";
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

    /// <summary>Para Role = Tool: a qué llamada responde.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Para Role = Tool: nombre de la herramienta (Gemini lo necesita).</summary>
    public string? ToolName { get; init; }

    /// <summary>Proveedor que generó Raw ("gemini", "deepseek").</summary>
    public string? RawProvider { get; init; }

    public JsonElement? Raw { get; init; }

    /// <summary>Para Role = Tool: imagen adjunta (captura de pantalla) en base64.</summary>
    public string? ImageBase64 { get; init; }

    public string? ImageMime { get; init; }

    public static ChatMessage User(string text) => new() { Role = ChatRole.User, Text = text };

    public static ChatMessage ToolResult(ToolCall call, string resultJson, string? imageBase64 = null, string? imageMime = null) => new()
    {
        Role = ChatRole.Tool,
        ToolCallId = call.Id,
        ToolName = call.Name,
        Text = resultJson,
        ImageBase64 = imageBase64,
        ImageMime = imageMime,
    };
}

public sealed record LlmResponse(string Text, IReadOnlyList<ToolCall> ToolCalls, ChatMessage AssistantMessage, string Provider,
    Kroko.Brain.Usage.LlmUsage? Usage = null);

public interface ILlmProvider
{
    /// <summary>Nombre corto: "gemini", "deepseek"…</summary>
    string Name { get; }

    /// <summary>True si tiene API key configurada.</summary>
    bool IsConfigured { get; }

    bool SupportsAudio { get; }
    bool SupportsVision { get; }

    Task<LlmResponse> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ToolDefinition> tools, CancellationToken ct);
}

/// <summary>Fallo al hablar con un proveedor de IA (HTTP, cuota, respuesta inválida…).</summary>
public sealed class LlmProviderException(string provider, string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string Provider { get; } = provider;
    public int? StatusCode { get; } = statusCode;

    /// <summary>
    /// Explicación para el dueño cuando el problema es de su cuenta y no de Kroko: sin saldo (402) o límite de uso (429).
    /// null = otro error.
    /// </summary>
    public string? OwnerMessage => StatusCode switch
    {
        402 => $"Se acabó el saldo de tu cuenta de {ProviderName}. Recárgalo (Gemini: ai.studio/projects → Facturación) o pon otra " +
               "clave en Configuración → Claves de IA. Mientras tanto no puedo pensar ni hablar con mi voz de siempre.",
        429 => $"{ProviderName} me pide esperar: llegaste al límite de uso por ahora. Inténtalo en un minuto, o pon una clave de " +
               "respaldo (DeepSeek) en Configuración → Claves de IA.",
        // Desde el 17/11/2026: el motivo real en vez de «revisa el internet o la API key»
        _ when GeminiKeyRing.IsBadKey(StatusCode, Message) =>
            $"Google no acepta tus claves de {ProviderName}: no son válidas, vencieron o la API está desactivada en su proyecto. " +
            "Revísalas en Configuración → Claves de IA → «Guardar y probar» (te dice cuál falla).",
        404 or 400 when Message.Contains("model", StringComparison.OrdinalIgnoreCase) =>
            $"El modelo de IA que uso no está disponible para tus claves de {ProviderName}. Avísale a quien te instaló Kroko " +
            "(se cambia en Llm:Gemini:Model del cerebro).",
        500 or 502 or 503 or 504 => $"{ProviderName} está saturado ahora mismo. Inténtalo en un momento.",
        null when Message.Contains("tardó", StringComparison.OrdinalIgnoreCase) =>
            $"{ProviderName} tardó demasiado en responder. Inténtalo otra vez.",
        null when Message.Contains("conectar", StringComparison.OrdinalIgnoreCase) =>
            $"El cerebro no logra comunicarse con {ProviderName} (¿internet del servidor?). Inténtalo en un momento.",
        _ => null,
    };

    private string ProviderName => Provider.Equals("gemini", StringComparison.OrdinalIgnoreCase) ? "Gemini (Google)" : Provider;
}

/// <summary>No hay ningún proveedor con API key configurada.</summary>
public sealed class NoLlmConfiguredException() : Exception("No hay ninguna API key de IA configurada");
