using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Kroko.Shared.Tools;

namespace Kroko.Tests;

/// <summary>«Cállate» / «descansa»: Kroko se calla, se esconde y deja de escuchar (14/11/2026).</summary>
public class QuietCommandTests
{
    [Theory]
    [InlineData("cállate")]
    [InlineData("¡Cállate!")]
    [InlineData("ya, cállate por favor")]
    [InlineData("Kroko, silencio")]
    [InlineData("croco cállate")]
    [InlineData("oye cro co descansa")]
    [InlineData("descansa")]
    [InlineData("basta ya")]
    [InlineData("a dormir")]
    public void Calla_a_Kroko(string said) => Assert.True(QuietCommand.Matches(said));

    [Theory]
    [InlineData("no te calles")]
    [InlineData("cállate cuando termines de leer el correo")]
    [InlineData("dime cómo se dice silencio en inglés")]
    [InlineData("abre Excel")]
    [InlineData("")]
    [InlineData(null)]
    public void No_confunde_otras_frases(string? said) => Assert.False(QuietCommand.Matches(said));
}

public class QuietCommandBrainTests
{
    [Fact]
    public async Task El_cerebro_no_le_pregunta_a_la_IA_por_un_callate()
    {
        var llm = new StubProvider("gemini", configured: true);
        await using var app = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(TestPaths.IsolatedMemory()));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<Kroko.Brain.Providers.ILlmProvider>();
                s.AddSingleton<Kroko.Brain.Providers.ILlmProvider>(llm);
            });
        });
        using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(app.Services);
        var agent = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Kroko.Brain.Agent.AgentLoop>(scope.ServiceProvider);
        var channel = new QuietChannel();
        await agent.HandleUserTextAsync("cállate", channel, default);

        Assert.Equal(0, llm.Calls);
        Assert.DoesNotContain(channel.Sent, m => m is Kroko.Shared.Protocol.ReplyMessage);
    }

    private sealed class QuietChannel : Kroko.Brain.Agent.IAgentChannel
    {
        public List<Kroko.Shared.Protocol.KrokoMessage> Sent { get; } = [];
        public string SessionId => "pc";
        public Task SendAsync(Kroko.Shared.Protocol.KrokoMessage message, CancellationToken ct) { Sent.Add(message); return Task.CompletedTask; }
        public Task<TResponse?> RequestAsync<TResponse>(Kroko.Shared.Protocol.KrokoMessage request, string callId, TimeSpan timeout, CancellationToken ct)
            where TResponse : Kroko.Shared.Protocol.KrokoMessage => Task.FromResult<TResponse?>(null);
    }
}

public class AllFacesTests
{
    [Fact]
    public void Kroko_puede_usar_todas_sus_caras_y_existen_en_la_App()
    {
        var json = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src", "Kroko.App", "Face", "expressions.json"));
        foreach (var key in Kroko.Shared.Protocol.Reactions.All.Keys)
            Assert.Contains($"\"key\": \"{key}\"", json);
        Assert.True(Kroko.Shared.Protocol.Reactions.All.Count >= 27);
    }

    [Theory]
    [InlineData("¡Listo! Ya anoté tu tarea.", "success")]
    [InlineData("Jaja, buena esa.", "laugh")]
    [InlineData("Lo siento, no pude abrir el archivo.", "sad")]
    [InlineData("De nada, para eso estoy.", "love")]
    [InlineData("Son las 3 de la tarde.", null)]
    public void En_voz_en_tiempo_real_la_cara_sale_de_lo_que_dice(string said, string? face) =>
        Assert.Equal(face, Kroko.Shared.Protocol.Reactions.Guess(said));
}
