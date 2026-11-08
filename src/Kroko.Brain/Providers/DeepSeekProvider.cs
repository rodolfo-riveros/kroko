using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Providers;

/// <summary>
/// DeepSeek por HTTP directo (API compatible con OpenAI): POST /chat/completions.
/// Reenvía el mensaje original del asistente (incluye reasoning_content, obligatorio con herramientas).
/// </summary>
public sealed class DeepSeekProvider(HttpClient http, IOptionsMonitor<LlmOptions> options, ILogger<DeepSeekProvider> log)
    : ILlmProvider
{
    public const string ProviderName = "deepseek";

    public string Name => ProviderName;
    public bool IsConfigured => !string.IsNullOrEmpty(options.CurrentValue.DeepSeek.ResolveApiKey());
    public bool SupportsAudio => false;
    public bool SupportsVision => false;

    public async Task<LlmResponse> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        var opt = options.CurrentValue;
        var key = opt.DeepSeek.ResolveApiKey() ?? throw new NoLlmConfiguredException();

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{opt.DeepSeek.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(BuildRequest(systemPrompt, history, tools, opt)),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmProviderException(Name, "No se pudo conectar con DeepSeek", null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmProviderException(Name, "DeepSeek tardó demasiado en responder", null, ex);
        }

        using (res)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new LlmProviderException(Name, $"DeepSeek respondió {(int)res.StatusCode}: {Trim(body)}", (int)res.StatusCode);
            return ParseResponse(body);
        }
    }

    internal static JsonObject BuildRequest(string systemPrompt, IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ToolDefinition> tools, LlmOptions opt)
    {
        var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });

        foreach (var m in history)
        {
            switch (m.Role)
            {
                case ChatRole.User:
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text });
                    break;
                case ChatRole.Tool:
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = m.ToolCallId,
                        // DeepSeek no ve imágenes: se le avisa en vez de mandarla
                        ["content"] = m.ImageBase64 is null ? m.Text : m.Text + " (Hay una captura, pero este modelo no puede ver imágenes.)",
                    });
                    break;
                default:
                    if (m.RawProvider == ProviderName && m.Raw is { } raw)
                    {
                        messages.Add(JsonNode.Parse(raw.GetRawText()));
                        break;
                    }
                    var msg = new JsonObject { ["role"] = "assistant", ["content"] = m.Text };
                    if (m.ToolCalls.Count > 0)
                    {
                        var calls = new JsonArray();
                        foreach (var c in m.ToolCalls)
                        {
                            calls.Add(new JsonObject
                            {
                                ["id"] = c.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.GetRawText() },
                            });
                        }
                        msg["tool_calls"] = calls;
                    }
                    messages.Add(msg);
                    break;
            }
        }

        var request = new JsonObject
        {
            ["model"] = opt.DeepSeek.Model,
            ["messages"] = messages,
            ["max_tokens"] = opt.MaxOutputTokens,
            ["thinking"] = new JsonObject { ["type"] = opt.DeepSeek.Thinking ? "enabled" : "disabled" },
            ["stream"] = false,
        };

        if (tools.Count > 0)
        {
            var list = new JsonArray();
            foreach (var t in tools)
            {
                list.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = JsonNode.Parse(t.Parameters.GetRawText()),
                    },
                });
            }
            request["tools"] = list;
        }

        return request;
    }

    internal LlmResponse ParseResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new LlmProviderException(Name, "DeepSeek no devolvió respuesta");

        var message = choices[0].GetProperty("message");
        var text = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()!.Trim()
            : "";

        var calls = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in tc.EnumerateArray())
            {
                var fn = call.GetProperty("function");
                var argsText = fn.TryGetProperty("arguments", out var a) ? a.GetString() : null;
                JsonElement args;
                try
                {
                    args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsText) ? "{}" : argsText).RootElement.Clone();
                }
                catch (JsonException)
                {
                    // La documentación avisa que los argumentos pueden venir mal formados: no rompemos el turno.
                    log.LogWarning("DeepSeek devolvió argumentos inválidos para {Tool}", fn.GetProperty("name").GetString());
                    args = JsonDocument.Parse("{}").RootElement.Clone();
                }
                calls.Add(new ToolCall(call.GetProperty("id").GetString() ?? $"call_{Guid.NewGuid():N}",
                    fn.GetProperty("name").GetString() ?? "", args));
            }
        }

        var assistant = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Text = text,
            ToolCalls = calls,
            RawProvider = ProviderName,
            Raw = message.Clone(),
        };
        return new LlmResponse(text, calls, assistant, Name);
    }

    private static string Trim(string s) => s.Length <= 400 ? s : s[..400] + "…";
}
