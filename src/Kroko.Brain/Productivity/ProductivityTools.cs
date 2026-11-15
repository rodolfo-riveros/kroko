using System.Globalization;
using System.Text.Json;
using Kroko.Brain.Agent;
using Kroko.Brain.Providers;
using Kroko.Shared.Protocol;
using Kroko.Shared.Tools;

namespace Kroko.Brain.Productivity;

/// <summary>Herramientas de productividad (Fase 2) que viven en el Brain: funcionan igual en un servidor.</summary>
public abstract class ProductivityTool(ProductivityStore store, TimeProvider time) : SyncBrainTool
{
    protected ProductivityStore Store => store;
    protected DateTimeOffset Now => time.GetLocalNow();
    protected TimeZoneInfo Zone => time.LocalTimeZone;
    protected DateOnly Today => DateOnly.FromDateTime(Now.DateTime);

    protected static DateOnly? ParseDate(string text) =>
        DateOnly.TryParseExact(text, ["yyyy-MM-dd", "yyyy-M-d"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    protected static object Describe(TaskItem t) => new { id = t.Id, tarea = t.Text, fecha = t.DueDate?.ToString("yyyy-MM-dd"), hecha = t.Done };
}

// ---------------------------------------------------------------- recordatorios

public sealed class RecordatorioCrearTool(ProductivityStore store, TimeProvider time) : ProductivityTool(store, time)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("recordatorio_crear",
        "Programa un recordatorio: a una hora concreta (fecha_hora en hora local de tu dueño, formato 2026-11-07T15:30) " +
        "o dentro de N minutos (en_minutos). Puede repetirse: nunca, diario, semanal o laborables. Kroko avisará en la PC (y por Telegram si está conectado).",
        """{"type":"object","properties":{"texto":{"type":"string"},"fecha_hora":{"type":"string"},"en_minutos":{"type":"integer","minimum":1},"repetir":{"type":"string","enum":["nunca","diario","semanal","laborables"]}},"required":["texto"]}""");

    public override ToolExecution Execute(JsonElement args)
    {
        var text = ToolCatalog.Str(args, "texto").Trim();
        if (text.Length == 0) return ToolConfirmation.Fail("Falta qué recordar.");

        DateTimeOffset due;
        var minutes = ToolCatalog.Int(args, "en_minutos");
        var when = ToolCatalog.Str(args, "fecha_hora");
        if (minutes > 0) due = Now.AddMinutes(minutes);
        else if (DateTime.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
            due = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone.GetUtcOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified)));
        else return ToolConfirmation.Fail("Dime cuándo: fecha_hora (2026-11-07T15:30) o en_minutos.");

        if (due <= Now.AddSeconds(-30)) return ToolConfirmation.Fail("Esa hora ya pasó. ¿Para cuándo lo programo?");
        var repeat = ToolCatalog.Str(args, "repetir") is { Length: > 0 } r ? r : "nunca";
        var id = Store.AddReminder(text, due, repeat);
        return BrainToolResult.Ok(new { id, recordatorio = text, cuando = due.ToString("dddd d 'de' MMMM, HH:mm", new CultureInfo("es-PE")), repetir = repeat });
    }
}

public sealed class RecordatoriosListarTool(ProductivityStore store, TimeProvider time) : ProductivityTool(store, time)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("recordatorios_listar",
        "Lista los recordatorios pendientes.", """{"type":"object","properties":{}}""", ToolRisk.Read);

    public override ToolExecution Execute(JsonElement args) =>
        BrainToolResult.Ok(new
        {
            recordatorios = Store.PendingReminders().Select(r => new
            {
                id = r.Id,
                texto = r.Text,
                cuando = r.Due.ToString("yyyy-MM-dd HH:mm"),
                repetir = r.Repeat,
            }),
        });
}

public sealed class RecordatorioBorrarTool(ProductivityStore store, TimeProvider time) : ProductivityTool(store, time)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("recordatorio_borrar",
        "Cancela un recordatorio por su id.", """{"type":"object","properties":{"id":{"type":"integer"}},"required":["id"]}""");

    public override ToolExecution Execute(JsonElement args) =>
        Store.DeleteReminder(ToolCatalog.Int(args, "id"))
            ? BrainToolResult.Ok(new { cancelado = true })
            : ToolConfirmation.Fail("No existe ese recordatorio.");
}

// ---------------------------------------------------------------- tareas

public sealed class TareaCrearTool(ProductivityStore store, TimeProvider time) : ProductivityTool(store, time)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("tarea_crear",
        "Añade una tarea a la lista de tu dueño (opcionalmente con fecha límite yyyy-MM-dd).",
        """{"type":"object","properties":{"texto":{"type":"string"},"fecha":{"type":"string"}},"required":["texto"]}""");

    public override ToolExecution Execute(JsonElement args)
    {
        var text = ToolCatalog.Str(args, "texto").Trim();
        if (text.Length == 0) return ToolConfirmation.Fail("Falta la tarea.");
        var id = Store.AddTask(text, ParseDate(ToolCatalog.Str(args, "fecha")));
        return BrainToolResult.Ok(new { id, tarea = text });
    }
}

public sealed class TareasListarTool(ProductivityStore store, TimeProvider time) : ProductivityTool(store, time)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("tareas_listar",
        "Lista las tareas pendientes (y, si se pide, las hechas en los últimos 7 días).",
        """{"type":"object","properties":{"incluir_hechas":{"type":"boolean"}}}""", ToolRisk.Read);

    public override ToolExecution Execute(JsonElement args)
    {
        var tasks = Store.Tasks(ToolCatalog.Bool(args, "incluir_hechas"));
        return BrainToolResult.Ok(new { hoy = Today.ToString("yyyy-MM-dd"), tareas = tasks.Select(Describe) });
    }
}

public sealed class TareaCompletarTool(ProductivityStore store, TimeProvider time) : ProductivityTool(store, time)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("tarea_completar",
        "Marca una tarea como hecha (o la reabre con hecha=false).",
        """{"type":"object","properties":{"id":{"type":"integer"},"hecha":{"type":"boolean"}},"required":["id"]}""");

    public override ToolExecution Execute(JsonElement args)
    {
        var done = !args.TryGetProperty("hecha", out var h) || h.ValueKind != JsonValueKind.False;
        return Store.CompleteTask(ToolCatalog.Int(args, "id"), done)
            ? BrainToolResult.Ok(new { hecha = done })
            : ToolConfirmation.Fail("No existe esa tarea.");
    }
}

public sealed class TareaBorrarTool(ProductivityStore store, TimeProvider time) : ProductivityTool(store, time)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("tarea_borrar",
        "Quita una tarea de la lista.", """{"type":"object","properties":{"id":{"type":"integer"}},"required":["id"]}""");

    public override ToolExecution Execute(JsonElement args) =>
        Store.DeleteTask(ToolCatalog.Int(args, "id")) ? BrainToolResult.Ok(new { borrada = true }) : ToolConfirmation.Fail("No existe esa tarea.");
}

// ---------------------------------------------------------------- hábitos

public sealed class HabitoCrearTool(ProductivityStore store, TimeProvider time) : ProductivityTool(store, time)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("habito_crear",
        "Empieza a seguir un hábito diario con su meta (\"tomar 8 vasos de agua\", \"leer 20 minutos\", \"ir al gimnasio\"). " +
        "Si pide que se lo recuerdes a ciertas horas, ponlas en avisos (se crean recordatorios diarios).",
        """
        {"type":"object","properties":{
          "nombre":{"type":"string","description":"Nombre corto: Beber agua, Leer, Ejercicio…"},
          "meta":{"type":"integer","description":"Cuántas veces o unidades al día (1 si es hacerlo una vez)"},
          "unidad":{"type":"string","description":"vasos, páginas, minutos… (opcional)"},
          "estilo":{"type":"string","enum":["agua","lectura","ejercicio","caminar","meditacion","sueno","estudio","comida","medicina","orden","pantalla","diario","musica","dinero","estrella"],"description":"La escena con la que lo celebras; si no lo sabes, omítelo"},
          "avisos":{"type":"string","description":"Horas para recordárselo cada día, p. ej. 10:00, 16:00"}
        },"required":["nombre"]}
        """);

    public override ToolExecution Execute(JsonElement args)
    {
        var name = ToolCatalog.Str(args, "nombre").Trim();
        if (name.Length == 0) return ToolConfirmation.Fail("Falta el nombre del hábito.");
        var goal = args.TryGetProperty("meta", out var m) && m.TryGetInt32(out var g) ? g : 1;
        var avisos = ToolCatalog.Str(args, "avisos").Trim();
        var times = avisos.Split([',', ';', ' ', 'y'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => TimeOnly.TryParse(t.Trim(), CultureInfo.InvariantCulture, out var time) ? time : (TimeOnly?)null)
            .Where(t => t is not null).Select(t => t!.Value).Distinct().ToList();

        var id = Store.AddHabit(name, goal, ToolCatalog.Str(args, "unidad"), ToolCatalog.Str(args, "estilo"),
            times.Count > 0 ? string.Join(",", times.Select(t => t.ToString("HH:mm"))) : null);

        // Avisos diarios del hábito (se entregan como cualquier recordatorio: en la PC y por Telegram si está vinculado)
        foreach (var t in times)
        {
            var due = new DateTimeOffset(Today.ToDateTime(t), Zone.GetUtcOffset(Today.ToDateTime(t)));
            if (due <= Now) due = due.AddDays(1);
            Store.AddReminder($"Hábito: {name}", due, "diario");
        }
        var habit = Store.Habits().First(x => x.Id == id);
        return BrainToolResult.Ok(new
        {
            id,
            habito = habit.Name,
            meta = habit.Goal,
            unidad = habit.Unit,
            estilo = habit.Style,
            avisos = times.Select(t => t.ToString("HH:mm")),
        });
    }
}

public sealed class HabitoMarcarTool(ProductivityStore store, TimeProvider time) : ProductivityTool(store, time)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("habito_marcar",
        "Registra un hábito hoy (o en otra fecha yyyy-MM-dd). Con meta de varias veces suma de a una (\"tomé un vaso de " +
        "agua\" = +1; cantidad para sumar más). hecho=true lo da por cumplido del todo; hecho=false lo deja en cero. " +
        "Si devuelve metaCumplida=true, celebra con success o love.",
        """{"type":"object","properties":{"nombre":{"type":"string"},"cantidad":{"type":"integer"},"hecho":{"type":"boolean"},"fecha":{"type":"string"}},"required":["nombre"]}""");

    public override ToolExecution Execute(JsonElement args)
    {
        var name = ToolCatalog.Str(args, "nombre");
        if (Store.FindHabit(name) is not { } id) return ToolConfirmation.Fail($"No sigo ningún hábito llamado \"{name}\". ¿Lo creo?");
        var day = ParseDate(ToolCatalog.Str(args, "fecha")) ?? Today;
        var before = Store.Habits().First(x => x.Id == id);

        if (args.TryGetProperty("hecho", out var h) && h.ValueKind is JsonValueKind.True or JsonValueKind.False)
            Store.MarkHabit(id, day, h.ValueKind == JsonValueKind.True);
        else if (before.Goal <= 1)
            Store.MarkHabit(id, day, true);
        else
            Store.AddToHabit(id, day, args.TryGetProperty("cantidad", out var c) && c.TryGetInt32(out var n) ? n : 1);

        var habit = Store.Habits().First(x => x.Id == id);
        return BrainToolResult.Ok(new
        {
            habito = habit.Name,
            llevas = habit.TodayCount,
            meta = habit.Goal,
            unidad = habit.Unit,
            hechoHoy = habit.DoneToday,
            metaCumplida = habit.DoneToday && !before.DoneToday, // recién llegó: ¡a celebrar!
            racha = habit.Streak,
        });
    }
}

public sealed class HabitosListarTool(ProductivityStore store, TimeProvider time) : ProductivityTool(store, time)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("habitos_listar",
        "Muestra los hábitos de tu dueño: cuánto lleva hoy de su meta, si la cumplió, su racha y cuántos de los últimos 7 días.",
        """{"type":"object","properties":{}}""", ToolRisk.Read);

    public override ToolExecution Execute(JsonElement args) =>
        BrainToolResult.Ok(new
        {
            habitos = Store.Habits().Select(h => new
            {
                h.Id, habito = h.Name, llevas = h.TodayCount, meta = h.Goal, unidad = h.Unit, hechoHoy = h.DoneToday,
                racha = h.Streak, ultimos7 = h.Last7Days,
            }),
        });
}

public sealed class HabitoBorrarTool(ProductivityStore store, TimeProvider time) : ProductivityTool(store, time)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("habito_borrar",
        "Deja de seguir un hábito.", """{"type":"object","properties":{"nombre":{"type":"string"}},"required":["nombre"]}""");

    public override ToolExecution Execute(JsonElement args) =>
        Store.FindHabit(ToolCatalog.Str(args, "nombre")) is { } id && Store.DeleteHabit(id)
            ? BrainToolResult.Ok(new { borrado = true })
            : ToolConfirmation.Fail("No sigo ese hábito.");
}

// ---------------------------------------------------------------- resumen del día

public sealed class ResumenDelDiaTool(ProductivityStore store, TimeProvider time) : ProductivityTool(store, time)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("resumen_del_dia",
        "Datos para el resumen del día de tu dueño: tareas (vencidas, de hoy y sin fecha), recordatorios de hoy y hábitos.",
        """{"type":"object","properties":{}}""", ToolRisk.Read);

    public override ToolExecution Execute(JsonElement args) => BrainToolResult.Ok(Build(Store, Today, Now));

    internal static object Build(ProductivityStore store, DateOnly today, DateTimeOffset now)
    {
        var tasks = store.Tasks(includeDone: false);
        var endOfDay = new DateTimeOffset(today.ToDateTime(new TimeOnly(23, 59)), now.Offset);
        return new
        {
            fecha = today.ToString("dddd d 'de' MMMM", new CultureInfo("es-PE")),
            tareasVencidas = tasks.Where(t => t.DueDate < today).Select(Describe),
            tareasDeHoy = tasks.Where(t => t.DueDate == today).Select(Describe),
            tareasSinFecha = tasks.Where(t => t.DueDate is null).Take(10).Select(Describe),
            recordatoriosDeHoy = store.PendingReminders().Where(r => r.Due <= endOfDay)
                .Select(r => new { texto = r.Text, hora = r.Due.ToString("HH:mm") }),
            habitos = store.Habits().Select(h => new { habito = h.Name, hechoHoy = h.DoneToday, racha = h.Streak }),
        };
    }
}
