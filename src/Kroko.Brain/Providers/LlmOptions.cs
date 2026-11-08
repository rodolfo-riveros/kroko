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

    public string? ResolveApiKey() => FirstNonEmpty(ApiKey, Environment.GetEnvironmentVariable("GEMINI_API_KEY"));

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

    public string? ResolveApiKey() => GeminiOptions.FirstNonEmpty(ApiKey, Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY"));
}
