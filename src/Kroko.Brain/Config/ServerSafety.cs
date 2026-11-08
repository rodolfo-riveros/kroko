namespace Kroko.Brain.Config;

public static class ServerSafety
{
    /// <summary>¿Alguna de las direcciones de escucha acepta conexiones de fuera de esta PC?</summary>
    public static bool ExposesOutsideThisPc(string urls)
    {
        foreach (var raw in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = raw.Replace("://*", "://wildcard").Replace("://+", "://wildcard");
            if (!Uri.TryCreate(text, UriKind.Absolute, out var u)) return true; // por si acaso, se trata como expuesto
            var host = u.Host.Trim('[', ']');
            if (host is not ("localhost" or "127.0.0.1" or "::1")) return true;
        }
        return false;
    }
}
