using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kroko.Shared.Protocol;

namespace Kroko.Brain.Agent;

/// <summary>
/// La tarjeta "¿Lo hago?" para cualquier acción peligrosa (de la App, del correo, de Notion…):
/// manda confirm.request y espera la respuesta de Pol. 60 s sin respuesta = cancelado.
/// </summary>
public static class ToolConfirmation
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Devuelve null si Pol aprobó; si no, el resultado (JSON) que se le da a la IA.</summary>
    public static async Task<ToolExecution?> RequestAsync(IAgentChannel channel, string callId, string tool, string text,
        ILogger log, CancellationToken ct)
    {
        log.LogInformation("Pidiendo confirmación para {Tool}: {Text}", tool, text);
        var answer = await channel.RequestAsync<ConfirmResponseMessage>(
            new ConfirmRequestMessage { SessionId = channel.SessionId, CallId = callId, Text = text, Tool = tool },
            callId, Timeout, ct);

        if (answer is not { Approved: true })
        {
            log.LogInformation("Acción {Tool} {Result}", tool, answer is null ? "sin respuesta (cancelada)" : "rechazada por tu dueño");
            return Fail(answer is null
                ? "Tu dueño no respondió a la confirmación; la acción se canceló."
                : "Tu dueño rechazó la acción. No la hagas y pregúntale qué prefiere.");
        }

        // Tras confirmar, la cara vuelve a "pensando" mientras se ejecuta
        await channel.SendAsync(new StateMessage { SessionId = channel.SessionId, State = FaceStates.Thinking }, ct);
        return null;
    }

    public static ToolExecution Fail(string error) =>
        new(new JsonObject { ["ok"] = false, ["error"] = error }.ToJsonString(Readable));
}

/// <summary>
/// Auditoría de las herramientas que se ejecutan en el Brain (correo, internet, Notion, navegador…).
/// Mismo formato que la de la App; la ventana "Registro de acciones" muestra ambas.
/// </summary>
public sealed class BrainAuditLog(ILogger<BrainAuditLog> log)
{
    private const int MaxText = 400;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Lock _lock = new();

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", "audit");

    /// <summary>Carpeta alternativa (pruebas).</summary>
    public string? OverrideFolder { get; set; }

    public void Record(string tool, ToolRisk risk, string args, string decision, bool ok, string result, long ms)
    {
        var entry = new
        {
            Time = DateTime.Now,
            Tool = tool,
            Risk = risk.ToString(),
            Args = Trim(args),
            Decision = decision,
            Ok = ok,
            Result = Trim(result),
            DurationMs = ms,
        };
        try
        {
            lock (_lock)
            {
                var dir = OverrideFolder ?? Folder;
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, $"audit-brain-{DateTime.Now:yyyy-MM}.jsonl"),
                    JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "No se pudo escribir la auditoría del Brain");
        }
    }

    private static string Trim(string s) => s.Length <= MaxText ? s : s[..MaxText] + "…";
}
