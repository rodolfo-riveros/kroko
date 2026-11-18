using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace Kroko.App.Tools.FileActions;

/// <summary>
/// Lo que se puede hacer con un archivo soltado sobre Kroko (17/11/2026, especificación «soltar archivos»): convertir y
/// ordenar. Nunca toca el original al convertir (el resultado va al lado con otro nombre) y nunca borra nada.
/// </summary>
public static class FileConvert
{
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif"];
    public static readonly string[] AudioExtensions = [".mp3", ".wav", ".m4a", ".aac", ".ogg", ".flac", ".wma", ".opus"];

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Imagen a otro formato (png, jpg, bmp, tiff o gif) con los codificadores de Windows. Devuelve la ruta nueva.</summary>
    public static string ImageTo(string path, string format)
    {
        format = format.Trim('.').ToLowerInvariant();
        BitmapEncoder encoder = format switch
        {
            "png" => new PngBitmapEncoder(),
            "jpg" or "jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
            "bmp" => new BmpBitmapEncoder(),
            "tif" or "tiff" => new TiffBitmapEncoder(),
            "gif" => new GifBitmapEncoder(),
            _ => throw new ArgumentException($"No sé convertir a «{format}».", nameof(format)),
        };
        var frame = LoadFrame(path);
        // JPG no tiene transparencia: se pone fondo blanco en vez de negro
        encoder.Frames.Add(format is "jpg" or "jpeg" ? BitmapFrame.Create(OnWhite(frame)) : frame);
        var target = PathGuard.UniqueTarget(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "." + (format == "jpeg" ? "jpg" : format));
        using var output = File.Create(target);
        encoder.Save(output);
        return target;
    }

    /// <summary>Varias imágenes en un solo PDF (una por página, ajustada a A4). Devuelve la ruta del PDF.</summary>
    public static string ImagesToPdf(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) throw new ArgumentException("No hay imágenes.");
        var builder = new PdfDocumentBuilder();
        foreach (var path in paths)
        {
            var frame = LoadFrame(path);
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(OnWhite(frame)));
            using var ms = new MemoryStream();
            encoder.Save(ms);

            // A4 en puntos, con 24 de margen, conservando la proporción de la imagen
            const double pageW = 595, pageH = 842, margin = 24;
            var scale = Math.Min((pageW - 2 * margin) / frame.PixelWidth, (pageH - 2 * margin) / frame.PixelHeight);
            var w = frame.PixelWidth * scale;
            var h = frame.PixelHeight * scale;
            var page = builder.AddPage(pageW, pageH);
            page.AddJpeg(ms.ToArray(), new PdfRectangle((pageW - w) / 2, (pageH - h) / 2, (pageW + w) / 2, (pageH + h) / 2));
        }
        var first = paths[0];
        var name = paths.Count == 1 ? Path.GetFileNameWithoutExtension(first) + ".pdf" : $"Imágenes {DateTime.Now:yyyy-MM-dd HH.mm}.pdf";
        var target = PathGuard.UniqueTarget(Path.GetDirectoryName(first)!, name);
        File.WriteAllBytes(target, builder.Build());
        return target;
    }

    /// <summary>ffmpeg del PATH (null si no está instalado: Kroko lo dice y no descarga nada solo).</summary>
    public static string? FindFfmpeg()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(exe)) return exe;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Audio a otro formato con ffmpeg. Devuelve la ruta nueva.</summary>
    public static async Task<string> AudioToAsync(string path, string format, CancellationToken ct)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException("Para convertir audio necesito ffmpeg, y no está instalado.");
        format = format.Trim('.').ToLowerInvariant();
        var target = PathGuard.UniqueTarget(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "." + format);
        using var p = Process.Start(new ProcessStartInfo(ffmpeg)
        {
            ArgumentList = { "-hide_banner", "-loglevel", "error", "-n", "-i", path, target },
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("No pude abrir ffmpeg.");
        var errors = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0 || !File.Exists(target))
            throw new InvalidOperationException("ffmpeg no pudo convertirlo: " + (errors.Length > 160 ? errors[..160] : errors).Trim());
        return target;
    }

    private static BitmapFrame LoadFrame(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }

    private static BitmapSource OnWhite(BitmapSource source)
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new System.Windows.Rect(0, 0, source.PixelWidth, source.PixelHeight);
            dc.DrawRectangle(System.Windows.Media.Brushes.White, null, rect);
            dc.DrawImage(source, rect);
        }
        var bmp = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }
}

/// <summary>Ordenar archivos soltados: propone carpeta por tipo o fecha, mueve y deja registro para deshacer.</summary>
public static class FileOrganize
{
    public sealed record Move(string From, string To);

    /// <summary>A dónde iría cada archivo (subcarpeta de su carpeta, por tipo o por año-mes).</summary>
    public static List<Move> Plan(IEnumerable<string> paths, bool byDate) => [.. paths.Select(p =>
    {
        var folder = byDate ? File.GetLastWriteTime(p).ToString("yyyy-MM") : OrdenarCarpetaTool.CategoryOf(Path.GetExtension(p));
        return new Move(p, Path.Combine(Path.GetDirectoryName(p)!, folder, Path.GetFileName(p)));
    })];

    /// <summary>Mueve (sin pisar nada) y guarda el registro de <see cref="OrderUndo"/>. Devuelve cuántos movió.</summary>
    public static int Apply(IReadOnlyList<Move> plan)
    {
        var moves = new List<object>();
        foreach (var m in plan)
        {
            PathGuard.EnsureWritable(m.From);
            var dir = Path.GetDirectoryName(m.To)!;
            Directory.CreateDirectory(dir);
            var target = PathGuard.UniqueTarget(dir, Path.GetFileName(m.To));
            File.Move(m.From, target);
            moves.Add(new { desde = m.From, hacia = target });
        }
        Directory.CreateDirectory(OrderUndo.LogDir);
        File.WriteAllText(Path.Combine(OrderUndo.LogDir, $"orden-{DateTime.Now:yyyyMMdd-HHmmss}.json"), JsonSerializer.Serialize(new
        {
            carpeta = Path.GetDirectoryName(plan[0].From),
            fecha = DateTime.Now,
            movimientos = moves,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return moves.Count;
    }
}

/// <summary>
/// «ver_imagen»: le muestra a la IA una imagen de la PC (para «Analizar» al soltar una foto o «¿qué dice esta imagen?»).
/// Respeta FileSendGuard: nunca imágenes de carpetas de Kroko, llaves ni perfiles de navegadores.
/// </summary>
public sealed class VerImagenTool : ILocalTool
{
    private const int MaxSide = 1600;

    public string Name => "ver_imagen";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct) => Task.Run(() =>
    {
        var path = PathResolver.Resolve(args.String("ruta"));
        if (!File.Exists(path)) return ToolOutcome.Fail($"No encuentro la imagen {path}.");
        if (!FileConvert.IsImage(path)) return ToolOutcome.Fail("Eso no es una imagen (PNG, JPG, BMP, TIFF o GIF).");
        FileSendGuard.EnsureSendable(path);

        BitmapSource source;
        using (var stream = File.OpenRead(path))
            source = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest > MaxSide)
        {
            var k = (double)MaxSide / longest;
            source = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(k, k));
        }
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ToolOutcome.Success(new
        {
            archivo = Path.GetFileName(path),
            ancho = source.PixelWidth,
            alto = source.PixelHeight,
            mimeType = "image/jpeg",
            imageBase64 = Convert.ToBase64String(ms.ToArray()),
        });
    }, ct);
}
