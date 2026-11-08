using System.Net.WebSockets;
using Kroko.Brain.Providers;
using Kroko.Brain.Tts;
using Kroko.Shared.Protocol;
using Kroko.Shared.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kroko.Tests;

/// <summary>Responder con voz o "solo chat": el cerebro hace la voz solo cuando la App la pide.</summary>
public class ReplyModeTests
{
    private const string Token = "token-de-prueba";

    private sealed class FakeTts : ITtsProvider
    {
        public int Calls;
        public Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<byte[]?>([1, 2, 3, 4]);
        }
    }

    private sealed class FakeStt : ISpeechToText
    {
        public Task<string?> TranscribeAsync(byte[] wav, CancellationToken ct) => Task.FromResult<string?>("hola");
        public Task<string?> TranscribeAsync(byte[] audio, string mimeType, CancellationToken ct) => Task.FromResult<string?>("hola");
    }

    private static WebApplicationFactory<Program> CreateBrain(FakeTts tts) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>(TestPaths.IsolatedMemory())
            {
                ["Agent:Token"] = Token,
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ILlmProvider>();
                s.AddSingleton<ILlmProvider>(new StubProvider("gemini", configured: true, reply: "[[happy]] ¡Hola!"));
                s.RemoveAll<ITtsProvider>();
                s.AddSingleton<ITtsProvider>(tts);
                s.RemoveAll<ISpeechToText>();
                s.AddSingleton<ISpeechToText>(new FakeStt());
            });
        });

    private static async Task<ReplyMessage> AskAsync(WebApplicationFactory<Program> app, KrokoMessage request)
    {
        var client = app.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers[KrokoProtocol.TokenHeader] = Token;
        var socket = await client.ConnectAsync(new Uri(app.Server.BaseAddress, KrokoProtocol.WebSocketPath), default);
        var t = new WebSocketTransport(socket);
        await t.SendAsync(request, default);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ReplyMessage? reply = null;
        while (true)
        {
            var (closed, msg) = await t.ReceiveAsync(timeout.Token);
            Assert.False(closed);
            if (msg is ReplyMessage r) reply = r;
            if (msg is StateMessage { State: FaceStates.Idle } && reply is not null) break;
        }
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fin", default);
        return reply;
    }

    [Fact]
    public async Task Lo_escrito_lleva_voz_solo_si_la_App_la_pide()
    {
        var tts = new FakeTts();
        await using var app = CreateBrain(tts);

        var sinPedir = await AskAsync(app, new UserTextMessage { SessionId = "s", Text = "hola" });
        Assert.Null(sinPedir.AudioBase64);

        var conVoz = await AskAsync(app, new UserTextMessage { SessionId = "s", Text = "hola", Speak = true });
        Assert.False(string.IsNullOrEmpty(conVoz.AudioBase64));
        Assert.Equal(1, tts.Calls);
    }

    [Fact]
    public async Task En_solo_chat_una_orden_hablada_no_gasta_voz()
    {
        var tts = new FakeTts();
        await using var app = CreateBrain(tts);
        var wav = Convert.ToBase64String(new byte[64]);

        var clasico = await AskAsync(app, new UserAudioMessage { SessionId = "s", AudioBase64 = wav });
        Assert.False(string.IsNullOrEmpty(clasico.AudioBase64));

        var soloChat = await AskAsync(app, new UserAudioMessage { SessionId = "s", AudioBase64 = wav, Speak = false });
        Assert.Null(soloChat.AudioBase64);
        Assert.Equal(1, tts.Calls);
    }

    [Fact]
    public void Una_App_antigua_sin_speak_sigue_funcionando()
    {
        var back = Assert.IsType<UserTextMessage>(KrokoProtocol.TryDeserialize("""{"type":"user.text","id":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","sessionId":"s","text":"hola"}"""));
        Assert.Null(back.Speak);
        var conSpeak = Assert.IsType<UserTextMessage>(KrokoProtocol.TryDeserialize(KrokoProtocol.Serialize(new UserTextMessage { Text = "x", Speak = false })));
        Assert.False(conSpeak.Speak);
    }

    [Fact]
    public void La_IA_puede_callarse_o_volver_a_hablar_sin_pedir_permiso()
    {
        var tool = Assert.Single(ToolCatalog.All, t => t.Name == "modo_respuesta");
        Assert.Equal(ToolRisk.Write, tool.Risk);
        Assert.Contains("cállate", tool.Description);
        Assert.Contains("háblame", tool.Description);
    }
}
