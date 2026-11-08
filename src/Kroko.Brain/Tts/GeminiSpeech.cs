using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kroko.Brain.Providers;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Tts;

/// <summary>Sección "Voice" de appsettings.json.</summary>
public sealed class VoiceOptions
{
    public const string Section = "Voice";

    public bool TtsEnabled { get; set; } = true;

    /// <summary>Modelo de voz de Gemini (verificado en la documentación oficial, 07/11/2026).</summary>
    public string TtsModel { get; set; } = "gemini-3.8-flash-tts";

    /// <summary>Voz prediseñada (Kore, Puck, Charon, Zephyr, Fenrir, Leda, Aoede…).</summary>
    public string Voice { get; set; } = "Puck";

    /// <summary>Voz en tiempo real (Gemini Live): conversación continua con audio en ambos sentidos.</summary>
    public bool LiveEnabled { get; set; } = true;

    /// <summary>Modelo de Gemini Live (verificado en la documentación oficial, 07/11/2026).</summary>
    public string LiveModel { get; set; } = "gemini-3.8-live";

    /// <summary>Cómo debe sonar (instrucción en lenguaje natural).</summary>
    public string Style { get; set; } = "Habla en español con acento {acento} suave, cálido, alegre y natural, a ritmo ágil.";

    /// <summary>Modelo para transcribir lo que dice Pol (vacío = el modelo principal de Gemini).</summary>
    public string? SttModel { get; set; }

    /// <summary>Textos más largos que esto no se convierten en voz (se leen en pantalla).</summary>
    public int MaxTtsChars { get; set; } = 1200;
}

/// <summary>Convierte texto en voz (sección 6). La App tiene un respaldo local si esto falla.</summary>
public interface ITtsProvider
{
    /// <summary>Devuelve un WAV o null si no se pudo (la App hablará con la voz de Windows).</summary>
    Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct);
}

/// <summary>Convierte la voz de Pol en texto.</summary>
public interface ISpeechToText
{
    Task<string?> TranscribeAsync(byte[] wav, CancellationToken ct);

    /// <summary>Otros formatos (las notas de voz de Telegram son audio/ogg).</summary>
    Task<string?> TranscribeAsync(byte[] audio, string mimeType, CancellationToken ct);
}

/// <summary>
/// Voz con Gemini: transcripción con generateContent (audio en línea) y síntesis con el modelo TTS
/// mediante la API Interactions (/v1beta/interactions), que devuelve un WAV de 24 kHz.
/// </summary>
public sealed class GeminiSpeech(HttpClient http, IOptionsMonitor<LlmOptions> llm, IOptionsMonitor<VoiceOptions> voice,
    Owner.OwnerProfileStore owner,
    ILogger<GeminiSpeech> log) : ITtsProvider, ISpeechToText
{
    public async Task<string?> TranscribeAsync(byte[] wav, CancellationToken ct) => await TranscribeAsync(wav, "audio/wav", ct);

    public async Task<string?> TranscribeAsync(byte[] audio, string mimeType, CancellationToken ct)
    {
        var opt = llm.CurrentValue.Gemini;
        var key = opt.ResolveApiKey() ?? throw new NoLlmConfiguredException();
        var model = string.IsNullOrWhiteSpace(voice.CurrentValue.SttModel) ? opt.Model : voice.CurrentValue.SttModel;

        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(
                    new JsonObject
                    {
                        ["text"] = "Transcribe exactamente lo que dice la persona (normalmente en español de Perú). " +
                                   "Habla con su asistente de escritorio, que se llama Kroko (escríbelo así). " +
                                   "Escribe los números con cifras y respeta nombres propios, programas y sitios web. " +
                                   "Responde solo con el texto transcrito, sin comillas ni comentarios. " +
                                   "Si no se entiende nada o solo hay ruido, responde con una cadena vacía.",
                    },
                    new JsonObject
                    {
                        ["inlineData"] = new JsonObject { ["mimeType"] = mimeType, ["data"] = Convert.ToBase64String(audio) },
                    }),
            }),
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = 512, ["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = "MINIMAL" } },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{opt.BaseUrl.TrimEnd('/')}/v1beta/models/{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("x-goog-api-key", key);
        using var res = await http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("Transcripción falló: {Status} {Body}", (int)res.StatusCode, Trim(json));
            throw new LlmProviderException("gemini", "No se pudo transcribir el audio", (int)res.StatusCode);
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("candidates", out var c) || c.GetArrayLength() == 0) return null;
        if (!c[0].TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts)) return null;
        var text = string.Concat(parts.EnumerateArray()
            .Where(p => !(p.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True))
            .Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : null)).Trim().Trim('"');
        return text.Length == 0 ? null : Kroko.Shared.KrokoName.Fix(text); // "croco", "cro co"… → Kroko
    }

    public async Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct)
    {
        var v = voice.CurrentValue;
        if (!v.TtsEnabled || string.IsNullOrWhiteSpace(text) || text.Length > v.MaxTtsChars) return null;
        var opt = llm.CurrentValue.Gemini;
        var key = opt.ResolveApiKey();
        if (key is null) return null;

        try
        {
            // Primero con estilo; si el servidor no acepta la anotación, se reintenta sin ella
            return await RequestAsync(text, v, key, opt.BaseUrl, withStyle: true, ct)
                   ?? await RequestAsync(text, v, key, opt.BaseUrl, withStyle: false, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Síntesis de voz falló: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<byte[]?> RequestAsync(string text, VoiceOptions v, string key, string baseUrl, bool withStyle, CancellationToken ct)
    {
        var textItem = new JsonObject { ["type"] = "text", ["text"] = text };
        if (withStyle && !string.IsNullOrWhiteSpace(v.Style))
            textItem["annotations"] = new JsonArray(new JsonObject
            {
                ["type"] = "speech_metadata",
                ["style"] = v.Style.Replace("{acento}", owner.Get().AccentOrDefault),
            });

        var body = new JsonObject
        {
            ["model"] = v.TtsModel,
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "user_input",
                ["content"] = new JsonArray(textItem),
            }),
            ["response_format"] = new JsonObject { ["type"] = "audio" },
            ["generation_config"] = new JsonObject
            {
                ["speech_config"] = new JsonArray(new JsonObject { ["voice"] = v.Voice }),
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/v1beta/interactions")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("x-goog-api-key", key);
        using var res = await http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("TTS ({Style}) respondió {Status}: {Body}", withStyle ? "con estilo" : "sin estilo", (int)res.StatusCode, Trim(json));
            return null;
        }

        var data = FindAudio(JsonNode.Parse(json));
        if (data is null)
        {
            log.LogWarning("TTS sin audio en la respuesta");
            return null;
        }
        var bytes = Convert.FromBase64String(data);
        return IsWav(bytes) ? bytes : WavFromPcm(bytes, 24000);
    }

    /// <summary>Busca el audio en steps[] (model_output → content[] type=audio → data), tolerando variaciones.</summary>
    internal static string? FindAudio(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["type"]?.GetValue<string>() == "audio" && obj["data"] is JsonValue d) return d.GetValue<string>();
                if (obj["output_audio"] is JsonObject oa && oa["data"] is JsonValue od) return od.GetValue<string>();
                foreach (var (_, child) in obj.Reverse())
                    if (FindAudio(child) is { } found) return found;
                return null;
            case JsonArray arr:
                for (var i = arr.Count - 1; i >= 0; i--)
                    if (FindAudio(arr[i]) is { } found) return found;
                return null;
            default:
                return null;
        }
    }

    internal static bool IsWav(byte[] b) => b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F';

    /// <summary>PCM 16 bits mono → WAV.</summary>
    internal static byte[] WavFromPcm(byte[] pcm, int sampleRate)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(sampleRate);
        w.Write(sampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
