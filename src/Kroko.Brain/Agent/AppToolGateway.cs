using System.Text.Json;
using System.Text.Json.Nodes;
using Kroko.Brain.Providers;
using Kroko.Shared.Protocol;
using Kroko.Shared.Tools;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Agent;

/// <summary>
/// Ejecuta las herramientas del catálogo en la App:
/// Dangerous → confirm.request y espera "sí" (60 s sin respuesta = cancelado) → tool.call → tool.result.
/// </summary>
public sealed class AppToolGateway(IOptionsMonitor<AgentOptions> options, ILogger<AppToolGateway> log) : IToolGateway
{
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IReadOnlyList<ToolDefinition> _tools = ToolCatalog.All
        .Select(t => new ToolDefinition(t.Name, t.Description, JsonDocument.Parse(t.Parameters).RootElement.Clone(), t.Risk))
        .ToArray();

    public IReadOnlyList<ToolDefinition> Tools => _tools;

    public async Task<ToolExecution> ExecuteAsync(ToolCall call, IAgentChannel channel, CancellationToken ct)
    {
        var spec = ToolCatalog.Find(call.Name);
        if (spec is null) return Fail($"No existe la herramienta {call.Name}.");

        var risk = spec.EffectiveRisk(call.Arguments);

        if (risk == ToolRisk.Dangerous)
        {
            var rejected = await ToolConfirmation.RequestAsync(channel, call.Id, call.Name, spec.ConfirmText(call.Arguments), log, ct);
            if (rejected is not null) return rejected;
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(5, options.CurrentValue.ToolTimeoutSeconds));
        var result = await channel.RequestAsync<ToolResultMessage>(
            new ToolCallMessage
            {
                SessionId = channel.SessionId,
                CallId = call.Id,
                Name = call.Name,
                Args = call.Arguments,
                Risk = risk,
            },
            call.Id, timeout, ct);

        if (result is null) return Fail("La herramienta tardó demasiado o la App no respondió.");
        if (!result.Ok) return Fail(result.Error ?? "La herramienta falló.");

        return Success(result.Data);
    }

    private static ToolExecution Fail(string error) =>
        new(new JsonObject { ["ok"] = false, ["error"] = error }.ToJsonString(Readable));

    /// <summary>Si el resultado trae una imagen (captura), se separa para mandarla como imagen a la IA.</summary>
    private static ToolExecution Success(JsonElement? data)
    {
        var node = data is { } d ? JsonNode.Parse(d.GetRawText()) : null;
        string? image = null, mime = null;
        if (node is JsonObject obj && obj["imageBase64"] is JsonValue img)
        {
            image = img.GetValue<string>();
            mime = obj["mimeType"]?.GetValue<string>() ?? "image/jpeg";
            obj.Remove("imageBase64");
            obj["imagen"] = "adjunta";
        }
        return new ToolExecution(new JsonObject { ["ok"] = true, ["data"] = node }.ToJsonString(Readable), image, mime);
    }
}
