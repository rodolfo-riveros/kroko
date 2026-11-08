namespace Kroko.Shared;

/// <summary>
/// Valores por defecto compartidos entre la App y el Brain.
/// Todo lo que pueda cambiar también se puede sobreescribir en appsettings.json.
/// </summary>
public static class KrokoDefaults
{
    public const string BrainUrl = "http://localhost:5080";
    public const string HealthPath = "/health";
}
