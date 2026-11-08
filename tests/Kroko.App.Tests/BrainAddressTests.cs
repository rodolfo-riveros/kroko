using System.Net;
using System.Net.Http;
using Kroko.App.Services;
using Kroko.Shared.Protocol;

namespace Kroko.App.Tests;

public class BrainAddressTests
{
    [Theory]
    [InlineData("kroko.midominio.com", "https://kroko.midominio.com")]
    [InlineData("https://kroko.midominio.com/", "https://kroko.midominio.com")]
    [InlineData("  https://Kroko.MiDominio.com/ws  ", "https://kroko.midominio.com")]
    [InlineData("wss://kroko.midominio.com/ws", "https://kroko.midominio.com")]
    [InlineData("https://kroko.midominio.com:8443/health", "https://kroko.midominio.com:8443")]
    [InlineData("localhost:5080", "http://localhost:5080")]
    [InlineData("http://127.0.0.1:5080", "http://127.0.0.1:5080")]
    [InlineData("ws://localhost:5080/ws", "http://localhost:5080")]
    public void Acepta_el_dominio_como_lo_escribas(string typed, string expected)
    {
        var r = BrainAddress.Normalize(typed);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(expected, r.Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://")]
    [InlineData("http://kroko.midominio.com")] // en internet tiene que ser https
    [InlineData("no es una dirección")]
    [InlineData("ftp://kroko.midominio.com")]
    public void Rechaza_lo_que_no_sirve_con_un_mensaje_claro(string typed)
    {
        var r = BrainAddress.Normalize(typed);
        Assert.False(r.Ok);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void Sabe_si_el_cerebro_es_de_esta_PC()
    {
        Assert.True(BrainAddress.Normalize("localhost:5080").IsLocal);
        Assert.False(BrainAddress.Normalize("kroko.midominio.com").IsLocal);
    }

    /// <summary>Un cerebro simulado: /health/llm abierto y /api/perfil solo con el token correcto.</summary>
    private sealed class FakeBrain(string token, bool up = true) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!up) throw new HttpRequestException("sin conexión");
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/health/llm")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"providers":["gemini"]}""") });
            if (path == "/api/perfil")
            {
                var ok = request.Headers.TryGetValues(KrokoProtocol.TokenHeader, out var v) && v.Single() == token;
                return Task.FromResult(new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task Comprobar_dice_si_esta_bien_si_falla_el_token_o_si_no_hay_cerebro()
    {
        var brain = new FakeBrain("secreto-largo-1234567890");

        var ok = await BrainAddress.ProbeAsync("kroko.midominio.com", "secreto-largo-1234567890", brain);
        Assert.True(ok.Ok);
        Assert.Contains("Conectado a https://kroko.midominio.com", ok.Message);
        Assert.Contains("gemini", ok.Message);

        var badToken = await BrainAddress.ProbeAsync("kroko.midominio.com", "otro", brain);
        Assert.False(badToken.Ok);
        Assert.Contains("token no es correcto", badToken.Message);

        var noToken = await BrainAddress.ProbeAsync("kroko.midominio.com", "", brain);
        Assert.Contains("falta el token", noToken.Message);

        var down = await BrainAddress.ProbeAsync("kroko.midominio.com", "x", new FakeBrain("x", up: false));
        Assert.False(down.Ok);
        Assert.Contains("No encuentro el cerebro", down.Message);
    }
}
