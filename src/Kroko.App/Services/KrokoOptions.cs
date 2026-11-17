using Kroko.Shared;

namespace Kroko.App.Services;

/// <summary>
/// Configuración de la App leída de la sección "Kroko" de appsettings.json.
/// </summary>
public sealed class KrokoOptions
{
    public const string Section = "Kroko";

    /// <summary>Dirección del cerebro (local hoy, servidor mañana).</summary>
    public string BrainUrl { get; set; } = KrokoDefaults.BrainUrl;

    /// <summary>Token compartido con el Brain (obligatorio cuando el Brain esté en un servidor).</summary>
    public string? BrainToken { get; set; }

    /// <summary>Si el Brain es local y no está encendido, la App lo arranca.</summary>
    public bool LaunchLocalBrain { get; set; } = true;

    /// <summary>Ruta a Kroko.Brain.exe (vacío = buscarlo automáticamente).</summary>
    public string? BrainExecutablePath { get; set; }

    /// <summary>Segundos sin actividad hasta que el panel de respuesta se esconde.</summary>
    public double PanelAutoHideSeconds { get; set; } = 8;

    /// <summary>Velocidad del efecto máquina de escribir (caracteres por segundo).</summary>
    public double TypingCharsPerSecond { get; set; } = 45;

    /// <summary>
    /// Dónde vive Kroko: "isla" = arriba al centro, en una isla pegada al borde de la pantalla que se despliega con un
    /// clic (pedido por Pol, 17/11/2026, como Troko Desk); "burbuja" = la burbuja flotante de la esquina.
    /// </summary>
    public string Look { get; set; } = "isla";

    public bool IsIsland => !string.Equals(Look?.Trim(), "burbuja", StringComparison.OrdinalIgnoreCase);

    /// <summary>La isla se esconde cuando no la usas y sale al llamarla o al pasar el cursor por arriba (17/11/2026).</summary>
    public bool IslandAutoHide { get; set; } = true;

    /// <summary>Escala de la burbuja: 0.75 a 1.5.</summary>
    public double BubbleScale { get; set; } = 1.0;

    /// <summary>Separación respecto a la esquina de la pantalla, en píxeles lógicos.</summary>
    public double ScreenMargin { get; set; } = 24;

    /// <summary>Aceptar acciones peligrosas que Pol aprobó desde Telegram (Fase 5).</summary>
    public bool AllowRemoteApprovals { get; set; } = true;

    /// <summary>
    /// Acciones peligrosas que se hacen sin tarjeta cuando las pidió el dueño hace poco (decidido por Pol, 08/11/2026:
    /// «por algo dije apágalo»). Si la orden no vino de él (automatización, aviso), la tarjeta sigue saliendo.
    /// </summary>
    public List<string>? AutoApproveTools { get; set; }

    /// <summary>Minutos desde la última orden del dueño en los que vale la aprobación automática.</summary>
    public double AutoApproveMinutes { get; set; } = 3;

    public static readonly string[] DefaultAutoApproveTools =
        ["apagar", "reiniciar", "cerrar_sesion", "ordenar_carpeta", "mover_archivos", "copiar_archivos"];

    public IReadOnlyList<string> EffectiveAutoApproveTools => AutoApproveTools ?? [.. DefaultAutoApproveTools];

    /// <summary>¿Esta acción se hace sin preguntar, sabiendo cuándo dio el dueño su última orden?</summary>
    public bool CanAutoApprove(string tool, DateTime? lastOwnerOrder, DateTime now) =>
        lastOwnerOrder is { } at && (now - at).TotalMinutes <= AutoApproveMinutes && now >= at
        && EffectiveAutoApproveTools.Contains(tool, StringComparer.OrdinalIgnoreCase);

    /// <summary>Esconderse cuando hay un juego/presentación a pantalla completa.</summary>
    public bool HideOnFullscreen { get; set; } = true;

    public bool SoundsEnabled { get; set; } = true;

    /// <summary>Volumen de los sonidos (0–1). Bajo por defecto.</summary>
    public double SoundVolume { get; set; } = 0.25;

    /// <summary>Minutos sin tocar a Kroko hasta que le entra sueño.</summary>
    public double SleepAfterMinutes { get; set; } = 15;

    /// <summary>Lo mismo pero de noche (se duerme antes).</summary>
    public double NightSleepAfterMinutes { get; set; } = 5;

    /// <summary>Minutos dormido antes de desaparecer de la pantalla (0 = nunca). Sigue escuchando «Oye Kroko».</summary>
    public double HideAfterSleepMinutes { get; set; } = 5;

    /// <summary>Arranca escondido (dormido): aparece con «Oye Kroko», el atajo, la bandeja o un aviso (decidido por Pol, 14/11/2026).</summary>
    public bool StartHidden { get; set; } = true;

    /// <summary>Segundos sin actividad tras los que se esconde otra vez (0 = no; entonces vale HideAfterSleepMinutes).</summary>
    public double HideAfterIdleSeconds { get; set; } = 60;

    public int NightStartHour { get; set; } = 23;
    public int NightEndHour { get; set; } = 6;

    public bool DistractionsEnabled { get; set; } = true;
    public double DistractionMinMinutes { get; set; } = 10;
    public double DistractionMaxMinutes { get; set; } = 20;

    /// <summary>Voz (sección 6).</summary>
    public VoiceSettings Voice { get; set; } = new();

    /// <summary>La IA de la PC (17/11/2026, IA mixta con Gemini).</summary>
    public LocalAiSettings LocalAi { get; set; } = new();

    /// <summary>Búsqueda por significado en tus documentos (índice en esta PC; los vectores los calcula el cerebro con Gemini).</summary>
    public DocumentSettings Documents { get; set; } = new();

    /// <summary>Alias de programas: "nombre que dices" → ruta o comando (p. ej. "mi editor": "C:\\…\\code.exe").</summary>
    public Dictionary<string, string> ProgramAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Lista blanca de scripts (sección 8): "nombre" → ruta a .ps1/.bat/.cmd/.exe. Kroko solo puede
    /// ejecutar estos, y siempre con confirmación.
    /// </summary>
    public Dictionary<string, string> ScriptWhitelist { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dónde se publican las versiones (carpeta web o GitHub Releases). Vacío = sin autoactualización.</summary>
    public string? UpdatesUrl { get; set; }

    /// <summary>Herramientas desactivadas (por nombre, p. ej. "apagar").</summary>
    public List<string> DisabledTools { get; set; } = [];

    /// <summary>Reaccionar a batería baja, descargas terminadas y conexión a internet.</summary>
    public bool SystemReactionsEnabled { get; set; } = true;
}

public sealed class VoiceSettings
{
    /// <summary>Atajo global para hablar (p. ej. "Ctrl+Space", "Ctrl+Alt+J").</summary>
    public string Hotkey { get; set; } = "Ctrl+Space";

    /// <summary>"Tap" = pulsas, hablas y se detiene solo al callarte; "PushToTalk" = mantener pulsado.</summary>
    public string Mode { get; set; } = "Tap";

    /// <summary>Segundos de silencio que cierran la grabación en modo Tap.</summary>
    public double SilenceSeconds { get; set; } = 1.2;

    public double MaxSeconds { get; set; } = 30;

    /// <summary>Nivel (0–1) a partir del cual se considera que hablas.</summary>
    public double SpeechThreshold { get; set; } = 0.22;

    /// <summary>Micrófono a usar (-1 = el predeterminado).</summary>
    public int MicrophoneDevice { get; set; } = -1;

    /// <summary>Responder con voz. Apagado = solo por chat (también se cambia diciéndole «no me hables con voz» / «háblame»). «Cállate» es otra cosa: lo calla y lo esconde.</summary>
    public bool RepliesWithVoice { get; set; } = true;

    /// <summary>Hablar también las respuestas a lo que escribes (con la voz de Gemini).</summary>
    public bool SpeakTypedReplies { get; set; } = true;

    /// <summary>¿La próxima respuesta lleva voz? (según el modo y si la orden fue hablada o escrita).</summary>
    public bool WantsVoice(bool spoken) => RepliesWithVoice && (spoken || SpeakTypedReplies);

    /// <summary>Si el Brain no manda audio, hablar con la voz de Windows.</summary>
    public bool LocalTtsFallback { get; set; } = true;

    /// <summary>Escuchar siempre (en esta PC, sin internet) la frase de activación «Oye Kroko» / «Hola Kroko».</summary>
    public WakeWordSettings WakeWord { get; set; } = new();

    /// <summary>Tras responder por voz, volver a escuchar sin repetir la frase (conversación).</summary>
    public bool Conversation { get; set; } = true;

    /// <summary>Voz en tiempo real (Gemini Live): responde en ~1 s mientras hablan. Si falla, se usa el modo clásico.</summary>
    public bool Realtime { get; set; } = true;

    /// <summary>Segundos que espera a que sigas hablando antes de dar la conversación por terminada.</summary>
    public double FollowUpSeconds { get; set; } = 8;

    /// <summary>Frases que terminan la conversación (además de quedarte callado).</summary>
    public List<string>? EndPhrases { get; set; }

    public IReadOnlyList<string> EffectiveEndPhrases => EndPhrases is { Count: > 0 } ? EndPhrases : DefaultEndPhrases;

    // Las listas por defecto van aparte: el enlazador de configuración AÑADE a una lista ya llena en vez de reemplazarla
    private static readonly string[] DefaultEndPhrases =
        ["eso es todo", "nada más", "gracias kroko", "adiós", "chau", "hasta luego", "listo gracias", "ya está"];

    /// <summary>Subtítulos en vivo de lo que dices (reconocimiento local con el modelo de Vosk).</summary>
    public bool LiveCaptions { get; set; } = true;
}

/// <summary>
/// Palabra de activación con Vosk (reconocimiento local). "kroko" no existe en el vocabulario español del
/// modelo, así que se escucha como "cro co"; la gramática incluye palabras parecidas que compiten
/// (croquetas, coloca, loco…) para evitar falsas alarmas. Ajustable sin recompilar.
/// </summary>
public sealed class WakeWordSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Seguir escuchando con un juego o vídeo a pantalla completa.</summary>
    public bool ListenInFullscreen { get; set; }

    public string ModelName { get; set; } = "vosk-model-small-es-0.42";
    public string ModelUrl { get; set; } = "https://alphacephei.com/vosk/models/vosk-model-small-es-0.42.zip";
    public string ModelSha256 { get; set; } = "09b239888f633ef2f0b4e09736e3d9936acfd810bc65d53fad45261762c6511f";
    public long ModelBytes { get; set; } = 39_817_833;

    /// <summary>Lo que Vosk debe oír para despertar (en su vocabulario).</summary>
    public List<string>? Phrases { get; set; }

    public IReadOnlyList<string> EffectivePhrases => Phrases is { Count: > 0 } ? Phrases : DefaultPhrases;

    // Solo «Oye Kroko» (decidido por Pol, 14/11/2026: «Hola Kroko» lo despertaba mientras conversaba)
    private static readonly string[] DefaultPhrases = ["oye cro co"];

    /// <summary>La frase tiene que ABRIR lo que dices (no vale en medio de una conversación).</summary>
    public bool RequireAtStart { get; set; } = true;

    /// <summary>Seguridad mínima de Vosk (0–1) en cada palabra de la frase. Más alto = más estricto.</summary>
    public double MinConfidence { get; set; } = 0.8;

    /// <summary>Palabras que compiten con la frase para no confundirla.</summary>
    public List<string>? Decoys { get; set; }

    public IReadOnlyList<string> EffectiveDecoys => Decoys is { Count: > 0 } ? Decoys : DefaultDecoys;

    private static readonly string[] DefaultDecoys =
    [
        "oye", "hola", "hoy", "voy", "como", "loco", "poco", "coco", "foco", "coloca", "colocó", "croquetas", "croqueta",
        "cocodrilo", "cro", "co", "creo", "comer", "calor", "a", "qué", "que", "tal", "cómo", "estás", "te", "va", "abre",
        "es", "hora", "el", "la", "eso", "ahí", "y", "por", "favor",
        // para oír «oye kroko, cállate» y que se calle sin pasar por la IA
        "cállate", "calla", "silencio", "descansa", "basta", "ya",
    ];
}

public sealed class DocumentSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Carpetas a indexar (vacío = Documentos y Escritorio).</summary>
    public List<string>? Folders { get; set; }

    public IReadOnlyList<string> EffectiveFolders => Folders is { Count: > 0 }
        ? Folders.Select(Environment.ExpandEnvironmentVariables).ToList()
        : [Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.Desktop)];

    /// <summary>Archivos más grandes se saltan.</summary>
    public int MaxFileMb { get; set; } = 15;

    /// <summary>Máximo de archivos nuevos o cambiados por pasada (para no gastar de golpe la cuota de la IA).</summary>
    public int MaxFilesPerPass { get; set; } = 300;

    public int RescanMinutes { get; set; } = 30;
}

/// <summary>
/// Sección "Kroko:LocalAi": la IA que corre en esta PC (llama.cpp + Qwen 3 4B; decidido por Pol el 17/11/2026, como Troko
/// Desk pero mixta: lo simple aquí, lo difícil con Gemini). Se descarga una sola vez cuando el dueño pulsa «Descargar».
/// Versiones verificadas en las fuentes oficiales el 17/11/2026 (GitHub ggml-org/llama.cpp b12483 y Hugging Face Qwen).
/// </summary>
public sealed class LocalAiSettings
{
    /// <summary>Usar la IA de la PC para lo simple (se enciende al terminar de descargarla).</summary>
    public bool Enabled { get; set; }

    public string ModelName { get; set; } = "Qwen3-4B";

    /// <summary>llama.cpp para Windows con Vulkan: funciona con tarjetas NVIDIA, AMD e Intel (y en la CPU si no hay).</summary>
    public string ServerZipUrl { get; set; } =
        "https://github.com/ggml-org/llama.cpp/releases/download/b12483/llama-b12483-bin-win-vulkan-x64.zip";

    public string ModelUrl { get; set; } = "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf";

    public int Port { get; set; } = 8131;

    public int ContextSize { get; set; } = 16384;

    /// <summary>Capas en la tarjeta de video (99 = todas). 0 = solo CPU.</summary>
    public int GpuLayers { get; set; } = 99;
}
