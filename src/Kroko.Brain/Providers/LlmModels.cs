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

public sealed record LlmResponse(string Text, IReadOnlyList<ToolCall> ToolCalls, ChatMessage AssistantMessage, string Provider);

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
}

/// <summary>No hay ningún proveedor con API key configurada.</summary>
public sealed class NoLlmConfiguredException() : Exception("No hay ninguna API key de IA configurada");
