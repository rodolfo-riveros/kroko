using Kroko.Brain.Memory;

namespace Kroko.Brain.Productivity;

public sealed record Reminder(long Id, string Text, DateTimeOffset Due, string Repeat, bool Delivered);

public sealed record TaskItem(long Id, string Text, DateOnly? DueDate, bool Done, DateTimeOffset Created);

/// <summary>
/// Un hábito con meta diaria (14/11/2026): "Beber agua" 8 vasos, "Leer" 1 vez… DoneToday = llegó a la meta hoy; la racha
/// cuenta días seguidos con la meta cumplida. Week = los últimos 7 días (de hace 6 días a hoy).
/// </summary>
public sealed record Habit(long Id, string Name, bool DoneToday, int Streak, int Last7Days)
{
    public int Goal { get; init; } = 1;
    public string Unit { get; init; } = "";
    public string Style { get; init; } = HabitStyles.Default;
    public int TodayCount { get; init; }
    public bool[] Week { get; init; } = new bool[7];
    public string RemindAt { get; init; } = "";
}

/// <summary>Recordatorios, tareas y hábitos de Pol (SQLite, sección 9 · Productividad).</summary>
public sealed class ProductivityStore(KrokoDatabase db, TimeProvider time)
{
    public static readonly string[] Repeats = ["nunca", "diario", "semanal", "laborables"];

    private DateTimeOffset Now => time.GetLocalNow();
    private DateOnly Today => DateOnly.FromDateTime(Now.DateTime);

    // ---------------------------------------------------------------- recordatorios

    public long AddReminder(string text, DateTimeOffset due, string repeat)
    {
        if (!Repeats.Contains(repeat)) repeat = "nunca";
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO reminders(text, due, repeat, created) VALUES($t, $d, $r, $c); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$t", text.Trim());
        cmd.Parameters.AddWithValue("$d", due.ToString("O"));
        cmd.Parameters.AddWithValue("$r", repeat);
        cmd.Parameters.AddWithValue("$c", KrokoDatabase.Now());
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<Reminder> PendingReminders()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, text, due, repeat, delivered FROM reminders WHERE delivered = 0 ORDER BY due";
        return ReadReminders(cmd);
    }

    /// <summary>Recordatorios vencidos que todavía no se entregaron.</summary>
    public IReadOnlyList<Reminder> DueReminders()
    {
        var now = Now;
        return PendingReminders().Where(r => r.Due <= now).ToList();
    }

    /// <summary>Marca como entregado; si se repite, lo reprograma para la siguiente vez.</summary>
    public void MarkDelivered(Reminder r)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        var next = NextOccurrence(r.Due, r.Repeat, Now);
        if (next is { } n)
        {
            cmd.CommandText = "UPDATE reminders SET due = $d WHERE id = $id";
            cmd.Parameters.AddWithValue("$d", n.ToString("O"));
        }
        else
        {
            cmd.CommandText = "UPDATE reminders SET delivered = 1 WHERE id = $id";
        }
        cmd.Parameters.AddWithValue("$id", r.Id);
        cmd.ExecuteNonQuery();
    }

    public bool DeleteReminder(long id) => Exec("DELETE FROM reminders WHERE id = $id", id);

    internal static DateTimeOffset? NextOccurrence(DateTimeOffset due, string repeat, DateTimeOffset now)
    {
        if (repeat == "nunca") return null;
        var next = due;
        do
        {
            next = repeat switch
            {
                "diario" => next.AddDays(1),
                "semanal" => next.AddDays(7),
                "laborables" => next.AddDays(next.DayOfWeek == DayOfWeek.Friday ? 3 : next.DayOfWeek == DayOfWeek.Saturday ? 2 : 1),
                _ => next.AddDays(1),
            };
        } while (next <= now);
        return next;
    }

    private static List<Reminder> ReadReminders(Microsoft.Data.Sqlite.SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<Reminder>();
        while (r.Read())
            list.Add(new Reminder(r.GetInt64(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2)), r.GetString(3), r.GetInt64(4) == 1));
        return list;
    }

    // ---------------------------------------------------------------- tareas

    public long AddTask(string text, DateOnly? due)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO tasks(text, due_date, created) VALUES($t, $d, $c); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$t", text.Trim());
        cmd.Parameters.AddWithValue("$d", (object?)due?.ToString("yyyy-MM-dd") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", KrokoDatabase.Now());
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<TaskItem> Tasks(bool includeDone)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        // Pendientes primero; dentro de cada grupo, por fecha límite y antigüedad. Las hechas, solo las de los últimos 7 días.
        cmd.CommandText = includeDone
            ? "SELECT id, text, due_date, done, created FROM tasks WHERE done = 0 OR done_at >= $since ORDER BY done, due_date IS NULL, due_date, id"
            : "SELECT id, text, due_date, done, created FROM tasks WHERE done = 0 ORDER BY due_date IS NULL, due_date, id";
        cmd.Parameters.AddWithValue("$since", Now.AddDays(-7).ToString("O"));
        using var r = cmd.ExecuteReader();
        var list = new List<TaskItem>();
        while (r.Read())
        {
            list.Add(new TaskItem(r.GetInt64(0), r.GetString(1),
                r.IsDBNull(2) ? null : DateOnly.Parse(r.GetString(2)), r.GetInt64(3) == 1, DateTimeOffset.Parse(r.GetString(4))));
        }
        return list;
    }

    public bool CompleteTask(long id, bool done = true)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE tasks SET done = $d, done_at = $at WHERE id = $id";
        cmd.Parameters.AddWithValue("$d", done ? 1 : 0);
        cmd.Parameters.AddWithValue("$at", done ? KrokoDatabase.Now() : DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool DeleteTask(long id) => Exec("DELETE FROM tasks WHERE id = $id", id);

    // ---------------------------------------------------------------- hábitos

    /// <summary>
    /// Crea el hábito (o actualiza su meta, unidad, estilo y avisos si ya existía). El estilo, si no se dice, se deduce
    /// del nombre ("Beber agua" → agua).
    /// </summary>
    public long AddHabit(string name, int goal = 1, string? unit = null, string? style = null, string? remindAt = null)
    {
        name = name.Trim();
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO habits(name, created, goal, unit, style, remind_at) VALUES($n, $c, $g, $u, $s, $r)
            ON CONFLICT(name) DO UPDATE SET
                goal = CASE WHEN $explicitGoal = 1 THEN excluded.goal ELSE habits.goal END,
                unit = CASE WHEN $u <> '' THEN excluded.unit ELSE habits.unit END,
                style = CASE WHEN $explicitStyle = 1 THEN excluded.style ELSE habits.style END,
                remind_at = CASE WHEN $explicitRemind = 1 THEN excluded.remind_at ELSE habits.remind_at END;
            SELECT id FROM habits WHERE name = $n;
            """;
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$c", KrokoDatabase.Now());
        cmd.Parameters.AddWithValue("$g", Math.Clamp(goal, 1, 99));
        cmd.Parameters.AddWithValue("$u", (unit ?? "").Trim());
        cmd.Parameters.AddWithValue("$s", HabitStyles.Normalize(style) ?? HabitStyles.Guess(name));
        cmd.Parameters.AddWithValue("$r", (remindAt ?? "").Trim());
        cmd.Parameters.AddWithValue("$explicitGoal", goal > 1 ? 1 : 0);
        cmd.Parameters.AddWithValue("$explicitStyle", HabitStyles.Normalize(style) is null ? 0 : 1);
        cmd.Parameters.AddWithValue("$explicitRemind", remindAt is null ? 0 : 1);
        return (long)cmd.ExecuteScalar()!;
    }

    public long? FindHabit(string name)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM habits WHERE name = $n OR name LIKE $like ORDER BY name = $n DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.Parameters.AddWithValue("$like", $"%{name.Trim()}%");
        return cmd.ExecuteScalar() as long?;
    }

    /// <summary>Marca el día como cumplido (llega a la meta) o lo borra.</summary>
    public void MarkHabit(long id, DateOnly day, bool done)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = done
            ? """
              INSERT INTO habit_log(habit_id, day, count) VALUES($id, $d, (SELECT goal FROM habits WHERE id = $id))
              ON CONFLICT(habit_id, day) DO UPDATE SET count = MAX(habit_log.count, (SELECT goal FROM habits WHERE id = $id))
              """
            : "DELETE FROM habit_log WHERE habit_id = $id AND day = $d";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Suma (o resta) a lo hecho ese día: "tomé un vaso de agua" = +1. Devuelve cuánto lleva.</summary>
    public int AddToHabit(long id, DateOnly day, int delta)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT count FROM habit_log WHERE habit_id = $id AND day = $d";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        var current = cmd.ExecuteScalar() is long n ? (int)n : 0;
        var next = Math.Clamp(current + delta, 0, 99);
        cmd.CommandText = next == 0
            ? "DELETE FROM habit_log WHERE habit_id = $id AND day = $d"
            : """
              INSERT INTO habit_log(habit_id, day, count) VALUES($id, $d, $n)
              ON CONFLICT(habit_id, day) DO UPDATE SET count = $n
              """;
        cmd.Parameters.AddWithValue("$n", next);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return next;
    }

    public bool DeleteHabit(long id) => Exec("DELETE FROM habits WHERE id = $id", id);

    public IReadOnlyList<Habit> Habits()
    {
        using var c = db.Open();
        var habits = new List<(long Id, string Name, int Goal, string Unit, string Style, string RemindAt)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, goal, unit, style, remind_at FROM habits ORDER BY created, name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                habits.Add((r.GetInt64(0), r.GetString(1), Math.Max(1, r.GetInt32(2)), r.GetString(3),
                    r.GetString(4).Length > 0 ? r.GetString(4) : HabitStyles.Guess(r.GetString(1)), r.GetString(5)));
        }

        var today = Today;
        var result = new List<Habit>();
        foreach (var h in habits)
        {
            var counts = new Dictionary<DateOnly, int>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT day, count FROM habit_log WHERE habit_id = $id AND day >= $since";
                cmd.Parameters.AddWithValue("$id", h.Id);
                cmd.Parameters.AddWithValue("$since", today.AddDays(-400).ToString("yyyy-MM-dd"));
                using var r = cmd.ExecuteReader();
                while (r.Read()) counts[DateOnly.Parse(r.GetString(0))] = r.GetInt32(1);
            }
            bool Done(DateOnly d) => counts.TryGetValue(d, out var n) && n >= h.Goal;

            // Racha: días seguidos con la meta cumplida hasta hoy (o hasta ayer si hoy aún no llega)
            var streak = 0;
            var day = Done(today) ? today : today.AddDays(-1);
            while (Done(day))
            {
                streak++;
                day = day.AddDays(-1);
            }
            var week = Enumerable.Range(0, 7).Select(i => Done(today.AddDays(i - 6))).ToArray();
            result.Add(new Habit(h.Id, h.Name, Done(today), streak, week.Count(x => x))
            {
                Goal = h.Goal,
                Unit = h.Unit,
                Style = h.Style,
                TodayCount = counts.GetValueOrDefault(today),
                Week = week,
                RemindAt = h.RemindAt,
            });
        }
        return result;
    }

    // ---------------------------------------------------------------- utilidades

    public string? GetValue(string key) => db.GetValue(key);

    public void SetValue(string key, string value) => db.SetValue(key, value);

    private bool Exec(string sql, long id)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }
}
