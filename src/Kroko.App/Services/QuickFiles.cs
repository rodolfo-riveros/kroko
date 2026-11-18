using System.IO;
using Kroko.App.Tools;
using Kroko.App.Tools.FileActions;

namespace Kroko.App.Services;

/// <summary>Un «bolsillo» de la isla: lo que se hace al soltar el archivo encima.</summary>
public sealed record Pocket(string Key, string Label, string Glyph)
{
    /// <summary>El icono (Glyph es el código hex de Segoe Fluent Icons: "E8AB").</summary>
    public string GlyphText => char.ConvertFromUtf32(Convert.ToInt32(Glyph, 16));
}

/// <summary>Lo que quedó al terminar: «PDF listo», el archivo nuevo y cómo deshacerlo.</summary>
public sealed record QuickResult(string Title, string? Path, string Detail, Func<string>? Undo = null);

/// <summary>
/// Acciones rápidas con archivos soltados sobre la isla (diseño Kroko 2, 18/11/2026). Los bolsillos cambian según el
/// tipo: imágenes → PDF; audio → MP3 y transcribir; documentos → resumir; todo → celular y bandeja. Nunca toca el original
/// y nada se borra (deshacer = el archivo nuevo a la Papelera).
/// </summary>
public sealed class QuickFiles(FileTray tray, BrainApiClient api, ActivityFeed activity, AuditLog audit)
{
    private static readonly string[] Documents = [".pdf", ".docx", ".doc", ".txt", ".md", ".rtf", ".pptx", ".xlsx", ".csv"];

    public static bool IsDocument(string path) => Documents.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Los tres bolsillos para estos archivos.</summary>
    public static IReadOnlyList<Pocket> PocketsFor(IReadOnlyList<string> files)
    {
        var bandeja = new Pocket("bandeja", "Guardar en la bandeja", "E7B8");
        var celular = new Pocket("celular", "Enviar al celular", "E724");
        if (files.Count > 0 && files.All(FileConvert.IsImage)) return [new("pdf", "Convertir a PDF", "E8AB"), celular, bandeja];
        if (files.Count > 0 && files.All(FileConvert.IsAudio))
            return [new("mp3", "Convertir a MP3", "E8D6"), new("transcribir", "Transcribir", "E8D2"), bandeja];
        if (files.Count == 1 && IsDocument(files[0])) return [new("resumir", "Resumir", "E9D9"), celular, bandeja];
        return [celular, new("telegram", "Enviar a Telegram", "E8BD"), bandeja];
    }

    /// <summary>«3 imágenes · 4,2 MB».</summary>
    public static string Describe(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return "";
        var size = DropSize(files);
        if (files.Count == 1) return $"{Path.GetFileName(files[0])} · {size}";
        var kind = files.All(FileConvert.IsImage) ? "imágenes" : files.All(FileConvert.IsAudio) ? "audios" : "archivos";
        return $"{files.Count} {kind} · {size}";
    }

    private static string DropSize(IReadOnlyList<string> files) =>
        ViewModels.DropPanelViewModel.Size(files.Where(File.Exists).Sum(f => new FileInfo(f).Length));

    /// <summary>Hace la acción. progress: (hechos, total, archivo actual).</summary>
    public async Task<QuickResult> RunAsync(string key, IReadOnlyList<string> files, IProgress<(int Done, int Total, string Current)> progress,
        CancellationToken ct)
    {
        Record(key, files);
        switch (key)
        {
            case "pdf":
            {
                var pdf = await Task.Run(() => FileConvert.ImagesToPdf(files, (i, f) => progress.Report((i, files.Count, Path.GetFileName(f)))), ct);
                progress.Report((files.Count, files.Count, Path.GetFileName(pdf)));
                var detail = $"{files.Count} página{(files.Count == 1 ? "" : "s")} · {DropSize([pdf])} · en {FolderName(pdf)}";
                activity.AddCreatedFile($"Convirtió {files.Count} {(files.Count == 1 ? "foto" : "fotos")} a {Path.GetFileName(pdf)}", "PDF listo", pdf);
                return new QuickResult("PDF listo", pdf, detail, () => RecycleBin.Send(pdf) ? "Listo: lo mandé a la Papelera" : "Ya no estaba");
            }
            case "mp3":
            {
                if (FileConvert.FindFfmpeg() is null)
                    throw new InvalidOperationException("Para convertir audio necesito ffmpeg (gratis, de ffmpeg.org). Yo no descargo nada solo.");
                var made = new List<string>();
                for (var i = 0; i < files.Count; i++)
                {
                    progress.Report((i, files.Count, Path.GetFileName(files[i])));
                    made.Add(await FileConvert.AudioToAsync(files[i], "mp3", ct));
                }
                progress.Report((files.Count, files.Count, ""));
                var last = made[^1];
                activity.AddCreatedFile($"Convirtió {made.Count} audio{(made.Count == 1 ? "" : "s")} a MP3", "MP3 listo", last);
                return new QuickResult("MP3 listo", last, $"{DropSize(made)} · en {FolderName(last)}",
                    () => made.Count(RecycleBin.Send) is var n && n > 0 ? $"Listo: mandé {n} a la Papelera" : "Ya no estaban");
            }
            case "transcribir":
            {
                var file = files[0];
                FileSendGuard.EnsureSendable(file);
                progress.Report((0, 1, Path.GetFileName(file)));
                var text = await api.TranscribeAsync(await File.ReadAllBytesAsync(file, ct), MimeOf(file), ct);
                if (text.Trim().Length == 0) throw new InvalidOperationException("No escuché palabras en ese audio.");
                var txt = PathGuard.UniqueTarget(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + " (transcripción).txt");
                await File.WriteAllTextAsync(txt, text.Trim(), ct);
                progress.Report((1, 1, Path.GetFileName(txt)));
                activity.AddCreatedFile($"Transcribió {Path.GetFileName(file)}", "Transcripción lista", txt);
                var words = text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
                return new QuickResult("Transcripción lista", txt, $"{words} palabras · en {FolderName(txt)}",
                    () => RecycleBin.Send(txt) ? "Listo: la mandé a la Papelera" : "Ya no estaba");
            }
            case "bandeja":
            {
                var n = tray.Add(files);
                return new QuickResult(n == 0 ? "Ya estaba en la bandeja" : "En la bandeja", files.Count == 1 ? files[0] : null,
                    "Arrástralo desde el panel Kroko → Bandeja a cualquier app. Se vacía sola a las 24 h.");
            }
            default:
                throw new ArgumentException($"Acción desconocida: {key}");
        }
    }

    private void Record(string key, IReadOnlyList<string> files) => audit.Record(new AuditEntry(
        DateTime.Now, "isla_" + key, "Write", string.Join(", ", files.Select(Path.GetFileName)), "soltado por el dueño", true, "", 0));

    private static string FolderName(string path) => Path.GetFileName(Path.GetDirectoryName(path)) ?? "";

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mp3",
        ".wav" => "audio/wav",
        ".m4a" or ".aac" => "audio/aac",
        ".ogg" or ".opus" => "audio/ogg",
        ".flac" => "audio/flac",
        _ => "audio/mpeg",
    };
}
