using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kroko.Shared.Tools;

namespace Kroko.App.Tools;

/// <summary>Una herramienta local (sección 9). Una clase por herramienta.</summary>
public interface ILocalTool
{
    string Name { get; }
    Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct);
}

/// <summary>Resultado de una herramienta: datos (para la IA) o un error legible.</summary>
public sealed record ToolOutcome(bool Ok, object? Data, string? Error)
{
    public static ToolOutcome Success(object? data = null) => new(true, data, null);
    public static ToolOutcome Fail(string error) => new(false, null, error);
}

/// <summary>Error en los argumentos o en una condición previa: se devuelve tal cual a la IA.</summary>
public sealed class ToolException(string message) : Exception(message);

/// <summary>Acceso cómodo y validado a los argumentos JSON.</summary>
public sealed class ToolArgs(JsonElement raw)
{
    public JsonElement Raw => raw;

    public string String(string name)
    {
        var v = ToolCatalog.Str(raw, name).Trim();
        return v.Length > 0 ? v : throw new ToolException($"Falta el parámetro '{name}'.");
    }

    public string? OptionalString(string name) =>
        ToolCatalog.Str(raw, name).Trim() is { Length: > 0 } v ? v : null;

    public int Int(string name, int min, int max)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
            throw new ToolException($"Falta el número '{name}'.");
        return Math.Clamp((int)Math.Round(v.GetDouble()), min, max);
    }

    public bool Bool(string name) => ToolCatalog.Bool(raw, name);

    public IReadOnlyList<string> StringArray(string name)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty(name, out var v))
            throw new ToolException($"Falta la lista '{name}'.");
        if (v.ValueKind == JsonValueKind.String) return [v.GetString()!];
        if (v.ValueKind != JsonValueKind.Array) throw new ToolException($"'{name}' debe ser una lista.");
        var list = v.EnumerateArray().Select(e => e.GetString()?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
        return list.Count > 0 ? list : throw new ToolException($"La lista '{name}' está vacía.");
    }
}

/// <summary>
/// Convierte lo que dice Pol ("escritorio", "descargas\fotos", "%USERPROFILE%\x") en rutas reales.
/// </summary>
public static class PathResolver
{
    private static readonly Dictionary<string, Func<string>> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["escritorio"] = () => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        ["desktop"] = () => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        ["documentos"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        ["mis documentos"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        ["documents"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        ["descargas"] = Downloads,
        ["downloads"] = Downloads,
        ["imagenes"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        ["imágenes"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        ["fotos"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        ["pictures"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        ["musica"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        ["música"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        ["music"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        ["videos"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        ["vídeos"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        ["usuario"] = () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ["mi carpeta"] = () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    };

    public static string Resolve(string input)
    {
        var text = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
        if (text.StartsWith('~')) text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + text[1..];

        // "escritorio" o "escritorio\algo"
        var sep = text.IndexOfAny(['\\', '/']);
        var head = sep < 0 ? text : text[..sep];
        if (Known.TryGetValue(head, out var folder))
            text = sep < 0 ? folder() : Path.Combine(folder(), text[(sep + 1)..]);

        if (!Path.IsPathRooted(text))
            throw new ToolException($"No sé dónde está \"{input}\". Dame la ruta completa o una carpeta conocida (escritorio, descargas…).");

        return Path.GetFullPath(text);
    }

    private static string Downloads()
    {
        if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, 0, out var ptr) == 0)
        {
            try
            {
                return Marshal.PtrToStringUni(ptr)!;
            }
            finally
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, nint token, out nint path);
}

/// <summary>
/// Zonas donde Kroko nunca escribe ni borra: Windows, Archivos de programa, la raíz de un disco
/// y la carpeta del propio Kroko.
/// </summary>
public static class PathGuard
{
    private static readonly string[] Protected =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        AppContext.BaseDirectory,
    ];

    public static void EnsureWritable(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        if (Path.GetPathRoot(full)?.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase) == true)
            throw new ToolException($"Por seguridad no toco la raíz del disco ({full}).");

        foreach (var p in Protected.Where(p => !string.IsNullOrEmpty(p)))
        {
            var root = Path.GetFullPath(p).TrimEnd('\\');
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ToolException($"Por seguridad no modifico nada dentro de {root}.");
        }
    }

    /// <summary>Si "destino\nombre" ya existe, devuelve "destino\nombre (2)", "(3)"…</summary>
    public static string UniqueTarget(string folder, string name)
    {
        var target = Path.Combine(folder, name);
        if (!File.Exists(target) && !Directory.Exists(target)) return target;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            target = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(target) && !Directory.Exists(target)) return target;
        }
    }
}
