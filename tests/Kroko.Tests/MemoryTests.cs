using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kroko.Brain.Agent;
using Kroko.Brain.Memory;
using Kroko.Brain.Providers;
using Kroko.Shared.Protocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kroko.Tests;

public class MemoryTests
{
    private static (KrokoDatabase Db, MemoryOptions Options) NewDb()
    {
        var paths = TestPaths.IsolatedMemory();
        var opt = new MemoryOptions { DatabasePath = paths["Memory:DatabasePath"], SuggestionsFile = paths["Memory:SuggestionsFile"] };
        return (new KrokoDatabase(Options.Create(opt), NullLogger<KrokoDatabase>.Instance), opt);
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void Recordar_no_duplica_y_se_puede_corregir_y_olvidar()
    {
        var (db, _) = NewDb();
        var memory = new MemoryStore(db);

        var id = memory.Remember("Su navegador favorito es Brave", "preferencia");
        Assert.Equal(id, memory.Remember("su navegador favorito es brave", "preferencia")); // mismo recuerdo
        Assert.Single(memory.All());

        Assert.True(memory.Update(id, "Su navegador favorito ahora es Firefox"));
        Assert.Contains("Firefox", memory.All()[0].Content);

        Assert.True(memory.Forget(id));
        Assert.Empty(memory.All());
    }

    [Theory]
    [InlineData("Mi contraseña del correo es gatito123")]
    [InlineData("La clave del banco es 4455")]
    [InlineData("Mi tarjeta es 4111 1111 1111 1111")]
    [InlineData("El PIN de la tarjeta es 9921")]
    public void Nunca_guarda_contrasenas_ni_datos_bancarios(string content)
    {
        var (db, _) = NewDb();
        var res = new RecordarTool(new MemoryStore(db)).Execute(Args(new { contenido = content }));
        Assert.Contains("\"ok\":false", res.Json);
        Assert.Empty(new MemoryStore(db).All());
    }

    [Fact]
    public void Rutinas_se_crean_reemplazan_y_borran()
    {
        var (db, _) = NewDb();
        var routines = new RoutineStore(db);
        var tool = new CrearRutinaTool(routines);

        tool.Execute(Args(new { nombre = "modo estudio", frases = new[] { "modo estudio", "a estudiar" }, pasos = new[] { "abrir Notion", "silenciar" } }));
        tool.Execute(Args(new { nombre = "Modo Estudio", pasos = new[] { "abrir Notion", "silenciar", "volumen al 0" } }));

        var r = Assert.Single(routines.All());
        Assert.Equal(3, r.Steps.Count); // se reemplazó (el nombre no distingue mayúsculas)
        Assert.True(routines.Delete("modo estudio"));
        Assert.Empty(routines.All());
    }

    [Fact]
    public void Buzon_cuenta_repeticiones_y_genera_markdown()
    {
        var (db, opt) = NewDb();
        var box = new SuggestionStore(db, Options.Create(opt), NullLogger<SuggestionStore>.Instance);

        box.Add("idea", "Leer mis correos de Gmail", "Pol pidió: ¿tengo correos nuevos?");
        box.Add("idea", "Leer mis correos de Gmail", "Pol pidió: léeme el último correo");

        var s = Assert.Single(box.All());
        Assert.Equal(2, s.Times);
        var md = File.ReadAllText(opt.SuggestionsFile!);
        Assert.Contains("Leer mis correos de Gmail", md);
        Assert.Contains("2 vez/veces", md);
    }

    [Fact]
    public async Task El_historial_sobrevive_un_reinicio_y_lo_antiguo_se_resume()
    {
        var (db, _) = NewDb();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguredLlm>(new SummaryLlm())
            .BuildServiceProvider();
        var summarizer = new ConversationSummarizer(services, db, NullLogger<ConversationSummarizer>.Instance);
        var agentOptions = new StaticMonitor<AgentOptions>(new AgentOptions { HistoryMaxMessages = 4 });

        var store = new ConversationStore(db, summarizer, agentOptions, NullLogger<ConversationStore>.Instance);
        var conv = store.Get("pol");
        for (var i = 1; i <= 4; i++)
            conv.Commit([ChatMessage.User($"mensaje {i}"), new ChatMessage { Role = ChatRole.Assistant, Text = $"[[happy]] respuesta {i}" }]);

        // "Reinicio": un almacén nuevo lee lo mismo de la base de datos
        var reloaded = new ConversationStore(db, summarizer, agentOptions, NullLogger<ConversationStore>.Instance).Get("pol");
        var snapshot = reloaded.Snapshot();
        Assert.Equal(4, snapshot.Count);
        Assert.Equal("mensaje 3", snapshot[0].Text); // lo más antiguo se recortó

        // ...y se resumió en segundo plano
        for (var i = 0; i < 50 && reloaded.Summary is null; i++) await Task.Delay(100);
        Assert.Equal("Pol saludó varias veces.", reloaded.Summary);

        // "Olvidar la conversación": el historial y el resumen desaparecen
        var fresh = new ConversationStore(db, summarizer, agentOptions, NullLogger<ConversationStore>.Instance);
        fresh.ClearAll();
        var afterClear = fresh.Get("pol");
        Assert.Empty(afterClear.Snapshot());
        Assert.Null(afterClear.Summary);
    }

    [Fact]
    public async Task Lo_que_recuerda_aparece_en_la_siguiente_orden_y_se_puede_borrar_desde_la_App()
    {
        var ia = new LearningLlm();
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(TestPaths.IsolatedMemory()));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ILlmProvider>();
                s.AddSingleton<ILlmProvider>(ia);
            });
        });

        // Sin token configurado, el WebSocket de pruebas cuenta como "esta computadora" solo si es loopback;
        // por eso usamos la API con el cliente en memoria y el agente directamente.
        using var scope = app.Services.CreateScope();
        var agent = scope.ServiceProvider.GetRequiredService<AgentLoop>();
        var channel = new NullChannel();

        await agent.HandleUserTextAsync("mi navegador favorito es Brave", channel, default);
        await agent.HandleUserTextAsync("¿cuál es mi navegador favorito?", channel, default);

        Assert.Contains("Su navegador favorito es Brave", ia.LastSystemPrompt);
        Assert.Contains("happy", channel.Sent.OfType<ExpressionMessage>().Select(e => e.Name));

        var memory = scope.ServiceProvider.GetRequiredService<MemoryStore>();
        var id = Assert.Single(memory.All()).Id;
        Assert.True(memory.Forget(id));
    }

    [Fact]
    public async Task La_api_de_memoria_rechaza_peticiones_sin_token()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?>(TestPaths.IsolatedMemory()) { ["Agent:Token"] = "secreto" })));

        var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/memoria")).StatusCode);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(KrokoProtocol.TokenHeader, "secreto");
        var json = await client.GetFromJsonAsync<JsonElement>("/api/memoria");
        Assert.Equal(JsonValueKind.Array, json.GetProperty("recuerdos").ValueKind);
    }

    // ---------------------------------------------------------------- dobles de prueba

    private sealed class SummaryLlm : IConfiguredLlm
    {
        public IReadOnlyList<string> ConfiguredProviders => ["fake"];

        public Task<LlmResponse> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history,
            IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            Assert.Contains("mensaje 1", history[0].Text);
            var msg = new ChatMessage { Role = ChatRole.Assistant, Text = "Pol saludó varias veces." };
            return Task.FromResult(new LlmResponse(msg.Text, [], msg, "fake"));
        }
    }

    /// <summary>1.ª orden: llama a "recordar". 2.ª orden: comprueba que el recuerdo está en el prompt.</summary>
    private sealed class LearningLlm : ILlmProvider
    {
        private int _calls;
        public string LastSystemPrompt { get; private set; } = "";
        public string Name => "gemini";
        public bool IsConfigured => true;
        public bool SupportsAudio => false;
        public bool SupportsVision => false;

        public Task<LlmResponse> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history,
            IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            LastSystemPrompt = systemPrompt;
            _calls++;
            if (_calls == 1)
            {
                Assert.Contains(tools, t => t.Name == "recordar");
                var call = new ToolCall("r1", "recordar", JsonSerializer.SerializeToElement(new { contenido = "Su navegador favorito es Brave", categoria = "preferencia" }));
                var m = new ChatMessage { Role = ChatRole.Assistant, ToolCalls = [call] };
                return Task.FromResult(new LlmResponse("", [call], m, Name));
            }
            var text = _calls == 2 ? "[[wink]] Anotado." : "[[happy]] Es Brave.";
            var msg = new ChatMessage { Role = ChatRole.Assistant, Text = text };
            return Task.FromResult(new LlmResponse(text, [], msg, Name));
        }
    }

    private sealed class NullChannel : IAgentChannel
    {
        public List<KrokoMessage> Sent { get; } = [];
        public string SessionId => "pruebas";

        public Task SendAsync(KrokoMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task<TResponse?> RequestAsync<TResponse>(KrokoMessage request, string callId, TimeSpan timeout, CancellationToken ct)
            where TResponse : KrokoMessage => Task.FromResult<TResponse?>(null);
    }

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
