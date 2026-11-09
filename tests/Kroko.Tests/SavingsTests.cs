using System.Text.Json;
using Kroko.Brain.Agent;
using Kroko.Brain.Memory;
using Kroko.Brain.Providers;
using Kroko.Brain.Usage;
using Kroko.Shared.Protocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kroko.Tests;

/// <summary>
/// Ahorro de tokens (08/11/2026, Pol gastó sus 26 soles de Gemini en dos días): conectores bajo demanda, instrucciones
/// aptas para la caché, resúmenes por tandas y medidor con tope diario.
/// </summary>
public class SavingsTests
{
    // ------------------------------------------------------------------ conectores bajo demanda

    private sealed class FakeTool(string name, ToolRisk risk, string description = "Hace algo útil en Notion. Detalles largos…") : IBrainTool
    {
        public int Runs;
        public JsonElement LastArgs;
        public ToolDefinition Definition { get; } = BrainToolResult.Define(name, description,
            """{"type":"object","properties":{"query":{"type":"string"}}}""", risk);

        public Task<ToolExecution> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            Runs++;
            LastArgs = args.Clone();
            return Task.FromResult(BrainToolResult.Ok(new { hecho = true }));
        }
    }

    private sealed class FakeSource(params IBrainTool[] tools) : IToolSource
    {
        public IEnumerable<IBrainTool> CurrentTools => tools;
    }

    private sealed class AnsweringChannel(bool approve) : IAgentChannel
    {
        public int Confirmations;
        public string SessionId => "pc";
        public Task SendAsync(KrokoMessage message, CancellationToken ct) => Task.CompletedTask;

        public Task<TResponse?> RequestAsync<TResponse>(KrokoMessage request, string callId, TimeSpan timeout, CancellationToken ct)
            where TResponse : KrokoMessage
        {
            if (request is ConfirmRequestMessage) Confirmations++;
            return Task.FromResult(new ConfirmResponseMessage { CallId = callId, Approved = approve } as TResponse);
        }
    }

    private static (CompositeToolGateway Gateway, FakeTool Search, FakeTool Delete) Gateway(bool onDemand = true)
    {
        var search = new FakeTool("notion_notion-search", ToolRisk.Read);
        var delete = new FakeTool("notion_notion-delete-pages", ToolRisk.Dangerous);
        var many = Enumerable.Range(1, 60).Select(i => (IBrainTool)new FakeTool($"todoist_tool-{i}", ToolRisk.Read, new string('x', 600)));
        var app = new AppToolGateway(new JarvisToolTests.StaticMonitor<AgentOptions>(new AgentOptions()), new ServiceCollection().BuildServiceProvider(),
            NullLogger<AppToolGateway>.Instance);
        var audit = new BrainAuditLog(NullLogger<BrainAuditLog>.Instance)
        {
            OverrideFolder = Path.Combine(Path.GetTempPath(), "kroko-brain-tests", Guid.NewGuid().ToString("N")),
        };
        var gateway = new CompositeToolGateway([], [new FakeSource([search, delete, .. many])], app, audit,
            NullLogger<CompositeToolGateway>.Instance,
            new JarvisToolTests.StaticMonitor<AgentOptions>(new AgentOptions { ConnectorToolsOnDemand = onDemand }));
        return (gateway, search, delete);
    }

    private static int Size(IReadOnlyList<ToolDefinition> tools) =>
        tools.Sum(t => t.Name.Length + t.Description.Length + t.Parameters.GetRawText().Length);

    [Fact]
    public void Las_herramientas_de_los_conectores_ya_no_van_en_cada_llamada()
    {
        var (onDemand, _, _) = Gateway();
        var (all, _, _) = Gateway(onDemand: false);

        Assert.DoesNotContain(onDemand.Tools, t => t.Name.StartsWith("notion_", StringComparison.Ordinal) || t.Name.StartsWith("todoist_", StringComparison.Ordinal));
        Assert.Contains(onDemand.Tools, t => t.Name == CompositeToolGateway.ListTool);
        Assert.Contains(onDemand.Tools, t => t.Name == CompositeToolGateway.UseTool);
        Assert.Contains(all.Tools, t => t.Name == "notion_notion-search");

        // Con 62 herramientas de conectores, lo que se manda en cada llamada baja muchísimo
        var connectorsSaved = Size(all.Tools) - Size(onDemand.Tools);
        Assert.True(connectorsSaved > 35_000, $"Solo se ahorraron {connectorsSaved} caracteres");
        Assert.Equal([("notion", 2), ("todoist", 60)], onDemand.Connectors.OrderBy(c => c.Connector).ToList());
    }

    [Fact]
    public async Task La_IA_ve_la_lista_corta_y_luego_los_argumentos_de_las_que_va_a_usar()
    {
        var (gateway, _, _) = Gateway();
        var list = await gateway.ExecuteAsync(new ToolCall("1", CompositeToolGateway.ListTool,
            JsonSerializer.SerializeToElement(new { conector = "notion" })), new AnsweringChannel(true), default);
        Assert.Contains("notion-search", list.Json);
        Assert.DoesNotContain("\"argumentos\"", list.Json); // la lista es corta: solo nombre y una línea

        var detail = await gateway.ExecuteAsync(new ToolCall("2", CompositeToolGateway.ListTool,
            JsonSerializer.SerializeToElement(new { conector = "notion", herramientas = new[] { "notion-search" } })), new AnsweringChannel(true), default);
        Assert.Contains("\"argumentos\"", detail.Json);
        Assert.Contains("query", detail.Json);
        Assert.DoesNotContain("delete-pages", detail.Json);

        var unknown = await gateway.ExecuteAsync(new ToolCall("3", CompositeToolGateway.ListTool,
            JsonSerializer.SerializeToElement(new { conector = "slack" })), new AnsweringChannel(true), default);
        Assert.Contains("\"ok\":false", unknown.Json);
        Assert.Contains("notion", unknown.Json);
    }

    [Fact]
    public async Task Usar_conector_ejecuta_la_herramienta_con_sus_argumentos()
    {
        var (gateway, search, _) = Gateway();
        var result = await gateway.ExecuteAsync(new ToolCall("1", CompositeToolGateway.UseTool,
            JsonSerializer.SerializeToElement(new { conector = "notion", herramienta = "notion-search", argumentos = """{"query":"reunión"}""" })),
            new AnsweringChannel(true), default);

        Assert.Contains("\"ok\":true", result.Json);
        Assert.Equal(1, search.Runs);
        Assert.Equal("reunión", search.LastArgs.GetProperty("query").GetString());
    }

    [Fact]
    public async Task Lo_peligroso_sigue_pidiendo_permiso_aunque_vaya_por_usar_conector()
    {
        var (gateway, _, delete) = Gateway();
        var channel = new AnsweringChannel(approve: false);
        var result = await gateway.ExecuteAsync(new ToolCall("1", CompositeToolGateway.UseTool,
            JsonSerializer.SerializeToElement(new { conector = "notion", herramienta = "notion-delete-pages", argumentos = "{}" })), channel, default);

        Assert.Equal(1, channel.Confirmations);
        Assert.Equal(0, delete.Runs);
        Assert.Contains("\"ok\":false", result.Json);
    }

    // ------------------------------------------------------------------ instrucciones aptas para la caché

    [Fact]
    public void La_hora_va_al_final_para_que_el_inicio_se_repita_igual()
    {
        using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(TestPaths.IsolatedMemory())));
        var prompt = app.Services.GetRequiredService<SystemPrompt>();
        var now = new DateTimeOffset(2026, 11, 8, 18, 0, 0, TimeSpan.FromHours(-5));
        var a = prompt.Build(now);
        var b2 = prompt.Build(now.AddMinutes(7));

        var common = 0;
        while (common < Math.Min(a.Length, b2.Length) && a[common] == b2[common]) common++;
        Assert.True(common > a.Length * 0.95, $"Solo {common} de {a.Length} caracteres se repiten iguales");
        Assert.Contains("Fecha y hora actual", a[(a.Length - 200)..]);
    }

    // ------------------------------------------------------------------ resúmenes por tandas

    private sealed class CountingSummaryLlm : IConfiguredLlm
    {
        public int Calls;
        public IReadOnlyList<string> ConfiguredProviders => ["prueba"];

        public Task<LlmResponse> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var msg = new ChatMessage { Role = ChatRole.Assistant, Text = "resumen" };
            return Task.FromResult(new LlmResponse("resumen", [], msg, "prueba"));
        }
    }

    [Fact]
    public async Task El_historial_se_resume_por_tandas_y_no_en_cada_orden()
    {
        var db = new KrokoDatabase(Options.Create(new MemoryOptions { DatabasePath = TestPaths.IsolatedMemory()["Memory:DatabasePath"] }),
            NullLogger<KrokoDatabase>.Instance);
        var llm = new CountingSummaryLlm();
        var summarizer = new ConversationSummarizer(new ServiceCollection().AddSingleton<IConfiguredLlm>(llm).BuildServiceProvider(), db,
            NullLogger<ConversationSummarizer>.Instance);
        var store = new ConversationStore(db, summarizer, new JarvisToolTests.StaticMonitor<AgentOptions>(new AgentOptions { HistoryMaxMessages = 20 }),
            NullLogger<ConversationStore>.Instance);
        var conv = store.Get("pol");

        // 30 órdenes: antes se resumía casi en cada una (≈20 veces); ahora solo cuando se llena (≈3 veces)
        for (var i = 1; i <= 30; i++)
        {
            conv.Commit([ChatMessage.User($"orden {i}"), new ChatMessage { Role = ChatRole.Assistant, Text = $"respuesta {i}" }]);
            await Task.Delay(5);
        }
        for (var i = 0; i < 50 && Volatile.Read(ref llm.Calls) < 3; i++) await Task.Delay(50);

        Assert.InRange(llm.Calls, 1, 6);
        Assert.True(conv.Snapshot().Count <= 20);
    }

    // ------------------------------------------------------------------ medidor y tope diario

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("Lima", TimeSpan.FromHours(-5), "Lima", "Lima");
    }

    private static UsageTracker Tracker(Clock clock) => new(
        new KrokoDatabase(Options.Create(new MemoryOptions { DatabasePath = TestPaths.IsolatedMemory()["Memory:DatabasePath"] }),
            NullLogger<KrokoDatabase>.Instance),
        clock, NullLogger<UsageTracker>.Instance);

    [Fact]
    public void El_medidor_suma_por_dia_mes_y_tipo()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.FromHours(-5)));
        var usage = Tracker(clock);
        usage.Record(new LlmUsage(40_000, 500, 30_000));
        using (UsageTracker.Scope("resumen")) usage.Record(new LlmUsage(2_000, 300));
        usage.Record(new LlmUsage(10_000, 4_000), "tiempo_real");

        Assert.Equal(56_800, usage.Today.Total);
        Assert.Equal(3, usage.Today.Calls);
        Assert.Equal(30_000, usage.Today.Cached);
        Assert.Equal(40_500, usage.Today.ByKind["chat"]);
        Assert.Equal(2_300, usage.Today.ByKind["resumen"]);
        Assert.Equal(14_000, usage.Today.ByKind["tiempo_real"]);

        clock.Now = clock.Now.AddDays(1); // otro día: el día empieza en cero, el mes sigue sumando
        usage.Record(new LlmUsage(1_000, 0));
        Assert.Equal(1_000, usage.Today.Total);
        Assert.Equal(57_800, usage.ThisMonth.Total);
    }

    [Fact]
    public void Al_pasar_el_tope_entra_en_modo_ahorro_y_avisa_una_sola_vez()
    {
        var usage = Tracker(new Clock(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.FromHours(-5))));
        Assert.False(usage.IsOverDailyLimit); // sin tope
        usage.DailyLimit = 50_000;
        usage.Record(new LlmUsage(30_000, 0));
        Assert.False(usage.IsOverDailyLimit);
        Assert.False(usage.TakeLimitNotice());

        usage.Record(new LlmUsage(25_000, 0));
        Assert.True(usage.IsOverDailyLimit);
        Assert.True(usage.TakeLimitNotice());
        Assert.False(usage.TakeLimitNotice()); // una sola vez al día
    }

    [Fact]
    public void Lee_el_uso_que_informan_Gemini_y_DeepSeek()
    {
        var gemini = UsageTracker.FromGemini(JsonDocument.Parse(
            """{"usageMetadata":{"promptTokenCount":48000,"candidatesTokenCount":120,"thoughtsTokenCount":30,"cachedContentTokenCount":40000}}""").RootElement);
        Assert.Equal(new LlmUsage(48_000, 150, 40_000), gemini);

        var live = UsageTracker.FromGemini(JsonDocument.Parse("""{"usageMetadata":{"promptTokenCount":900,"responseTokenCount":300}}""").RootElement);
        Assert.Equal(new LlmUsage(900, 300), live);

        var deepseek = UsageTracker.FromOpenAi(JsonDocument.Parse(
            """{"usage":{"prompt_tokens":5000,"completion_tokens":200,"prompt_cache_hit_tokens":4000}}""").RootElement);
        Assert.Equal(new LlmUsage(5_000, 200, 4_000), deepseek);

        Assert.Null(UsageTracker.FromGemini(JsonDocument.Parse("{}").RootElement));
    }
}
