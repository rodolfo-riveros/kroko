using Microsoft.Extensions.Configuration;

namespace Kroko.Brain.Config;

/// <summary>Dice de qué fuente sale cada API key (.env, user-secrets, variable de entorno…) sin revelar su valor.</summary>
public static class KeySources
{
    private static readonly (string Name, string Key, string EnvFallback)[] Keys =
    [
        ("gemini", "Llm:Gemini:ApiKey", "GEMINI_API_KEY"),
        ("deepseek", "Llm:DeepSeek:ApiKey", "DEEPSEEK_API_KEY"),
        ("tavily", "Web:TavilyApiKey", "TAVILY_API_KEY"),
    ];

    public static Dictionary<string, string> Describe(IConfiguration configuration)
    {
        var result = new Dictionary<string, string>();
        var root = configuration as IConfigurationRoot;
        foreach (var (name, key, envFallback) in Keys)
        {
            string? source = null;
            if (root is not null)
            {
                // El último proveedor que tiene el valor es el que manda
                foreach (var provider in root.Providers.Reverse())
                {
                    if (provider.TryGet(key, out var v) && !string.IsNullOrWhiteSpace(v))
                    {
                        source = Label(provider);
                        break;
                    }
                }
            }
            if (source is null && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envFallback)))
                source = $"variable de entorno {envFallback}";
            result[name] = source ?? "falta";
        }
        return result;
    }

    private static string Label(IConfigurationProvider p)
    {
        var text = p.ToString() ?? p.GetType().Name;
        if (text.Contains("secrets.json", StringComparison.OrdinalIgnoreCase)) return "user-secrets";
        if (p is SecretConfigurationProvider) return "asistente Configurar Kroko";
        if (p.GetType().Name.Contains("Memory", StringComparison.Ordinal)) return ".env";
        if (p.GetType().Name.Contains("EnvironmentVariables", StringComparison.Ordinal)) return "variables de entorno";
        if (p.GetType().Name.Contains("CommandLine", StringComparison.Ordinal)) return "línea de comandos";
        return text;
    }
}
