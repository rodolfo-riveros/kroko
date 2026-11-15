namespace Kroko.Brain.Providers;

/// <summary>
/// Varias claves de Gemini que se turnan (pedido por Pol, 14/11/2026: «de 2 a 4 claves gratis; si se acaba una, la
/// reemplaza otra»). Cuando una clave responde 402 (sin saldo) o 429 (límite de uso), se aparta un rato y se usa la
/// siguiente: 12 h si se quedó sin saldo o sin cuota del día, 2 min si es el límite por minuto. Lo comparten el chat,
/// la voz, la transcripción, la búsqueda, los vectores y la voz en tiempo real.
/// </summary>
public static class GeminiKeyRing
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, DateTimeOffset> Resting = [];

    /// <summary>Para las pruebas: el reloj se puede cambiar.</summary>
    internal static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>La primera clave que no está descansando; si todas descansan, la que vuelve antes.</summary>
    public static string? Pick(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0) return null;
        lock (Gate)
        {
            var now = Now();
            foreach (var k in keys)
                if (!Resting.TryGetValue(k, out var until) || until <= now) return k;
            return keys.OrderBy(k => Resting[k]).First();
        }
    }

    /// <summary>
    /// Avisa que una clave falló. Devuelve true si vale la pena reintentar con otra (era un problema de esa clave).
    /// </summary>
    public static bool Report(string? key, int? status, string? message = null)
    {
        if (string.IsNullOrEmpty(key) || status is not (402 or 429)) return false;
        var perDay = status == 402 || (message?.Contains("day", StringComparison.OrdinalIgnoreCase) ?? false)
                     || (message?.Contains("credits", StringComparison.OrdinalIgnoreCase) ?? false);
        lock (Gate) Resting[key] = Now() + (perDay ? TimeSpan.FromHours(12) : TimeSpan.FromMinutes(2));
        return true;
    }

    /// <summary>Cuántas claves están disponibles ahora mismo (para Ajustes).</summary>
    public static int Available(IReadOnlyList<string> keys)
    {
        lock (Gate)
        {
            var now = Now();
            return keys.Count(k => !Resting.TryGetValue(k, out var until) || until <= now);
        }
    }

    internal static void Reset()
    {
        lock (Gate) Resting.Clear();
    }
}
