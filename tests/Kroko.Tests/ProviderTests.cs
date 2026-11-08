using System.Net;
using System.Text;
using System.Text.Json;
using Kroko.Brain.Providers;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kroko.Tests;

public class ProviderTests
{
    private static readonly ToolDefinition SampleTool = new("estado_equipo", "Batería y CPU",
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement, ToolRisk.Read);

    [Fact]
    public async Task Gemini_envia_clave_modelo_y_prompt_y_reenvia_la_thought_signature()
    {
        var handler = new FakeHandler("""
            {"candidates":[{"content":{"role":"model","parts":[
              {"text":"[[happy]] Hola Pol","thoughtSignature":"FIRMA123"}]}}]}
            """);
        var gemini = new GeminiProvider(new HttpClient(handler), Options(o => o.Gemini.ApiKey = "clave-g"),
            NullLogger<GeminiProvider>.Instance);

        var first = await gemini.ChatAsync("Eres Kroko", [ChatMessage.User("hola")], [SampleTool], default);

        Assert.Equal("[[happy]] Hola Pol", first.Text);
        var req = handler.Requests.Single();
        Assert.Contains("models/gemini-3.6-flash:generateContent", req.Url);
        Assert.Equal("clave-g", req.Headers["x-goog-api-key"]);
        using (var body = JsonDocument.Parse(req.Body))
        {
            Assert.Equal("Eres Kroko", body.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.Equal("LOW", body.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
            Assert.Equal("estado_equipo", body.RootElement.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("name").GetString());
        }

        // Segundo turno: el mensaje del modelo debe viajar tal cual, con su firma
        await gemini.ChatAsync("Eres Kroko", [ChatMessage.User("hola"), first.AssistantMessage, ChatMessage.User("¿y?")], [], default);
        Assert.Contains("FIRMA123", handler.Requests[1].Body);
    }

    [Fact]
    public async Task Gemini_lee_llamadas_a_herramientas()
    {
        var handler = new FakeHandler("""
            {"candidates":[{"content":{"role":"model","parts":[
              {"functionCall":{"name":"estado_equipo","args":{"detalle":true}}}]}}]}
            """);
        var gemini = new GeminiProvider(new HttpClient(handler), Options(o => o.Gemini.ApiKey = "k"),
            NullLogger<GeminiProvider>.Instance);

        var res = await gemini.ChatAsync("p", [ChatMessage.User("¿batería?")], [SampleTool], default);

        var call = Assert.Single(res.ToolCalls);
        Assert.Equal("estado_equipo", call.Name);
        Assert.True(call.Arguments.GetProperty("detalle").GetBoolean());

        // El resultado se devuelve como functionResponse, sin inventar ids que Gemini no conoce
        var history = new List<ChatMessage> { ChatMessage.User("¿batería?"), res.AssistantMessage, ChatMessage.ToolResult(call, """{"bateria":80}""") };
        var body = GeminiProvider.BuildRequest("p", history, [SampleTool], Options(o => { }).CurrentValue).ToJsonString();
        Assert.Contains("\"functionResponse\"", body);
        Assert.Contains("\"bateria\":80", body);
        Assert.DoesNotContain(GeminiProvider.GeneratedIdPrefix, body);
    }

    [Fact]
    public async Task Gemini_error_http_lanza_LlmProviderException()
    {
        var handler = new FakeHandler("""{"error":{"message":"API key not valid"}}""", HttpStatusCode.BadRequest);
        var gemini = new GeminiProvider(new HttpClient(handler), Options(o => o.Gemini.ApiKey = "mala"),
            NullLogger<GeminiProvider>.Instance);

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() =>
            gemini.ChatAsync("p", [ChatMessage.User("hola")], [], default));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task DeepSeek_usa_bearer_desactiva_thinking_y_conserva_reasoning_content()
    {
        var handler = new FakeHandler("""
            {"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"[[wink]] Hecho",
              "reasoning_content":"RAZONAMIENTO-XYZ"}}]}
            """);
        var ds = new DeepSeekProvider(new HttpClient(handler), Options(o => o.DeepSeek.ApiKey = "clave-d"),
            NullLogger<DeepSeekProvider>.Instance);

        var first = await ds.ChatAsync("Eres Kroko", [ChatMessage.User("hola")], [SampleTool], default);
        Assert.Equal("[[wink]] Hecho", first.Text);

        var req = handler.Requests.Single();
        Assert.EndsWith("/chat/completions", req.Url);
        Assert.Equal("Bearer clave-d", req.Headers["Authorization"]);
        using (var body = JsonDocument.Parse(req.Body))
        {
            Assert.Equal("deepseek-flash", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.Equal("function", body.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
        }

        await ds.ChatAsync("Eres Kroko", [ChatMessage.User("hola"), first.AssistantMessage, ChatMessage.User("¿y?")], [SampleTool], default);
        Assert.Contains("RAZONAMIENTO-XYZ", handler.Requests[1].Body);
    }

    [Fact]
    public async Task Fallback_pasa_al_siguiente_proveedor_si_el_primero_falla()
    {
        var failing = new StubProvider("gemini", configured: true, fail: true);
        var working = new StubProvider("deepseek", configured: true, reply: "[[happy]] desde deepseek");
        var router = new FallbackLlmProvider([working, failing], Options(o => { }), NullLogger<FallbackLlmProvider>.Instance);

        var res = await router.ChatAsync("p", [ChatMessage.User("hola")], [], default);

        Assert.Equal("deepseek", res.Provider);
        Assert.Equal(1, failing.Calls); // se intentó primero gemini (por el orden configurado)
    }

    [Fact]
    public async Task Fallback_sin_claves_lanza_NoLlmConfigured()
    {
        var router = new FallbackLlmProvider([new StubProvider("gemini", configured: false)], Options(o => { }),
            NullLogger<FallbackLlmProvider>.Instance);
        await Assert.ThrowsAsync<NoLlmConfiguredException>(() => router.ChatAsync("p", [], [], default));
    }

    // ---------------------------------------------------------------- utilidades

    internal static IOptionsMonitor<LlmOptions> Options(Action<LlmOptions> configure)
    {
        var o = new LlmOptions();
        configure(o);
        return new StaticMonitor<LlmOptions>(o);
    }

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    internal sealed class FakeHandler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(string Url, string Body, Dictionary<string, string> Headers)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), body, headers));
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}

internal sealed class StubProvider(string name, bool configured, bool fail = false, string reply = "[[happy]] Hola") : ILlmProvider
{
    public int Calls { get; private set; }
    public string Name => name;
    public bool IsConfigured => configured;
    public bool SupportsAudio => false;
    public bool SupportsVision => false;

    public Task<LlmResponse> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        Calls++;
        if (fail) throw new LlmProviderException(name, "falla simulada", 503);
        var msg = new ChatMessage { Role = ChatRole.Assistant, Text = reply };
        return Task.FromResult(new LlmResponse(reply, [], msg, name));
    }
}
