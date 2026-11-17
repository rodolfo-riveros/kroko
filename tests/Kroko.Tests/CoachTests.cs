using Kroko.Brain.Automation;
using Kroko.Brain.Memory;
using Kroko.Brain.Productivity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kroko.Tests;

/// <summary>El asistente que ayuda por su cuenta (17/11/2026): plan, seguimiento y hábitos, como máximo 3 al día.</summary>
public class CoachTests
{
    private static KrokoDatabase Db() => new(
        Options.Create(new MemoryOptions { DatabasePath = TestPaths.IsolatedMemory()["Memory:DatabasePath"] }),
        NullLogger<KrokoDatabase>.Instance);

    [Fact]
    public void Crea_sus_tres_automatizaciones_una_sola_vez_y_respeta_si_las_borras()
    {
        var db = Db();
        var automations = new AutomationStore(db);
        var store = new ProductivityStore(db, TimeProvider.System);

        Coach.EnsureSeeded(automations, store);
        Assert.Equal(["Plan del día", "Seguimiento del día", "Hábitos y cierre del día"], automations.All().Select(a => a.Name));
        Assert.True(Coach.IsOn(automations, store));

        // Si el dueño las borra, no vuelven a aparecer al reiniciar
        foreach (var a in automations.All()) automations.Delete(a.Id);
        Coach.EnsureSeeded(automations, store);
        Assert.Empty(automations.All());
    }

    [Fact]
    public void Se_apaga_y_se_enciende_desde_Avisos()
    {
        var db = Db();
        var automations = new AutomationStore(db);
        var store = new ProductivityStore(db, TimeProvider.System);
        Coach.EnsureSeeded(automations, store);

        Coach.SetOn(automations, store, false);
        Assert.False(Coach.IsOn(automations, store));
        Assert.All(automations.All(), a => Assert.False(a.Enabled));
        Coach.SetOn(automations, store, true);
        Assert.True(Coach.IsOn(automations, store));
    }

    [Fact]
    public void Nunca_manda_mas_de_tres_al_dia()
    {
        var store = new ProductivityStore(Db(), TimeProvider.System);
        var today = new DateOnly(2026, 11, 17);
        Assert.True(Coach.TakeBudget(store, today, 3));
        Assert.True(Coach.TakeBudget(store, today, 3));
        Assert.True(Coach.TakeBudget(store, today, 3));
        Assert.False(Coach.TakeBudget(store, today, 3));
        Assert.True(Coach.TakeBudget(store, today.AddDays(1), 3)); // al día siguiente, de nuevo
    }

    [Theory]
    [InlineData("NADA", true)]
    [InlineData(" nada. ", true)]
    [InlineData("", true)]
    [InlineData("¿Ya llamaste al dentista?", false)]
    public void Si_no_hay_nada_util_se_calla(string reply, bool silent) => Assert.Equal(silent, Coach.SaysNothing(reply));
}
