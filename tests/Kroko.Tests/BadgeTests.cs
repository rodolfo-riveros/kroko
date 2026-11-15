using System.Text.Json;
using Kroko.Brain.Agent;
using Kroko.Brain.Providers;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kroko.Tests;

/// <summary>Check verde con una palabra junto a Kroko tras cada acción (14/11/2026).</summary>
public class BadgeTests
{
    [Theory]
    [InlineData("tarea_crear", "Anotado")]
    [InlineData("google_calendario_crear", "Agendado")]
    [InlineData("correo_enviar", "Enviado")]
    [InlineData("todoist_add-tasks", "Anotado")]
    [InlineData("notion_notion-create-pages", "Guardado")]
    [InlineData("estado_equipo", null)] // una consulta no lleva check
    [InlineData("buscar_en_internet", null)]
    public void Cada_accion_tiene_su_palabra(string tool, string? word) => Assert.Equal(word, Badges.For(tool));

    private sealed class Tool(string name, bool ok) : IBrainTool
    {
        public ToolDefinition Definition { get; } = BrainToolResult.Define(name, "prueba", """{"type":"object","properties":{}}""");
        public Task<ToolExecution> ExecuteAsync(JsonElement args, CancellationToken ct) =>
            Task.FromResult(ok ? BrainToolResult.Ok(new { hecho = true }) : BrainToolResult.Fail("no se pudo"));
    }

    private sealed class Channel : IAgentChannel
    {
        public List<KrokoMessage> Sent { get; } = [];
        public string SessionId => "pc";
        public Task SendAsync(KrokoMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
        public Task<TResponse?> RequestAsync<TResponse>(KrokoMessage request, string callId, TimeSpan timeout, CancellationToken ct)
            where TResponse : KrokoMessage => Task.FromResult<TResponse?>(null);
    }

    private static CompositeToolGateway Gateway(params IBrainTool[] tools) => new(tools, [],
        new AppToolGateway(new JarvisToolTests.StaticMonitor<AgentOptions>(new AgentOptions()), new ServiceCollection().BuildServiceProvider(),
            NullLogger<AppToolGateway>.Instance),
        new BrainAuditLog(NullLogger<BrainAuditLog>.Instance) { OverrideFolder = Path.Combine(Path.GetTempPath(), "kroko-brain-tests", Guid.NewGuid().ToString("N")) },
        NullLogger<CompositeToolGateway>.Instance);

    [Fact]
    public async Task Solo_sale_el_check_si_la_accion_salio_bien()
    {
        var channel = new Channel();
        var gateway = Gateway(new Tool("tarea_crear", ok: true), new Tool("recordatorio_crear", ok: false));
        var empty = JsonDocument.Parse("{}").RootElement;

        await gateway.ExecuteAsync(new ToolCall("1", "tarea_crear", empty), channel, default);
        await gateway.ExecuteAsync(new ToolCall("2", "recordatorio_crear", empty), channel, default);

        var badge = Assert.Single(channel.Sent.OfType<BadgeMessage>());
        Assert.Equal("Anotado", badge.Text);
    }
}
