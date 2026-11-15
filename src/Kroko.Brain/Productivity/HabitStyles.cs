namespace Kroko.Brain.Productivity;

/// <summary>
/// Estilos de hábito: cada uno tiene su escena animada en la App (Kroko bebe agua, lee, levanta pesas…). Si no se
/// dice, se deduce del nombre.
/// </summary>
public static class HabitStyles
{
    public const string Default = "estrella";

    /// <summary>Estilo → palabras del nombre que lo delatan.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> All = new Dictionary<string, string[]>
    {
        ["agua"] = ["agua", "beber", "hidrat", "vaso"],
        ["lectura"] = ["leer", "lectura", "libro", "página", "pagina"],
        ["ejercicio"] = ["ejercicio", "gimnasio", "gym", "pesas", "entrenar", "flexiones", "abdominales"],
        ["caminar"] = ["caminar", "pasos", "correr", "trotar", "paseo"],
        ["meditacion"] = ["medit", "respirar", "respiración", "yoga", "mindfulness"],
        ["sueno"] = ["dormir", "sueño", "acostarme", "descansar"],
        ["estudio"] = ["estudiar", "estudio", "curso", "inglés", "ingles", "aprender", "practicar"],
        ["comida"] = ["fruta", "verdura", "comer sano", "desayun", "almorz", "ensalada"],
        ["medicina"] = ["pastilla", "medicina", "vitamina", "remedio"],
        ["orden"] = ["ordenar", "limpiar", "cama", "casa"],
        ["pantalla"] = ["pantalla", "celular", "redes", "móvil", "movil"],
        ["diario"] = ["escribir", "diario", "journal", "gratitud", "agradecer"],
        ["musica"] = ["música", "musica", "guitarra", "piano", "cantar"],
        ["dinero"] = ["ahorrar", "ahorro", "gastos", "dinero"],
        [Default] = [],
    };

    public static string Guess(string name)
    {
        var n = name.ToLowerInvariant();
        foreach (var (style, words) in All)
            if (words.Any(w => n.Contains(w, StringComparison.Ordinal))) return style;
        return Default;
    }

    /// <summary>Un estilo válido o null.</summary>
    public static string? Normalize(string? style)
    {
        var s = (style ?? "").Trim().ToLowerInvariant().Replace("ó", "o").Replace("ñ", "n");
        return All.ContainsKey(s) ? s : null;
    }
}
