using Kroko.Brain.Agent;
using Kroko.Brain.Providers;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kroko.Tests;

/// <summary>IA mixta (17/11/2026): lo simple lo piensa la PC del dueño (Qwen 3 4B), lo difícil Gemini.</summary>
[Collection("ClavesIA")]
public class LocalLlmTests : IDisposable
{
    /// <summary>Deja todo como estaba: las claves y la IA de la PC viven en memoria compartida.</summary>
    public void Dispose()
    {
        AppProvidedKeys.Clear();
        LocalLlm.Channels.Clear();
    }

    /// <summary>La App de la PC: contesta lo que le pide el cerebro como si fuera su llama.cpp.</summary>
    private sealed class FakeApp(Func<string, LocalLlmResultMessage> answer) : IAgentChannel
    {
        public readonly List<string> Bodies = [];
        public string SessionId => "s";
        public Task SendAsync(KrokoMessage message, CancellationToken ct) => Task.CompletedTask;

        public Task<TResponse?> RequestAsync<TResponse>(KrokoMessage request, string callId, TimeSpan timeout, CancellationToken ct)
            where TResponse : KrokoMessage
        {
            var body = ((LocalLlmRequestMessage)request).Body;
            Bodies.Add(body);
            return Task.FromResult(answer(body) as TResponse);
        }
    }

    private static LocalLlmResultMessage Says(string text) => new()
    {
        Ok = true,
        Body = $$$"""{"choices":[{"message":{"role":"assistant","content":"{{{text}}}"}}]}""",
    };

    private static (FallbackLlmProvider Router, StubProvider Cloud, FakeApp App) Build(Func<string, LocalLlmResultMessage> answer)
    {
        AppProvidedKeys.Clear();
        LocalLlm.Channels.Clear();
        var owner = new object();
        var app = new FakeApp(answer);
        AppProvidedKeys.Set(owner, [], null, "Qwen3-4B");
        LocalLlm.Channels[owner] = app;
        var monitor = new JarvisToolTests.StaticMonitor<LlmOptions>(new LlmOptions { Order = ["gemini"] });
        var cloud = new StubProvider("gemini", configured: true, reply: "Respuesta de Gemini");
        var local = new LocalLlmProvider(monitor, NullLogger<LocalLlmProvider>.Instance);
        return (new FallbackLlmProvider([cloud], monitor, NullLogger<FallbackLlmProvider>.Instance, null, local), cloud, app);
    }

    private static ToolDefinition Tool(string name) => new(name, "x", System.Text.Json.JsonDocument.Parse("""{"type":"object"}""").RootElement, ToolRisk.Read);

    [Fact]
    public async Task Lo_simple_lo_piensa_la_PC_sin_gastar_Gemini()
    {
        var (router, cloud, app) = Build(_ => Says("¡Listo, anotado!"));
        var r = await router.ChatAsync("s", [ChatMessage.User("anota una tarea: comprar pan")], [Tool("tarea_crear"), Tool("correo_enviar")], default);

        Assert.Equal("local", r.Provider);
        Assert.Equal(0, cloud.Calls);
        // A la IA de la PC solo le llegan las herramientas que puede usar (no el correo)
        Assert.Contains("tarea_crear", app.Bodies[0]);
        Assert.DoesNotContain("correo_enviar", app.Bodies[0]);
        Assert.Contains("enable_thinking", app.Bodies[0]);
    }

    [Theory]
    [InlineData("¿cómo estará el clima mañana en Urubamba?")]
    [InlineData("revisa mi correo")]
    [InlineData("créame una página en Notion con mis pendientes")]
    public async Task Lo_dificil_va_a_Gemini(string order)
    {
        var (router, cloud, app) = Build(_ => Says("no debería llegar aquí"));
        var r = await router.ChatAsync("s", [ChatMessage.User(order)], [], default);
        Assert.Equal("gemini", r.Provider);
        Assert.Empty(app.Bodies);
    }

    [Fact]
    public async Task Si_la_PC_falla_responde_Gemini()
    {
        var (router, cloud, _) = Build(_ => new LocalLlmResultMessage { Ok = false, Error = "se cerró" });
        var r = await router.ChatAsync("s", [ChatMessage.User("hola Kroko")], [], default);
        Assert.Equal("gemini", r.Provider);
        Assert.Equal(1, cloud.Calls);
    }

    [Fact]
    public async Task Sin_la_IA_de_la_PC_todo_sigue_con_Gemini()
    {
        var (router, cloud, _) = Build(_ => Says("x"));
        AppProvidedKeys.Clear(); // la App no tiene la IA encendida
        var r = await router.ChatAsync("s", [ChatMessage.User("hola")], [], default);
        Assert.Equal("gemini", r.Provider);
    }
}
