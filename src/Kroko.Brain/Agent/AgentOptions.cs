namespace Kroko.Brain.Agent;

/// <summary>Sección "Agent" de appsettings.json.</summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    /// <summary>Máximo de pasos (llamadas a la IA) por cada orden.</summary>
    public int MaxSteps { get; set; } = 15;

    /// <summary>Mensajes que se recuerdan por sesión (después se olvidan los más viejos).</summary>
    public int HistoryMaxMessages { get; set; } = 30;

    /// <summary>Archivo con la personalidad (relativo a la carpeta del Brain).</summary>
    public string SystemPromptPath { get; set; } = "prompts/system.md";

    /// <summary>
    /// Token compartido con la App (cabecera X-Kroko-Token). Si está vacío, solo se aceptan
    /// conexiones desde esta misma computadora. Obligatorio cuando el Brain viva en un servidor.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>Tiempo máximo que se espera a que la App termine una herramienta.</summary>
    public int ToolTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Ahorro de tokens: las herramientas de los conectores (Notion, Todoist, navegador… ~150 descripciones largas) no se
    /// mandan en cada llamada; la IA las pide con ver_herramientas_conector y las usa con usar_conector.
    /// </summary>
    public bool ConnectorToolsOnDemand { get; set; } = true;
}
