using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Kroko.Brain.Providers;
using Kroko.Brain.Tts;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Agent;

/// <summary>
/// Conversación por voz en tiempo real con Gemini Live (WebSocket bidireccional, servidor a servidor: la clave
/// nunca sale del cerebro). La App manda el micrófono (PCM 16 kHz) y recibe la voz de Kroko (PCM 24 kHz) a medida
/// que se genera. Las herramientas pasan por el mismo IToolGateway que el chat normal (riesgos, tarjeta de
/// confirmación y auditoría) y lo dicho se guarda en el historial para que la conversación escrita siga el hilo.
/// </summary>
public sealed class LiveSession : IAsyncDisposable
{
    private const string Endpoint = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    private readonly IAgentChannel _app;
    private readonly IToolGateway _tools;
    private readonly SystemPrompt _prompt;
    private readonly ConversationStore _conversations;
    private readonly LlmOptions _llm;
    private readonly VoiceOptions _voice;
    private readonly TimeProvider _time;
    private readonly ILogger<LiveSession> _log;
    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Channel<byte[]> _audioIn = System.Threading.Channels.Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly StringBuilder _userTurn = new();
    private readonly StringBuilder _krokoTurn = new();
    private string? _lastExpression;
    private Task? _receive;
    private Task? _pump;

    public LiveSession(IAgentChannel app, IToolGateway tools, SystemPrompt prompt, ConversationStore conversations,
        IOptionsMonitor<LlmOptions> llm, IOptionsMonitor<VoiceOptions> voice, TimeProvider time, ILogger<LiveSession> log)
    {
        _app = app;
        _tools = tools;
        _prompt = prompt;
        _conversations = conversations;
        _llm = llm.CurrentValue;
        _voice = voice.CurrentValue;
        _time = time;
        _log = log;
    }

    public bool IsOpen => _ws.State == WebSocketState.Open && !_cts.IsCancellationRequested;

    public async Task StartAsync(CancellationToken ct)
    {
        var key = _llm.Gemini.ResolveApiKey() ?? throw new NoLlmConfiguredException();
        _ws.Options.SetRequestHeader("x-goog-api-key", key);
        await _ws.ConnectAsync(new Uri(Endpoint), ct);

        var conversation = _conversations.Get(_app.SessionId);
        var system = _prompt.Build(_time.GetLocalNow(), conversation, expressionTags: false) + """


            Ahora hablas EN VOZ y en tiempo real: respuestas cortas y naturales, como en una llamada.
            No uses etiquetas de expresión [[…]], viñetas ni Markdown: todo lo que escribas se dice en voz alta.
            """;
        var decls = new JsonArray();
        foreach (var t in _tools.Tools)
        {
            decls.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parametersJsonSchema"] = JsonNode.Parse(t.Parameters.GetRawText()),
            });
        }

        var setup = new JsonObject
        {
            ["model"] = $"models/{_voice.LiveModel}",
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray("AUDIO"),
                ["speechConfig"] = new JsonObject
                {
                    ["voiceConfig"] = new JsonObject { ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = _voice.Voice } },
                },
            },
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
            ["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = decls }),
            ["inputAudioTranscription"] = new JsonObject(),
            ["outputAudioTranscription"] = new JsonObject(),
            // Conversaciones largas: Gemini resume lo antiguo en vez de cortar la sesión
            ["contextWindowCompression"] = new JsonObject { ["slidingWindow"] = new JsonObject() },
        };
        await SendAsync(new JsonObject { ["setup"] = setup }, ct);

        _receive = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        _pump = Task.Run(() => PumpAudioAsync(_cts.Token), CancellationToken.None);
        _log.LogInformation("Voz en tiempo real abierta ({Model})", _voice.LiveModel);
    }

    /// <summary>Audio del micrófono (PCM 16 kHz). Si llega más rápido de lo que sale, se descarta lo más viejo.</summary>
    public void PushAudio(byte[] pcm) => _audioIn.Writer.TryWrite(pcm);

    private async Task PumpAudioAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var pcm in _audioIn.Reader.ReadAllAsync(ct))
            {
                await SendAsync(new JsonObject
                {
                    ["realtimeInput"] = new JsonObject
                    {
                        ["audio"] = new JsonObject { ["data"] = Convert.ToBase64String(pcm), ["mimeType"] = "audio/pcm;rate=16000" },
                    },
                }, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        _log.LogInformation("Gemini cerró la voz en tiempo real: {Reason}", _ws.CloseStatusDescription);
                        await EmitAsync("ended", _ws.CloseStatusDescription, CancellationToken.None);
                        return;
                    }
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                if (JsonNode.Parse(ms.ToArray()) is JsonObject msg) await HandleAsync(msg, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error en la voz en tiempo real");
            await EmitAsync("error", "Se cortó la voz en tiempo real.", CancellationToken.None);
        }
    }

    private async Task HandleAsync(JsonObject msg, CancellationToken ct)
    {
        if (msg["setupComplete"] is not null)
        {
            await EmitAsync("started", null, ct);
            return;
        }
        if (msg["toolCall"]?["functionCalls"] is JsonArray calls)
        {
            // Las herramientas pueden pedir confirmación (tarjeta): se atienden sin bloquear la recepción
            _ = Task.Run(() => RunToolsAsync(calls.DeepClone().AsArray(), ct), CancellationToken.None);
            return;
        }
        if (msg["goAway"] is not null)
        {
            await EmitAsync("ended", "La sesión de voz llegó a su límite.", ct);
            return;
        }
        if (msg["serverContent"] is not JsonObject content) return;

        if (content["inputTranscription"]?["text"]?.GetValue<string>() is { Length: > 0 } heard)
        {
            _userTurn.Append(heard);
            await EmitAsync("you", Kroko.Shared.KrokoName.Fix(_userTurn.ToString().Trim()), ct);
        }
        if (content["outputTranscription"]?["text"]?.GetValue<string>() is { Length: > 0 } said)
        {
            _krokoTurn.Append(said);
            // Si aun así coló una etiqueta [[happy]], no se muestra: se convierte en la cara que pedía
            var (clean, expression) = ExpressionTag.Extract(Kroko.Shared.KrokoName.Fix(_krokoTurn.ToString()));
            if (expression is not null && expression != _lastExpression)
            {
                _lastExpression = expression;
                await _app.SendAsync(new ExpressionMessage { SessionId = _app.SessionId, Name = expression }, ct);
            }
            await EmitAsync("kroko", clean, ct);
        }
        if (content["modelTurn"]?["parts"] is JsonArray parts)
        {
            foreach (var p in parts)
            {
                if (p?["inlineData"]?["data"]?.GetValue<string>() is { Length: > 0 } audio)
                    await _app.SendAsync(new LiveAudioMessage { SessionId = _app.SessionId, Data = audio }, ct);
            }
        }
        if (content["interrupted"]?.GetValue<bool>() == true) await EmitAsync("interrupted", null, ct);
        if (content["turnComplete"]?.GetValue<bool>() == true) await CompleteTurnAsync(ct);
    }

    private async Task RunToolsAsync(JsonArray calls, CancellationToken ct)
    {
        var responses = new JsonArray();
        foreach (var c in calls)
        {
            var id = c?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
            var name = c?["name"]?.GetValue<string>() ?? "";
            var args = JsonSerializer.SerializeToElement(c?["args"] ?? new JsonObject());
            await _app.SendAsync(new StateMessage { SessionId = _app.SessionId, State = FaceStates.Thinking }, ct);
            ToolExecution result;
            try
            {
                result = await _tools.ExecuteAsync(new ToolCall(id, name, args), _app, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Falló la herramienta {Tool} en voz en tiempo real", name);
                result = ToolConfirmation.Fail("Algo falló al hacerlo.");
            }
            // Las tarjetas (tareas, estado del equipo) también salen en el panel
            if (Cards.FromToolResult(name, ExtractData(result.Json)) is { } card)
                await _app.SendAsync(card with { SessionId = _app.SessionId }, ct);
            responses.Add(new JsonObject
            {
                ["id"] = id,
                ["name"] = name,
                ["response"] = new JsonObject { ["result"] = JsonNode.Parse(result.Json) },
            });
        }
        await SendAsync(new JsonObject { ["toolResponse"] = new JsonObject { ["functionResponses"] = responses } }, ct);
    }

    private static JsonElement ExtractData(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("data", out var d) ? d.Clone() : default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>Fin de un turno: se guarda en el historial (para que el chat escrito sepa de qué hablaron).</summary>
    private async Task CompleteTurnAsync(CancellationToken ct)
    {
        var user = Kroko.Shared.KrokoName.Fix(_userTurn.ToString().Trim());
        var kroko = ExpressionTag.Extract(Kroko.Shared.KrokoName.Fix(_krokoTurn.ToString())).Text; // al historial, sin etiquetas
        _lastExpression = null;
        _userTurn.Clear();
        _krokoTurn.Clear();
        if (user.Length > 0 || kroko.Length > 0)
        {
            var turn = new List<ChatMessage>();
            if (user.Length > 0) turn.Add(ChatMessage.User(user));
            if (kroko.Length > 0) turn.Add(new ChatMessage { Role = ChatRole.Assistant, Text = kroko });
            _conversations.Get(_app.SessionId).Commit(turn);
        }
        await EmitAsync("turn", kroko, ct);
    }

    private Task EmitAsync(string kind, string? text, CancellationToken ct) =>
        _app.SendAsync(new LiveEventMessage { SessionId = _app.SessionId, Kind = kind, Text = text }, ct);

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws.State == WebSocketState.Open) await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _audioIn.Writer.TryComplete();
        await _cts.CancelAsync();
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "fin", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is WebSocketException or TimeoutException or OperationCanceledException) { }
        try
        {
            await Task.WhenAll(_receive ?? Task.CompletedTask, _pump ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or WebSocketException) { }
        _ws.Dispose();
        _cts.Dispose();
        _log.LogInformation("Voz en tiempo real cerrada");
    }
}
