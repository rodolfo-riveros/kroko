using System.Net.WebSockets;
using Kroko.Brain.Providers;
using Kroko.Shared.Protocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kroko.Tests;

/// <summary>
/// Prueba de punta a punta del Brain: WebSocket real en memoria, agente real, IA simulada.
/// Es exactamente el criterio de la Fase 1B: le escribo "hola" y responde con texto y expresión.
/// </summary>
public class EndToEndTests
{
    private const string Token = "token-de-prueba";

    private static WebApplicationFactory<Program> CreateBrain(params ILlmProvider[] providers) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>(TestPaths.IsolatedMemory())
            {
                ["Agent:Token"] = Token,
            }));
            b.ConfigureServices(s =>
            {
                // Nada de llamadas reales a Gemini/DeepSeek en las pruebas
                s.RemoveAll<ILlmProvider>();
                foreach (var p in providers) s.AddSingleton(p);
            });
        });

    private static async Task<(WebSocketTransport Transport, WebSocket Socket)> ConnectAsync(WebApplicationFactory<Program> app, string? token = Token)
    {
        var client = app.Server.CreateWebSocketClient();
        if (token is not null) client.ConfigureRequest = r => r.Headers[KrokoProtocol.TokenHeader] = token;
        var socket = await client.ConnectAsync(new Uri(app.Server.BaseAddress, KrokoProtocol.WebSocketPath), default);
        return (new WebSocketTransport(socket), socket);
    }

    private static async Task<List<KrokoMessage>> ReceiveUntilIdleAsync(WebSocketTransport t)
    {
        var received = new List<KrokoMessage>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var (closed, msg) = await t.ReceiveAsync(timeout.Token);
            Assert.False(closed);
            Assert.NotNull(msg);
            received.Add(msg!);
            if (msg is StateMessage { State: FaceStates.Idle }) return received;
        }
    }

    [Fact]
    public async Task Hola_responde_con_texto_y_expresion()
    {
        await using var app = CreateBrain(new StubProvider("gemini", configured: true, reply: "[[happy]] ¡Hola, Pol! ¿En qué te ayudo?"));
        var (t, socket) = await ConnectAsync(app);

        await t.SendAsync(new UserTextMessage { SessionId = "s", Text = "hola" }, default);
        var msgs = await ReceiveUntilIdleAsync(t);

        Assert.Equal(FaceStates.Thinking, Assert.IsType<StateMessage>(msgs[0]).State);
        var reply = Assert.Single(msgs.OfType<ReplyMessage>());
        Assert.Equal("¡Hola, Pol! ¿En qué te ayudo?", reply.Text);
        Assert.False(reply.IsError);
        Assert.Equal("happy", Assert.Single(msgs.OfType<ExpressionMessage>()).Name);
        Assert.All(msgs, m => Assert.Equal("s", m.SessionId));

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fin", default);
    }

    [Fact]
    public async Task Ping_recibe_pong()
    {
        await using var app = CreateBrain();
        var (t, _) = await ConnectAsync(app);

        await t.SendAsync(new PingMessage { SessionId = "s" }, default);
        var (_, msg) = await t.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.IsType<PongMessage>(msg);
    }

    [Fact]
    public async Task Sin_api_key_explica_el_problema_con_cara_confundida()
    {
        await using var app = CreateBrain(new StubProvider("gemini", configured: false));
        var (t, _) = await ConnectAsync(app);

        await t.SendAsync(new UserTextMessage { SessionId = "s", Text = "hola" }, default);
        var msgs = await ReceiveUntilIdleAsync(t);

        var reply = Assert.Single(msgs.OfType<ReplyMessage>());
        Assert.True(reply.IsError);
        Assert.Contains("API key", reply.Text);
        Assert.Equal("confused", Assert.Single(msgs.OfType<ExpressionMessage>()).Name);
    }

    [Fact]
    public async Task Si_la_ia_falla_responde_triste_sin_romperse()
    {
        await using var app = CreateBrain(new StubProvider("gemini", configured: true, fail: true));
        var (t, _) = await ConnectAsync(app);

        await t.SendAsync(new UserTextMessage { SessionId = "s", Text = "hola" }, default);
        var msgs = await ReceiveUntilIdleAsync(t);

        Assert.True(Assert.Single(msgs.OfType<ReplyMessage>()).IsError);
        Assert.Equal("sad", Assert.Single(msgs.OfType<ExpressionMessage>()).Name);
    }

    [Fact]
    public async Task Token_incorrecto_es_rechazado()
    {
        await using var app = CreateBrain();
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(app, token: "malo"));
    }
}
