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
        ["music"] = "hablan de música",
    };

    public static bool IsValid(string? name) => name is not null && All.ContainsKey(name);
}
