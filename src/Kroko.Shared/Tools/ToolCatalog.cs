using System.Text.Json;
using Kroko.Shared.Protocol;

namespace Kroko.Shared.Tools;

/// <summary>
/// Definición de una herramienta: lo que la IA ve (nombre, descripción, parámetros) y su riesgo.
/// El riesgo puede depender de los argumentos (mover 1 archivo = Write, varios = Dangerous).
/// </summary>
public sealed class ToolSpec
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>JSON Schema de los parámetros.</summary>
    public required string Parameters { get; init; }

    public ToolRisk Risk { get; init; } = ToolRisk.Read;

    /// <summary>Riesgo según los argumentos (si es null se usa Risk).</summary>
    public Func<JsonElement, ToolRisk>? RiskFor { get; init; }

    /// <summary>Texto para la tarjeta de confirmación ("Apagar la PC en 10 min").</summary>
    public Func<JsonElement, string>? Describe { get; init; }

    public ToolRisk EffectiveRisk(JsonElement args) => RiskFor?.Invoke(args) ?? Risk;

    public string ConfirmText(JsonElement args) => Describe?.Invoke(args) ?? $"Ejecutar {Name}";
}

/// <summary>
/// Catálogo único de herramientas (sección 9). Lo usan el Brain (para ofrecerlas a la IA y decidir
/// cuándo pedir confirmación) y la App (para ejecutarlas y volver a comprobar el riesgo).
/// </summary>
public static class ToolCatalog
{
    private const string NoParams = """{"type":"object","properties":{}}""";

    public static readonly IReadOnlyList<ToolSpec> All =
    [
        // ---------------------------------------------------------------- sistema
        new()
        {
            Name = "abrir_programa",
            Description = "Abre un programa o app de Windows por su nombre (Excel, Chrome, Spotify, Calculadora…).",
            Parameters = """{"type":"object","properties":{"nombre":{"type":"string","description":"Nombre del programa"}},"required":["nombre"]}""",
        },
        new()
        {
            Name = "cerrar_programa",
            Description = "Cierra un programa abierto. Primero lo cierra con normalidad; solo fuerza el cierre si forzar=true.",
            Parameters = """{"type":"object","properties":{"nombre":{"type":"string"},"forzar":{"type":"boolean","description":"Forzar el cierre (puede perder cambios sin guardar)"}},"required":["nombre"]}""",
            Risk = ToolRisk.Dangerous,
            Describe = a => $"Cerrar {Str(a, "nombre")}" + (Bool(a, "forzar") ? " a la fuerza (puede perder cambios sin guardar)" : ""),
        },
        new()
        {
            Name = "abrir_carpeta",
            Description = "Abre una carpeta en el Explorador. Acepta rutas o nombres como escritorio, descargas, documentos, imágenes.",
            Parameters = """{"type":"object","properties":{"ruta":{"type":"string"}},"required":["ruta"]}""",
        },
        new()
        {
            Name = "abrir_archivo",
            Description = "Abre un archivo con su programa predeterminado.",
            Parameters = """{"type":"object","properties":{"ruta":{"type":"string"}},"required":["ruta"]}""",
        },
        new()
        {
            Name = "abrir_url",
            Description = "Abre una página web (http/https) en el navegador predeterminado.",
            Parameters = """{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}""",
        },
        new()
        {
            Name = "estado_equipo",
            Description = "Estado de la PC: batería, CPU, RAM, discos, red y tiempo encendida.",
            Parameters = NoParams,
        },
        new()
        {
            Name = "volumen",
            Description = "Cambia el volumen general del sistema (0–100).",
            Parameters = """{"type":"object","properties":{"nivel":{"type":"integer","minimum":0,"maximum":100}},"required":["nivel"]}""",
            Risk = ToolRisk.Write,
        },
        new()
        {
            Name = "silenciar",
            Description = "Silencia o quita el silencio del sonido del sistema.",
            Parameters = """{"type":"object","properties":{"silenciar":{"type":"boolean","description":"true = silenciar, false = quitar silencio"}},"required":["silenciar"]}""",
            Risk = ToolRisk.Write,
        },
        new()
        {
            Name = "musica",
            Description = "Controla la música o el video que suena en la PC (Spotify, YouTube, cualquier reproductor): " +
                          "pausar/reanudar, siguiente, anterior o detener con las teclas multimedia; o \"poner\" música abriendo " +
                          "la búsqueda en Spotify (si está instalado) o YouTube Music. Para el volumen usa volumen.",
            Parameters = """{"type":"object","properties":{"accion":{"type":"string","enum":["pausar_reanudar","siguiente","anterior","detener","poner"]},"consulta":{"type":"string","description":"Qué poner: canción, artista, playlist o género (solo con poner)"},"servicio":{"type":"string","enum":["spotify","youtube_music"]}},"required":["accion"]}""",
            Risk = ToolRisk.Write,
        },
        new()
        {
            Name = "modo_respuesta",
            Description = "Cambia cómo respondes en la PC: con voz (hablas y escribes) o solo por chat (solo escribes, sin audio). " +
                          "Úsala cuando tu dueño diga cosas como \"cállate\", \"no me hables\", \"mándamelo por chat\", \"solo escribe\" " +
                          "(voz=false) o \"háblame\", \"respóndeme con voz\", \"ya puedes hablar\" (voz=true). Se recuerda aunque reinicies.",
            Parameters = """{"type":"object","properties":{"voz":{"type":"boolean","description":"true = responder con voz; false = solo por chat"}},"required":["voz"]}""",
            Risk = ToolRisk.Write,
        },
        new()
        {
            Name = "mostrar_mi_dia",
            Description = "Abre en la PC la ventana \"Mi día\" (agenda, tareas, recordatorios, hábitos y clima) para que tu dueño lo vea de un vistazo.",
            Parameters = """{"type":"object","properties":{}}""",
            Risk = ToolRisk.Read,
        },
        new()
        {
            Name = "buscar_en_documentos",
            Description = "Busca POR SIGNIFICADO en los documentos de tu dueño (Documentos y Escritorio: Word, PDF, texto). " +
                          "Úsalo para \"¿dónde está el contrato de Ana?\" o \"¿qué dice mi informe sobre ventas?\". Devuelve archivos y el fragmento más parecido.",
            Parameters = """{"type":"object","properties":{"pregunta":{"type":"string"},"cuantos":{"type":"integer","minimum":1,"maximum":10}},"required":["pregunta"]}""",
            Risk = ToolRisk.Read,
        },
        new()
        {
            Name = "leer_documento",
            Description = "Lee el texto de un documento (Word, PDF, texto) por su ruta, para resumirlo o responder sobre él.",
            Parameters = """{"type":"object","properties":{"ruta":{"type":"string"}},"required":["ruta"]}""",
            Risk = ToolRisk.Read,
        },
        new()
        {
            Name = "brillo",
            Description = "Cambia el brillo de la pantalla (0–100). Solo funciona en laptops o pantallas compatibles.",
            Parameters = """{"type":"object","properties":{"nivel":{"type":"integer","minimum":0,"maximum":100}},"required":["nivel"]}""",
            Risk = ToolRisk.Write,
        },
        new()
        {
            Name = "apagar",
            Description = "Programa el apagado de la PC dentro de N minutos (0 = ahora).",
            Parameters = """{"type":"object","properties":{"minutos":{"type":"integer","minimum":0,"maximum":1440}},"required":["minutos"]}""",
            Risk = ToolRisk.Dangerous,
            Describe = a => Int(a, "minutos") is var m && m > 0 ? $"Apagar la PC en {m} min" : "Apagar la PC ahora",
        },
        new()
        {
            Name = "reiniciar",
            Description = "Programa el reinicio de la PC dentro de N minutos (0 = ahora).",
            Parameters = """{"type":"object","properties":{"minutos":{"type":"integer","minimum":0,"maximum":1440}},"required":["minutos"]}""",
            Risk = ToolRisk.Dangerous,
            Describe = a => Int(a, "minutos") is var m && m > 0 ? $"Reiniciar la PC en {m} min" : "Reiniciar la PC ahora",
        },
        new()
        {
            Name = "cerrar_sesion",
            Description = "Cierra la sesión de Windows del usuario.",
            Parameters = NoParams,
            Risk = ToolRisk.Dangerous,
            Describe = _ => "Cerrar tu sesión de Windows ahora (se cerrarán tus programas)",
        },
        new()
        {
            Name = "cancelar_apagado",
            Description = "Cancela un apagado o reinicio programado.",
            Parameters = NoParams,
            Risk = ToolRisk.Write,
        },
        new()
        {
            Name = "bloquear_pc",
            Description = "Bloquea la PC (pantalla de inicio de sesión).",
            Parameters = NoParams,
            Risk = ToolRisk.Write,
        },
        new()
        {
            Name = "suspender",
            Description = "Pone la PC en suspensión.",
            Parameters = NoParams,
            Risk = ToolRisk.Write,
        },
        new()
        {
            Name = "captura_pantalla",
            Description = "Toma una captura de la pantalla principal para que puedas verla y describirla.",
            Parameters = NoParams,
        },
        new()
        {
            Name = "portapapeles_leer",
            Description = "Lee el texto que hay en el portapapeles.",
            Parameters = NoParams,
            Risk = ToolRisk.Write,
        },
        new()
        {
            Name = "portapapeles_escribir",
            Description = "Copia un texto al portapapeles.",
            Parameters = """{"type":"object","properties":{"texto":{"type":"string"}},"required":["texto"]}""",
            Risk = ToolRisk.Write,
        },
        new()
        {
            Name = "procesos_listar",
            Description = "Lista los programas abiertos y los que más memoria usan.",
            Parameters = NoParams,
        },

        new()
        {
            Name = "ejecutar_script",
            Description = "Ejecuta uno de los scripts que tu dueño autorizó en Ajustes (lista blanca) por su nombre. No ejecuta nada fuera de esa lista.",
            Parameters = """{"type":"object","properties":{"nombre":{"type":"string"}},"required":["nombre"]}""",
            Risk = ToolRisk.Dangerous,
            Describe = a => $"Ejecutar el script «{Str(a, "nombre")}» de tu lista blanca",
        },

        // ---------------------------------------------------------------- productividad en la PC (Fase 2)
        new()
        {
            Name = "temporizador",
            Description = "Pone un temporizador visible en el panel de Kroko (cuenta regresiva circular) y avisa con sonido al terminar.",
            Parameters = """{"type":"object","properties":{"minutos":{"type":"number","minimum":0.1,"maximum":600},"etiqueta":{"type":"string","description":"Para qué es (p. ej. pasta, descanso)"}},"required":["minutos"]}""",
            Risk = ToolRisk.Write,
        },
        new()
        {
            Name = "temporizador_cancelar",
            Description = "Cancela el temporizador o el pomodoro en marcha.",
            Parameters = NoParams,
            Risk = ToolRisk.Write,
        },
        new()
        {
            Name = "pomodoro",
            Description = "Inicia o detiene un pomodoro: bloques de trabajo (25 min) y descanso (5 min; cada 4 bloques, 15 min). Durante el trabajo Kroko pone cara de concentración.",
            Parameters = """{"type":"object","properties":{"accion":{"type":"string","enum":["iniciar","detener"]},"minutos_trabajo":{"type":"integer","minimum":1,"maximum":180},"minutos_descanso":{"type":"integer","minimum":1,"maximum":60}},"required":["accion"]}""",
            Risk = ToolRisk.Write,
        },
        new()
        {
            Name = "modo_concentracion",
            Description = "Activa o desactiva el modo concentración: silencia las notificaciones emergentes de Windows y Kroko se pone en modo foco. Opcionalmente se apaga solo tras N minutos.",
            Parameters = """{"type":"object","properties":{"activar":{"type":"boolean"},"minutos":{"type":"integer","minimum":1,"maximum":600}},"required":["activar"]}""",
            Risk = ToolRisk.Write,
        },

        // ---------------------------------------------------------------- control visual (Fase 4)
        new()
        {
            Name = "leer_ventana_activa",
            Description = "Lee la ventana que tu dueño tiene delante (título, programa y sus elementos: botones, campos, menús, textos) con un número para cada uno. Úsalo antes de hacer clic o escribir.",
            Parameters = NoParams,
        },
        new()
        {
            Name = "clic",
            Description = "Hace clic en un elemento de la ventana activa (por su número de leer_ventana_activa o por su texto), o en coordenadas x,y. Devuelve cómo quedó la ventana para verificar. Si el botón es irreversible (enviar, eliminar, comprar, pagar, publicar…) pon irreversible=true.",
            Parameters = """{"type":"object","properties":{"elemento":{"type":"integer","description":"Número del elemento"},"texto":{"type":"string","description":"Texto del botón o elemento"},"x":{"type":"integer"},"y":{"type":"integer"},"descripcion":{"type":"string","description":"Qué es y qué pasará al pulsarlo"},"irreversible":{"type":"boolean"}}}""",
            Risk = ToolRisk.Write,
            RiskFor = a => Bool(a, "irreversible") || LooksIrreversible(Str(a, "texto") + " " + Str(a, "descripcion")) ? ToolRisk.Dangerous : ToolRisk.Write,
            Describe = a => $"Pulsar «{(Str(a, "texto") is { Length: > 0 } t ? t : Str(a, "descripcion"))}» en la ventana activa",
        },
        new()
        {
            Name = "escribir",
            Description = "Escribe texto en la ventana activa (en el elemento indicado o donde esté el cursor). Nunca en campos de contraseña.",
            Parameters = """{"type":"object","properties":{"texto":{"type":"string"},"elemento":{"type":"integer"}},"required":["texto"]}""",
            Risk = ToolRisk.Write,
        },
        new()
        {
            Name = "teclas",
            Description = "Pulsa una tecla o combinación en la ventana activa (Enter, Tab, Ctrl+S, Ctrl+C, Alt+Tab, F5…).",
            Parameters = """{"type":"object","properties":{"atajo":{"type":"string"}},"required":["atajo"]}""",
            Risk = ToolRisk.Write,
            RiskFor = a => IsDangerousShortcut(Str(a, "atajo")) ? ToolRisk.Dangerous : ToolRisk.Write,
            Describe = a => $"Pulsar {Str(a, "atajo")} en la ventana activa",
        },

        // ---------------------------------------------------------------- archivos
        new()
        {
            Name = "listar_archivos",
            Description = "Lista el contenido de una carpeta (carpetas y archivos con tamaño y fecha).",
            Parameters = """{"type":"object","properties":{"ruta":{"type":"string"},"patron":{"type":"string","description":"Filtro opcional, p. ej. *.pdf"}},"required":["ruta"]}""",
        },
        new()
        {
            Name = "buscar_archivos",
            Description = "Busca archivos por nombre (y opcionalmente por texto dentro) en una carpeta y sus subcarpetas.",
            Parameters = """{"type":"object","properties":{"carpeta":{"type":"string"},"patron":{"type":"string","description":"Parte del nombre o comodín, p. ej. factura o *.xlsx"},"contenido":{"type":"string","description":"Texto a buscar dentro de archivos de texto (opcional)"}},"required":["carpeta","patron"]}""",
        },
        new()
        {
            Name = "mover_archivos",
            Description = "Mueve archivos o carpetas a una carpeta de destino (nunca sobrescribe: renombra si ya existe).",
            Parameters = """{"type":"object","properties":{"origenes":{"type":"array","items":{"type":"string"}},"destino":{"type":"string"}},"required":["origenes","destino"]}""",
            Risk = ToolRisk.Write,
            RiskFor = a => Count(a, "origenes") > 1 ? ToolRisk.Dangerous : ToolRisk.Write,
            Describe = a => $"Mover {Count(a, "origenes")} elementos a {Str(a, "destino")}:\n{List(a, "origenes")}",
        },
        new()
        {
            Name = "copiar_archivos",
            Description = "Copia archivos o carpetas a una carpeta de destino (nunca sobrescribe: renombra si ya existe).",
            Parameters = """{"type":"object","properties":{"origenes":{"type":"array","items":{"type":"string"}},"destino":{"type":"string"}},"required":["origenes","destino"]}""",
            Risk = ToolRisk.Write,
            RiskFor = a => Count(a, "origenes") > 1 ? ToolRisk.Dangerous : ToolRisk.Write,
            Describe = a => $"Copiar {Count(a, "origenes")} elementos a {Str(a, "destino")}:\n{List(a, "origenes")}",
        },
        new()
        {
            Name = "renombrar",
            Description = "Cambia el nombre de un archivo o carpeta.",
            Parameters = """{"type":"object","properties":{"ruta":{"type":"string"},"nuevo_nombre":{"type":"string"}},"required":["ruta","nuevo_nombre"]}""",
            Risk = ToolRisk.Write,
        },
        new()
        {
            Name = "crear_carpeta",
            Description = "Crea una carpeta nueva.",
            Parameters = """{"type":"object","properties":{"ruta":{"type":"string"}},"required":["ruta"]}""",
            Risk = ToolRisk.Write,
        },
        new()
        {
            Name = "enviar_a_papelera",
            Description = "Envía archivos o carpetas a la Papelera de reciclaje (se pueden recuperar). Nunca borra de forma permanente.",
            Parameters = """{"type":"object","properties":{"rutas":{"type":"array","items":{"type":"string"}}},"required":["rutas"]}""",
            Risk = ToolRisk.Dangerous,
            Describe = a => $"Enviar a la Papelera {Count(a, "rutas")} elemento(s):\n{List(a, "rutas")}",
        },
        new()
        {
            Name = "ordenar_carpeta",
            Description = "Ordena los archivos sueltos de una carpeta en subcarpetas por tipo (Imágenes, Documentos…) o por fecha (año-mes). Devuelve un resumen de lo que movió.",
            Parameters = """{"type":"object","properties":{"ruta":{"type":"string"},"criterio":{"type":"string","enum":["tipo","fecha"]}},"required":["ruta"]}""",
            Risk = ToolRisk.Dangerous,
            Describe = a => $"Ordenar {Str(a, "ruta")} por {(Str(a, "criterio") is { Length: > 0 } c ? c : "tipo")} (moverá los archivos sueltos a subcarpetas)",
        },
    ];

    private static readonly Dictionary<string, ToolSpec> ByName = All.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    public static ToolSpec? Find(string name) => ByName.GetValueOrDefault(name);

    // ---------------------------------------------------------------- lectura segura de argumentos

    // ---------------------------------------------------------------- reglas de la Fase 4

    private static readonly string[] IrreversibleWords =
    [
        "enviar", "send", "eliminar", "borrar", "delete", "remove", "quitar", "comprar", "buy", "pagar", "pay", "checkout",
        "publicar", "publish", "post", "confirmar", "confirm", "aceptar", "accept", "transferir", "transfer", "desinstalar",
        "uninstall", "formatear", "format", "vaciar", "empty", "reset", "restablecer", "cerrar sesión", "sign out", "log out",
    ];

    /// <summary>¿El texto de un botón o su descripción suena a algo irreversible?</summary>
    public static bool LooksIrreversible(string text)
    {
        var t = text.ToLowerInvariant();
        return IrreversibleWords.Any(w => System.Text.RegularExpressions.Regex.IsMatch(t, $@"(^|\W){System.Text.RegularExpressions.Regex.Escape(w)}(\W|$)"));
    }

    /// <summary>Atajos que cierran, borran o apagan cosas.</summary>
    public static bool IsDangerousShortcut(string shortcut)
    {
        var s = shortcut.Replace(" ", "").ToLowerInvariant();
        return s is "alt+f4" or "ctrl+w" or "ctrl+shift+w" or "shift+delete" or "shift+supr" or "ctrl+shift+delete"
            or "delete" or "supr" or "ctrl+alt+delete" or "win+l" or "ctrl+q";
    }

    public static string Str(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    public static bool Bool(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static int Int(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(v.GetDouble())
            : 0;

    public static int Count(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.GetArrayLength()
            : 0;

    private static string List(JsonElement a, string name)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return "";
        var items = v.EnumerateArray().Select(e => "• " + Path.GetFileName((e.GetString() ?? "").TrimEnd('\\', '/'))).ToList();
        return items.Count <= 6 ? string.Join("\n", items) : string.Join("\n", items.Take(5)) + $"\n• … y {items.Count - 5} más";
    }
}

