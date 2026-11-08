using System.Net;
using System.Text.Json;
using Kroko.Brain.Agent;
using Kroko.Brain.Email;
using Kroko.Brain.Mcp;
using Kroko.Brain.Providers;
using Kroko.Brain.Web;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kroko.Tests;

public class JarvisToolTests
{
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    // ---------------------------------------------------------------- internet

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("172.20.3.4", false)]
    [InlineData("169.254.169.254", false)] // metadatos de la nube
    [InlineData("::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("142.250.78.14", true)]
    public void Solo_lee_direcciones_publicas(string ip, bool expected) =>
        Assert.Equal(expected, LeerPaginaTool.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public async Task Rechaza_localhost_y_red_local()
    {
        var tool = new LeerPaginaTool(new HttpClient(), new TavilyClient(new HttpClient(), new StaticMonitor<WebOptions>(new WebOptions()), NullLogger<TavilyClient>.Instance), NullLogger<LeerPaginaTool>.Instance);
        foreach (var url in new[] { "http://localhost:5080/api/memoria", "http://192.168.1.1/admin", "http://127.0.0.1" })
        {
            var res = await tool.ExecuteAsync(Args(new { url }), default);
            Assert.Contains("\"ok\":false", res.Json);
        }
    }

    [Fact]
    public void Limpia_el_html_de_una_pagina()
    {
        var html = """
            <html><head><title>Clima &amp; más</title><style>.x{}</style><script>alert(1)</script></head>
            <body><nav>Menú</nav><h1>Urubamba</h1><p>Máxima de 25&nbsp;°C</p><footer>©</footer></body></html>
            """;
        var (title, text) = LeerPaginaTool.ExtractText(html, isHtml: true);
        Assert.Equal("Clima & más", title);
        Assert.Contains("Urubamba", text);
        Assert.Contains("Máxima de 25", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("Menú", text);
    }

    [Fact]
    public void Lee_respuesta_y_fuentes_de_la_busqueda()
    {
        var json = """
            {"candidates":[{"content":{"parts":[{"text":"Hoy en Urubamba: 25 °C."}]},
             "groundingMetadata":{"groundingChunks":[{"web":{"uri":"https://example.com/clima","title":"Meteored"}}]}}]}
            """;
        var res = BuscarEnInternetTool.Parse(json);
        Assert.Contains("25 °C", res.Json);
        Assert.Contains("Meteored", res.Json);
        Assert.Contains("https://example.com/clima", res.Json);
    }

    // ---------------------------------------------------------------- correo

    [Theory]
    [InlineData("INBOX#42", "INBOX", 42u)]
    [InlineData("[Gmail]/Enviados#7", "[Gmail]/Enviados", 7u)]
    [InlineData("Trabajo#Clientes#9", "Trabajo#Clientes", 9u)]
    public void Ids_de_correo(string text, string folder, uint uid)
    {
        Assert.True(MailId.TryParse(text, out var id));
        Assert.Equal(folder, id.Folder);
        Assert.Equal(uid, id.Uid.Id);
        Assert.Equal(text, id.ToString());
    }

    [Theory]
    [InlineData("sin-numero")]
    [InlineData("INBOX#0")]
    [InlineData("#5")]
    public void Ids_invalidos(string text) => Assert.False(MailId.TryParse(text, out _));

    [Fact]
    public void Direcciones_invalidas_se_rechazan() =>
        Assert.Throws<EmailToolException>(() => CorreoEnviarTool.Addresses(["juan arroba gmail"]));

    [Fact]
    public async Task Correo_sin_configurar_explica_que_falta()
    {
        var email = new EmailService(new StaticMonitor<EmailOptions>(new EmailOptions()), TestPaths.Owner(), TimeProvider.System, NullLogger<EmailService>.Instance);
        var res = await new CorreoBuscarTool(email).ExecuteAsync(Args(new { }), default);
        Assert.Contains("\"ok\":false", res.Json);
        Assert.Contains("contraseña de aplicación", res.Json);
    }

    [Fact]
    public void La_tarjeta_de_enviar_muestra_destinatario_asunto_y_texto()
    {
        var tool = new CorreoEnviarTool(new EmailService(new StaticMonitor<EmailOptions>(new EmailOptions()), TestPaths.Owner(), TimeProvider.System, NullLogger<EmailService>.Instance));
        var text = tool.Describe(Args(new { para = new[] { "ana@gmail.com" }, asunto = "Reunión", cuerpo = "Nos vemos el lunes." }));
        Assert.Contains("ana@gmail.com", text);
        Assert.Contains("Reunión", text);
        Assert.Contains("Nos vemos el lunes.", text);
        Assert.Equal(ToolRisk.Dangerous, tool.Definition.Risk);
    }

    // ---------------------------------------------------------------- confirmación de herramientas del Brain

    [Fact]
    public async Task Lo_peligroso_del_Brain_no_se_ejecuta_sin_el_si_de_Pol()
    {
        var auditDir = Path.Combine(Path.GetTempPath(), "kroko-brain-tests", Guid.NewGuid().ToString("N"));
        var audit = new BrainAuditLog(NullLogger<BrainAuditLog>.Instance) { OverrideFolder = auditDir };
        var gateway = new CompositeToolGateway([new ConfirmarAccionTool()], [],
            new AppToolGateway(new StaticMonitor<AgentOptions>(new AgentOptions()), NullLogger<AppToolGateway>.Instance),
            audit, NullLogger<CompositeToolGateway>.Instance);
        var call = new ToolCall("c1", "confirmar_accion", Args(new { accion = "Comprar zapatillas por S/ 200" }));

        var denied = await gateway.ExecuteAsync(call, new AnsweringChannel(approve: false), default);
        Assert.Contains("rechazó", denied.Json);

        var approving = new AnsweringChannel(approve: true);
        var approved = await gateway.ExecuteAsync(call, approving, default);
        Assert.Contains("\"ok\":true", approved.Json);
        Assert.Equal("Comprar zapatillas por S/ 200", approving.LastConfirmText);

        var lines = File.ReadAllLines(Directory.GetFiles(auditDir).Single());
        Assert.Equal(2, lines.Length);
    }

    // ---------------------------------------------------------------- conectores

    [Theory]
    [InlineData("notion-get-users", true)]
    [InlineData("notion-search", true)]
    [InlineData("notion-update-page", false)]
    [InlineData("browser_cookie_set", true)]
    public void Reglas_de_riesgo_con_comodin(string tool, bool matches) =>
        Assert.Equal(matches, McpServerOptions.Matches(["notion-get*", "notion-search", "browser_cookie*"], tool));

    [Fact]
    public void Sin_conectores_no_hay_herramientas_extra()
    {
        var hub = new McpHub(new StaticMonitor<McpOptions>(new McpOptions()), NullLogger<McpHub>.Instance);
        Assert.Empty(hub.CurrentTools);
        Assert.Empty(hub.Status());
    }

    // ---------------------------------------------------------------- utilidades

    private sealed class AnsweringChannel(bool approve) : IAgentChannel
    {
        public string? LastConfirmText { get; private set; }
        public string SessionId => "pruebas";

        public Task SendAsync(KrokoMessage message, CancellationToken ct) => Task.CompletedTask;

        public Task<TResponse?> RequestAsync<TResponse>(KrokoMessage request, string callId, TimeSpan timeout, CancellationToken ct)
            where TResponse : KrokoMessage
        {
            if (request is ConfirmRequestMessage c) LastConfirmText = c.Text;
            KrokoMessage answer = new ConfirmResponseMessage { CallId = callId, Approved = approve };
            return Task.FromResult(answer as TResponse);
        }
    }

    internal sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

public class DotEnvTests
{
    [Fact]
    public void Entiende_clave_suelta_y_nombres_habituales()
    {
        var map = Kroko.Brain.Config.DotEnv.Parse(
        [
            "# comentario",
            "AQ.clave-de-gemini-de-ejemplo",
            "DEEPSEEK_API_KEY=\"sk-ejemplo\"",
            "GMAIL_ADDRESS=pol@gmail.com",
            "Email__AppPassword='abcd efgh'",
        ]);
        Assert.Equal("AQ.clave-de-gemini-de-ejemplo", map["Llm:Gemini:ApiKey"]);
        Assert.Equal("sk-ejemplo", map["Llm:DeepSeek:ApiKey"]);
        Assert.Equal("pol@gmail.com", map["Email:Address"]);
        Assert.Equal("abcd efgh", map["Email:AppPassword"]);
    }
}

public class SpeechTests
{
    [Fact]
    public void Encuentra_el_audio_en_la_respuesta_de_Interactions()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse("""
            {"steps":[{"type":"user_input"},{"type":"model_output","content":[{"type":"text","text":"x"},{"type":"audio","data":"QUJD"}]}]}
            """);
        Assert.Equal("QUJD", Kroko.Brain.Tts.GeminiSpeech.FindAudio(json));
    }

    [Fact]
    public void Pcm_sin_cabecera_se_convierte_en_wav()
    {
        var wav = Kroko.Brain.Tts.GeminiSpeech.WavFromPcm(new byte[4800], 24000);
        Assert.True(Kroko.Brain.Tts.GeminiSpeech.IsWav(wav));
        Assert.Equal(44 + 4800, wav.Length);
    }
}
