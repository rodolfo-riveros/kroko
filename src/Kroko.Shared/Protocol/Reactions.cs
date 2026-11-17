namespace Kroko.Shared.Protocol;

/// <summary>
/// Expresiones puntuales que el Brain puede pedir a la cara (deben existir en expressions.json de la App).
/// La descripción le explica a la IA cuándo usar cada una.
/// </summary>
public static class Reactions
{
    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        ["happy"] = "saludos, buenas noticias, respuesta amable normal",
        ["success"] = "terminaste una tarea con éxito",
        ["laugh"] = "algo gracioso",
        ["laugh_closed"] = "algo muy gracioso",
        ["wink"] = "broma o complicidad, 'déjamelo a mí'",
        ["confident"] = "'hecho, fácil'",
        ["cool"] = "logro especial o momento épico (úsalo poco)",
        ["love"] = "cuando tu dueño te agradece o te dice algo cariñoso",
        ["oops"] = "te equivocaste en algo menor",
        ["embarrassed"] = "Tu dueño te corrige",
        ["nervous_laugh"] = "un error que ya arreglaste",
        ["surprised"] = "algo inesperado",
        ["shocked"] = "algo grave",
        ["angry"] = "solo en broma, si tu dueño te molesta a propósito",
        ["sad"] = "no pudiste hacer algo importante",
        ["confused"] = "no entendiste lo que te pidieron",
        ["idea"] = "propones una sugerencia",
        ["music"] = "hablan de música o te piden poner música",
        // 17/11/2026: el resto de sus caras, para que use toda su personalidad
        ["crying"] = "algo muy triste, o en broma cuando te dicen que te van a apagar o borrar",
        ["sleepy"] = "es muy tarde, te piden descansar o hablan de dormir",
        ["yawn"] = "algo aburrido o largo, en broma",
        ["waking"] = "te saludan por la mañana o te despiertan",
        ["alert"] = "avisas de algo importante que hay que revisar (una reunión ya, un correo urgente)",
        ["error"] = "una herramienta o una app falló",
        ["dizzy"] = "te piden muchas cosas a la vez o algo muy enredado",
        ["focus"] = "empiezan a trabajar o estudiar, o arrancas un pomodoro",
        ["bored"] = "en broma, si te piden lo mismo muchas veces",
    };

    /// <summary>
    /// Adivina la cara por lo que dice Kroko cuando no puso etiqueta (la voz en tiempo real nunca la pone): así también
    /// hablando muestra su personalidad. null = no hay una cara clara (se queda la de hablar).
    /// </summary>
    public static string? Guess(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.ToLowerInvariant();
        bool Has(params string[] words) => words.Any(w => t.Contains(w, StringComparison.Ordinal));
        if (Has("jaja", "jeje", "ja, ja")) return "laugh";
        if (Has("lo siento", "no pude", "no logré", "no puedo", "perdón", "disculpa")) return "sad";
        if (Has("ups", "me equivoqué", "error mío")) return "oops";
        if (Has("¡listo", "listo,", "listo.", "hecho", "ya está", "anotado", "agendado", "guardado", "enviado")) return "success";
        if (Has("de nada", "con gusto", "para eso estoy", "un placer")) return "love";
        if (Has("¿qué quisiste", "no entendí", "no te entendí", "¿a qué te refieres")) return "confused";
        if (Has("te sugiero", "se me ocurre", "¿qué tal si", "idea")) return "idea";
        if (Has("¡wow", "¡vaya", "¡increíble", "en serio?")) return "surprised";
        if (Has("buenos días", "buenas tardes", "buenas noches", "hola")) return "happy";
        if (Has("a concentrarnos", "a trabajar", "pomodoro", "a estudiar")) return "focus";
        if (Has("canción", "música", "playlist")) return "music";
        if (Has("cuidado", "importante", "urgente", "atención")) return "alert";
        return null;
    }

    public static bool IsValid(string? name) => name is not null && All.ContainsKey(name);

    /// <summary>Quita etiquetas de expresión [[happy]] que se hayan colado en un texto que se va a mostrar.</summary>
    public static string StripTags(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\[\[\s*[a-zA-Z_]+\s*\]{1,2}\s*", "").Trim();
}
