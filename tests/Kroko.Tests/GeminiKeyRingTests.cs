using System.Net;
using System.Text;
using Kroko.Brain.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kroko.Tests;

/// <summary>De 2 a 4 claves de Gemini que se turnan: si una se agota, sigue la otra sin que el dueño lo note (14/11/2026).</summary>
[Collection("GeminiKeyRing")]
public class GeminiKeyRingTests
{
    public GeminiKeyRingTests()
    {
        GeminiKeyRing.Reset();
        GeminiKeyRing.Now = () => DateTimeOffset.UtcNow;
    }

    [Fact]
    public void Una_clave_sin_saldo_descansa_12_horas_y_sin_cuota_por_minuto_2_minutos()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        GeminiKeyRing.Now = () => now;
        string[] keys = ["a", "b", "c"];

        Assert.Equal("a", GeminiKeyRing.Pick(keys));
        Assert.True(GeminiKeyRing.Report("a", 402, "Your prepayment credits are depleted"));
        Assert.Equal("b", GeminiKeyRing.Pick(keys));
        Assert.True(GeminiKeyRing.Report("b", 429, "Resource exhausted, retry in 30s"));
        Assert.Equal("c", GeminiKeyRing.Pick(keys));
        Assert.Equal(1, GeminiKeyRing.Available(keys));

        now = now.AddMinutes(3); // la del límite por minuto ya volvió; la sin saldo, no
        Assert.Equal("b", GeminiKeyRing.Pick(keys));
        now = now.AddHours(13);
        Assert.Equal("a", GeminiKeyRing.Pick(keys));

        Assert.False(GeminiKeyRing.Report("a", 500)); // un error del servidor no es culpa de la clave
    }

    private sealed class FakeGemini(params HttpStatusCode[] answersByKey) : HttpMessageHandler
    {
        public readonly List<string> KeysUsed = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = request.Headers.GetValues("x-goog-api-key").Single();
            KeysUsed.Add(key);
            var status = answersByKey[int.Parse(key[^1..]) - 1];
            var body = status == HttpStatusCode.OK
                ? """{"candidates":[{"content":{"parts":[{"text":"[[happy]] Hola"}]}}]}"""
                : """{"error":{"code":402,"message":"Your prepayment credits are depleted"}}""";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Si_la_primera_clave_se_agota_responde_con_la_siguiente_en_el_acto()
    {
        var gemini = new FakeGemini(HttpStatusCode.PaymentRequired, HttpStatusCode.OK);
        var opt = new LlmOptions { Gemini = new GeminiOptions { KeysFromAppOnly = false, ApiKey = "clave-1", ApiKey2 = "clave-2", BaseUrl = "https://gemini.test" } };
        var provider = new GeminiProvider(new HttpClient(gemini), new JarvisToolTests.StaticMonitor<LlmOptions>(opt),
            NullLogger<GeminiProvider>.Instance);

        var reply = await provider.ChatAsync("sistema", [ChatMessage.User("hola")], [], default);

        Assert.Equal("[[happy]] Hola", reply.Text);
        Assert.Equal(["clave-1", "clave-2"], gemini.KeysUsed);

        // La siguiente orden ya va directo con la que funciona
        gemini.KeysUsed.Clear();
        await provider.ChatAsync("sistema", [ChatMessage.User("otra")], [], default);
        Assert.Equal(["clave-2"], gemini.KeysUsed);
    }

    [Fact]
    public async Task Si_todas_se_agotan_avisa_que_se_acabo_el_saldo()
    {
        var gemini = new FakeGemini(HttpStatusCode.PaymentRequired, HttpStatusCode.PaymentRequired);
        var opt = new LlmOptions { Gemini = new GeminiOptions { KeysFromAppOnly = false, ApiKey = "clave-1", ApiKey2 = "clave-2", BaseUrl = "https://gemini.test" } };
        var provider = new GeminiProvider(new HttpClient(gemini), new JarvisToolTests.StaticMonitor<LlmOptions>(opt),
            NullLogger<GeminiProvider>.Instance);

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => provider.ChatAsync("s", [ChatMessage.User("hola")], [], default));
        Assert.Equal(402, ex.StatusCode);
        Assert.Contains("saldo", ex.OwnerMessage);
        Assert.Equal(2, gemini.KeysUsed.Count);
    }

    [Fact]
    public void Las_claves_repetidas_o_vacias_no_cuentan()
    {
        var g = new GeminiOptions { KeysFromAppOnly = false, ApiKey = "x", ApiKey2 = " x ", ApiKey3 = "", ApiKey4 = "y" };
        Assert.Equal(["x", "y"], g.AllApiKeys());
    }
}
