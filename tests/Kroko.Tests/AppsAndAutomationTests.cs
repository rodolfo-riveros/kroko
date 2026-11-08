using System.Text.Json;
using Kroko.Brain.Agent;
using Kroko.Brain.Apps;
using Kroko.Brain.Automation;
using Kroko.Brain.Config;
using Kroko.Brain.Memory;
using Kroko.Brain.Providers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kroko.Tests;

/// <summary>Automatizaciones con IA y conectores de tareas (Trello, Google).</summary>
public class AppsAndAutomationTests
{
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private static AutomationStore NewStore() =>
        new(new KrokoDatabase(Options.Create(new MemoryOptions { DatabasePath = TestPaths.IsolatedMemory()["Memory:DatabasePath"] }),
            NullLogger<KrokoDatabase>.Instance));

    [Fact]
    public void Entiende_los_dias_en_espanol()
    {
        Assert.Equal(5, AutomationItem.ParseDays(["laborables"]).Count);
        Assert.Equal(7, AutomationItem.ParseDays(["todos"]).Count);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Wednesday], AutomationItem.ParseDays(["miércoles", "lunes"]));
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Saturday], AutomationItem.ParseDays(["fin de semana"]));
        Assert.Throws<ArgumentException>(() => AutomationItem.ParseDays(["feriado"]));
    }

    [Fact]
    public void Solo_toca_a_su_hora_una_vez_al_dia_y_no_horas_despues()
    {
        var a = new AutomationItem(1, "Pendientes", "Dime mis pendientes", AutomationItem.ParseDays(["laborables"]),
            new TimeOnly(8, 0), "ambos", true, null);
        var monday = new DateTimeOffset(2026, 11, 9, 8, 0, 30, TimeSpan.FromHours(-5));
        Assert.True(a.IsDue(monday));
        Assert.False(a.IsDue(monday.AddMinutes(-5)));                                     // aún no
        Assert.False(a.IsDue(monday.AddHours(4)));                                        // la PC estaba apagada: no de golpe
        Assert.False((a with { LastRun = DateOnly.FromDateTime(monday.DateTime) }).IsDue(monday.AddMinutes(1))); // ya corrió hoy
        Assert.False(a.IsDue(monday.AddDays(-1)));                                        // domingo
        Assert.False((a with { Enabled = false }).IsDue(monday));
        Assert.Equal("de lunes a viernes", a.DaysText);
    }

    [Fact]
    public void Las_herramientas_crean_listan_y_borran_automatizaciones()
    {
        var store = NewStore();
        var created = new AutomatizacionCrearTool(store).Execute(Args(new
        {
            nombre = "Noticias", instrucciones = "Resume las 5 noticias más importantes de Perú", dias = new[] { "todos" }, hora = "7:00", enviar_por = "telegram",
        }));
        Assert.Contains("\"ok\":true", created.Json);
        Assert.Contains("todos los días a las 07:00", created.Json);

        var bad = new AutomatizacionCrearTool(store).Execute(Args(new { nombre = "X", instrucciones = "algo largo", dias = new[] { "lunes" }, hora = "25:99" }));
        Assert.Contains("\"ok\":false", bad.Json);

        var a = Assert.Single(store.All());
        Assert.Equal("telegram", a.Deliver);
        Assert.Contains("Noticias", new AutomatizacionesListarTool(store).Execute(Args(new { })).Json);
        Assert.Contains("\"ok\":true", new AutomatizacionCambiarTool(store).Execute(Args(new { id = a.Id, accion = "pausar" })).Json);
        Assert.False(store.Get(a.Id)!.Enabled);
        Assert.Contains("\"ok\":true", new AutomatizacionCambiarTool(store).Execute(Args(new { id = a.Id, accion = "borrar" })).Json);
        Assert.Empty(store.All());
    }

    [Fact]
    public async Task Una_automatizacion_corre_con_la_IA_y_no_deja_su_conversacion_guardada()
    {
        var ia = new StubProvider("fake", true, reply: "[[idea]] • Tarea 1\n• Tarea 2");
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(TestPaths.IsolatedMemory()));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ILlmProvider>();
                s.AddSingleton<ILlmProvider>(ia);
            });
        });
        var store = app.Services.GetRequiredService<AutomationStore>();
        var id = store.Add("Pendientes", "Dime mis pendientes", AutomationItem.ParseDays(["todos"]), new TimeOnly(8, 0), "ambos");

        var reply = await app.Services.GetRequiredService<AutomationRunner>().RunAsync(store.Get(id)!, default);

        Assert.Contains("Tarea 1", reply);
        Assert.Equal(1, ia.Calls);
        Assert.NotNull(store.Get(id)!.LastRun);
        var db = app.Services.GetRequiredService<KrokoDatabase>();
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE conversation LIKE 'automatizacion-%'";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task Trello_y_Google_sin_conectar_lo_explican_sin_romperse()
    {
        var trello = new TrelloClient(new HttpClient(), new StaticMonitor<TrelloOptions>(new TrelloOptions()));
        var res = await new TrelloTablerosTool(trello).ExecuteAsync(Args(new { }), default);
        Assert.Contains("\"ok\":false", res.Json);
        Assert.Contains("Trello", res.Json);

        var secrets = new SecretStore(Path.Combine(Path.GetTempPath(), "kroko-brain-tests", Guid.NewGuid().ToString("N"), "s.dat"));
        var google = new GoogleAccount(new HttpClient(), new StaticMonitor<GoogleOptions>(new GoogleOptions()), secrets, TimeProvider.System,
            NullLogger<GoogleAccount>.Instance);
        var g = await new GoogleTareasListarTool(google).ExecuteAsync(Args(new { }), default);
        Assert.Contains("\"ok\":false", g.Json);
        Assert.Contains("Google", g.Json);
    }

    [Fact]
    public void El_permiso_de_Google_usa_PKCE_y_pide_solo_lo_necesario()
    {
        var secrets = new SecretStore(Path.Combine(Path.GetTempPath(), "kroko-brain-tests", Guid.NewGuid().ToString("N"), "s.dat"));
        var google = new GoogleAccount(new HttpClient(), new StaticMonitor<GoogleOptions>(new GoogleOptions { ClientId = "id.apps.googleusercontent.com", ClientSecret = "x" }),
            secrets, TimeProvider.System, NullLogger<GoogleAccount>.Instance);
        var url = google.StartAuthorization("http://127.0.0.1:5080/api/google/oauth/callback");
        Assert.StartsWith("https://accounts.google.com/", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains(Uri.EscapeDataString("https://www.googleapis.com/auth/drive.readonly"), url);
        Assert.DoesNotContain(Uri.EscapeDataString("https://www.googleapis.com/auth/drive "), url); // Drive solo lectura
        Assert.False(google.CompleteAuthorizationAsync("state-inventado", "code", default).Result);
    }

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
