using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kroko.Shared.Protocol;

/// <summary>
/// Mensaje del protocolo App ↔ Brain (sección 3 del CLAUDE.md).
/// Se serializa como JSON con un campo "type" que indica el tipo concreto.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(UserAudioMessage), MessageTypes.UserAudio)]
[JsonDerivedType(typeof(UserTextMessage), MessageTypes.UserText)]
[JsonDerivedType(typeof(ToolResultMessage), MessageTypes.ToolResult)]
[JsonDerivedType(typeof(ConfirmResponseMessage), MessageTypes.ConfirmResponse)]
[JsonDerivedType(typeof(StateMessage), MessageTypes.State)]
[JsonDerivedType(typeof(ExpressionMessage), MessageTypes.Expression)]
[JsonDerivedType(typeof(TranscriptMessage), MessageTypes.Transcript)]
[JsonDerivedType(typeof(ToolCallMessage), MessageTypes.ToolCall)]
[JsonDerivedType(typeof(ConfirmRequestMessage), MessageTypes.ConfirmRequest)]
[JsonDerivedType(typeof(ReplyMessage), MessageTypes.Reply)]
[JsonDerivedType(typeof(PingMessage), MessageTypes.Ping)]
[JsonDerivedType(typeof(PongMessage), MessageTypes.Pong)]
[JsonDerivedType(typeof(StopMessage), MessageTypes.Stop)]
[JsonDerivedType(typeof(NotifyMessage), MessageTypes.Notify)]
[JsonDerivedType(typeof(CardMessage), MessageTypes.Card)]
[JsonDerivedType(typeof(LiveStartMessage), MessageTypes.LiveStart)]
[JsonDerivedType(typeof(LiveAudioMessage), MessageTypes.LiveAudio)]
[JsonDerivedType(typeof(LiveStopMessage), MessageTypes.LiveStop)]
[JsonDerivedType(typeof(LiveEventMessage), MessageTypes.LiveEvent)]
public abstract record KrokoMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string SessionId { get; init; } = "";
}

public static class MessageTypes
{
    public const string UserAudio = "user.audio";
    public const string UserText = "user.text";
    public const string ToolResult = "tool.result";
    public const string ConfirmResponse = "confirm.response";
    public const string State = "state";
    public const string Expression = "expression";
    public const string Transcript = "transcript";
    public const string ToolCall = "tool.call";
    public const string ConfirmRequest = "confirm.request";
    public const string Reply = "reply";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Stop = "control.stop";
    public const string Notify = "notify";
    public const string Card = "card";
    public const string LiveStart = "live.start";
    public const string LiveAudio = "live.audio";
    public const string LiveStop = "live.stop";
    public const string LiveEvent = "live.event";
}

/// <summary>Estados de la cara que el Brain puede pedir.</summary>
public static class FaceStates
{
    public const string Idle = "idle";
    public const string Listening = "listening";
    public const string Thinking = "thinking";
    public const string Speaking = "speaking";
}

public enum ToolRisk { Read, Write, Dangerous }

// ---------------- App → Brain

/// <summary>Audio WAV 16 kHz mono en base64 (Fase 1C).</summary>
public sealed record UserAudioMessage : KrokoMessage
{
    public string AudioBase64 { get; init; } = "";

    /// <summary>¿Responder también con voz? null = sí (comportamiento clásico de las órdenes por voz).</summary>
    public bool? Speak { get; init; }
}

public sealed record UserTextMessage : KrokoMessage
{
    public string Text { get; init; } = "";

    /// <summary>¿Responder también con voz? null = no (comportamiento clásico de lo escrito).</summary>
    public bool? Speak { get; init; }
}

public sealed record ToolResultMessage : KrokoMessage
{
    public string CallId { get; init; } = "";
    public bool Ok { get; init; }
    public JsonElement? Data { get; init; }
    public string? Error { get; init; }
}

public sealed record ConfirmResponseMessage : KrokoMessage
{
    public string CallId { get; init; } = "";
    public bool Approved { get; init; }
}

// ---------------- Brain → App

public sealed record StateMessage : KrokoMessage
{
    public string State { get; init; } = FaceStates.Idle;
}

public sealed record ExpressionMessage : KrokoMessage
{
    public string Name { get; init; } = "";

    /// <summary>Duración en ms (null = la que tenga la expresión).</summary>
    public int? DurationMs { get; init; }
}

public sealed record TranscriptMessage : KrokoMessage
{
    public string Text { get; init; } = "";
}

public sealed record ToolCallMessage : KrokoMessage
{
    public string CallId { get; init; } = "";
    public string Name { get; init; } = "";
    public JsonElement Args { get; init; }
    public ToolRisk Risk { get; init; }

    /// <summary>
    /// Si Pol aprobó esta acción peligrosa fuera de la PC (p. ej. con los botones de Telegram), por dónde.
    /// La App solo lo acepta si Kroko:AllowRemoteApprovals está activado.
    /// </summary>
    public string? ApprovedRemotely { get; init; }
}

public sealed record ConfirmRequestMessage : KrokoMessage
{
    public string CallId { get; init; } = "";
    public string Text { get; init; } = "";

    /// <summary>Herramienta que se quiere ejecutar (para el registro de auditoría).</summary>
    public string Tool { get; init; } = "";
}

public sealed record ReplyMessage : KrokoMessage
{
    public string Text { get; init; } = "";

    /// <summary>Audio TTS en base64 (Fase 1C). Null si solo hay texto.</summary>
    public string? AudioBase64 { get; init; }

    /// <summary>True si la respuesta explica un fallo (sin conexión con la IA, etc.).</summary>
    public bool IsError { get; init; }
}

// ---------------- Ambos

public sealed record PingMessage : KrokoMessage;

public sealed record PongMessage : KrokoMessage;

/// <summary>
/// App → Brain: interruptor de emergencia (Ctrl+Alt+K). Cancela la orden en curso y
/// cualquier herramienta o confirmación pendiente.
/// </summary>
public sealed record StopMessage : KrokoMessage;

/// <summary>
/// Brain → App: aviso que no responde a una orden (recordatorio que vence, resumen del día…).
/// La App lo muestra en el panel, con sonido y, si trae audio, lo dice en voz alta.
/// </summary>
public sealed record NotifyMessage : KrokoMessage
{
    /// <summary>"recordatorio", "resumen" o "aviso".</summary>
    public string Kind { get; init; } = "aviso";

    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public string? Expression { get; init; }
    public string? AudioBase64 { get; init; }
}

/// <summary>
/// Tarjeta visual para el panel (Brain → App): una lista con checks (tareas, hábitos) o el estado del equipo
/// con barras. Acompaña a la respuesta hablada; no la reemplaza.
/// </summary>
public sealed record CardMessage : KrokoMessage
{
    /// <summary>"lista" o "equipo".</summary>
    public string Kind { get; init; } = "lista";

    public string Title { get; init; } = "";
    public List<CardItem> Items { get; init; } = [];
}

/// <summary>Una fila de la tarjeta. Done = check (listas); Value = 0–1 para la barra (equipo).</summary>
public sealed record CardItem(string Text, string? Detail = null, bool? Done = null, double? Value = null);

/// <summary>Voz en tiempo real (Gemini Live): la App pide abrir una conversación continua.</summary>
public sealed record LiveStartMessage : KrokoMessage;

/// <summary>Audio PCM 16 bits mono en base64: de la App al cerebro a 16 kHz, del cerebro a la App a 24 kHz.</summary>
public sealed record LiveAudioMessage : KrokoMessage
{
    public string Data { get; init; } = "";
}

/// <summary>La App cierra la conversación en tiempo real.</summary>
public sealed record LiveStopMessage : KrokoMessage;

/// <summary>
/// Evento de la conversación en tiempo real (cerebro → App). Kind: "started", "you" (lo que dijiste), "kroko" (lo que dice),
/// "turn" (terminó de hablar), "interrupted" (lo cortaste), "ended", "error".
/// </summary>
public sealed record LiveEventMessage : KrokoMessage
{
    public string Kind { get; init; } = "";
    public string? Text { get; init; }
}

/// <summary>Opciones JSON comunes y helpers de (de)serialización del protocolo.</summary>
public static class KrokoProtocol
{
    public const string WebSocketPath = "/ws";
    public const string TokenHeader = "X-Kroko-Token";
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>Tamaño máximo de un mensaje (los audios de la Fase 1C caben de sobra).</summary>
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // "type" puede venir en cualquier posición del JSON (no solo la primera)
        AllowOutOfOrderMetadataProperties = true,
        // Tildes y eñes legibles (no es HTML: no hace falta escaparlas)
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(KrokoMessage message) => JsonSerializer.Serialize(message, Json);

    /// <summary>Devuelve null si el JSON no es un mensaje válido (no lanza excepción).</summary>
    public static KrokoMessage? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<KrokoMessage>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
