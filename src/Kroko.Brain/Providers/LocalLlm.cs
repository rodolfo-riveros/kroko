using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Kroko.Brain.Agent;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Providers;

/// <summary>Sección "Llm:Local": la IA de la PC del dueño (llama.cpp + Qwen 3 4B), para lo simple (17/11/2026).</summary>
public sealed class LocalLlmOptions
{
    /// <summary>Usar la IA de la PC cuando la App la tenga lista (la App también la puede apagar).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Más largo que esto (caracteres) ya no es «simple»: va a Gemini.</summary>
    public int MaxChars { get; set; } = 280;

    /// <summary>Segundos que se espera a la IA de la PC antes de pasar a Gemini.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Herramientas que puede usar la IA de la PC (con comodín final). Lo demás lo hace Gemini.</summary>
    public List<string> Tools { get; set; } =
    [
        "abrir_*", "cerrar_programa", "estado_equipo", "volumen", "silenciar", "brillo", "apagar", "reiniciar", "cancelar_apagado",
        "bloquear_pc", "suspender", "temporizador*", "pomodoro", "modo_concentracion", "musica", "ordenar_carpeta", "deshacer_orden",
        "listar_archivos", "buscar_archivos", "crear_carpeta", "modo_respuesta", "mostrar_mi_dia", "portapapeles_*",
        "tarea*", "recordatorio*", "habito*", "recordar", "actualizar_recuerdo", "olvidar", "resumen_del_dia", "perfil_actualizar",
    ];

    /// <summary>Si la orden habla de esto, necesita a Gemini (internet, correo, apps, ver la pantalla…).</summary>
    public List<string> NeedsCloud { get; set; } =
    [
        "internet", "busca en", "búscame", "googlea", "clima", "tiempo hará", "pronóstico", "noticia", "precio", "dólar",
        "correo", "email", "gmail", "notion", "drive", "calendar", "calendario", "evento", "reunión", "trello", "todoist",
        "whatsapp", "formulario", "página", "web", "pantalla", "captura", "ventana", "documento", "pdf", "traduce", "contacto",
        "automatiza", "cada día", "todos los días", "telegram", "archivo a", "mándame",
    ];
}

/// <summary>Las conexiones de la App que tienen IA local, para pedirle que piense.</summary>
public static class LocalLlm
{
    public static readonly ConcurrentDictionary<object, IAgentChannel> Channels = new();

    /// <summary>¿La orden es simple (la puede resolver la IA de la PC)?</summary>
    public static bool IsSimple(IReadOnlyList<ChatMessage> history, LocalLlmOptions o)
    {
        var lastUser = history.LastOrDefault(m => m.Role == ChatRole.User);
        if (lastUser is null) return false;
        var text = lastUser.Text;
        if (text.StartsWith('[')) return false; // automatizaciones y avisos: con Gemini
        if (text.Length > o.MaxChars) return false;
        if (history.Any(m => m.ImageBase64 is not null)) return false;
        var lower = text.ToLowerInvariant();
        return !o.NeedsCloud.Any(k => lower.Contains(k, StringComparison.Ordinal));
    }

    public static bool Allowed(string tool, LocalLlmOptions o) => o.Tools.Any(p =>
        p.EndsWith('*') ? tool.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase) : tool.Equals(p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// La IA de la PC (llama.cpp con Qwen 3 4B en la App). El cerebro le manda la petición por el WebSocket y la App la pasa a
/// su llama.cpp local. Solo se usa para lo simple (ver <see cref="LocalLlm.IsSimple"/>), con menos herramientas.
/// </summary>
public sealed class LocalLlmProvider(IOptionsMonitor<LlmOptions> options, ILogger<LocalLlmProvider> log) : ILlmProvider
{
    public const string ProviderName = "local";

    public string Name => ProviderName;

    public bool IsConfigured => options.CurrentValue.Local.Enabled && Channel() is not null;

    public bool SupportsAudio => false;
    public bool SupportsVision => false;

    private static IAgentChannel? Channel() =>
        AppProvidedKeys.LocalModel is not null && AppProvidedKeys.Owner is { } owner && LocalLlm.Channels.TryGetValue(owner, out var c) ? c : null;

    public async Task<LlmResponse> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        var channel = Channel() ?? throw new LlmProviderException(Name, "La IA de tu PC no está lista");
        var opt = options.CurrentValue;
        var request = DeepSeekProvider.BuildRequest(systemPrompt, history, tools, opt);
        request["model"] = AppProvidedKeys.LocalModel;
        request.Remove("thinking");
        request["max_tokens"] = Math.Min(opt.MaxOutputTokens, 1024);
        // Qwen 3 piensa en voz alta por defecto: aquí, respuestas directas y rápidas
        request["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };

        var callId = Guid.NewGuid().ToString("N");
        var result = await channel.RequestAsync<LocalLlmResultMessage>(
            new LocalLlmRequestMessage { SessionId = channel.SessionId, CallId = callId, Body = request.ToJsonString() },
            callId, TimeSpan.FromSeconds(Math.Max(10, opt.Local.TimeoutSeconds)), ct);
        if (result is null) throw new LlmProviderException(Name, "La IA de tu PC tardó demasiado");
        if (!result.Ok || result.Body is null) throw new LlmProviderException(Name, $"La IA de tu PC falló: {result.Error}");
        return DeepSeekProvider.ParseOpenAi(result.Body, Name, log);
    }
}
