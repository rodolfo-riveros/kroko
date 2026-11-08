using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Web;

/// <summary>
/// Sección "Web". La clave de Tavily NO va aquí: .env (TAVILY_API_KEY=tvly-…), user-secrets
/// (Web:TavilyApiKey) o el asistente de bienvenida.
/// </summary>
public sealed class WebOptions
{
    public const string Section = "Web";

    public string? TavilyApiKey { get; set; }

    /// <summary>Orden de búsqueda: "gemini" (Google vía Gemini) y "tavily". Si el primero falla, se usa el siguiente.</summary>
    public List<string> SearchOrder { get; set; } = ["gemini", "tavily"];

    public string TavilyBaseUrl { get; set; } = "https://api.tavily.com";

    public bool TavilyConfigured => !string.IsNullOrWhiteSpace(TavilyApiKey);
}

/// <summary>
/// Tavily (app.tavily.com): buscador para asistentes de IA. Search devuelve resultados limpios y una
/// respuesta; Extract lee páginas (también las que bloquean a los programas o cargan con JavaScript).
/// Plan gratuito: 1.000 créditos al mes (búsqueda básica = 1 crédito; Extract básico = 1 crédito cada 5 páginas).
/// </summary>
public sealed class TavilyClient(HttpClient http, IOptionsMonitor<WebOptions> options, ILogger<TavilyClient> log)
{
    public bool IsConfigured => options.CurrentValue.TavilyConfigured;

    public sealed record SearchResult(string? Answer, IReadOnlyList<(string Title, string Url, string Content)> Results);

    public async Task<SearchResult?> SearchAsync(string query, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["query"] = query,
            ["search_depth"] = "basic",
            ["max_results"] = 5,
            ["include_answer"] = "basic",
        };
        using var doc = await PostAsync("/search", body, ct);
        if (doc is null) return null;

        var root = doc.RootElement;
        var answer = root.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        var results = new List<(string, string, string)>();
        if (root.TryGetProperty("results", out var rs))
        {
            foreach (var r in rs.EnumerateArray())
            {
                results.Add((r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    r.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                    r.TryGetProperty("content", out var c) ? c.GetString() ?? "" : ""));
            }
        }
        return new SearchResult(answer, results);
    }

    /// <summary>Texto de una página (Markdown) o null si no se pudo.</summary>
    public async Task<string?> ExtractAsync(string url, CancellationToken ct)
    {
        var body = new JsonObject { ["urls"] = new JsonArray(url), ["extract_depth"] = "basic", ["format"] = "markdown" };
        using var doc = await PostAsync("/extract", body, ct);
        if (doc is null) return null;
        if (doc.RootElement.TryGetProperty("results", out var rs) && rs.GetArrayLength() > 0 &&
            rs[0].TryGetProperty("raw_content", out var raw) && raw.ValueKind == JsonValueKind.String)
            return raw.GetString();
        return null;
    }

    private async Task<JsonDocument?> PostAsync(string path, JsonObject body, CancellationToken ct)
    {
        var opt = options.CurrentValue;
        if (!opt.TavilyConfigured) return null;
        using var req = new HttpRequestMessage(HttpMethod.Post, opt.TavilyBaseUrl.TrimEnd('/') + path) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opt.TavilyApiKey!.Trim());
        try
        {
            using var res = await http.SendAsync(req, ct);
            var json = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Tavily {Path} respondió {Status}", path, (int)res.StatusCode);
                return null;
            }
            return JsonDocument.Parse(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Tavily {Path} falló: {Message}", path, ex.Message);
            return null;
        }
    }
}
