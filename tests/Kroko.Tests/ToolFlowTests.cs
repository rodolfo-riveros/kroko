using System.Text.Json;
using Kroko.Brain.Providers;
using Kroko.Shared.Protocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kroko.Tests;

/// <summary>
/// Flujo completo de herramientas por WebSocket: la IA (simulada) pide una herramienta,
/// el Brain pide confirmación si es peligrosa, la "App" (este test) responde.
/// </summary>
public class ToolFlowTests
{
    private const string Token = "t";

    private static WebApplicationFactory<Program> CreateBrain(ILlmProvider provider) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>(TestPaths.IsolatedMemory()) { ["Agent:Token"] = Token }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ILlmProvider>();
                s.AddSingleton(provider);
            });
        });

    /// <summary>
    /// Conversa como lo haría la App: responde confirmaciones con <paramref name="approve"/> y
    /// las herramientas con un resultado de ejemplo. Devuelve todo lo recibido hasta "idle".
    /// </summary>
    private static async Task<List<KrokoMessage>> RunAsync(WebApplicationFactory<Program> app, string text, bool approve)
    {
        var client = app.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers[KrokoProtocol.TokenHeader] = Token;
        var socket = await client.ConnectAsync(new Uri(app.Server.BaseAddress, KrokoProtocol.WebSocketPath), default);
        var t = new WebSocketTransport(socket);
        await t.SendAsync(new UserTextMessage { SessionId = "s", Text = text }, default);

        var received = new List<KrokoMessage>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var (_, msg) = await t.ReceiveAsync(timeout.Token);
            Assert.NotNull(msg);
            received.Add(msg!);
            switch (msg)
            {
                case ConfirmRequestMessage c:
                    await t.SendAsync(new ConfirmResponseMessage { SessionId = "s", CallId = c.CallId, Approved = approve }, default);
                    break;
                case ToolCallMessage call:
                    await t.SendAsync(new ToolResultMessage
                    {
                        SessionId = "s",
                        CallId = call.CallId,
                        Ok = true,
                        Data = JsonSerializer.SerializeToElement(new { programado = "apagado", hora = "23:40" }),
                    }, default);
                    break;
                case StateMessage { State: FaceStates.Idle } when received.OfType<ReplyMessage>().Any():
                    return received;
            }
        }
    }

    [Fact]
    public async Task Apagar_pide_confirmacion_y_si_aprueba_se_ejecuta()
    {
        var ia = new ScriptedProvider("apagar", """{"minutos":10}""", "[[success]] Listo, la PC se apaga a las 23:40.");
        await using var app = CreateBrain(ia);

        var msgs = await RunAsync(app, "apaga la PC en 10 minutos", approve: true);

        var confirm = Assert.Single(msgs.OfType<ConfirmRequestMessage>());
        Assert.Equal("Apagar la PC en 10 min", confirm.Text);
        Assert.Equal("apagar", confirm.Tool);

        var call = Assert.Single(msgs.OfType<ToolCallMessage>());
        Assert.Equal(ToolRisk.Dangerous, call.Risk);
        Assert.True(msgs.IndexOf(confirm) < msgs.IndexOf(call)); // primero la tarjeta, después la acción

        Assert.Contains("23:40", Assert.Single(msgs.OfType<ReplyMessage>()).Text);
        Assert.Contains("\"ok\":true", ia.LastToolResult);
    }

    [Fact]
    public async Task Si_Pol_rechaza_la_accion_nunca_llega_a_la_App()
    {
        var ia = new ScriptedProvider("enviar_a_papelera", """{"rutas":["C:\\a.txt","C:\\b.txt"]}""", "[[oops]] Vale, no borro nada.");
        await using var app = CreateBrain(ia);

        var msgs = await RunAsync(app, "borra esos archivos", approve: false);

        Assert.Single(msgs.OfType<ConfirmRequestMessage>());
        Assert.Empty(msgs.OfType<ToolCallMessage>());
        Assert.Contains("rechazó", ia.LastToolResult);
    }

    [Fact]
    public async Task Lectura_no_pide_confirmacion()
    {
        var ia = new ScriptedProvider("estado_equipo", "{}", "[[happy]] Tu batería está al 80 %.");
        await using var app = CreateBrain(ia);

        var msgs = await RunAsync(app, "¿cómo va mi batería?", approve: true);

        Assert.Empty(msgs.OfType<ConfirmRequestMessage>());
        Assert.Equal(ToolRisk.Read, Assert.Single(msgs.OfType<ToolCallMessage>()).Risk);
    }

    [Fact]
    public async Task Mover_un_archivo_no_pide_confirmacion_pero_varios_si()
    {
        var uno = new ScriptedProvider("mover_archivos", """{"origenes":["C:\\a.txt"],"destino":"C:\\x"}""", "[[success]] Movido.");
        await using (var app = CreateBrain(uno))
            Assert.Empty((await RunAsync(app, "mueve a.txt", approve: true)).OfType<ConfirmRequestMessage>());

        var varios = new ScriptedProvider("mover_archivos", """{"origenes":["C:\\a.txt","C:\\b.txt"],"destino":"C:\\x"}""", "[[success]] Movidos.");
        await using (var app = CreateBrain(varios))
            Assert.Single((await RunAsync(app, "mueve los dos", approve: true)).OfType<ConfirmRequestMessage>());
    }

    /// <summary>IA de mentira: la primera vez pide una herramienta; la segunda responde con texto.</summary>
    private sealed class ScriptedProvider(string tool, string argsJson, string finalText) : ILlmProvider
    {
        private int _calls;
        public string LastToolResult { get; private set; } = "";
        public string Name => "gemini";
        public bool IsConfigured => true;
        public bool SupportsAudio => false;
        public bool SupportsVision => false;

        public Task<LlmResponse> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history,
            IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            Assert.Contains(tools, t => t.Name == tool); // el catálogo llega a la IA
            if (_calls++ == 0)
            {
                var call = new ToolCall("call-1", tool, JsonDocument.Parse(argsJson).RootElement.Clone());
                var msg = new ChatMessage { Role = ChatRole.Assistant, ToolCalls = [call] };
                return Task.FromResult(new LlmResponse("", [call], msg, Name));
            }

            LastToolResult = history.Last(m => m.Role == ChatRole.Tool).Text;
            var final = new ChatMessage { Role = ChatRole.Assistant, Text = finalText };
            return Task.FromResult(new LlmResponse(finalText, [], final, Name));
        }
    }
}
