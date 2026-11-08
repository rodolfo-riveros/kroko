using System.Text.Json;
using System.Text.Json.Nodes;
using Kroko.Brain.Providers;
using Kroko.Shared.Protocol;
using Kroko.Shared.Tools;
using System.Text.Encodings.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Agent;

/// <summary>
/// Ejecuta las herramientas del catálogo en la App:
/// Dangerous → confirm.request y espera "sí" (60 s sin respuesta = cancelado) → tool.call → tool.result.
/// </summary>
public sealed class AppToolGateway(IOptionsMonitor<AgentOptions> options, IServiceProvider services, ILogger<AppToolGateway> log)
    : IToolGateway
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

        var (node, image, mime, file) = Split(result.Data);
        // El bot de Telegram se pide aquí (no en el constructor): el bot depende del agente, que depende de esto
        var telegram = services.GetService<ITelegramDelivery>();

        if (file is not null)
        {
            var problem = telegram is null ? "Telegram no está disponible en este cerebro." : await telegram.SendFileAsync(channel, file, ct);
            if (problem is not null) return Fail(problem);
            log.LogInformation("Archivo enviado por Telegram: {Name} ({Bytes} bytes)", file.Name, file.Data.Length);
            if (node is JsonObject sent) sent["enviado"] = "al Telegram de tu dueño";
        }
        if (image is not null && telegram is not null)
            await telegram.SendImageIfTelegramAsync(channel, Convert.FromBase64String(image), null, ct);

        return new ToolExecution(new JsonObject { ["ok"] = true, ["data"] = node }.ToJsonString(Readable), image, mime);
    }

    private static ToolExecution Fail(string error) =>
        new(new JsonObject { ["ok"] = false, ["error"] = error }.ToJsonString(Readable));

    /// <summary>
    /// Separa del resultado lo que no debe ir como texto a la IA: una imagen (captura, va como imagen) o un archivo
    /// (enviar_archivo, va a Telegram y la IA solo ve su nombre).
    /// </summary>
    internal static (JsonNode? Node, string? Image, string? Mime, FileAttachment? File) Split(JsonElement? data)
    {
        var node = data is { } d ? JsonNode.Parse(d.GetRawText()) : null;
        string? image = null, mime = null;
        FileAttachment? file = null;
        if (node is JsonObject obj && obj["imageBase64"] is JsonValue img)
        {
            image = img.GetValue<string>();
            mime = obj["mimeType"]?.GetValue<string>() ?? "image/jpeg";
            obj.Remove("imageBase64");
            obj["imagen"] = "adjunta";
        }
        else if (node is JsonObject o && o["archivoBase64"] is JsonValue content)
        {
            var name = o["nombre"]?.GetValue<string>() ?? "archivo";
            file = new FileAttachment(name, o["mimeType"]?.GetValue<string>() ?? "application/octet-stream",
                Convert.FromBase64String(content.GetValue<string>()), o["mensaje"]?.GetValue<string>());
            o.Remove("archivoBase64");
        }
        return (node, image, mime, file);
    }
}
