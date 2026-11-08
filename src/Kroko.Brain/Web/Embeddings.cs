using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kroko.Brain.Agent;
using Kroko.Brain.Providers;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Web;

/// <summary>
/// Vectores de texto con Gemini (gemini-embedding-2) para que la App busque en los documentos por significado.
/// La clave de IA se queda en el cerebro: la App solo manda textos y recibe números.
/// </summary>
public sealed class EmbeddingService(HttpClient http, IOptionsMonitor<LlmOptions> options, ILogger<EmbeddingService> log)
{
    public const int MaxBatch = 100;

    /// <summary>kind = "documento" (con título) o "consulta".</summary>
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, string kind, IReadOnlyList<string?>? titles, CancellationToken ct)
    {
        var g = options.CurrentValue.Gemini;
        var key = g.ResolveApiKey() ?? throw new NoLlmConfiguredException();
        var model = "models/" + g.EmbeddingModel;
        var requests = new JsonArray();
        for (var i = 0; i < texts.Count; i++)
        {
            // gemini-embedding-2 no usa task_type: la tarea va como prefijo del texto
            var text = kind == "consulta"
                ? $"task: search result | query: {texts[i]}"
                : $"title: {(titles?.ElementAtOrDefault(i) is { Length: > 0 } t ? t : "none")} | text: {texts[i]}";
            requests.Add(new JsonObject
            {
                ["model"] = model,
                ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
                ["output_dimensionality"] = g.EmbeddingDimensions,
            });
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{g.BaseUrl.TrimEnd('/')}/v1beta/{model}:batchEmbedContents")
        {
            Content = JsonContent.Create(new JsonObject { ["requests"] = requests }),
        };
        req.Headers.Add("x-goog-api-key", key);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("Embeddings respondió {Status}", (int)res.StatusCode);
            throw new LlmProviderException("gemini", "no se pudieron calcular los vectores", (int)res.StatusCode);
        }
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.GetProperty("embeddings").EnumerateArray()
            .Select(e => e.GetProperty("values").EnumerateArray().Select(v => v.GetSingle()).ToArray())
            .ToArray();
    }
}

public static class EmbeddingApi
{
    public sealed record Request(List<string> Textos, string? Tipo, List<string?>? Titulos);

    public static void MapEmbeddingApi(this WebApplication app)
    {
        app.MapPost("/api/vectores", async (HttpContext ctx, Request body, EmbeddingService svc, IOptionsMonitor<AgentOptions> agent, CancellationToken ct) =>
        {
            if (!BrainAuth.IsAuthorized(ctx, agent.CurrentValue.Token)) return Results.Unauthorized();
            if (body.Textos is not { Count: > 0 } || body.Textos.Count > EmbeddingService.MaxBatch)
                return Results.BadRequest(new { error = $"Entre 1 y {EmbeddingService.MaxBatch} textos." });
            try
            {
                var vectors = await svc.EmbedAsync(body.Textos.Select(t => t.Length > 20_000 ? t[..20_000] : t).ToList(),
                    body.Tipo ?? "documento", body.Titulos, ct);
                return Results.Ok(new { vectores = vectors });
            }
            catch (Exception ex) when (ex is LlmProviderException or NoLlmConfiguredException or HttpRequestException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 503);
            }
        });
    }
}
