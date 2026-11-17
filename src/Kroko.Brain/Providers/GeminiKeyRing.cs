namespace Kroko.Brain.Providers;

/// <summary>Para qué se usa Gemini: cada trabajo tiene su clave preferida.</summary>
public enum GeminiUse
{
    Chat,
    Live,
    Transcribe,
    Speech,
    Search,
    Embeddings,
}

/// <summary>
/// Varias claves de Gemini (hasta 4) que se reparten el trabajo (pedido por Pol, 17/11/2026: «una para una cosa, la
/// otra para otra, así la tarea se reparte y sale más rápido»). Cada trabajo tiene su clave: con 4 claves, la 1 piensa
/// (chat y voz en tiempo real), la 2 entiende la voz, la 3 habla y la 4 busca (internet y documentos); con menos claves
/// los trabajos se reparten en ronda. Así los límites por minuto de una clave no frenan a los otros trabajos.
///
/// Si una clave falla, se aparta y ese trabajo usa la siguiente:
///  · 402 / sin créditos → la clave queda apartada 12 h para TODO (no tiene saldo).
///  · 429 → solo para ese trabajo (la cuota es por modelo): 12 h si es la del día, 2 min si es la del minuto.
///  · 500/503/504 (Gemini saturado) → 30 s solo para ese trabajo, y se reintenta en el acto con otra.
/// </summary>
public static class GeminiKeyRing
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<(string Key, GeminiUse? Use), DateTimeOffset> Resting = [];

    /// <summary>Para las pruebas: el reloj se puede cambiar.</summary>
    internal static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Qué clave (posición) prefiere cada trabajo, con 4 claves.</summary>
    internal static int PreferredSlot(GeminiUse use) => use switch
    {
        GeminiUse.Chat or GeminiUse.Live => 0,
        GeminiUse.Transcribe => 1,
        GeminiUse.Speech => 2,
        _ => 3,
    };

    /// <summary>Las claves en el orden en que las prueba ese trabajo: primero la suya y luego las demás en ronda.</summary>
    public static IReadOnlyList<string> Order(IReadOnlyList<string> keys, GeminiUse use)
    {
        if (keys.Count <= 1) return keys;
        var start = PreferredSlot(use) % keys.Count;
        return [.. Enumerable.Range(0, keys.Count).Select(i => keys[(start + i) % keys.Count])];
    }

    /// <summary>La clave de ese trabajo que no está apartada; si todas lo están, la que vuelve antes.</summary>
    public static string? Pick(IReadOnlyList<string> keys, GeminiUse use = GeminiUse.Chat)
    {
        if (keys.Count == 0) return null;
        lock (Gate)
        {
            var now = Now();
            var ordered = Order(keys, use);
            foreach (var k in ordered)
                if (RestingUntil(k, use) <= now) return k;
            return ordered.OrderBy(k => RestingUntil(k, use)).First();
        }
    }

    private static DateTimeOffset RestingUntil(string key, GeminiUse use)
    {
        var all = Resting.TryGetValue((key, null), out var a) ? a : DateTimeOffset.MinValue;
        var one = Resting.TryGetValue((key, use), out var b) ? b : DateTimeOffset.MinValue;
        return all > one ? all : one;
    }

    /// <summary>
    /// Avisa que una clave falló en ese trabajo. Devuelve true si vale la pena reintentar en el acto con otra clave.
    /// </summary>
    public static bool Report(string? key, int? status, string? message = null, GeminiUse use = GeminiUse.Chat)
    {
        if (string.IsNullOrEmpty(key) || status is null) return false;
        var credits = status == 402 || (message?.Contains("credits", StringComparison.OrdinalIgnoreCase) ?? false);
        TimeSpan rest;
        GeminiUse? scope = use;
        if (credits)
        {
            rest = TimeSpan.FromHours(12);
            scope = null; // sin saldo: no sirve para nada
        }
        else if (status == 429)
        {
            rest = message?.Contains("day", StringComparison.OrdinalIgnoreCase) == true ? TimeSpan.FromHours(12) : TimeSpan.FromMinutes(2);
        }
        else if (status is 500 or 503 or 504)
        {
            rest = TimeSpan.FromSeconds(30);
        }
        else return false;

        lock (Gate) Resting[(key, scope)] = Now() + rest;
        return true;
    }

    /// <summary>Cuántas claves pueden pensar (chat) ahora mismo (para Ajustes).</summary>
    public static int Available(IReadOnlyList<string> keys, GeminiUse use = GeminiUse.Chat)
    {
        lock (Gate)
        {
            var now = Now();
            return keys.Count(k => RestingUntil(k, use) <= now);
        }
    }

    internal static void Reset()
    {
        lock (Gate) Resting.Clear();
    }
}
