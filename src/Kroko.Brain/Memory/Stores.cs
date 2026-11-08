using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Memory;

public sealed record MemoryItem(long Id, string Category, string Content, DateTimeOffset Updated);

public sealed record Routine(long Id, string Name, IReadOnlyList<string> Triggers, IReadOnlyList<string> Steps, DateTimeOffset Updated);

public sealed record Suggestion(long Id, string Kind, string Title, string Detail, int Times, string Status, DateTimeOffset Updated);

/// <summary>Contenido que Kroko se niega a guardar (sección 8: nunca contraseñas ni datos bancarios).</summary>
public static partial class SensitiveData
{
    [GeneratedRegex(@"contrase(ñ|n)a|password|passwd|\bclave\s+(de|del)\s+(banco|tarjeta|correo|wifi|acceso)|\bpin\b|\bcvv\b|\bcvc\b", RegexOptions.IgnoreCase)]
    private static partial Regex Words();

    [GeneratedRegex(@"(?:\d[ -]?){13,19}")]
    private static partial Regex CardNumber();

    public static bool LooksSensitive(string text) => Words().IsMatch(text) || CardNumber().IsMatch(text);
}

/// <summary>Lo que Kroko sabe de Pol: preferencias, datos, personas, hábitos.</summary>
public sealed class MemoryStore(KrokoDatabase db)
{
    public static readonly string[] Categories = ["preferencia", "dato", "persona", "habito", "otro"];

    public IReadOnlyList<MemoryItem> All(int? limit = null)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, category, content, updated FROM memories ORDER BY updated DESC" + (limit is { } l ? $" LIMIT {l}" : "");
        using var r = cmd.ExecuteReader();
        var list = new List<MemoryItem>();
        while (r.Read()) list.Add(new MemoryItem(r.GetInt64(0), r.GetString(1), r.GetString(2), DateTimeOffset.Parse(r.GetString(3))));
        return list;
    }

    /// <summary>Guarda un recuerdo (si ya existe uno igual, solo lo actualiza). Devuelve su id.</summary>
    public long Remember(string content, string category)
    {
        content = content.Trim();
        if (!Categories.Contains(category)) category = "otro";
        using var c = db.Open();

        using (var find = c.CreateCommand())
        {
            find.CommandText = "SELECT id FROM memories WHERE lower(content) = lower($c) LIMIT 1";
            find.Parameters.AddWithValue("$c", content);
            if (find.ExecuteScalar() is long existing)
            {
                Touch(c, existing, content, category);
                return existing;
            }
        }

        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO memories(category, content, created, updated) VALUES($k, $c, $t, $t); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$k", category);
        cmd.Parameters.AddWithValue("$c", content);
        cmd.Parameters.AddWithValue("$t", KrokoDatabase.Now());
        return (long)cmd.ExecuteScalar()!;
    }

    public bool Update(long id, string content)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE memories SET content = $c, updated = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$c", content.Trim());
        cmd.Parameters.AddWithValue("$t", KrokoDatabase.Now());
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool Forget(long id)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM memories WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static void Touch(Microsoft.Data.Sqlite.SqliteConnection c, long id, string content, string category)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE memories SET content = $c, category = $k, updated = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$c", content);
        cmd.Parameters.AddWithValue("$k", category);
        cmd.Parameters.AddWithValue("$t", KrokoDatabase.Now());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>Rutinas que Pol le enseña: nombre, frases que la activan y pasos en lenguaje natural.</summary>
public sealed class RoutineStore(KrokoDatabase db)
{
    public IReadOnlyList<Routine> All()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, triggers, steps, updated FROM routines ORDER BY name";
        using var r = cmd.ExecuteReader();
        var list = new List<Routine>();
        while (r.Read())
        {
            list.Add(new Routine(r.GetInt64(0), r.GetString(1),
                JsonSerializer.Deserialize<string[]>(r.GetString(2)) ?? [],
                JsonSerializer.Deserialize<string[]>(r.GetString(3)) ?? [],
                DateTimeOffset.Parse(r.GetString(4))));
        }
        return list;
    }

    /// <summary>Crea o reemplaza una rutina (por nombre).</summary>
    public long Save(string name, IReadOnlyList<string> triggers, IReadOnlyList<string> steps)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO routines(name, triggers, steps, created, updated) VALUES($n, $tr, $st, $t, $t)
            ON CONFLICT(name) DO UPDATE SET triggers = $tr, steps = $st, updated = $t;
            SELECT id FROM routines WHERE name = $n;
            """;
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.Parameters.AddWithValue("$tr", JsonSerializer.Serialize(triggers));
        cmd.Parameters.AddWithValue("$st", JsonSerializer.Serialize(steps));
        cmd.Parameters.AddWithValue("$t", KrokoDatabase.Now());
        return (long)cmd.ExecuteScalar()!;
    }

    public bool Delete(string name)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM routines WHERE name = $n";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool Delete(long id)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM routines WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }
}

/// <summary>
/// Buzón de mejoras: lo que Kroko no supo hacer o lo que falló. Se mantiene una copia legible
/// en Markdown para revisarla e implementar las mejoras (con revisión de Pol).
/// </summary>
public sealed class SuggestionStore(KrokoDatabase db, IOptions<MemoryOptions> options, ILogger<SuggestionStore> log)
{
    public string FilePath => string.IsNullOrWhiteSpace(options.Value.SuggestionsFile)
        ? Path.Combine(MemoryOptions.DefaultFolder, "buzon-de-mejoras.md")
        : Environment.ExpandEnvironmentVariables(options.Value.SuggestionsFile);

    public IReadOnlyList<Suggestion> All()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, kind, title, detail, times, status, updated FROM suggestions ORDER BY status = 'hecha', times DESC, updated DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<Suggestion>();
        while (r.Read())
            list.Add(new Suggestion(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), DateTimeOffset.Parse(r.GetString(6))));
        return list;
    }

    /// <summary>Anota una idea o un fallo. Si ya existe con el mismo título, suma una vez más.</summary>
    public long Add(string kind, string title, string detail)
    {
        kind = kind is "fallo" ? "fallo" : "idea";
        title = title.Trim();
        long id;
        using (var c = db.Open())
        {
            using var find = c.CreateCommand();
            find.CommandText = "SELECT id FROM suggestions WHERE lower(title) = lower($t) AND status <> 'hecha' LIMIT 1";
            find.Parameters.AddWithValue("$t", title);
            if (find.ExecuteScalar() is long existing)
            {
                using var upd = c.CreateCommand();
                upd.CommandText = "UPDATE suggestions SET times = times + 1, detail = $d, updated = $now WHERE id = $id";
                upd.Parameters.AddWithValue("$d", detail.Trim());
                upd.Parameters.AddWithValue("$now", KrokoDatabase.Now());
                upd.Parameters.AddWithValue("$id", existing);
                upd.ExecuteNonQuery();
                id = existing;
            }
            else
            {
                using var ins = c.CreateCommand();
                ins.CommandText = "INSERT INTO suggestions(kind, title, detail, created, updated) VALUES($k, $t, $d, $now, $now); SELECT last_insert_rowid();";
                ins.Parameters.AddWithValue("$k", kind);
                ins.Parameters.AddWithValue("$t", title);
                ins.Parameters.AddWithValue("$d", detail.Trim());
                ins.Parameters.AddWithValue("$now", KrokoDatabase.Now());
                id = (long)ins.ExecuteScalar()!;
            }
        }
        Export();
        return id;
    }

    public bool SetStatus(long id, string status)
    {
        using (var c = db.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE suggestions SET status = $s, updated = $now WHERE id = $id";
            cmd.Parameters.AddWithValue("$s", status);
            cmd.Parameters.AddWithValue("$now", KrokoDatabase.Now());
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() == 0) return false;
        }
        Export();
        return true;
    }

    public bool Delete(long id)
    {
        using (var c = db.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM suggestions WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() == 0) return false;
        }
        Export();
        return true;
    }

    /// <summary>Escribe el buzón como Markdown (para leerlo y convertirlo en tareas de desarrollo).</summary>
    private void Export()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Buzón de mejoras de Kroko");
            sb.AppendLine();
            sb.AppendLine("Lo que Kroko no supo hacer o lo que le falló. Se genera solo; no lo edites a mano.");
            sb.AppendLine();
            foreach (var s in All())
            {
                var mark = s.Status == "hecha" ? "x" : " ";
                sb.AppendLine($"- [{mark}] **#{s.Id} {s.Title}** ({s.Kind}, {s.Times} vez/veces, {s.Updated:yyyy-MM-dd})");
                if (!string.IsNullOrWhiteSpace(s.Detail)) sb.AppendLine($"  - {s.Detail.ReplaceLineEndings(" ")}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "No se pudo escribir {Path}", FilePath);
        }
    }
}
