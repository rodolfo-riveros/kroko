namespace Kroko.Brain.Providers;

/// <summary>
/// Las claves de IA que la App le presta al cerebro (decidido por Pol, 15/11/2026: «el cerebro no puede tener la API de
/// Gemini»). Viven solo en memoria mientras la App está conectada; nunca se escriben en disco ni en los registros, y se
/// olvidan cuando la App se desconecta. Cada persona tiene su propio cerebro, así que basta una sola copia.
/// </summary>
public static class AppProvidedKeys
{
    private static readonly Lock Gate = new();
    private static IReadOnlyList<string> _gemini = [];
    private static string? _deepSeek;
    private static object? _owner;

    public static IReadOnlyList<string> Gemini
    {
        get { lock (Gate) return _gemini; }
    }

    public static string? DeepSeek
    {
        get { lock (Gate) return _deepSeek; }
    }

    /// <summary>¿Hay alguna App conectada que haya prestado claves?</summary>
    public static bool HasAny
    {
        get { lock (Gate) return _gemini.Count > 0 || !string.IsNullOrEmpty(_deepSeek); }
    }

    /// <summary>La App (owner = su conexión) presta sus claves; reemplazan a las anteriores.</summary>
    public static void Set(object owner, IEnumerable<string> gemini, string? deepSeek)
    {
        lock (Gate)
        {
            _owner = owner;
            _gemini = [.. gemini.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct().Take(4)];
            _deepSeek = string.IsNullOrWhiteSpace(deepSeek) ? null : deepSeek.Trim();
        }
    }

    /// <summary>Esa conexión se cerró: si era la que prestó las claves, se olvidan.</summary>
    public static void Forget(object owner)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(owner, _owner)) return;
            _owner = null;
            _gemini = [];
            _deepSeek = null;
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            _owner = null;
            _gemini = [];
            _deepSeek = null;
        }
    }
}
