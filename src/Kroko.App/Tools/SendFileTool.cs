using System.IO;

namespace Kroko.App.Tools;

/// <summary>
/// "Mándame el contrato por Telegram": lee el archivo y lo entrega al cerebro, que lo envía como documento al
/// Telegram del dueño (nunca a otro chat). El archivo viaja dentro del resultado ("archivoBase64") y el cerebro lo
/// quita antes de dárselo a la IA, igual que las capturas de pantalla.
/// </summary>
public sealed class EnviarArchivoTool : ILocalTool
{
    public string Name => "enviar_archivo";

    public async Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var path = PathResolver.Resolve(args.String("ruta"));
        if (Directory.Exists(path))
            throw new ToolException($"{path} es una carpeta. Dime qué archivo de dentro quieres (puedo listarlos).");
        if (!File.Exists(path))
            throw new ToolException($"No encontré el archivo {path}. Búscalo con buscar_archivos.");

        FileSendGuard.EnsureSendable(path);
        var info = new FileInfo(path);
        if (info.Length > FileSendGuard.MaxBytes)
            throw new ToolException($"{info.Name} pesa {info.Length / 1024.0 / 1024.0:0.#} MB; por Telegram puedo mandar hasta " +
                                    $"{FileSendGuard.MaxBytes / 1024 / 1024} MB.");

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, ct);
        }
        catch (IOException)
        {
            throw new ToolException($"{info.Name} está abierto en otro programa y no lo puedo leer. Ciérralo y lo intento otra vez.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new ToolException($"Windows no me deja leer {info.Name}.");
        }

        return ToolOutcome.Success(new
        {
            nombre = info.Name,
            ruta = path,
            bytes = bytes.Length,
            mimeType = FileSendGuard.MimeOf(info.Name),
            mensaje = args.OptionalString("mensaje"),
            archivoBase64 = Convert.ToBase64String(bytes),
        });
    }
}

/// <summary>Qué archivos pueden salir de la PC por Telegram: nunca secretos, llaves ni perfiles de navegador.</summary>
public static class FileSendGuard
{
    /// <summary>Telegram acepta hasta 50 MB por documento enviado por un bot; se deja margen.</summary>
    public const long MaxBytes = 45L * 1024 * 1024;

    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly string[] BlockedFolders =
    [
        Path.Combine(Local, "Kroko"),                       // memoria, claves cifradas, permisos de Kroko
        Path.Combine(Profile, ".ssh"),
        Path.Combine(Profile, ".gnupg"),
        Path.Combine(Profile, ".aws"),
        Path.Combine(Profile, ".azure"),
        Path.Combine(Profile, ".docker"),
        Path.Combine(Profile, ".kube"),
        Path.Combine(Roaming, "Microsoft", "Credentials"),
        Path.Combine(Roaming, "Microsoft", "Protect"),
        Path.Combine(Local, "Microsoft", "Credentials"),
        Path.Combine(Local, "Google", "Chrome", "User Data"),
        Path.Combine(Local, "Microsoft", "Edge", "User Data"),
        Path.Combine(Local, "BraveSoftware"),
        Path.Combine(Roaming, "Mozilla", "Firefox"),
        Path.Combine(Roaming, "Opera Software"),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
    ];

    private static readonly string[] BlockedExtensions =
        [".pem", ".key", ".pfx", ".p12", ".ppk", ".kdbx", ".kdb", ".keychain", ".ovpn", ".gpg", ".asc", ".jks", ".keystore"];

    private static readonly string[] BlockedNames =
        ["secretos.dat", "wallet.dat", "login data", "cookies", "web data", ".git-credentials", ".netrc", ".npmrc", ".pypirc",
         "credentials", "credentials.json", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "known_hosts", "authorized_keys"];

    public static void EnsureSendable(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var folder in BlockedFolders.Where(f => !string.IsNullOrEmpty(f)))
        {
            var root = Path.GetFullPath(folder).TrimEnd('\\');
            if (full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ToolException("Por seguridad no envío archivos de esa carpeta (contraseñas, llaves o datos privados).");
        }

        var name = Path.GetFileName(full).ToLowerInvariant();
        var ext = Path.GetExtension(name);
        if (name == ".env" || name.StartsWith(".env.", StringComparison.Ordinal) || ext == ".env"
            || BlockedExtensions.Contains(ext) || BlockedNames.Contains(name)
            || name.StartsWith("id_rsa", StringComparison.Ordinal) || name.StartsWith("id_ed25519", StringComparison.Ordinal))
            throw new ToolException($"Por seguridad no envío {Path.GetFileName(full)}: parece contener contraseñas o llaves.");
    }

    public static string MimeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".txt" or ".md" or ".csv" => "text/plain",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".zip" => "application/zip",
        ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4",
        _ => "application/octet-stream",
    };
}
