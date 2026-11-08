using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Authentication;

namespace Kroko.Brain.Mcp;

/// <summary>
/// Sección "Mcp" de appsettings.json: los conectores (servidores MCP) de Kroko.
/// Agregar un servicio nuevo = agregar un bloque aquí (sección 9, Fase 3).
/// </summary>
public sealed class McpOptions
{
    public const string Section = "Mcp";

    /// <summary>
    /// Dirección pública del cerebro cuando está en un servidor (p. ej. https://kroko.midominio.com).
    /// Con ella, el permiso OAuth vuelve al servidor y la App abre el enlace en el navegador de Pol.
    /// Vacío = modo local (el navegador se abre en esta PC y vuelve a 127.0.0.1).
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Interruptor general de los conectores.</summary>
    public bool Enabled { get; set; } = true;

    public Dictionary<string, McpServerOptions> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class McpServerOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Nombre para Pol y para la IA ("Navegador web", "Notion"…).</summary>
    public string? Title { get; set; }

    /// <summary>Indicaciones extra para la IA sobre cómo usar este conector.</summary>
    public string? Instructions { get; set; }

    // ---- Servidor local (stdio)
    public string? Command { get; set; }
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string?> Env { get; set; } = [];

    /// <summary>Variables de Env que deben tener valor; si falta alguna, el conector no arranca.</summary>
    public List<string> RequiredEnv { get; set; } = [];

    // ---- Servidor remoto (HTTP)
    public string? Url { get; set; }
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>Requiere iniciar sesión (OAuth). Solo se conecta tras pulsar "Conectar" la primera vez.</summary>
    public bool OAuth { get; set; }

    /// <summary>
    /// No arranca hasta que Pol pulse "Conectar" la primera vez (p. ej. porque descarga código de npm).
    /// La aceptación se recuerda.
    /// </summary>
    public bool RequiresConsent { get; set; }

    public string? ConsentNote { get; set; }

    /// <summary>Cliente OAuth propio (Google lo exige). Vacío = registro automático (Notion).</summary>
    public string? OAuthClientId { get; set; }
    public string? OAuthClientSecret { get; set; }
    public List<string>? OAuthScopes { get; set; }
    public int OAuthRedirectPort { get; set; } = 53682;

    // ---- Riesgo de cada herramienta (admite comodín final: "notion-get*")
    public List<string> ReadOnlyTools { get; set; } = [];
    public List<string> WriteTools { get; set; } = [];
    public List<string> DangerousTools { get; set; } = [];
    public List<string> DisabledTools { get; set; } = [];

    /// <summary>Riesgo de lo que no esté en ninguna lista ni marcado como solo lectura por el servidor.</summary>
    public string DefaultRisk { get; set; } = "Dangerous";

    public bool IsRemote => !string.IsNullOrWhiteSpace(Url);

    public static bool Matches(IEnumerable<string> patterns, string tool) => patterns.Any(p =>
        p.EndsWith('*') ? tool.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
                        : tool.Equals(p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Guarda los permisos (tokens OAuth) de un conector cifrados con la cuenta de Windows de Pol (DPAPI),
/// para no tener que volver a dar "Permitir" en cada arranque.
/// </summary>
public sealed class FileTokenCache(string serverName) : ITokenCache
{
    private static readonly byte[] Entropy = "Kroko.Mcp.Tokens.v1"u8.ToArray();

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", "conectores");

    private string FilePath => Path.Combine(Folder, $"{serverName}.token");

    public bool Exists => File.Exists(FilePath);

    public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(FilePath)) return ValueTask.FromResult<TokenContainer?>(null);
            var raw = File.ReadAllBytes(FilePath);
            var json = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(raw, Entropy, DataProtectionScope.CurrentUser) : raw;
            return ValueTask.FromResult(JsonSerializer.Deserialize<TokenContainer>(json));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            return ValueTask.FromResult<TokenContainer?>(null);
        }
    }

    public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Folder);
        var json = JsonSerializer.SerializeToUtf8Bytes(tokens);
        // En Windows, cifrado con la cuenta de Pol. En un servidor Linux (Fase 5) el archivo queda solo para su usuario.
        var data = OperatingSystem.IsWindows() ? ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser) : json;
        File.WriteAllBytes(FilePath, data);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return ValueTask.CompletedTask;
    }

    public void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}

/// <summary>Recuerda que Pol aceptó activar un conector que lo requiere.</summary>
public static class ConnectorConsent
{
    private static string File_(string name) => Path.Combine(FileTokenCache.Folder, $"{name}.aceptado");

    public static bool Given(string name) => File.Exists(File_(name));

    public static void Give(string name)
    {
        Directory.CreateDirectory(FileTokenCache.Folder);
        File.WriteAllText(File_(name), DateTimeOffset.Now.ToString("O"));
    }

    public static void Revoke(string name)
    {
        if (File.Exists(File_(name))) File.Delete(File_(name));
    }
}
