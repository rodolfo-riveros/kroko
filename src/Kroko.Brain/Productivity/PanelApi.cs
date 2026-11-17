using Kroko.Brain.Agent;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Productivity;

/// <summary>
/// API del panel «Kroko» de la App (14/11/2026): el día de un vistazo (hábitos, tareas y avisos) y los botones del panel
/// (sumar un vaso, cumplir una tarea, crear un hábito…). Lo mismo que Kroko hace por voz, pero con un clic.
/// </summary>
public static class PanelApi
{
    public sealed record HabitDto(string Nombre, int Meta = 1, string? Unidad = null, string? Estilo = null);
    public sealed record AmountDto(int Cantidad = 1);
    public sealed record TaskDto(string Texto, string? Fecha = null);
    public sealed record DoneDto(bool Hecha = true);

    public static void MapPanelApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(async (ctx, next) =>
        {
            var token = ctx.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<AgentOptions>>().CurrentValue.Token;
            return BrainAuth.IsAuthorized(ctx.HttpContext, token) ? await next(ctx) : Results.Unauthorized();
        });

        api.MapGet("/hoy", (ProductivityStore store, TimeProvider time) => Results.Ok(Today(store, time)));

        // ---------------------------------------------------------------- hábitos

        api.MapPost("/habitos", (HabitDto body, ProductivityStore store) =>
        {
            if (string.IsNullOrWhiteSpace(body.Nombre)) return Results.BadRequest(new { error = "Falta el nombre del hábito." });
            var id = store.AddHabit(body.Nombre, body.Meta, body.Unidad, body.Estilo);
            return Results.Ok(Describe(store.Habits().First(h => h.Id == id)));
        });

        api.MapPost("/habitos/{id:long}/sumar", (long id, AmountDto? body, ProductivityStore store, TimeProvider time) =>
        {
            if (store.Habits().FirstOrDefault(h => h.Id == id) is not { } before) return Results.NotFound();
            store.AddToHabit(id, DateOnly.FromDateTime(time.GetLocalNow().DateTime), body?.Cantidad ?? 1);
            var after = store.Habits().First(h => h.Id == id);
            return Results.Ok(new { habito = Describe(after), metaCumplida = after.DoneToday && !before.DoneToday });
        });

        api.MapPost("/habitos/{id:long}/reiniciar", (long id, ProductivityStore store, TimeProvider time) =>
        {
            store.MarkHabit(id, DateOnly.FromDateTime(time.GetLocalNow().DateTime), done: false);
            return store.Habits().FirstOrDefault(h => h.Id == id) is { } h ? Results.Ok(Describe(h)) : Results.NotFound();
        });

        api.MapDelete("/habitos/{id:long}", (long id, ProductivityStore store) =>
            store.DeleteHabit(id) ? Results.NoContent() : Results.NotFound());

        // ---------------------------------------------------------------- tareas

        api.MapPost("/tareas", (TaskDto body, ProductivityStore store) =>
        {
            if (string.IsNullOrWhiteSpace(body.Texto)) return Results.BadRequest(new { error = "Falta el texto de la tarea." });
            DateOnly? due = DateOnly.TryParse(body.Fecha, out var d) ? d : null;
            return Results.Ok(new { id = store.AddTask(body.Texto.Trim(), due) });
        });

        api.MapPost("/tareas/{id:long}/completar", (long id, DoneDto? body, ProductivityStore store) =>
            store.CompleteTask(id, body?.Hecha ?? true) ? Results.NoContent() : Results.NotFound());

        api.MapDelete("/tareas/{id:long}", (long id, ProductivityStore store) =>
            store.DeleteTask(id) ? Results.NoContent() : Results.NotFound());

        api.MapDelete("/recordatorios/{id:long}", (long id, ProductivityStore store) =>
            store.DeleteReminder(id) ? Results.NoContent() : Results.NotFound());

        // ---------------------------------------------------------------- avisos automáticos (panel → Ajustes)

        api.MapGet("/avisos", (ProductivityStore store, Automation.AutomationStore automations, IOptionsMonitor<ProactiveOptions> proactive,
            IOptionsMonitor<ProductivityOptions> productivity) =>
            Results.Ok(Notices(store, proactive.CurrentValue, productivity.CurrentValue) with { Asistente = Coach.IsOn(automations, store) }));

        api.MapPut("/avisos", (NoticesDto body, ProductivityStore store, Automation.AutomationStore automations,
            IOptionsMonitor<ProactiveOptions> proactive, IOptionsMonitor<ProductivityOptions> productivity) =>
        {
            if (body.Asistente is { } coachOn) Coach.SetOn(automations, store, coachOn);
            NoticePreferences.Set(store, "reuniones", body.Reuniones);
            NoticePreferences.Set(store, "correos", body.Correos);
            NoticePreferences.Set(store, "vencidas", body.TareasVencidas);
            NoticePreferences.Set(store, "resumen", body.ResumenDelDia);
            return Results.Ok(Notices(store, proactive.CurrentValue, productivity.CurrentValue) with { Asistente = Coach.IsOn(automations, store) });
        });
    }

    public sealed record NoticesDto(bool Reuniones, bool Correos, bool TareasVencidas, bool ResumenDelDia, bool? Asistente = null);

    private static NoticesDto Notices(ProductivityStore s, ProactiveOptions p, ProductivityOptions o) => new(
        NoticePreferences.MeetingMinutes(s, p) > 0,
        NoticePreferences.Emails(s, p),
        NoticePreferences.OverdueHour(s, p) >= 0,
        NoticePreferences.SummaryHour(s, o) >= 0);

    internal static object Today(ProductivityStore store, TimeProvider time)
    {
        var now = time.GetLocalNow();
        var today = DateOnly.FromDateTime(now.DateTime);
        var habits = store.Habits();
        var tasks = store.Tasks(includeDone: true)
            .Where(t => !t.Done || DateOnly.FromDateTime(t.Created.DateTime) == today || t.DueDate == today)
            .OrderBy(t => t.Done).ThenBy(t => t.DueDate ?? DateOnly.MaxValue).ThenBy(t => t.Created)
            .ToList();
        var reminders = store.PendingReminders().Where(r => r.Due > now).OrderBy(r => r.Due).Take(20).ToList();

        return new
        {
            fecha = today.ToString("yyyy-MM-dd"),
            resumen = new
            {
                habitosHechos = habits.Count(h => h.DoneToday),
                habitosTotal = habits.Count,
                tareasPendientes = tasks.Count(t => !t.Done),
                proximoAviso = reminders.FirstOrDefault() is { } next ? next.Due.ToOffset(now.Offset).ToString("HH:mm") : null,
                proximoAvisoTexto = reminders.FirstOrDefault()?.Text,
            },
            habitos = habits.Select(Describe),
            tareas = tasks.Select(t => new { id = t.Id, texto = t.Text, fecha = t.DueDate?.ToString("yyyy-MM-dd"), hecha = t.Done }),
            recordatorios = reminders.Select(r => new
            {
                id = r.Id,
                texto = r.Text,
                cuando = r.Due.ToOffset(now.Offset).ToString("yyyy-MM-dd HH:mm"),
                repetir = r.Repeat,
            }),
        };
    }

    private static object Describe(Habit h) => new
    {
        id = h.Id,
        nombre = h.Name,
        llevas = h.TodayCount,
        meta = h.Goal,
        unidad = h.Unit,
        estilo = h.Style,
        hecho = h.DoneToday,
        racha = h.Streak,
        semana = h.Week,
        avisos = h.RemindAt,
    };
}
