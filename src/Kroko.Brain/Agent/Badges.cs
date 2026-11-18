using System.Text.Json;

namespace Kroko.Brain.Agent;

/// <summary>
/// La palabra del check verde que aparece junto a Kroko cuando hace algo (14/11/2026, como en el Troko-Desk): solo
/// para acciones (no para consultas), corta y clara. null = sin check.
/// </summary>
public static class Badges
{
    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tarea_crear"] = "Anotado",
        ["tarea_completar"] = "Hecho",
        ["recordatorio_crear"] = "Listo",
        ["habito_crear"] = "Hábito nuevo",
        ["habito_marcar"] = "Registrado",
        ["guardar_en_notion"] = "Guardado",
        ["recordar"] = "Lo recordaré",
        ["crear_rutina"] = "Aprendido",
        ["automatizacion_crear"] = "Programado",
        ["google_calendario_crear"] = "Agendado",
        ["google_tarea_crear"] = "Anotado",
        ["google_tarea_completar"] = "Hecho",
        ["trello_crear_tarjeta"] = "Anotado",
        ["correo_enviar"] = "Enviado",
        ["correo_responder"] = "Enviado",
        ["enviar_archivo"] = "Enviado",
        ["temporizador"] = "En marcha",
        ["pomodoro"] = "A enfocarse",
        ["modo_concentracion"] = "Silencio",
        ["abrir_programa"] = "Abierto",
        ["abrir_carpeta"] = "Abierto",
        ["abrir_archivo"] = "Abierto",
        ["abrir_url"] = "Abierto",
        ["ordenar_carpeta"] = "Ordenado",
        ["mover_archivos"] = "Movido",
        ["copiar_archivos"] = "Copiado",
        ["enviar_a_papelera"] = "A la papelera",
        ["volumen"] = "Listo",
        ["brillo"] = "Listo",
        ["apagar"] = "Apagando",
        ["reiniciar"] = "Reiniciando",
        ["cancelar_apagado"] = "Cancelado",
    };

    public static string? For(string tool)
    {
        if (Words.TryGetValue(tool, out var word)) return word;
        // Conectores (todoist_add-tasks, notion_notion-create-pages…): crear = Anotado
        var lower = tool.ToLowerInvariant();
        if (lower.StartsWith("todoist_add", StringComparison.Ordinal)) return "Anotado";
        if (lower.StartsWith("todoist_complete", StringComparison.Ordinal)) return "Hecho";
        if (lower.Contains("create", StringComparison.Ordinal)) return "Guardado";
        return null;
    }

    // ---------------------------------------------------------------- «Lo que hizo Kroko» (18/11/2026, diseño Kroko 2)

    private static readonly Dictionary<string, string> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tarea_crear"] = "Anotó la tarea",
        ["tarea_completar"] = "Marcó como hecha",
        ["recordatorio_crear"] = "Programó un aviso",
        ["habito_crear"] = "Creó el hábito",
        ["habito_marcar"] = "Registró",
        ["guardar_en_notion"] = "Guardó en Notion",
        ["recordar"] = "Recordará",
        ["crear_rutina"] = "Aprendió la rutina",
        ["automatizacion_crear"] = "Programó",
        ["google_calendario_crear"] = "Agendó",
        ["google_tarea_crear"] = "Anotó en Google Tasks",
        ["google_tarea_completar"] = "Completó en Google Tasks",
        ["trello_crear_tarjeta"] = "Creó en Trello",
        ["correo_enviar"] = "Envió un correo",
        ["correo_responder"] = "Respondió un correo",
    };

    private static readonly string[] TitleKeys = ["titulo", "title", "texto", "text", "nombre", "name", "asunto", "subject", "content", "contenido"];
    private static readonly string[] UrlKeys = ["url", "enlace", "htmlLink", "link", "shortUrl", "webViewLink"];

    /// <summary>
    /// La frase de la actividad («Guardó en Notion: Pendientes del lunes») y el enlace para «Ver» si el resultado trae uno.
    /// Solo para lo que hace el cerebro; lo de la PC lo cuenta la App. null = no se anota.
    /// </summary>
    public static (string Detail, string? Url)? Describe(string tool, JsonElement args, string resultJson)
    {
        var verb = Verbs.TryGetValue(tool, out var v) ? v
            : tool.StartsWith("notion", StringComparison.OrdinalIgnoreCase) && tool.Contains("create", StringComparison.OrdinalIgnoreCase) ? "Creó en Notion"
            : tool.StartsWith("todoist_add", StringComparison.OrdinalIgnoreCase) ? "Anotó en Todoist"
            : null;
        if (verb is null) return null;
        var title = Find(args, TitleKeys);
        string? url = null;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            url = FindUrl(doc.RootElement, 0);
        }
        catch (JsonException) { }
        if (title is { Length: > 60 }) title = title[..57] + "…";
        return (title is { Length: > 0 } ? verb + ": " + title : verb, url);
    }

    private static string? Find(JsonElement e, string[] keys)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var k in keys)
            if (e.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s)
                return s.Trim();
        return null;
    }

    private static string? FindUrl(JsonElement e, int depth)
    {
        if (depth > 4) return null;
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (UrlKeys.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String
                    && p.Value.GetString() is { } s && s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return s;
            }
            foreach (var p in e.EnumerateObject())
                if (FindUrl(p.Value, depth + 1) is { } inner) return inner;
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
                if (FindUrl(item, depth + 1) is { } inner) return inner;
        }
        return null;
    }
}
