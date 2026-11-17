using Kroko.Brain.Memory;
using Kroko.Brain.Providers;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Agent;

/// <summary>Por dónde el agente le habla a la App de una sesión concreta.</summary>
public interface IAgentChannel
{
    string SessionId { get; }
    Task SendAsync(KrokoMessage message, CancellationToken ct);

    /// <summary>
    /// Envía una petición y espera la respuesta con el mismo callId (tool.result o confirm.response).
    /// Devuelve null si se agota el tiempo.
    /// </summary>
    Task<TResponse?> RequestAsync<TResponse>(KrokoMessage request, string callId, TimeSpan timeout, CancellationToken ct)
        where TResponse : KrokoMessage;
}

/// <summary>
/// Bucle del agente (sección 7): recibe una orden, consulta a la IA, ejecuta herramientas
/// si las pide (máx. MaxSteps pasos) y devuelve respuesta + expresión.
/// </summary>
public sealed class AgentLoop(
    IConfiguredLlm llm,
    IToolGateway tools,
    ConversationStore store,
    SystemPrompt systemPrompt,
    SuggestionStore suggestions,
    IOptionsMonitor<AgentOptions> options,
    TimeProvider time,
    Tts.ITtsProvider tts,
    Tts.ISpeechToText stt,
    ILogger<AgentLoop> log,
    Usage.UsageTracker? usage = null)
{
    /// <summary>Orden por voz: transcribe, enseña lo entendido y responde hablando.</summary>
    public async Task HandleUserAudioAsync(byte[] wav, IAgentChannel channel, CancellationToken ct, string mimeType = "audio/wav", bool speakReply = true, bool audioLater = false)
    {
        await channel.SendAsync(new StateMessage { SessionId = channel.SessionId, State = FaceStates.Thinking }, ct);
        var heard = System.Diagnostics.Stopwatch.StartNew();
        string? text;
        try
        {
            text = await stt.TranscribeAsync(wav, mimeType, ct);
        }
        catch (NoLlmConfiguredException)
        {
            await FinishAsync(channel, speakReply, NoKeysMessage, "confused", true, ct, audioLater);
            return;
        }
        catch (LlmProviderException ex)
        {
            log.LogWarning("No se pudo transcribir: {Message}", ex.Message);
            // Sin saldo o límite de uso: se dice claro (no es que no te entendió)
            if (ex.OwnerMessage is { } account) await FinishAsync(channel, speakReply, account, "sad", true, ct, audioLater);
            else await FinishAsync(channel, speakReply, "No pude entender el audio. ¿Me lo repites?", "confused", true, ct, audioLater);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            await FinishAsync(channel, speakReply, "No te escuché bien. ¿Me lo repites?", "confused", false, ct, audioLater);
            return;
        }

        log.LogInformation("Transcripción: {Chars} caracteres en {Ms} ms", text.Length, heard.ElapsedMilliseconds);
        await channel.SendAsync(new TranscriptMessage { SessionId = channel.SessionId, Text = text }, ct);
        await HandleUserTextAsync(text, channel, ct, speak: speakReply, audioLater: audioLater);
    }

    /// <param name="speak">true = la respuesta también se convierte en voz (órdenes por voz).</param>
    public async Task HandleUserTextAsync(string text, IAgentChannel channel, CancellationToken ct, bool speak = false, bool audioLater = false)
    {
        // «Cállate» / «descansa» en la PC: la App ya lo calló y lo escondió; no se le pregunta a la IA (ni gasta)
        if (!channel.SessionId.StartsWith("telegram-", StringComparison.Ordinal) && Kroko.Shared.Tools.QuietCommand.Matches(text))
        {
            await channel.SendAsync(new StateMessage { SessionId = channel.SessionId, State = FaceStates.Idle }, ct);
            return;
        }

        await channel.SendAsync(new StateMessage { SessionId = channel.SessionId, State = FaceStates.Thinking }, ct);

        var conversation = store.Get(channel.SessionId);
        var turn = new List<ChatMessage> { ChatMessage.User(text) };

        try
        {
            var thinking = System.Diagnostics.Stopwatch.StartNew();
            var prompt = systemPrompt.Build(time.GetLocalNow(), conversation);
            var maxSteps = Math.Max(1, options.CurrentValue.MaxSteps);

            for (var step = 1; step <= maxSteps; step++)
            {
                var history = conversation.Snapshot().Concat(turn).ToList();
                var response = await llm.ChatAsync(prompt, history, tools.Tools, ct);
                turn.Add(response.AssistantMessage);

                if (response.ToolCalls.Count == 0)
                {
                    conversation.Commit(turn);
                    var (clean, expression) = ExpressionTag.Extract(response.Text);
                    log.LogInformation("Respuesta de {Provider} en {Steps} paso(s) y {Ms} ms, expresión {Expression}",
                        response.Provider, step, thinking.ElapsedMilliseconds, expression ?? "(ninguna)");
                    await FinishAsync(channel, speak,
                        string.IsNullOrWhiteSpace(clean) ? "Mmm, me quedé sin palabras. ¿Me lo repites?" : clean,
                        expression ?? (string.IsNullOrWhiteSpace(clean) ? "confused" : null),
                        isError: false, ct, audioLater);
                    return;
                }

                foreach (var call in response.ToolCalls)
                {
                    log.LogInformation("La IA pide la herramienta {Tool}", call.Name);
                    var result = await tools.ExecuteAsync(call, channel, ct);
                    turn.Add(ChatMessage.ToolResult(call, result.Json, result.ImageBase64, result.ImageMime));

                    // Un fallo inesperado de una herramienta va al buzón de mejoras para revisarlo
                    if (result.Json.Contains("Algo falló al hacerlo", StringComparison.Ordinal))
                        suggestions.Add("fallo", $"La herramienta {call.Name} falló de forma inesperada", $"Pidió: {text}");
                }
            }

            log.LogWarning("Se alcanzó el máximo de {Max} pasos", maxSteps);
            suggestions.Add("fallo", "Me enredé con demasiados pasos", $"Pidió: {text}");
            await FinishAsync(channel, speak, "Me enredé con tantos pasos. ¿Lo intentamos de otra forma?", "oops", true, ct, audioLater);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Pol mandó otra orden o se desconectó: no respondemos y el historial no se toca.
            log.LogInformation("Orden cancelada");
        }
        catch (NoLlmConfiguredException)
        {
            await FinishAsync(channel, speak,
                NoKeysMessage,
                "confused", true, ct, audioLater);
        }
        catch (LlmProviderException ex)
        {
            log.LogError(ex, "Ningún proveedor de IA respondió");
            await FinishAsync(channel, speak,
                ex.OwnerMessage ?? "No me pude conectar con mi cerebro de IA. Revisa el internet o la API key y lo intento de nuevo.",
                "sad", true, ct, audioLater);
        }
    }

    /// <summary>
    /// Sin claves de IA (15/11/2026 viven en la PC): o la PC está apagada / Kroko cerrado, o aún no se pusieron.
    /// </summary>
    private static string NoKeysMessage => "Para pensar necesito tu clave de Gemini, y esa clave vive en tu PC: abre Kroko en la " +
                                          "computadora (si aún no la pusiste, clic derecho en Kroko → Configurar Kroko → Cerebro de IA).";

    /// <param name="audioLater">
    /// La App acepta el texto primero y la voz después (17/11/2026): así la respuesta aparece en cuanto la IA termina,
    /// sin esperar los segundos que tarda en generarse la voz.
    /// </param>
    private async Task FinishAsync(IAgentChannel channel, bool speak, string text, string? expression, bool isError,
        CancellationToken ct, bool audioLater = false)
    {
        // La primera vez del día que se pasa el tope de gasto, se avisa al final de la respuesta
        if (usage?.TakeLimitNotice() == true)
            text += "\n\n(Llegaste a tu tope diario de IA: paso a modo ahorro, sin voz en tiempo real ni mi voz de Gemini. " +
                    "Puedes cambiar el tope en Ajustes → Cerebro.)";

        if (speak && audioLater)
        {
            await channel.SendAsync(new ReplyMessage { SessionId = channel.SessionId, Text = text, IsError = isError, AudioFollows = true }, ct);
            if (expression is not null)
                await channel.SendAsync(new ExpressionMessage { SessionId = channel.SessionId, Name = expression }, ct);
            var started = System.Diagnostics.Stopwatch.StartNew();
            var later = await tts.SynthesizeAsync(text, ct);
            log.LogInformation("Voz lista en {Ms} ms{Note}", started.ElapsedMilliseconds, later is null ? " (sin voz de Gemini)" : "");
            await channel.SendAsync(new ReplyAudioMessage
            {
                SessionId = channel.SessionId,
                AudioBase64 = later is null ? null : Convert.ToBase64String(later),
            }, ct);
            await channel.SendAsync(new StateMessage { SessionId = channel.SessionId, State = FaceStates.Idle }, ct);
            return;
        }

        string? audio = null;
        if (speak)
        {
            var wav = await tts.SynthesizeAsync(text, ct);
            if (wav is not null) audio = Convert.ToBase64String(wav);
            else log.LogInformation("Sin voz de Gemini: la App usará la voz de Windows");
        }
        await channel.SendAsync(new ReplyMessage { SessionId = channel.SessionId, Text = text, IsError = isError, AudioBase64 = audio }, ct);
        if (expression is not null)
            await channel.SendAsync(new ExpressionMessage { SessionId = channel.SessionId, Name = expression }, ct);
        await channel.SendAsync(new StateMessage { SessionId = channel.SessionId, State = FaceStates.Idle }, ct);
    }
}
