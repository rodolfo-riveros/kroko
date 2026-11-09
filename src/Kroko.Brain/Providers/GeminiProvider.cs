using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Providers;

/// <summary>
/// Gemini por HTTP directo: POST /v1beta/models/{modelo}:generateContent.
/// Devuelve en cada turno el "content" original del modelo para conservar sus thought signatures.
/// </summary>
public sealed class GeminiProvider(HttpClient http, IOptionsMonitor<LlmOptions> options, ILogger<GeminiProvider> log)
    : ILlmProvider
{
    public const string ProviderName = "gemini";

    /// <summary>Ids inventados por Kroko cuando Gemini no manda uno: no se le reenvían.</summary>
    internal const string GeneratedIdPrefix = "kroko_";

    public string Name => ProviderName;
    public bool IsConfigured => !string.IsNullOrEmpty(options.CurrentValue.Gemini.ResolveApiKey());
    public bool SupportsAudio => true;
    public bool SupportsVision => true;

    public async Task<LlmResponse> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        var opt = options.CurrentValue;
        var key = opt.Gemini.ResolveApiKey() ?? throw new NoLlmConfiguredException();

        var url = $"{opt.Gemini.BaseUrl.TrimEnd('/')}/v1beta/models/{Uri.EscapeDataString(opt.Gemini.Model)}:generateContent";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(BuildRequest(systemPrompt, history, tools, opt)),
        };
        req.Headers.Add("x-goog-api-key", key);

        using var res = await SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new LlmProviderException(Name, $"Gemini respondió {(int)res.StatusCode}: {Trim(body)}", (int)res.StatusCode);

        return ParseResponse(body);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmProviderException(Name, "No se pudo conectar con Gemini", null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmProviderException(Name, "Gemini tardó demasiado en responder", null, ex);
        }
    }

    internal static JsonObject BuildRequest(string systemPrompt, IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ToolDefinition> tools, LlmOptions opt)
    {
        var contents = new JsonArray();
        JsonArray? pendingToolParts = null;

        foreach (var m in history)
        {
            if (m.Role == ChatRole.Tool)
            {
                // Varias respuestas de herramientas seguidas van juntas en un mismo turno "user"
                if (pendingToolParts is null)
                {
                    pendingToolParts = [];
                    contents.Add(new JsonObject { ["role"] = "user", ["parts"] = pendingToolParts });
                }
                var response = new JsonObject
                {
                    ["name"] = m.ToolName,
                    ["response"] = new JsonObject { ["result"] = ParseOrString(m.Text) },
                };
                if (IsRealId(m.ToolCallId)) response["id"] = m.ToolCallId;
                if (m.ImageBase64 is { Length: > 0 } image)
                {
                    // Capturas de pantalla: Gemini las recibe como imagen dentro de la respuesta de la función
                    response["parts"] = new JsonArray(new JsonObject
                    {
                        ["inlineData"] = new JsonObject { ["mimeType"] = m.ImageMime ?? "image/jpeg", ["data"] = image },
                    });
                }
                pendingToolParts.Add(new JsonObject { ["functionResponse"] = response });
                continue;
            }
            pendingToolParts = null;

            if (m.Role == ChatRole.User)
            {
                contents.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = m.Text }),
                });
            }
            else if (m.RawProvider == ProviderName && m.Raw is { } raw)
            {
                contents.Add(JsonNode.Parse(raw.GetRawText()));
            }
            else
            {
                var parts = new JsonArray();
                if (!string.IsNullOrEmpty(m.Text)) parts.Add(new JsonObject { ["text"] = m.Text });
                foreach (var c in m.ToolCalls)
                {
                    var call = new JsonObject
                    {
                        ["name"] = c.Name,
                        ["args"] = JsonNode.Parse(c.Arguments.GetRawText()),
                    };
                    if (IsRealId(c.Id)) call["id"] = c.Id;
                    parts.Add(new JsonObject { ["functionCall"] = call });
                }
                if (parts.Count == 0) parts.Add(new JsonObject { ["text"] = "" });
                contents.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
            }
        }

        var generation = new JsonObject { ["maxOutputTokens"] = opt.MaxOutputTokens };
        if (!string.IsNullOrWhiteSpace(opt.Gemini.ThinkingLevel))
            generation["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = opt.Gemini.ThinkingLevel };

        var request = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = systemPrompt }),
            },
            ["contents"] = contents,
            ["generationConfig"] = generation,
        };

        if (tools.Count > 0)
        {
            var decls = new JsonArray();
            foreach (var t in tools)
            {
                decls.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parametersJsonSchema"] = JsonNode.Parse(t.Parameters.GetRawText()),
                });
            }
            request["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = decls });
        }

        return request;
    }

    internal LlmResponse ParseResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            var reason = root.TryGetProperty("promptFeedback", out var fb) && fb.TryGetProperty("blockReason", out var br)
                ? br.GetString()
                : "sin candidatos";
            log.LogWarning("Gemini no devolvió respuesta ({Reason})", reason);
            throw new LlmProviderException(Name, $"Gemini no devolvió respuesta ({reason})");
        }

        var candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var content))
        {
            var finish = candidate.TryGetProperty("finishReason", out var fr) ? fr.GetString() : "?";
            throw new LlmProviderException(Name, $"Gemini terminó sin contenido ({finish})");
        }

        var text = new List<string>();
        var calls = new List<ToolCall>();
        if (content.TryGetProperty("parts", out var parts))
        {
            foreach (var p in parts.EnumerateArray())
            {
                if (p.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True)
                    continue;
                if (p.TryGetProperty("text", out var t)) text.Add(t.GetString() ?? "");
                if (p.TryGetProperty("functionCall", out var fc))
                {
                    var id = fc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var args = fc.TryGetProperty("args", out var a) ? a.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                    calls.Add(new ToolCall(id ?? GeneratedIdPrefix + Guid.NewGuid().ToString("N"), fc.GetProperty("name").GetString() ?? "", args));
                }
            }
        }

        var joined = string.Concat(text).Trim();
        var assistant = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Text = joined,
            ToolCalls = calls,
            RawProvider = ProviderName,
            Raw = content.Clone(),
        };
        return new LlmResponse(joined, calls, assistant, Name, Kroko.Brain.Usage.UsageTracker.FromGemini(root));
    }

    private static JsonNode? ParseOrString(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    private static bool IsRealId(string? id) => !string.IsNullOrEmpty(id) && !id.StartsWith(GeneratedIdPrefix, StringComparison.Ordinal);

    private static string Trim(string s) => s.Length <= 400 ? s : s[..400] + "…";
}
