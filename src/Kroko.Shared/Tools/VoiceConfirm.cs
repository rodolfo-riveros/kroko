namespace Kroko.Shared.Tools;

/// <summary>
/// Qué acciones peligrosas se pueden aprobar diciendo «sí, hazlo» en una conversación por voz (decidido por Pol:
/// solo las medianas). Borrar, apagar, cerrar sesión, enviar correos o mensajes, ejecutar scripts, comprar/publicar
/// (confirmar_accion), clics irreversibles y conectores externos siguen pidiendo clic o Enter.
/// </summary>
public static class VoiceConfirm
{
    private static readonly HashSet<string> Medium = new(StringComparer.OrdinalIgnoreCase)
    {
        "cerrar_programa",
        "mover_archivos",
        "copiar_archivos",
        "renombrar",
        "crear_carpeta",
        "ordenar_carpeta",
        "google_calendario_crear",
        "trello_archivar",
    };

    public static bool IsAllowed(string? tool) => tool is not null && Medium.Contains(tool);

    /// <summary>Palabras que el reconocedor local escucha como respuesta ("si" va sin tilde en su vocabulario).</summary>
    public static readonly string[] YesPhrases = ["si", "si hazlo", "hazlo", "dale", "claro", "adelante", "okey", "vale", "si dale"];
    public static readonly string[] NoPhrases = ["no", "cancela", "cancelar", "nunca", "mejor no", "espera", "no lo hagas"];

    /// <summary>true = sí, false = no, null = no quedó claro.</summary>
    public static bool? Interpret(string heard)
    {
        var words = heard.ToLowerInvariant().Replace("sí", "si").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w is "no" or "cancela" or "cancelar" or "nunca" or "espera")) return false;
        if (words.Any(w => w is "si" or "hazlo" or "dale" or "claro" or "adelante" or "okey" or "vale")) return true;
        return null;
    }
}
