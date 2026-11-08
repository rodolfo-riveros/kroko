using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Memory;

/// <summary>Sección "Memory" de appsettings.json.</summary>
public sealed class MemoryOptions
{
    public const string Section = "Memory";

    /// <summary>Archivo SQLite (vacío = %LOCALAPPDATA%\Kroko\kroko.db).</summary>
    public string? DatabasePath { get; set; }

    /// <summary>Copia legible del buzón de mejoras (vacío = %LOCALAPPDATA%\Kroko\buzon-de-mejoras.md).</summary>
    public string? SuggestionsFile { get; set; }

    /// <summary>Máximo de recuerdos que se le muestran a la IA en cada orden.</summary>
    public int MaxMemoriesInPrompt { get; set; } = 80;

    internal static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko");
}

/// <summary>
/// La memoria permanente de Kroko (sección 7): un archivo SQLite con recuerdos, rutinas,
/// buzón de mejoras e historial de conversación. Crea las tablas la primera vez.
/// </summary>
public sealed class KrokoDatabase
{
    private const int SchemaVersion = 3;
    private readonly string _connectionString;

    public KrokoDatabase(IOptions<MemoryOptions> options, ILogger<KrokoDatabase> log)
    {
        var path = string.IsNullOrWhiteSpace(options.Value.DatabasePath)
            ? Path.Combine(MemoryOptions.DefaultFolder, "kroko.db")
            : Environment.ExpandEnvironmentVariables(options.Value.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        DatabasePath = path;

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();

        Migrate();
        log.LogInformation("Memoria de Kroko en {Path}", path);
    }

    public string DatabasePath { get; }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 3000;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        using (var wal = c.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteNonQuery();
        }

        using var version = c.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        var current = Convert.ToInt32(version.ExecuteScalar());
        if (current >= SchemaVersion) return;

        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS memories (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                category  TEXT NOT NULL,
                content   TEXT NOT NULL,
                created   TEXT NOT NULL,
                updated   TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS routines (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                name      TEXT NOT NULL UNIQUE COLLATE NOCASE,
                triggers  TEXT NOT NULL,
                steps     TEXT NOT NULL,
                created   TEXT NOT NULL,
                updated   TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS suggestions (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                kind      TEXT NOT NULL,
                title     TEXT NOT NULL,
                detail    TEXT NOT NULL,
                times     INTEGER NOT NULL DEFAULT 1,
                status    TEXT NOT NULL DEFAULT 'nueva',
                created   TEXT NOT NULL,
                updated   TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS messages (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation  TEXT NOT NULL,
                json          TEXT NOT NULL,
                created       TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_messages_conversation ON messages(conversation, id);
            CREATE TABLE IF NOT EXISTS summaries (
                conversation  TEXT PRIMARY KEY,
                text          TEXT NOT NULL,
                updated       TEXT NOT NULL
            );
            -- Versión 2: productividad (Fase 2)
            CREATE TABLE IF NOT EXISTS reminders (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                text       TEXT NOT NULL,
                due        TEXT NOT NULL,
                repeat     TEXT NOT NULL DEFAULT 'nunca',
                delivered  INTEGER NOT NULL DEFAULT 0,
                created    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_reminders_due ON reminders(delivered, due);
            CREATE TABLE IF NOT EXISTS tasks (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                text       TEXT NOT NULL,
                due_date   TEXT,
                done       INTEGER NOT NULL DEFAULT 0,
                created    TEXT NOT NULL,
                done_at    TEXT
            );
            CREATE TABLE IF NOT EXISTS habits (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                name       TEXT NOT NULL UNIQUE COLLATE NOCASE,
                created    TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS habit_log (
                habit_id   INTEGER NOT NULL REFERENCES habits(id) ON DELETE CASCADE,
                day        TEXT NOT NULL,
                PRIMARY KEY (habit_id, day)
            );
            CREATE TABLE IF NOT EXISTS kv (
                key        TEXT PRIMARY KEY,
                value      TEXT NOT NULL
            );
            -- Versión 3: automatizaciones programadas
            CREATE TABLE IF NOT EXISTS automations (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                name         TEXT NOT NULL,
                instructions TEXT NOT NULL,
                days         TEXT NOT NULL,
                time         TEXT NOT NULL,
                deliver      TEXT NOT NULL,
                enabled      INTEGER NOT NULL DEFAULT 1,
                last_run     TEXT,
                created      TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"PRAGMA user_version = {SchemaVersion};";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public static string Now() => DateTimeOffset.Now.ToString("O");

    /// <summary>Valor guardado en la tabla clave/valor (o null).</summary>
    public string? GetValue(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM kv WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetValue(string key, string value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO kv(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
