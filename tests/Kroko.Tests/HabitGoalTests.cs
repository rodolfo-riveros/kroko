using System.Net.Http.Json;
using System.Text.Json;
using Kroko.Brain.Memory;
using Kroko.Brain.Productivity;
using Kroko.Shared.Protocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kroko.Tests;

/// <summary>Hábitos con meta diaria, rachas y celebración (como el tracker de hábitos de Troko-Desk, 14/11/2026).</summary>
public class HabitGoalTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("Lima", TimeSpan.FromHours(-5), "Lima", "Lima");
    }

    private static KrokoDatabase Db(string? path = null) => new(
        Options.Create(new MemoryOptions { DatabasePath = path ?? TestPaths.IsolatedMemory()["Memory:DatabasePath"] }),
        NullLogger<KrokoDatabase>.Instance);

    [Fact]
    public void Ocho_vasos_de_agua_se_cuentan_de_a_uno_y_al_llegar_se_cumple()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.FromHours(-5)));
        var store = new ProductivityStore(Db(), clock);
        var id = store.AddHabit("Beber agua", goal: 8, unit: "vasos");
        var today = new DateOnly(2026, 11, 14);

        for (var i = 0; i < 7; i++) store.AddToHabit(id, today, 1);
        var h = store.Habits().Single();
        Assert.Equal(7, h.TodayCount);
        Assert.False(h.DoneToday);
        Assert.Equal("agua", h.Style); // deducido del nombre
        Assert.Equal("vasos", h.Unit);

        store.AddToHabit(id, today, 1);
        h = store.Habits().Single();
        Assert.True(h.DoneToday);
        Assert.Equal(1, h.Streak);
        Assert.True(h.Week[^1]); // hoy es el último de la semana

        // Ayer también se cumplió: racha de 2. Un día a medias no cuenta.
        store.MarkHabit(id, today.AddDays(-1), true);
        store.AddToHabit(id, today.AddDays(-3), 3);
        h = store.Habits().Single();
        Assert.Equal(2, h.Streak);
        Assert.Equal(2, h.Last7Days);

        Assert.Equal(0, store.AddToHabit(id, today, -50)); // nunca baja de cero
    }

    [Fact]
    public void Una_base_de_datos_antigua_se_actualiza_sin_perder_los_habitos()
    {
        var path = TestPaths.IsolatedMemory()["Memory:DatabasePath"]!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var c = new SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE habits (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE COLLATE NOCASE, created TEXT NOT NULL);
                CREATE TABLE habit_log (habit_id INTEGER NOT NULL, day TEXT NOT NULL, PRIMARY KEY (habit_id, day));
                INSERT INTO habits(name, created) VALUES('Leer', '2026-11-01');
                INSERT INTO habit_log(habit_id, day) VALUES(1, '2026-11-14');
                PRAGMA user_version = 3;
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var store = new ProductivityStore(Db(path), new Clock(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.FromHours(-5))));
        var leer = Assert.Single(store.Habits());
        Assert.Equal("Leer", leer.Name);
        Assert.Equal(1, leer.Goal);
        Assert.True(leer.DoneToday); // el registro viejo cuenta como 1 de 1
        Assert.Equal("lectura", leer.Style);
    }

    [Theory]
    [InlineData("Beber agua", "agua")]
    [InlineData("Leer 20 minutos", "lectura")]
    [InlineData("Ir al gimnasio", "ejercicio")]
    [InlineData("Meditar", "meditacion")]
    [InlineData("Tocar guitarra", "musica")]
    [InlineData("Llamar a mamá", "estrella")]
    public void El_estilo_sale_del_nombre(string name, string style) => Assert.Equal(style, HabitStyles.Guess(name));

    [Fact]
    public async Task El_panel_lee_el_dia_y_suma_un_vaso_con_un_clic()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>(TestPaths.IsolatedMemory())
            {
                ["Agent:Token"] = "secreto-de-prueba",
            })));
        var http = app.CreateClient();
        http.DefaultRequestHeaders.Add(KrokoProtocol.TokenHeader, "secreto-de-prueba");

        var created = await http.PostAsJsonAsync("/api/habitos", new { nombre = "Beber agua", meta = 2, unidad = "vasos" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await http.PostAsJsonAsync("/api/tareas", new { texto = "Comprar pan" });

        var first = await (await http.PostAsJsonAsync($"/api/habitos/{id}/sumar", new { cantidad = 1 })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(first.GetProperty("metaCumplida").GetBoolean());
        var second = await (await http.PostAsJsonAsync($"/api/habitos/{id}/sumar", new { cantidad = 1 })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(second.GetProperty("metaCumplida").GetBoolean()); // la App celebra

        var hoy = await http.GetFromJsonAsync<JsonElement>("/api/hoy");
        Assert.Equal(1, hoy.GetProperty("resumen").GetProperty("habitosHechos").GetInt32());
        Assert.Equal(1, hoy.GetProperty("resumen").GetProperty("tareasPendientes").GetInt32());
        Assert.Equal("Comprar pan", hoy.GetProperty("tareas")[0].GetProperty("texto").GetString());

        http.DefaultRequestHeaders.Remove(KrokoProtocol.TokenHeader);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await http.GetAsync("/api/hoy")).StatusCode);
    }
}
