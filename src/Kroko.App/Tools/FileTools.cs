using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Kroko.App.Native;

namespace Kroko.App.Tools;

// ======================================================================== lectura

public sealed class ListarArchivosTool : ILocalTool
{
    private const int Max = 200;

    public string Name => "listar_archivos";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var ruta = PathResolver.Resolve(args.String("ruta"));
        if (!Directory.Exists(ruta)) return Task.FromResult(ToolOutcome.Fail($"La carpeta {ruta} no existe."));
        var patron = args.OptionalString("patron") ?? "*";

        var dir = new DirectoryInfo(ruta);
        var opts = new EnumerationOptions { IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        var carpetas = dir.EnumerateDirectories("*", opts)
            .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden))
            .OrderBy(d => d.Name)
            .Select(d => new { nombre = d.Name, modificado = d.LastWriteTime.ToString("yyyy-MM-dd") })
            .ToList();
        var archivos = dir.EnumerateFiles(patron, opts)
            .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden))
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new { nombre = f.Name, tamano = FileSizes.Human(f.Length), modificado = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm") })
            .ToList();

        var total = carpetas.Count + archivos.Count;
        return Task.FromResult(ToolOutcome.Success(new
        {
            ruta,
            carpetas = carpetas.Take(Max / 2),
            archivos = archivos.Take(Max),
            totalCarpetas = carpetas.Count,
            totalArchivos = archivos.Count,
            recortado = total > Max,
        }));
    }
}

public sealed class BuscarArchivosTool : ILocalTool
{
    private const int MaxResults = 100;
    private static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(20);

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".html", ".htm", ".log", ".ini", ".cfg", ".yaml", ".yml",
        ".cs", ".js", ".ts", ".py", ".php", ".java", ".sql", ".css", ".bat", ".ps1", ".rtf",
    };

    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "$Recycle.Bin", "AppData", "Windows",
    };

    public string Name => "buscar_archivos";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct) => Task.Run(() =>
    {
        var carpeta = PathResolver.Resolve(args.String("carpeta"));
        if (!Directory.Exists(carpeta)) return ToolOutcome.Fail($"La carpeta {carpeta} no existe.");
        var patron = args.String("patron");
        if (!patron.Contains('*') && !patron.Contains('?')) patron = $"*{patron}*";
        var contenido = args.OptionalString("contenido");

        var watch = Stopwatch.StartNew();
        var results = new List<object>();
        var revisados = 0;
        var porTiempo = false;

        foreach (var file in Enumerate(carpeta, patron))
        {
            ct.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeLimit) { porTiempo = true; break; }
            revisados++;

            if (contenido is not null && !ContainsText(file, contenido)) continue;
            results.Add(new { ruta = file.FullName, tamano = FileSizes.Human(file.Length), modificado = file.LastWriteTime.ToString("yyyy-MM-dd") });
            if (results.Count >= MaxResults) break;
        }

        return ToolOutcome.Success(new
        {
            encontrados = results.Count,
            resultados = results,
            limiteAlcanzado = results.Count >= MaxResults || porTiempo,
            nota = porTiempo ? "Búsqueda cortada a los 20 s: prueba una carpeta más concreta." : null,
        });
    }, ct);

    private static IEnumerable<FileInfo> Enumerate(string root, string pattern)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<FileInfo> files;
            IEnumerable<DirectoryInfo> subdirs;
            try
            {
                files = dir.EnumerateFiles(pattern, opts).ToList();
                subdirs = dir.EnumerateDirectories("*", opts)
                    .Where(d => !SkipFolders.Contains(d.Name) && !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var f in files) yield return f;
            foreach (var d in subdirs) stack.Push(d);
        }
    }

    private static bool ContainsText(FileInfo file, string text)
    {
        if (!TextExtensions.Contains(file.Extension) || file.Length > 5 * 1024 * 1024) return false;
        try
        {
            return File.ReadLines(file.FullName).Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

// ======================================================================== escritura

public sealed class MoverArchivosTool : ILocalTool
{
    public string Name => "mover_archivos";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct) =>
        Task.Run(() => FileTransfer.Run(args, move: true, ct), ct);
}

public sealed class CopiarArchivosTool : ILocalTool
{
    public string Name => "copiar_archivos";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct) =>
        Task.Run(() => FileTransfer.Run(args, move: false, ct), ct);
}

internal static class FileTransfer
{
    public static ToolOutcome Run(ToolArgs args, bool move, CancellationToken ct)
    {
        var destino = PathResolver.Resolve(args.String("destino"));
        PathGuard.EnsureWritable(destino);
        var origenes = args.StringArray("origenes").Select(PathResolver.Resolve).ToList();

        foreach (var o in origenes)
        {
            if (!File.Exists(o) && !Directory.Exists(o)) return ToolOutcome.Fail($"No existe: {o}");
            if (move) PathGuard.EnsureWritable(o);
            if (Directory.Exists(o) && (destino + "\\").StartsWith(o.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return ToolOutcome.Fail($"No puedo meter la carpeta {o} dentro de sí misma.");
        }

        Directory.CreateDirectory(destino);
        var hechos = new List<string>();
        var errores = new List<string>();
        foreach (var o in origenes)
        {
            ct.ThrowIfCancellationRequested();
            var target = PathGuard.UniqueTarget(destino, Path.GetFileName(o.TrimEnd('\\')));
            try
            {
                if (File.Exists(o))
                {
                    if (move) File.Move(o, target);
                    else File.Copy(o, target);
                }
                else if (move)
                {
                    MoveDirectory(o, target);
                }
                else
                {
                    CopyDirectory(o, target, ct);
                }
                hechos.Add(Path.GetFileName(target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errores.Add($"{Path.GetFileName(o)}: {ex.Message}");
            }
        }

        if (hechos.Count == 0) return ToolOutcome.Fail("No se pudo " + (move ? "mover" : "copiar") + " nada. " + string.Join(" | ", errores));
        return ToolOutcome.Success(new { destino, hechos, errores });
    }

    private static void MoveDirectory(string from, string to)
    {
        if (string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(from, to);
            return;
        }
        // Entre discos distintos: copiar y, solo si todo salió bien, borrar el original
        CopyDirectory(from, to, CancellationToken.None);
        Directory.Delete(from, recursive: true);
    }

    internal static void CopyDirectory(string from, string to, CancellationToken ct)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        }
        foreach (var d in Directory.EnumerateDirectories(from))
            CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)), ct);
    }
}

public sealed class RenombrarTool : ILocalTool
{
    public string Name => "renombrar";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var ruta = PathResolver.Resolve(args.String("ruta"));
        var nuevo = args.String("nuevo_nombre").Trim();
        if (nuevo.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || nuevo is "." or "..")
            return Task.FromResult(ToolOutcome.Fail($"\"{nuevo}\" no es un nombre válido (sin \\ / : * ? \" < > |)."));
        PathGuard.EnsureWritable(ruta);

        var isFile = File.Exists(ruta);
        if (!isFile && !Directory.Exists(ruta)) return Task.FromResult(ToolOutcome.Fail($"No existe: {ruta}"));

        // Si no puso extensión al renombrar un archivo, conservamos la original
        if (isFile && Path.GetExtension(nuevo).Length == 0) nuevo += Path.GetExtension(ruta);
        var target = Path.Combine(Path.GetDirectoryName(ruta.TrimEnd('\\'))!, nuevo);
        if (File.Exists(target) || Directory.Exists(target))
            return Task.FromResult(ToolOutcome.Fail($"Ya existe algo llamado {nuevo} en esa carpeta."));

        if (isFile) File.Move(ruta, target);
        else Directory.Move(ruta, target);
        return Task.FromResult(ToolOutcome.Success(new { antes = Path.GetFileName(ruta), ahora = nuevo }));
    }
}

public sealed class CrearCarpetaTool : ILocalTool
{
    public string Name => "crear_carpeta";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var ruta = PathResolver.Resolve(args.String("ruta"));
        PathGuard.EnsureWritable(ruta);
        if (Directory.Exists(ruta)) return Task.FromResult(ToolOutcome.Success(new { creada = ruta, yaExistia = true }));
        Directory.CreateDirectory(ruta);
        return Task.FromResult(ToolOutcome.Success(new { creada = ruta }));
    }
}

public sealed class EnviarAPapeleraTool : ILocalTool
{
    public string Name => "enviar_a_papelera";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var rutas = args.StringArray("rutas").Select(PathResolver.Resolve).ToList();
        foreach (var r in rutas)
        {
            if (!File.Exists(r) && !Directory.Exists(r)) return Task.FromResult(ToolOutcome.Fail($"No existe: {r}"));
            PathGuard.EnsureWritable(r);

            // En USB o unidades de red no hay Papelera: Windows borraría para siempre. Nunca lo hacemos.
            var drive = new DriveInfo(Path.GetPathRoot(r)!);
            if (drive.DriveType != DriveType.Fixed)
                return Task.FromResult(ToolOutcome.Fail($"{r} está en una unidad sin Papelera ({drive.DriveType}); no lo borro para no perderlo para siempre."));
        }

        var op = new NativeMethods.SHFILEOPSTRUCT
        {
            wFunc = NativeMethods.FO_DELETE,
            pFrom = string.Join('\0', rutas) + "\0\0",
            fFlags = NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_SILENT | NativeMethods.FOF_NOERRORUI,
        };
        var code = NativeMethods.SHFileOperation(ref op);
        var quedan = rutas.Where(r => File.Exists(r) || Directory.Exists(r)).ToList();

        if (code != 0 || op.fAnyOperationsAborted || quedan.Count == rutas.Count)
            return Task.FromResult(ToolOutcome.Fail($"Windows no pudo enviarlo a la Papelera (código {code}). Quizá algún archivo está abierto."));

        return Task.FromResult(ToolOutcome.Success(new
        {
            enviadosAPapelera = rutas.Count - quedan.Count,
            noSePudieron = quedan.Select(Path.GetFileName),
        }));
    }
}

public sealed class OrdenarCarpetaTool : ILocalTool
{
    private static readonly (string Folder, string[] Ext)[] Categories =
    [
        ("Imágenes", [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".svg", ".tif", ".tiff", ".raw", ".ico"]),
        ("Documentos", [".pdf", ".doc", ".docx", ".odt", ".rtf", ".txt", ".md", ".xls", ".xlsx", ".csv", ".ods", ".ppt", ".pptx", ".odp", ".epub"]),
        ("Videos", [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v"]),
        ("Música", [".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma"]),
        ("Comprimidos", [".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz"]),
        ("Instaladores", [".exe", ".msi", ".msix", ".appx", ".iso"]),
        ("Código", [".cs", ".js", ".ts", ".py", ".php", ".java", ".html", ".css", ".json", ".xml", ".sql", ".ps1", ".bat", ".sh"]),
    ];

    // En el escritorio, los accesos directos se quedan donde están
    private static readonly HashSet<string> Keep = new(StringComparer.OrdinalIgnoreCase) { ".lnk", ".url", ".ini" };

    public string Name => "ordenar_carpeta";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct) => Task.Run(() =>
    {
        var ruta = PathResolver.Resolve(args.String("ruta"));
        if (!Directory.Exists(ruta)) return ToolOutcome.Fail($"La carpeta {ruta} no existe.");
        PathGuard.EnsureWritable(ruta);
        var porFecha = string.Equals(args.OptionalString("criterio"), "fecha", StringComparison.OrdinalIgnoreCase);

        var files = new DirectoryInfo(ruta).EnumerateFiles()
            .Where(f => !Keep.Contains(f.Extension)
                        && !f.Attributes.HasFlag(FileAttributes.Hidden)
                        && !f.Attributes.HasFlag(FileAttributes.System))
            .ToList();

        if (files.Count == 0) return ToolOutcome.Success(new { movidos = 0, mensaje = "No había archivos sueltos que ordenar." });

        var moves = new List<object>();
        var porCarpeta = new Dictionary<string, int>();
        var omitidos = new List<string>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var folder = porFecha ? f.LastWriteTime.ToString("yyyy-MM") : CategoryOf(f.Extension);
            var targetDir = Path.Combine(ruta, folder);
            try
            {
                Directory.CreateDirectory(targetDir);
                var target = PathGuard.UniqueTarget(targetDir, f.Name);
                File.Move(f.FullName, target);
                moves.Add(new { desde = f.FullName, hacia = target });
                porCarpeta[folder] = porCarpeta.GetValueOrDefault(folder) + 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                omitidos.Add($"{f.Name} (en uso o sin permiso)");
            }
        }

        // Registro de lo que se movió, para poder deshacerlo si hace falta
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", "ordenes");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, $"orden-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(logPath, JsonSerializer.Serialize(new { carpeta = ruta, fecha = DateTime.Now, movimientos = moves },
            new JsonSerializerOptions { WriteIndented = true }));

        return ToolOutcome.Success(new { movidos = moves.Count, porCarpeta, omitidos, registro = logPath });
    }, ct);

    private static string CategoryOf(string ext) =>
        Categories.FirstOrDefault(c => c.Ext.Contains(ext, StringComparer.OrdinalIgnoreCase)).Folder ?? "Otros";
}

internal static class FileSizes
{
    public static string Human(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
