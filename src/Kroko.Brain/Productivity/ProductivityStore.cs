using Kroko.Brain.Memory;

namespace Kroko.Brain.Productivity;

public sealed record Reminder(long Id, string Text, DateTimeOffset Due, string Repeat, bool Delivered);

public sealed record TaskItem(long Id, string Text, DateOnly? DueDate, bool Done, DateTimeOffset Created);

public sealed record Habit(long Id, string Name, bool DoneToday, int Streak, int Last7Days);

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

    public long AddHabit(string name)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO habits(name, created) VALUES($n, $c) ON CONFLICT(name) DO NOTHING;
            SELECT id FROM habits WHERE name = $n;
            """;
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.Parameters.AddWithValue("$c", KrokoDatabase.Now());
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

    public void MarkHabit(long id, DateOnly day, bool done)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = done
            ? "INSERT OR IGNORE INTO habit_log(habit_id, day) VALUES($id, $d)"
            : "DELETE FROM habit_log WHERE habit_id = $id AND day = $d";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        cmd.ExecuteNonQuery();
    }

    public bool DeleteHabit(long id) => Exec("DELETE FROM habits WHERE id = $id", id);

    public IReadOnlyList<Habit> Habits()
    {
        using var c = db.Open();
        var habits = new List<(long Id, string Name)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name FROM habits ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read()) habits.Add((r.GetInt64(0), r.GetString(1)));
        }

        var today = Today;
        var result = new List<Habit>();
        foreach (var (id, name) in habits)
        {
            var days = new HashSet<DateOnly>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT day FROM habit_log WHERE habit_id = $id AND day >= $since";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$since", today.AddDays(-400).ToString("yyyy-MM-dd"));
                using var r = cmd.ExecuteReader();
                while (r.Read()) days.Add(DateOnly.Parse(r.GetString(0)));
            }
            // Racha: días seguidos hasta hoy (o hasta ayer si hoy aún no lo hizo)
            var streak = 0;
            var d = days.Contains(today) ? today : today.AddDays(-1);
            while (days.Contains(d))
            {
                streak++;
                d = d.AddDays(-1);
            }
            var last7 = Enumerable.Range(0, 7).Count(i => days.Contains(today.AddDays(-i)));
            result.Add(new Habit(id, name, days.Contains(today), streak, last7));
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
