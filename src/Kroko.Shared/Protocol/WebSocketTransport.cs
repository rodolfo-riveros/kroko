using System.Net.WebSockets;
using System.Text;

namespace Kroko.Shared.Protocol;

/// <summary>
/// Envía y recibe mensajes JSON completos sobre un WebSocket (usado por la App y el Brain).
/// Los envíos se serializan con un semáforo: WebSocket no admite dos envíos a la vez.
/// </summary>
public sealed class WebSocketTransport(WebSocket socket)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WebSocket Socket => socket;

    public async Task SendAsync(KrokoMessage message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(KrokoProtocol.Serialize(message));
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Lee el siguiente mensaje de texto completo. Devuelve null cuando el otro lado cierra.
    /// Los mensajes que no se entienden se devuelven como null en <paramref name="invalid"/>.
    /// </summary>
    public async Task<(bool Closed, KrokoMessage? Message)> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return (true, null);

            ms.Write(buffer, 0, result.Count);
            if (ms.Length > KrokoProtocol.MaxMessageBytes)
                throw new InvalidDataException("Mensaje demasiado grande");

            if (result.EndOfMessage) break;
        }

        if (ms.Length == 0) return (false, null);
        var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        return (false, KrokoProtocol.TryDeserialize(json));
    }

    public async Task CloseAsync(string reason, CancellationToken ct)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, ct).ConfigureAwait(false);
            }
            catch (WebSocketException) { }
        }
    }
}
