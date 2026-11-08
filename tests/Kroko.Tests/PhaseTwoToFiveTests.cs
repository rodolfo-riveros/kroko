using System.Text.Json;
using Kroko.Brain.Config;
using Kroko.Brain.Memory;
using Kroko.Brain.Productivity;
using Kroko.Shared.Protocol;
using Kroko.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kroko.Tests;

public class PhaseTwoToFiveTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("PE", TimeSpan.FromHours(-5), "PE", "PE");
    }

    private static readonly DateTimeOffset Monday9 = new(2026, 11, 9, 9, 0, 0, TimeSpan.FromHours(-5));

    private static (ProductivityStore Store, FixedTime Time) NewStore()
    {
        var paths = TestPaths.IsolatedMemory();
        var db = new KrokoDatabase(Options.Create(new MemoryOptions { DatabasePath = paths["Memory:DatabasePath"] }), NullLogger<KrokoDatabase>.Instance);
        var time = new FixedTime(Monday9);
        return (new ProductivityStore(db, time), time);
    }

    // ---------------------------------------------------------------- Fase 2

    [Fact]
    public void Recordatorios_vencen_y_los_que_se_repiten_se_reprograman()
    {
        var (store, time) = NewStore();
        store.AddReminder("tomar agua", Monday9.AddMinutes(30), "diario");
        store.AddReminder("llamar a Ana", Monday9.AddMinutes(10), "nunca");
        Assert.Empty(store.DueReminders());

        time.Now = Monday9.AddMinutes(45);
        var due = store.DueReminders();
        Assert.Equal(2, due.Count);
        foreach (var r in due) store.MarkDelivered(r);

        var pending = Assert.Single(store.PendingReminders()); // "llamar a Ana" ya se entregó
        Assert.Equal("tomar agua", pending.Text);
        Assert.Equal(Monday9.AddDays(1).AddMinutes(30), pending.Due);
    }

    [Theory]
    [InlineData("laborables", 2026, 11, 13, 2026, 11, 16)] // viernes → lunes
    [InlineData("semanal", 2026, 11, 9, 2026, 11, 16)]
    public void Siguiente_repeticion(string repeat, int y, int m, int d, int ny, int nm, int nd)
    {
        var due = new DateTimeOffset(y, m, d, 8, 0, 0, TimeSpan.FromHours(-5));
        var next = ProductivityStore.NextOccurrence(due, repeat, due);
        Assert.Equal(new DateTimeOffset(ny, nm, nd, 8, 0, 0, TimeSpan.FromHours(-5)), next);
    }

    [Fact]
    public void Tareas_y_habitos_con_racha()
    {
        var (store, _) = NewStore();
        var t = store.AddTask("Enviar informe", new DateOnly(2026, 11, 9));
        store.AddTask("Comprar pan", null);
        Assert.Equal(2, store.Tasks(false).Count);
        store.CompleteTask(t);
        Assert.Single(store.Tasks(false));
        Assert.Equal(2, store.Tasks(true).Count);

        var h = store.AddHabit("Leer");
        store.MarkHabit(h, new DateOnly(2026, 11, 7), true);
        store.MarkHabit(h, new DateOnly(2026, 11, 8), true);
        store.MarkHabit(h, new DateOnly(2026, 11, 9), true);
        var habit = Assert.Single(store.Habits());
        Assert.True(habit.DoneToday);
        Assert.Equal(3, habit.Streak);
    }

    [Fact]
    public async Task El_programador_solo_entrega_si_hay_a_quien_avisar()
    {
        var (store, time) = NewStore();
        store.AddReminder("reunión", Monday9.AddMinutes(-1), "nunca");
        var notifier = new FakeNotifier { IsAvailable = false };
        var scheduler = new ReminderScheduler(store, [notifier],
            new ServiceCollection().BuildServiceProvider(),
            new JarvisToolTests.StaticMonitor<ProductivityOptions>(new ProductivityOptions { DailySummaryHour = -1, SpeakNotifications = false }),
            time, TestPaths.Owner(), NullLogger<ReminderScheduler>.Instance);

        await scheduler.TickAsync(default);
        Assert.Empty(notifier.Sent);                 // PC apagada y sin Telegram: espera
        Assert.Single(store.PendingReminders());

        notifier.IsAvailable = true;
        await scheduler.TickAsync(default);
        Assert.Equal("reunión", Assert.Single(notifier.Sent).Text);
        Assert.Empty(store.PendingReminders());
    }

    private sealed class FakeNotifier : INotifier
    {
        public bool IsAvailable { get; set; }
        public List<NotifyMessage> Sent { get; } = [];

        public Task<bool> NotifyAsync(NotifyMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.FromResult(true);
        }
    }

    // ---------------------------------------------------------------- Fase 4

    [Theory]
    [InlineData("Enviar", true)]
    [InlineData("Eliminar cuenta", true)]
    [InlineData("Comprar ahora", true)]
    [InlineData("Enviados", false)]
    [InlineData("Guardar borrador", false)]
    [InlineData("Siguiente", false)]
    public void Botones_irreversibles(string text, bool expected) => Assert.Equal(expected, ToolCatalog.LooksIrreversible(text));

    [Fact]
    public void Clic_en_boton_irreversible_pide_confirmacion()
    {
        var clic = ToolCatalog.Find("clic")!;
        Assert.Equal(ToolRisk.Dangerous, clic.EffectiveRisk(JsonSerializer.SerializeToElement(new { texto = "Enviar" })));
        Assert.Equal(ToolRisk.Write, clic.EffectiveRisk(JsonSerializer.SerializeToElement(new { texto = "Siguiente" })));
        var teclas = ToolCatalog.Find("teclas")!;
        Assert.Equal(ToolRisk.Dangerous, teclas.EffectiveRisk(JsonSerializer.SerializeToElement(new { atajo = "Alt+F4" })));
        Assert.Equal(ToolRisk.Write, teclas.EffectiveRisk(JsonSerializer.SerializeToElement(new { atajo = "Ctrl+S" })));
    }

    // ---------------------------------------------------------------- Fase 5

    [Theory]
    [InlineData("http://localhost:5080", false)]
    [InlineData("http://127.0.0.1:5080;http://[::1]:5080", false)]
    [InlineData("http://+:8080", true)]
    [InlineData("http://*:8080", true)]
    [InlineData("http://0.0.0.0:8080", true)]
    [InlineData("http://192.168.1.20:5080", true)]
    public void Detecta_cuando_el_cerebro_queda_expuesto(string urls, bool exposed) =>
        Assert.Equal(exposed, ServerSafety.ExposesOutsideThisPc(urls));
}
