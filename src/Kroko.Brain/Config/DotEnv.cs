using System.Text.RegularExpressions;

namespace Kroko.Brain.Config;

/// <summary>
/// Lee un archivo .env y lo añade a la configuración. Acepta:
/// - líneas "NOMBRE=valor" (p. ej. GEMINI_API_KEY=..., Email__AppPassword=...);
/// - una clave suelta sin nombre: si parece de Gemini (AIza… / AQ.…) se usa como Llm:Gemini:ApiKey,
///   si parece de DeepSeek (sk-…) como Llm:DeepSeek:ApiKey.
/// Búsqueda: KROKO_ENV_FILE, carpeta del Brain y sus padres (el repo en desarrollo), %LOCALAPPDATA%\Kroko\.env.
/// Nunca se registra el valor de nada en los logs.
/// </summary>
public static partial class DotEnv
{
    [GeneratedRegex(@"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_.:]*)\s*=\s*(.*)\s*$")]
    private static partial Regex Assignment();

    public static string? Find(string startDir)
    {
        if (Environment.GetEnvironmentVariable("KROKO_ENV_FILE") is { Length: > 0 } explicitFile && File.Exists(explicitFile))
            return explicitFile;

        for (var dir = new DirectoryInfo(startDir); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".env");
            if (File.Exists(candidate)) return candidate;
        }

        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", ".env");
        return File.Exists(local) ? local : null;
    }

    /// <summary>Convierte el .env en pares de configuración ("Llm:Gemini:ApiKey" = …).</summary>
    public static Dictionary<string, string?> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var m = Assignment().Match(line);
            if (m.Success)
            {
                var key = m.Groups[1].Value;
                var value = Unquote(m.Groups[2].Value);
                result[MapKey(key)] = value;
                continue;
            }

            // Clave suelta sin nombre
            var bare = Unquote(line);
            if (bare.Contains(' ')) continue;
            if (bare.StartsWith("AIza", StringComparison.Ordinal) || bare.StartsWith("AQ.", StringComparison.Ordinal))
                result.TryAdd("Llm:Gemini:ApiKey", bare);
            else if (bare.StartsWith("tvly-", StringComparison.Ordinal))
                result.TryAdd("Web:TavilyApiKey", bare);
            else if (bare.StartsWith("sk-", StringComparison.Ordinal))
                result.TryAdd("Llm:DeepSeek:ApiKey", bare);
        }
        return result;
    }

    /// <summary>Nombres habituales → claves de configuración de Kroko.</summary>
    private static string MapKey(string key) => key.ToUpperInvariant() switch
    {
        "GEMINI_API_KEY" or "GOOGLE_API_KEY" => "Llm:Gemini:ApiKey",
        "DEEPSEEK_API_KEY" => "Llm:DeepSeek:ApiKey",
        "TAVILY_API_KEY" => "Web:TavilyApiKey",
        "TRELLO_API_KEY" => "Trello:ApiKey",
        "TRELLO_TOKEN" => "Trello:Token",
        "GOOGLE_CLIENT_ID" => "Google:ClientId",
        "GOOGLE_CLIENT_SECRET" => "Google:ClientSecret",
        "GMAIL_ADDRESS" or "EMAIL_ADDRESS" => "Email:Address",
        "GMAIL_APP_PASSWORD" or "EMAIL_APP_PASSWORD" => "Email:AppPassword",
        "KROKO_TOKEN" => "Agent:Token",
        "TELEGRAM_BOT_TOKEN" => "Telegram:BotToken",
        _ => key.Replace("__", ":"),
    };

    private static string Unquote(string v)
    {
        v = v.Trim();
        if (v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\'')) v = v[1..^1];
        return v;
    }
}
