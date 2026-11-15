namespace Kroko.Brain.Providers;

/// <summary>
/// Sección "Llm" de appsettings.json. Las API keys NO van ahí: se guardan con
/// dotnet user-secrets (Llm:Gemini:ApiKey, Llm:DeepSeek:ApiKey) o variables de entorno
/// (Llm__Gemini__ApiKey, GEMINI_API_KEY, DEEPSEEK_API_KEY).
/// </summary>
public sealed class LlmOptions
{
    public const string Section = "Llm";

    /// <summary>Orden de preferencia; si el primero falla se usa el siguiente.</summary>
    public List<string> Order { get; set; } = ["gemini", "deepseek"];

    public int MaxOutputTokens { get; set; } = 1024;
    public int TimeoutSeconds { get; set; } = 60;

    public GeminiOptions Gemini { get; set; } = new();
    public DeepSeekOptions DeepSeek { get; set; } = new();
}

public sealed class GeminiOptions
{
    public string Model { get; set; } = "gemini-3.6-flash";
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com";

    /// <summary>MINIMAL, LOW, MEDIUM o HIGH (vacío = el del modelo). LOW = respuestas rápidas.</summary>
    public string? ThinkingLevel { get; set; } = "LOW";

    /// <summary>Modelo de vectores para buscar en documentos por significado.</summary>
    public string EmbeddingModel { get; set; } = "gemini-embedding-2";

    public int EmbeddingDimensions { get; set; } = 768;

    public string? ApiKey { get; set; }

    /// <summary>Claves extra (gratis) que se turnan cuando una se agota (Configurar Kroko → Cerebro de IA).</summary>
    public string? ApiKey2 { get; set; }
    public string? ApiKey3 { get; set; }
    public string? ApiKey4 { get; set; }

    /// <summary>
    /// true (por defecto, decidido por Pol 15/11/2026): las claves vienen SOLO de la App conectada (AppProvidedKeys) y el
    /// cerebro ignora las de appsettings, .env, user-secrets o variables de entorno. false solo para pruebas.
    /// </summary>
    public bool KeysFromAppOnly { get; set; } = true;

    /// <summary>Todas las claves que se pueden usar ahora, sin repetir, en orden.</summary>
    public IReadOnlyList<string> AllApiKeys() => KeysFromAppOnly
        ? AppProvidedKeys.Gemini
        : [.. new[] { ApiKey, Environment.GetEnvironmentVariable("GEMINI_API_KEY"), ApiKey2, ApiKey3, ApiKey4 }
            .Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k!.Trim()).Distinct()];

    /// <summary>La clave que toca usar ahora (la primera que no esté agotada; ver GeminiKeyRing).</summary>
    public string? ResolveApiKey() => GeminiKeyRing.Pick(AllApiKeys());

    internal static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}

public sealed class DeepSeekOptions
{
    public string Model { get; set; } = "deepseek-flash";
    public string BaseUrl { get; set; } = "https://api.deepseek.com";

    /// <summary>Modo "pensando" (más lento y caro). Para un compañero de escritorio, apagado.</summary>
    public bool Thinking { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>true (por defecto): la clave viene solo de la App conectada, como las de Gemini.</summary>
    public bool KeysFromAppOnly { get; set; } = true;

    public string? ResolveApiKey() => KeysFromAppOnly
        ? AppProvidedKeys.DeepSeek
        : GeminiOptions.FirstNonEmpty(ApiKey, Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY"));
}
