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
}
