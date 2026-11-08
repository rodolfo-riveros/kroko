using System.Text;

namespace Kroko.Brain.Agent;

/// <summary>Qué apps de tareas, agenda y notas tiene vinculadas el dueño en este momento.</summary>
public sealed record AppLinks(bool Google, bool Trello, bool Todoist, bool Notion)
{
    public bool Any => Google || Trello || Todoist || Notion;
}

/// <summary>
/// Reglas para que la IA elija dónde guardar cada cosa según lo que está vinculado de verdad
/// (Calendar para eventos, Todoist/Google Tasks/Trello para tareas, Notion para notas) y solo si no hay nada,
/// en la memoria propia de Kroko. Se arma en cada orden: si el dueño vincula una app, la siguiente orden ya la usa.
/// </summary>
public static class AppRouting
{
    /// <summary>La base de datos de Notion donde Kroko guarda notas y agenda (decidido por Pol, 08/11/2026).</summary>
    public const string NotionDatabase = "Kroko · Notas y agenda";

    public static string Describe(AppLinks l, string owner)
    {
        var linked = new List<string>();
        var missing = new List<string>();
        (l.Google ? linked : missing).Add("Google (Calendar y Tasks)");
        (l.Todoist ? linked : missing).Add("Todoist");
        (l.Trello ? linked : missing).Add("Trello");
        (l.Notion ? linked : missing).Add("Notion");

        var sb = new StringBuilder();
        sb.AppendLine($"Dónde guardar las cosas de {owner} (apps vinculadas AHORA: {(linked.Count > 0 ? string.Join(", ", linked) : "ninguna")}" +
                      $"{(missing.Count > 0 ? $"; sin vincular: {string.Join(", ", missing)}" : "")}):");
        sb.AppendLine($"- Si {owner} nombra una app, usa esa. Si no está vinculada, díselo en una frase, ofrécele abrir_ventana " +
                      "(asistente) para vincularla y, mientras tanto, guárdalo en tu memoria si quiere.");
        sb.AppendLine($"- Si en tus recuerdos {owner} ya dijo dónde prefiere guardar algo, respétalo. Si te corrige (\"eso va en Trello\"), " +
                      "hazlo ahí y guarda la preferencia con recordar.");

        // Eventos con día y hora (decidido por Pol, 08/11/2026: Notion es su agenda y el calendario le avisa en el móvil)
        sb.AppendLine((l.Notion, l.Google) switch
        {
            (true, true) => "- Citas, reuniones, cumpleaños y eventos con día y hora → los DOS: guardar_en_notion (tipo Agenda o " +
                            "Reunión, con fecha) y el evento en Google Calendar (google_calendario_crear; en la descripción pon el " +
                            "enlace que devuelve guardar_en_notion). Si además quiere que lo avises, recordatorio_crear.",
            (true, false) => "- Citas, reuniones y eventos con día y hora → guardar_en_notion (tipo Agenda o Reunión, con fecha). " +
                             "Google Calendar no está vinculado: si quiere que lo avises, crea también un recordatorio_crear.",
            (false, true) => "- Citas, reuniones, cumpleaños y eventos con día y hora → Google Calendar (google_calendario_crear). " +
                             "Si además quiere que lo avises, crea también un recordatorio_crear.",
            _ => "- Citas, reuniones y eventos con día y hora → recordatorio_crear (tu memoria; Google Calendar no está vinculado).",
        });

        // Tareas
        var tasks = l.Todoist ? "Todoist (sus herramientas todoist_…)"
            : l.Google ? "Google Tasks (google_tarea_crear)"
            : l.Trello ? "Trello (trello_crear_tarjeta, en el tablero y lista que corresponda; si no sabes cuál, mira trello_tableros)"
            : "tu memoria (tarea_crear)";
        sb.AppendLine($"- Tareas y pendientes sueltos (\"tengo que…\", \"anota que debo…\") → {tasks}.");
        if (l.Trello && (l.Todoist || l.Google))
            sb.AppendLine("- Si la tarea es de un proyecto, un tablero, un equipo o un cliente → Trello (trello_crear_tarjeta).");

        // Notas
        if (l.Notion)
        {
            sb.AppendLine("- Notas, ideas, apuntes, listas y su agenda → guardar_en_notion, en UN solo paso (ella sola crea la base " +
                          $"«{NotionDatabase}» la primera vez y la recuerda; no la busques ni la crees tú con las herramientas notion_…). " +
                          "Las tareas no van a Notion salvo que lo pida.");
            sb.AppendLine("- Cada nota: título corto y claro, el tipo que corresponda, fecha si la tiene, etiquetas útiles y el " +
                          "contenido completo (con viñetas o casillas si es una lista). Para LEER o buscar sus notas sí usa notion-search " +
                          "y notion-fetch. Crear notas no pide permiso; editar, mover o borrar páginas que ya existen sí.");
        }
        else sb.AppendLine(l.Google
                ? "- Notas e ideas cortas → recordar empezando con \"Nota:\" (Notion no está vinculado). Si es largo, ofrécele vincular Notion."
                : "- Notas e ideas → recordar empezando con \"Nota:\" (tu memoria; Notion no está vinculado).");

        sb.AppendLine("- Avisos a una hora (\"recuérdame a las 5…\") → siempre recordatorio_crear: es lo único con lo que tú le avisas, " +
                      "también por voz y Telegram.");
        sb.AppendLine("- Al guardar algo, di en una frase dónde quedó (\"Listo, en tu Google Tasks\").");
        sb.AppendLine("- Pendientes: cuando pregunte qué tiene pendiente, revisa TODAS las apps vinculadas de arriba y además tus " +
                      "tareas y recordatorios, y júntalo en un solo resumen ordenado por urgencia.");
        if (!l.Any)
            sb.AppendLine($"- Como no hay ninguna app vinculada, todo va a tu memoria. Si {owner} pregunta, cuéntale que puede vincular " +
                          "Google, Todoist, Trello o Notion en \"Configurar Kroko\" (abrir_ventana asistente).");
        return sb.ToString();
    }
}
