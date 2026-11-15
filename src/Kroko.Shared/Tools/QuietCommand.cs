using System.Globalization;
using System.Text;

namespace Kroko.Shared.Tools;

/// <summary>
/// «Cállate», «silencio», «descansa»… (decidido por Pol, 14/11/2026): Kroko deja de hablar, termina la conversación,
/// se esconde y deja de escuchar hasta el próximo «Oye Kroko». Lo decide la App al instante, sin pasar por la IA.
/// Solo cuenta si es una orden corta: «no te calles» o «cállate cuando acabes de leer» no la activan.
/// </summary>
public static class QuietCommand
{
    private static readonly string[] Commands =
    [
        "callate", "calla", "callese", "callense", "silencio", "descansa", "a dormir", "vete a dormir", "basta", "basta ya",
        "ya callate", "callate ya", "callate por favor", "ya basta", "descansa kroko", "callate kroko", "kroko callate",
        "kroko descansa", "oye kroko callate", "oye kroko descansa", "ya descansa", "chitón", "chiton", "shh", "shhh",
    ];

    private static readonly HashSet<string> Normalized = [.. Commands.Select(Normalize)];

    public static bool Matches(string? said)
    {
        var text = Normalize(said ?? "");
        if (text.Length == 0 || text.Split(' ').Length > 5) return false;
        // Se quitan saludos y cortesías del principio o del final ("ya, cállate por favor", "kroko, silencio")
        foreach (var candidate in new[] { text, Trim(text) })
            if (Normalized.Contains(candidate)) return true;
        return false;
    }

    private static string Trim(string text)
    {
        string[] fillers = ["oye", "kroko", "ya", "por favor", "porfa", "ahora", "mejor"];
        var t = " " + text + " ";
        foreach (var f in fillers) t = t.Replace(" " + f + " ", " ", StringComparison.Ordinal);
        return string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Minúsculas, sin tildes ni signos; «cro co» y «croco» cuentan como Kroko.</summary>
    public static string Normalize(string s)
    {
        var decomposed = KrokoName.Fix(s).ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetter(c) || c == ' ' ? c : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
