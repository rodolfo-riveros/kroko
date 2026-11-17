using Kroko.Brain.Agent;
using Kroko.Brain.Providers;
using Kroko.Shared.Protocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kroko.Tests;

/// <summary>
/// Cuando se acaba el saldo de Gemini (402) Kroko lo dice claro, en vez de "no te entendí" con la voz de Windows
/// (pasó el 08/11/2026: «Your prepayment credits are depleted»).
/// </summary>
public class OutOfCreditTests
{
    private sealed class BrokeProvider(int status) : ILlmProvider
    {
        public string Name => "gemini";
        public bool IsConfigured => true;
        public bool SupportsAudio => true;
        public bool SupportsVision => true;

        public Task<LlmResponse> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history,
            IReadOnlyList<ToolDefinition> tools, CancellationToken ct) =>
            throw new LlmProviderException("gemini", "Your prepayment credits are depleted", status);
    }

    private sealed class Channel : IAgentChannel
    {
        public List<KrokoMessage> Sent { get; } = [];
        public string SessionId => "pruebas-saldo";

        public Task SendAsync(KrokoMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task<TResponse?> RequestAsync<TResponse>(KrokoMessage request, string callId, TimeSpan timeout, CancellationToken ct)
            where TResponse : KrokoMessage => Task.FromResult<TResponse?>(null);
    }

    [Theory]
    [InlineData(402, "Se acabó el saldo de tu cuenta de Gemini (Google)")]
    [InlineData(429, "llegaste al límite de uso")]
    public async Task Explica_que_el_problema_es_la_cuenta_de_Gemini(int status, string expected)
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(TestPaths.IsolatedMemory()));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ILlmProvider>();
                s.AddSingleton<ILlmProvider>(new BrokeProvider(status));
            });
        });
        using var scope = app.Services.CreateScope();
        var channel = new Channel();
        await scope.ServiceProvider.GetRequiredService<AgentLoop>().HandleUserTextAsync("hola", channel, default);

        var reply = Assert.Single(channel.Sent.OfType<ReplyMessage>());
        Assert.Contains(expected, reply.Text);
        Assert.True(reply.IsError);
    }

    [Fact]
    public void Cada_error_explica_su_motivo()
    {
        Assert.Contains("saturado", new LlmProviderException("gemini", "falla", 503).OwnerMessage);
        Assert.Contains("no acepta tus claves", new LlmProviderException("gemini", "API key not valid", 400).OwnerMessage);
        Assert.Null(new LlmProviderException("gemini", "falla rara", 418).OwnerMessage); // lo desconocido: mensaje general
    }
}
