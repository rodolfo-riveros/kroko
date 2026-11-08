using System.Text.RegularExpressions;

namespace Kroko.Shared;

/// <summary>
/// El nombre se escribe "Kroko", con K. Los reconocedores de voz no conocen la palabra y escriben "croco",
/// "cro co", "kroco", "croko"…: aquí se corrige en todo lo que se muestra o se guarda (dictado, subtítulos,
/// voz en tiempo real, notas de voz de Telegram y respuestas).
/// </summary>
public static partial class KrokoName
{
    public const string Name = "Kroko";

    [GeneratedRegex(@"\b[ck]r[oó]\s?[ck]{1,2}[oó]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Variants();

    public static string Fix(string? text) => string.IsNullOrEmpty(text) ? text ?? "" : Variants().Replace(text, Name);
}
