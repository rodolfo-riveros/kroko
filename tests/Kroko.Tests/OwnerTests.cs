using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kroko.Brain.Agent;
using Kroko.Brain.Config;
using Kroko.Brain.Memory;
using Kroko.Brain.Owner;
using Kroko.Brain.Productivity;
using Kroko.Brain.Telegram;
using Kroko.Shared.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kroko.Tests;

/// <summary>Kroko se adapta a su dueño: perfil, zona horaria, claves cifradas y vinculación de Telegram.</summary>
public class OwnerTests
{
    private static OwnerProfileStore NewProfileStore()
    {
        var paths = TestPaths.IsolatedMemory();
        var db = new KrokoDatabase(Options.Create(new MemoryOptions { DatabasePath = paths["Memory:DatabasePath"] }),
            NullLogger<KrokoDatabase>.Instance);
        return new OwnerProfileStore(db, NullLogger<OwnerProfileStore>.Instance);
    }

    [Fact]
    public void El_perfil_se_guarda_y_decide_como_llamar_al_dueno()
    {
        var store = NewProfileStore();
        Assert.False(store.Get().IsComplete);

        var saved = store.Save(new OwnerProfile { Name = "  Ana María Quispe ", City = "Cusco", Country = "Perú", TimeZone = "America/Lima" });
        Assert.Equal("Ana María Quispe", saved.Name);
        Assert.Equal("Ana", saved.Nickname);
        Assert.Equal("Cusco, Perú", saved.Location);
        Assert.Equal("latinoamericano neutro", saved.AccentOrDefault);

        saved = store.Save(saved with { CallMe = "Anita" });
        Assert.Equal("Anita", store.Get().Nickname);
    }

    [Fact]
    public void Una_zona_horaria_inventada_se_rechaza()
    {
        var store = NewProfileStore();
        Assert.Throws<ArgumentException>(() => store.Save(new OwnerProfile { Name = "Ana", TimeZone = "Marte/Olympus" }));
    }

    [Fact]
    public void La_hora_de_Kroko_es_la_del_dueno_aunque_el_servidor_este_en_otra_zona()
    {
        var store = NewProfileStore();
        var time = new OwnerTimeProvider(store);
        store.Save(new OwnerProfile { Name = "Ana", TimeZone = "Asia/Tokyo" });

        var now = time.GetLocalNow();
        Assert.Equal(TimeSpan.FromHours(9), now.Offset);

        store.Save(new OwnerProfile { Name = "Ana", TimeZone = "America/Lima" });
        Assert.Equal(TimeSpan.FromHours(-5), time.GetLocalNow().Offset);
    }

    [Fact]
    public void El_prompt_usa_el_nombre_y_la_ciudad_del_perfil()
    {
        const string personality = "Vives en la computadora de {nombre}{ubicacion}. Hablas español {acento}.";
        var p = new OwnerProfile { Name = "Rodolfo Riveros", City = "Urubamba", Country = "Perú", Accent = "peruano" };
        Assert.Equal("Vives en la computadora de Rodolfo, en Urubamba, Perú. Hablas español peruano.", SystemPrompt.Personalize(personality, p));

        var empty = SystemPrompt.Personalize(personality, new OwnerProfile());
        Assert.Equal("Vives en la computadora de tu dueño. Hablas español latinoamericano neutro.", empty);
    }

    [Fact]
    public void Kroko_actualiza_el_perfil_conversando()
    {
        var store = NewProfileStore();
        store.Save(new OwnerProfile { Name = "Rodolfo" });
        var tool = new PerfilActualizarTool(store);

        var ok = tool.Execute(JsonDocument.Parse("""{"como_llamarte":"Rodo","ciudad":"Cusco","zona_horaria":"America/Lima"}""").RootElement);
        Assert.Contains("\"ok\":true", ok.Json);
        Assert.Equal("Rodo", store.Get().Nickname);
        Assert.Equal("Cusco", store.Get().City);

        var bad = tool.Execute(JsonDocument.Parse("""{"zona_horaria":"Narnia/Centro"}""").RootElement);
        Assert.Contains("\"ok\":false", bad.Json);
        Assert.Equal("America/Lima", store.Get().TimeZone);
    }

    [Fact]
    public void Las_claves_se_guardan_cifradas_y_recargan_la_configuracion()
    {
        var path = Path.Combine(Path.GetTempPath(), "kroko-brain-tests", Guid.NewGuid().ToString("N"), "secretos.dat");
        var store = new SecretStore(path);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Web:TavilyApiKey"] = "tvly-de-appsettings" })
            .Add(new SecretConfigurationSource(store))
            .Build();

        var reloaded = 0;
        Microsoft.Extensions.Primitives.ChangeToken.OnChange(config.GetReloadToken, () => reloaded++);

        store.Update(new Dictionary<string, string?> { ["Web:TavilyApiKey"] = "tvly-del-asistente-123" });
        Assert.Equal("tvly-del-asistente-123", config["Web:TavilyApiKey"]);
        Assert.True(reloaded > 0);

        // En disco no aparece en claro (DPAPI en Windows)
        var raw = File.ReadAllText(path);
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain("tvly-del-asistente-123", raw);

        // Otra instancia (otro arranque) la lee igual
        Assert.Equal("tvly-del-asistente-123", new SecretStore(path).Snapshot()["Web:TavilyApiKey"]);

        // Vacío = borrar → vuelve a mandar la configuración anterior
        store.Update(new Dictionary<string, string?> { ["Web:TavilyApiKey"] = "" });
        Assert.Equal("tvly-de-appsettings", config["Web:TavilyApiKey"]);

        // Nada de tocar el token del Brain u otras claves desde la API
        Assert.Throws<ArgumentException>(() => store.Update(new Dictionary<string, string?> { ["Agent:Token"] = "x" }));
    }

    [Fact]
    public void Telegram_se_vincula_solo_con_el_codigo_vigente_y_una_vez()
    {
        var path = Path.Combine(Path.GetTempPath(), "kroko-brain-tests", Guid.NewGuid().ToString("N"), "secretos.dat");
        var secrets = new SecretStore(path);
        var bot = new TelegramBot(new StaticOptions<TelegramOptions>(new TelegramOptions()), new ServiceCollection().BuildServiceProvider(),
            new AppConnections(), secrets, TimeProvider.System, NullLogger<TelegramBot>.Instance);

        Assert.False(bot.TryConsumePairingCode("123456")); // sin código pedido
        var (code, _) = bot.StartPairing();
        Assert.Equal(6, code.Length);
        Assert.False(bot.TryConsumePairingCode("000000"));
        Assert.True(bot.TryConsumePairingCode("/start " + code)); // el enlace t.me/bot?start=código
        Assert.False(bot.TryConsumePairingCode(code)); // un solo uso
    }

    [Fact]
    public async Task La_api_del_asistente_guarda_perfil_y_claves_sin_devolverlas()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?>(TestPaths.IsolatedMemory()) { ["Agent:Token"] = "secreto" })));

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/perfil")).StatusCode);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(KrokoProtocol.TokenHeader, "secreto");

        var before = await client.GetFromJsonAsync<JsonElement>("/api/perfil");
        Assert.False(before.GetProperty("completo").GetBoolean());

        var put = await client.PutAsJsonAsync("/api/perfil", new { nombre = "Ana Quispe", ciudad = "Cusco", zonaHoraria = "America/Lima" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/perfil", new { nombre = "" })).StatusCode);

        var secret = await client.PutAsJsonAsync("/api/configuracion/secretos", new Dictionary<string, string> { ["Web:TavilyApiKey"] = "tvly-prueba-xyz" });
        try
        {
        Assert.Equal(HttpStatusCode.NoContent, secret.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync("/api/configuracion/secretos", new Dictionary<string, string> { ["Agent:Token"] = "x" })).StatusCode);

        var status = await client.GetStringAsync("/api/configuracion");
        Assert.DoesNotContain("tvly-prueba-xyz", status);
        using var doc = JsonDocument.Parse(status);
        Assert.True(doc.RootElement.GetProperty("perfilCompleto").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("tavily").GetBoolean());
        Assert.Equal("asistente Configurar Kroko", doc.RootElement.GetProperty("fuentes").GetProperty("tavily").GetString());

        // El prompt del agente ya usa el nombre
        var prompt = app.Services.GetRequiredService<SystemPrompt>().Build(DateTimeOffset.Now);
        Assert.Contains("Tu dueño: Ana Quispe", prompt);
        Assert.Contains("[[happy]] Hola Ana", prompt);
        Assert.DoesNotContain("Pol", prompt);
        }
        finally
        {
            // El archivo de claves de las pruebas es compartido: se deja limpio para las demás
            await client.PutAsJsonAsync("/api/configuracion/secretos", new Dictionary<string, string> { ["Web:TavilyApiKey"] = "" });
        }
    }

    private sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
