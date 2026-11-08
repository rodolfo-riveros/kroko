using System.Text.Json;
using Kroko.Brain.Memory;

namespace Kroko.Brain.Owner;

/// <summary>
/// Quién es el dueño de esta instalación de Kroko. Se llena en el asistente de bienvenida
/// (la primera vez que se abre la App) y se puede cambiar cuando quiera.
/// </summary>
public sealed record OwnerProfile
{
    /// <summary>Nombre completo, p. ej. "Rodolfo Riveros".</summary>
    public string Name { get; init; } = "";
    /// <summary>Cómo quiere que Kroko lo llame (vacío = primer nombre).</summary>
    public string CallMe { get; init; } = "";
    public string City { get; init; } = "";
    public string Country { get; init; } = "";
    /// <summary>Zona horaria IANA (America/Lima) o de Windows; vacía = la del equipo.</summary>
    public string TimeZone { get; init; } = "";
    /// <summary>Acento de Kroko: "peruano", "mexicano", "neutro"…</summary>
    public string Accent { get; init; } = "";
    /// <summary>Lo que el dueño quiera contarle de sí mismo (trabajo, intereses…).</summary>
    public string About { get; init; } = "";

    public bool IsComplete => !string.IsNullOrWhiteSpace(Name);

    /// <summary>Cómo se dirige Kroko a su dueño.</summary>
    public string Nickname => !string.IsNullOrWhiteSpace(CallMe) ? CallMe.Trim()
        : IsComplete ? Name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0] : "";

    public string Location => string.Join(", ", new[] { City, Country }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));

    public string AccentOrDefault => string.IsNullOrWhiteSpace(Accent) ? "latinoamericano neutro" : Accent.Trim();

    /// <summary>Recorta y limita cada campo (lo escribe una persona en un formulario).</summary>
    public OwnerProfile Normalized() => this with
    {
        Name = Clip(Name, 80),
        CallMe = Clip(CallMe, 40),
        City = Clip(City, 60),
        Country = Clip(Country, 60),
        TimeZone = Clip(TimeZone, 64),
        Accent = Clip(Accent, 40),
        About = Clip(About, 1200),
    };

    private static string Clip(string? s, int max)
    {
        s = (s ?? "").Replace('\r', ' ').Trim();
        return s.Length <= max ? s : s[..max];
    }
}

/// <summary>Guarda el perfil en la tabla kv de kroko.db. Lectura en caché; avisa cuando cambia.</summary>
public sealed class OwnerProfileStore(KrokoDatabase db, ILogger<OwnerProfileStore> log)
{
    private const string Key = "owner.profile";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _lock = new();
    private OwnerProfile? _cached;

    public event Action<OwnerProfile>? Changed;

    public OwnerProfile Get()
    {
        lock (_lock)
        {
            if (_cached is not null) return _cached;
            try
            {
                _cached = db.GetValue(Key) is { Length: > 0 } json
                    ? JsonSerializer.Deserialize<OwnerProfile>(json, Json) ?? new OwnerProfile()
                    : new OwnerProfile();
            }
            catch (Exception ex) when (ex is JsonException or Microsoft.Data.Sqlite.SqliteException)
            {
                log.LogWarning(ex, "No se pudo leer el perfil del dueño");
                return new OwnerProfile();
            }
            return _cached;
        }
    }

    public OwnerProfile Save(OwnerProfile profile)
    {
        profile = profile.Normalized();
        if (profile.TimeZone.Length > 0 && OwnerTimeProvider.FindZone(profile.TimeZone) is null)
            throw new ArgumentException($"No conozco la zona horaria \"{profile.TimeZone}\".");
        lock (_lock)
        {
            db.SetValue(Key, JsonSerializer.Serialize(profile, Json));
            _cached = profile;
        }
        log.LogInformation("Perfil del dueño guardado ({Name})", profile.Nickname);
        Changed?.Invoke(profile);
        return profile;
    }
}

/// <summary>
/// Reloj de Kroko: la hora "local" es la del dueño (su zona horaria del perfil), no la del equipo.
/// Importa en el servidor, donde el contenedor vive en UTC.
/// </summary>
public sealed class OwnerTimeProvider(OwnerProfileStore profiles) : TimeProvider
{
    public override TimeZoneInfo LocalTimeZone =>
        FindZone(profiles.Get().TimeZone) ?? TimeZoneInfo.Local;

    /// <summary>Acepta ids IANA ("America/Lima") o de Windows ("SA Pacific Standard Time").</summary>
    public static TimeZoneInfo? FindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>Convierte una fecha a la hora del dueño.</summary>
    public static DateTimeOffset ToOwner(TimeProvider time, DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, time.LocalTimeZone);
}
