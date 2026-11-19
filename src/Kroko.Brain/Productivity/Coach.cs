using Kroko.Brain.Automation;

namespace Kroko.Brain.Productivity;

/// <summary>
/// El asistente que ayuda por su cuenta (pedido por Pol, 17/11/2026: «un asistente que haga todo o sugiera más cosas;
/// a veces estoy fuera de casa»). Son tres automatizaciones con IA que Kroko crea una sola vez (el dueño las ve, cambia
/// o borra como cualquier otra, y las apaga en Configuración → Avisos):
///  · 07:30 Plan del día: reuniones, tareas, hábitos con racha, clima y 2–3 sugerencias.
///  · 13:00 Seguimiento: lo pendiente de hoy, «¿ya hiciste X?» y sugerencias; si no hay nada útil, se calla.
///  · 20:00 Hábitos y cierre: celebra rachas, avisa las que están por perderse y propone mover pendientes a mañana.
/// Llegan por Telegram y a la PC, y nunca más de <see cref="ProductivityOptions.MaxProactivePerDay"/> al día (Pol: «pocos,
/// hasta 3»). Las reuniones y los correos importantes siguen siendo avisos aparte.
/// </summary>
public static class Coach
{
    /// <summary>Respuesta de la IA cuando no hay nada que valga la pena decir: no se envía nada.</summary>
    public const string Nothing = "NADA";

    internal const string KeyIds = "coach.ids";
    internal const string KeySeeded = "coach.seeded.v1";

    public sealed record Seed(string Name, TimeOnly Time, string Instructions);

    public static readonly Seed[] Seeds =
    [
        new("Plan del día", new TimeOnly(7, 30), """
            Arma mi plan de hoy, como un buen asistente personal. Revisa con tus herramientas: mis reuniones y eventos de hoy
            (Google Calendar o mi agenda de Notion, si están vinculados), mis tareas de hoy y las vencidas de todas mis apps,
            mis hábitos de hoy con su racha, y el clima de hoy en mi ciudad (buscar_en_internet). Escribe un mensaje corto
            (máximo 10 líneas, con viñetas «• ») y termina con 2 o 3 sugerencias concretas para que mi día salga bien
            (por ejemplo: salir antes por el tráfico, llevar paraguas, preparar algo para una reunión, bloquear un rato
            para lo más importante). No inventes nada que no hayas visto.
            """),
        new("Seguimiento del día", new TimeOnly(13, 0), """
            Haz seguimiento de mi día. Revisa: tareas y recordatorios de hoy que siguen sin hacer, correos importantes sin
            responder y lo que tengo esta tarde. Si hay algo que valga la pena, escríbeme un mensaje breve (máximo 6 líneas):
            pregúntame por lo pendiente («¿ya llamaste a…? dime y la marco como hecha») y dame 1 o 2 sugerencias útiles.
            Si todo está al día y no hay nada útil que decir, responde exactamente NADA (sin nada más).
            """),
        new("Hábitos y cierre del día", new TimeOnly(20, 0), """
            Cierra mi día. Revisa mis hábitos: celebra las rachas que sigo (por ejemplo «¡7 días tomando agua!») y avísame
            de los que aún no cumplí hoy y cuya racha se perdería. Revisa las tareas de hoy sin terminar y propón moverlas
            a mañana (no las muevas sin que te lo diga). Mensaje breve y con ánimo (máximo 6 líneas). Si no tengo hábitos
            ni pendientes, responde exactamente NADA.
            """),
    ];

    /// <summary>Crea las tres automatizaciones la primera vez (si el dueño las borra, no vuelven).</summary>
    public static void EnsureSeeded(AutomationStore automations, ProductivityStore store)
    {
        if (store.GetValue(KeySeeded) is not null) return;
        var days = Enum.GetValues<DayOfWeek>();
        var ids = Seeds.Select(s => automations.Add(s.Name, s.Instructions.Trim(), days, s.Time, "ambos")).ToList();
        store.SetValue(KeyIds, string.Join(",", ids));
        store.SetValue(KeySeeded, "1");
    }

    public static IReadOnlyList<long> Ids(ProductivityStore store) =>
        [.. (store.GetValue(KeyIds) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse)];

    public static bool IsCoach(ProductivityStore store, long automationId) => Ids(store).Contains(automationId);

    /// <summary>¿Está encendido? (lo están si alguna de sus automatizaciones sigue activa).</summary>
    public static bool IsOn(AutomationStore automations, ProductivityStore store)
    {
        var ids = Ids(store);
        return automations.All().Any(a => ids.Contains(a.Id) && a.Enabled);
    }

    public static void SetOn(AutomationStore automations, ProductivityStore store, bool on)
    {
        foreach (var id in Ids(store)) automations.SetEnabled(id, on);
    }

    /// <summary>¿La IA dijo que no hay nada que contar?</summary>
    public static bool SaysNothing(string? text) =>
        string.IsNullOrWhiteSpace(text) || text.Trim().TrimEnd('.', '!').Equals(Nothing, StringComparison.OrdinalIgnoreCase);

    /// <summary>Toma un mensaje del cupo diario; false si ya se mandaron todos los de hoy.</summary>
    public static bool TakeBudget(ProductivityStore store, DateOnly today, int maxPerDay)
    {
        if (maxPerDay <= 0) return true; // 0 = sin límite
        var key = $"coach.enviados.{today:yyyy-MM-dd}";
        var sent = int.TryParse(store.GetValue(key), out var n) ? n : 0;
        if (sent >= maxPerDay) return false;
        store.SetValue(key, (sent + 1).ToString());
        return true;
    }
}

/// <summary>Al arrancar el cerebro, crea las automatizaciones del asistente si es la primera vez.</summary>
public sealed class CoachSeeder(AutomationStore automations, ProductivityStore store, IConfiguration config, ILogger<CoachSeeder> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Las pruebas lo apagan: si no, a las 7:30, 13:00 o 20:00 el asistente corre en medio de una prueba
        if (config.GetValue("Productivity:SeedCoach", true) == false) return Task.CompletedTask;
        try
        {
            Coach.EnsureSeeded(automations, store);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            log.LogWarning("No pude preparar el asistente proactivo: {Message}", ex.Message);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
