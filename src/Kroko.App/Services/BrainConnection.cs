using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Windows;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kroko.App.Services;

/// <summary>
/// Conexión WebSocket con Kroko.Brain. Se reconecta sola (1 s, 2 s, 4 s… hasta 30 s) y manda
/// un latido cada 15 s. Los eventos se lanzan en el hilo de la interfaz.
/// </summary>
public sealed class BrainConnection(IOptions<KrokoOptions> options, ILogger<BrainConnection> log) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private WebSocketTransport? _transport;
    private bool _connected;

    /// <summary>
    /// Identifica la conversación. Es fijo entre reinicios (se guarda en %LOCALAPPDATA%Krokosession-id.txt)
    /// para que Kroko continúe la misma conversación en vez de empezar de cero.
    /// </summary>
    public string SessionId { get; } = LoadOrCreateSessionId();

    private static string LoadOrCreateSessionId()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", "session-id.txt");
        try
        {
            if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: 32 } existing) return existing;
            var id = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, id);
            return id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    public bool IsConnected => _connected;

    public event Action<bool>? ConnectionChanged;
    public event Action<KrokoMessage>? MessageReceived;

    /// <summary>Envía un mensaje. Devuelve false si no hay conexión.</summary>
    public async Task<bool> SendAsync(KrokoMessage message)
    {
        var t = _transport;
        if (t is null || !_connected) return false;
        try
        {
            await t.SendAsync(message with { SessionId = SessionId }, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            log.LogWarning("No se pudo enviar al Brain: {Message}", ex.Message);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        var uri = BuildUri(options.Value.BrainUrl);
        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            attempt++;
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = KrokoProtocol.HeartbeatInterval;
            if (!string.IsNullOrEmpty(options.Value.BrainToken))
                socket.Options.SetRequestHeader(KrokoProtocol.TokenHeader, options.Value.BrainToken);

            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(uri, connectTimeout.Token);

                _transport = new WebSocketTransport(socket);
                SetConnected(true);
                log.LogInformation("Conectado al Brain en {Uri}", uri);
                backoff = TimeSpan.FromSeconds(1);
                attempt = 0;

                await RunAsync(_transport, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidDataException or HttpRequestException)
            {
                // Los primeros intentos fallan mientras el Brain arranca: no ensuciamos el log.
                if (_connected || attempt > 3) log.LogInformation("Sin conexión con el Brain: {Message}", ex.Message);
            }
            finally
            {
                _transport = null;
                SetConnected(false);
            }

            try
            {
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 400));
                await Task.Delay(backoff + jitter, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private async Task RunAsync(WebSocketTransport transport, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = HeartbeatAsync(transport, linked.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var (closed, message) = await transport.ReceiveAsync(linked.Token);
                if (closed) break;
                if (message is null or PongMessage) continue;
                Dispatch(() => MessageReceived?.Invoke(message));
            }
        }
        finally
        {
            await linked.CancelAsync();
            try { await heartbeat; } catch (OperationCanceledException) { }
            await transport.CloseAsync("adiós", CancellationToken.None);
        }
    }

    private async Task HeartbeatAsync(WebSocketTransport transport, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(KrokoProtocol.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await transport.SendAsync(new PingMessage { SessionId = SessionId }, ct);
        }
        catch (WebSocketException)
        {
            // La recepción detectará el cierre y se reconectará.
        }
    }

    private void SetConnected(bool value)
    {
        if (_connected == value) return;
        _connected = value;
        Dispatch(() => ConnectionChanged?.Invoke(value));
    }

    private static void Dispatch(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        d.BeginInvoke(action);
    }

    internal static Uri BuildUri(string brainUrl)
    {
        var b = new UriBuilder(brainUrl);
        b.Scheme = b.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        b.Port = new Uri(brainUrl).Port;
        b.Path = KrokoProtocol.WebSocketPath;
        return b.Uri;
    }
}
