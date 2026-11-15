using System.Net.WebSockets;
using Kroko.Brain.Config;
using Kroko.Brain.Providers;
using Kroko.Shared.Protocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kroko.Tests;

/// <summary>
/// «El cerebro no puede tener la API de Gemini» (Pol, 15/11/2026): las claves viven en la PC, la App las presta al
/// conectarse, el cerebro las tiene solo en memoria y las olvida cuando la App se va.
/// </summary>
[Collection("ClavesIA")]
public class AppProvidedKeysTests
{
    private const string Token = "token-de-prueba";

    [Fact]
    public void El_cerebro_borra_las_claves_de_IA_que_tenia_guardadas_y_no_acepta_nuevas()
    {
        var path = Path.Combine(Path.GetTempPath(), "kroko-brain-tests", Guid.NewGuid().ToString("N"), "secretos.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var old = new SecretStore(path);
        old.Update(new Dictionary<string, string?> { ["Llm:Gemini:ApiKey"] = "AQ.vieja", ["Web:TavilyApiKey"] = "tvly-x" }, @internal: true);

        var reopened = new SecretStore(path); // al arrancar
        Assert.False(reopened.Has("Llm:Gemini:ApiKey"));
        Assert.True(reopened.Has("Web:TavilyApiKey")); // lo demás se queda

        Assert.Throws<ArgumentException>(() =>
            reopened.Update(new Dictionary<string, string?> { ["Llm:Gemini:ApiKey"] = "AQ.nueva" }));
        Assert.Throws<ArgumentException>(() =>
            reopened.Update(new Dictionary<string, string?> { ["Llm:DeepSeek:ApiKey"] = "sk-x" }));
    }

    [Fact]
    public void Aunque_haya_una_clave_en_la_configuracion_el_cerebro_no_la_usa()
    {
        AppProvidedKeys.Clear();
        var g = new GeminiOptions { ApiKey = "AQ.en-el-servidor" };
        Assert.Empty(g.AllApiKeys());
        Assert.Null(g.ResolveApiKey());
        Assert.Null(new DeepSeekOptions { ApiKey = "sk-en-el-servidor" }.ResolveApiKey());
    }

    [Fact]
    public async Task La_App_presta_sus_claves_al_conectarse_y_el_cerebro_las_olvida_al_irse()
    {
        AppProvidedKeys.Clear();
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>(TestPaths.IsolatedMemory())
            {
                ["Agent:Token"] = Token,
            })));
        var llm = app.Services.GetRequiredService<IOptionsMonitor<LlmOptions>>();
        var client = app.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers[KrokoProtocol.TokenHeader] = Token;
        var socket = await client.ConnectAsync(new Uri(app.Server.BaseAddress, KrokoProtocol.WebSocketPath), default);
        var t = new WebSocketTransport(socket);

        await t.SendAsync(new CredentialsMessage { SessionId = "s", Gemini = ["AQ.una", "AQ.dos"], DeepSeek = "sk-tres" }, default);
        await WaitUntil(() => llm.CurrentValue.Gemini.AllApiKeys().Count == 2);
        Assert.Equal(["AQ.una", "AQ.dos"], llm.CurrentValue.Gemini.AllApiKeys());
        Assert.Equal("sk-tres", llm.CurrentValue.DeepSeek.ResolveApiKey());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fin", default);
        await WaitUntil(() => llm.CurrentValue.Gemini.AllApiKeys().Count == 0);
        Assert.Null(llm.CurrentValue.DeepSeek.ResolveApiKey());
    }

    [Fact]
    public void Las_claves_nunca_salen_en_los_registros()
    {
        var text = new CredentialsMessage { Gemini = ["AQ.secreta"], DeepSeek = "sk-secreta" }.ToString();
        Assert.DoesNotContain("secreta", text);
        Assert.Contains("gemini: 1", text);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }
}
