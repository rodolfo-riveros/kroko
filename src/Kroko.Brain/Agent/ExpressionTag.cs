using System.Text.RegularExpressions;
using Kroko.Shared.Protocol;

namespace Kroko.Brain.Agent;

/// <summary>
/// Extrae la etiqueta [[expresión]] que la IA pone en su respuesta y limpia el texto.
/// Si la etiqueta no existe o no es válida, la expresión es null (la cara vuelve al reposo).
/// </summary>
public static partial class ExpressionTag
{
    // Acepta también la etiqueta mal cerrada ("[[success]") que a veces escribe la IA
    [GeneratedRegex(@"\[\[\s*([a-zA-Z_]+)\s*\]{1,2}")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\*\*|__|^#{1,6}\s*", RegexOptions.Multiline)]
    private static partial Regex MarkdownRegex();

    public static (string Text, string? Expression) Extract(string raw)
    {
        string? expression = null;
        foreach (Match m in TagRegex().Matches(raw))
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            if (Reactions.IsValid(name))
            {
                expression = name;
                break;
            }
        }

        var text = TagRegex().Replace(raw, "");
        text = MarkdownRegex().Replace(text, "");
        text = Kroko.Shared.KrokoName.Fix(text); // las respuestas siempre con "Kroko"
        return (text.Trim(), expression);
    }
}
