using System.Collections.Concurrent;
using System.Net.WebSockets;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Agent;

/// <summary>
/// Atiende una conexión WebSocket de la App: autentica, recibe mensajes y lanza el agente.
/// - Una orden nueva cancela la anterior.
/// - control.stop (Ctrl+Alt+K) cancela la orden en curso y todo lo pendiente.
/// - tool.result / confirm.response se entregan a quien los esperaba (por callId).
/// </summary>
public sealed class SessionHandler(AgentLoop agent, Productivity.AppConnections apps, IOptionsMonitor<AgentOptions> options,
    IOptionsMonitor<Tts.VoiceOptions> voice, IServiceProvider services, ILogger<SessionHandler> log)
{
    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!BrainAuth.IsAuthorized(context, options.CurrentValue.Token))
        {
            log.LogWarning("Conexión rechazada desde {Ip}: token inválido", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var transport = new WebSocketTransport(socket);
        var channel = new Channel(transport);
        var registration = apps.Add(channel);
        var aborted = context.RequestAborted;
        log.LogInformation("App conectada");

        CancellationTokenSource? current = null;
        Task running = Task.CompletedTask;
        LiveSession? live = null;

        async Task StopLiveAsync()
        {
            if (live is null) return;
            var l = live;
            live = null;
            await l.DisposeAsync();
        }

        async Task CancelCurrentAsync()
        {
            if (current is null) return;
            await current.CancelAsync();
            channel.CancelPending();
            try { await running; } catch (OperationCanceledException) { }
            current.Dispose();
            current = null;
        }

        try
        {
            while (!aborted.IsCancellationRequested)
            {
                var (closed, message) = await transport.ReceiveAsync(aborted);
                if (closed) break;
                if (message is not null && !string.IsNullOrEmpty(message.SessionId)) channel.SessionId = message.SessionId;

                switch (message)
                {
                    case null:
                        log.LogWarning("Mensaje no reconocido; se ignora");
                        break;

                    case PingMessage ping:
                        await transport.SendAsync(new PongMessage { SessionId = ping.SessionId }, aborted);
                        break;

                    case ToolResultMessage r:
                        channel.Complete(r.CallId, r);
                        break;

                    case ConfirmResponseMessage c:
                        channel.Complete(c.CallId, c);
                        break;

                    case StopMessage:
                        log.LogWarning("Interruptor de emergencia: se cancela todo");
                        await CancelCurrentAsync();
                        await StopLiveAsync();
                        await transport.SendAsync(new StateMessage { SessionId = channel.SessionId, State = FaceStates.Idle }, aborted);
                        break;

                    case UserTextMessage text when !string.IsNullOrWhiteSpace(text.Text):
                        await CancelCurrentAsync();
                        current = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                        var token = current.Token;
                        running = Task.Run(() => RunSafeAsync(() => agent.HandleUserTextAsync(text.Text.Trim(), channel, token)), CancellationToken.None);
                        break;

                    case UserAudioMessage audio when !string.IsNullOrEmpty(audio.AudioBase64):
                    {
                        byte[] wav;
                        try
                        {
                            wav = Convert.FromBase64String(audio.AudioBase64);
                        }
                        catch (FormatException)
                        {
                            log.LogWarning("Audio inválido; se ignora");
                            break;
                        }
                        await CancelCurrentAsync();
                        current = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                        var audioToken = current.Token;
                        running = Task.Run(() => RunSafeAsync(() => agent.HandleUserAudioAsync(wav, channel, audioToken)), CancellationToken.None);
                        break;
                    }

                    case LiveStartMessage:
                        await CancelCurrentAsync();
                        await StopLiveAsync();
                        if (!voice.CurrentValue.LiveEnabled)
                        {
                            await transport.SendAsync(new LiveEventMessage { SessionId = channel.SessionId, Kind = "error", Text = "La voz en tiempo real está desactivada." }, aborted);
                            break;
                        }
                        try
                        {
                            live = ActivatorUtilities.CreateInstance<LiveSession>(services, (IAgentChannel)channel);
                            await live.StartAsync(aborted);
                        }
                        catch (Exception ex) when (ex is WebSocketException or Providers.NoLlmConfiguredException or HttpRequestException or InvalidOperationException)
                        {
                            log.LogWarning("No se pudo abrir la voz en tiempo real: {Message}", ex.Message);
                            await StopLiveAsync();
                            await transport.SendAsync(new LiveEventMessage { SessionId = channel.SessionId, Kind = "error", Text = "No pude abrir la voz en tiempo real." }, aborted);
                        }
                        break;

                    case LiveAudioMessage audio when live is { IsOpen: true } && !string.IsNullOrEmpty(audio.Data):
                        try
                        {
                            live.PushAudio(Convert.FromBase64String(audio.Data));
                        }
                        catch (FormatException) { }
                        break;

                    case LiveAudioMessage:
                        break; // sesión ya cerrada: el audio sobrante se ignora

                    case LiveStopMessage:
                        await StopLiveAsync();
                        break;

                    default:
                        log.LogDebug("Mensaje {Type} aún no soportado", message.GetType().Name);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidDataException)
        {
            log.LogInformation("Conexión con la App terminada ({Reason})", ex.Message);
        }
        finally
        {
            apps.Remove(registration);
            await CancelCurrentAsync();
            await StopLiveAsync();
            await transport.CloseAsync("adiós", CancellationToken.None);
            log.LogInformation("App desconectada");
        }
    }

    private async Task RunSafeAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            log.LogDebug("No se pudo entregar la respuesta: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error inesperado en el agente");
        }
    }

    /// <summary>Canal de una conexión: envía mensajes y empareja peticiones con respuestas por callId.</summary>
    private sealed class Channel(WebSocketTransport transport) : IAgentChannel
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<KrokoMessage>> _pending = new();

        public string SessionId { get; set; } = "";

        public Task SendAsync(KrokoMessage message, CancellationToken ct) => transport.SendAsync(message, ct);

        public async Task<TResponse?> RequestAsync<TResponse>(KrokoMessage request, string callId, TimeSpan timeout, CancellationToken ct)
            where TResponse : KrokoMessage
        {
            var tcs = new TaskCompletionSource<KrokoMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[callId] = tcs;
            try
            {
                await transport.SendAsync(request, ct);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);
                try
                {
                    var response = await tcs.Task.WaitAsync(timeoutCts.Token);
                    return response as TResponse;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return null; // se agotó el tiempo
                }
            }
            finally
            {
                _pending.TryRemove(callId, out _);
            }
        }

        public void Complete(string callId, KrokoMessage response)
        {
            if (_pending.TryRemove(callId, out var tcs)) tcs.TrySetResult(response);
        }

        public void CancelPending()
        {
            foreach (var key in _pending.Keys)
                if (_pending.TryRemove(key, out var tcs)) tcs.TrySetCanceled();
        }
    }
}
