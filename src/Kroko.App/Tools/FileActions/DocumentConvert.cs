using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace Kroko.App.Tools.FileActions;

/// <summary>
/// Conversiones de documentos (Kroko 2, 18/11/2026): PDF a texto, unir y separar PDFs, texto a PDF, Word a texto y
/// Word/Excel/PowerPoint a PDF (con el Office de la PC o LibreOffice si está instalado; si no, lo dice). Como todo
/// lo de soltar archivos: el original nunca se toca y el resultado queda al lado con otro nombre.
/// </summary>
public static class DocumentConvert
{
    public static readonly string[] OfficeExtensions = [".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".odt", ".ods", ".odp", ".rtf"];

    public static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    public static bool IsOffice(string path) => OfficeExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static bool IsText(string path) => Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".md" or ".csv" or ".log";
    public static bool IsWord(string path) => Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase);

    /// <summary>El texto de un PDF o un Word, en un .txt al lado.</summary>
    public static string ToText(string path)
    {
        var text = Services.DocumentText.Extract(path, int.MaxValue);
        if (text.Trim().Length == 0) throw new InvalidOperationException("No encontré texto (quizá es un PDF escaneado: para eso usa «Analizar»).");
        var target = PathGuard.UniqueTarget(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".txt");
        File.WriteAllText(target, text);
        return target;
    }

    /// <summary>Varios PDF en uno, en el orden en que los soltaste.</summary>
    public static string MergePdfs(IReadOnlyList<string> paths)
    {
        if (paths.Count < 2) throw new ArgumentException("Suelta dos o más PDF para unirlos.");
        var bytes = PdfMerger.Merge([.. paths]);
        var target = PathGuard.UniqueTarget(Path.GetDirectoryName(paths[0])!, $"PDF unidos {DateTime.Now:yyyy-MM-dd HH.mm}.pdf");
        File.WriteAllBytes(target, bytes);
        return target;
    }

    /// <summary>Un PDF en un archivo por página, dentro de una carpeta nueva. Devuelve la carpeta.</summary>
    public static string SplitPdf(string path)
    {
        int pages;
        using (var doc = UglyToad.PdfPig.PdfDocument.Open(path)) pages = doc.NumberOfPages;
        if (pages < 2) throw new InvalidOperationException("Ese PDF tiene una sola página.");
        var folder = PathGuard.UniqueTarget(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + " (páginas)");
        Directory.CreateDirectory(folder);
        var source = File.ReadAllBytes(path);
        for (var p = 1; p <= pages; p++)
        {
            var one = PdfMerger.Merge([source], [[p]]);
            File.WriteAllBytes(Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(path)} - página {p}.pdf"), one);
        }
        return folder;
    }

    /// <summary>Un texto (.txt, .md) a PDF A4 con letra Helvetica 11.</summary>
    public static string TextToPdf(string path)
    {
        var lines = Wrap(File.ReadAllText(path), 92);
        var builder = new PdfDocumentBuilder();
        // La letra de Windows (Arial) tiene tildes y ñ; si no estuviera, la estándar del PDF (sin tildes)
        var arial = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        var trueType = File.Exists(arial);
        var font = trueType ? builder.AddTrueTypeFont(File.ReadAllBytes(arial))
            : builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        const double top = 800, bottom = 50, left = 50, lineHeight = 15;
        PdfPageBuilder? page = null;
        var y = 0.0;
        foreach (var line in lines)
        {
            if (page is null || y < bottom)
            {
                page = builder.AddPage(595, 842);
                y = top;
            }
            if (line.Length > 0) page.AddText(trueType ? Printable(line) : Plain(line), 11, new PdfPoint(left, y), font);
            y -= lineHeight;
        }
        if (page is null) builder.AddPage(595, 842);
        var target = PathGuard.UniqueTarget(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".pdf");
        File.WriteAllBytes(target, builder.Build());
        return target;
    }

    /// <summary>Word, Excel o PowerPoint a PDF: con el Office de la PC (invisible, solo lectura) o con LibreOffice.</summary>
    public static async Task<string> OfficeToPdfAsync(string path, CancellationToken ct)
    {
        var target = PathGuard.UniqueTarget(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".pdf");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        string? officeError = null;
        try
        {
            await RunSta(() => ExportWithOffice(path, target, ext));
            if (File.Exists(target)) return target;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Reflection.TargetInvocationException)
        {
            officeError = ex.Message;
        }

        if (FindLibreOffice() is { } soffice)
        {
            using var p = Process.Start(new ProcessStartInfo(soffice)
            {
                ArgumentList = { "--headless", "--convert-to", "pdf", "--outdir", Path.GetDirectoryName(path)!, path },
                CreateNoWindow = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("No pude abrir LibreOffice.");
            await p.WaitForExitAsync(ct);
            var made = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".pdf");
            if (File.Exists(made)) return made;
        }
        throw new InvalidOperationException(officeError is null
            ? "Para pasar documentos de Office a PDF necesito Microsoft Office o LibreOffice (gratis) instalado."
            : "Office no pudo exportarlo a PDF. ¿Está abierto o protegido?");
    }

    private static void ExportWithOffice(string path, string target, string ext)
    {
        if (ext is ".docx" or ".doc" or ".rtf" or ".odt")
        {
            dynamic word = CreateCom("Word.Application");
            try
            {
                word.Visible = false;
                word.DisplayAlerts = 0;
                dynamic doc = word.Documents.Open(path, false, true);
                doc.SaveAs2(target, 17); // wdFormatPDF
                doc.Close(false);
            }
            finally { word.Quit(false); }
        }
        else if (ext is ".xlsx" or ".xls" or ".ods")
        {
            dynamic excel = CreateCom("Excel.Application");
            try
            {
                excel.Visible = false;
                excel.DisplayAlerts = false;
                dynamic book = excel.Workbooks.Open(path, 0, true);
                book.ExportAsFixedFormat(0, target); // xlTypePDF
                book.Close(false);
            }
            finally { excel.Quit(); }
        }
        else
        {
            dynamic ppt = CreateCom("PowerPoint.Application");
            try
            {
                dynamic show = ppt.Presentations.Open(path, -1, 0, 0); // solo lectura, sin ventana
                show.SaveAs(target, 32); // ppSaveAsPDF
                show.Close();
            }
            finally { ppt.Quit(); }
        }
    }

    private static dynamic CreateCom(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException($"{progId} no está instalado."))
        ?? throw new InvalidOperationException($"No pude abrir {progId}.");

    private static Task RunSta(Action work)
    {
        var done = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                work();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }

    private static string? FindLibreOffice()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var exe = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    /// <summary>Corta el texto en líneas de N caracteres (sin partir palabras).</summary>
    internal static List<string> Wrap(string text, int width)
    {
        var result = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Replace("\t", "    ");
            while (line.Length > width)
            {
                var cut = line.LastIndexOf(' ', width);
                if (cut <= 0) cut = width;
                result.Add(line[..cut]);
                line = line[cut..].TrimStart();
            }
            result.Add(line);
        }
        return result;
    }

    /// <summary>Arial no dibuja emojis ni símbolos raros: se cambian por «?».</summary>
    private static string Printable(string s) => new([.. s.Select(c => char.IsSurrogate(c) || char.IsControl(c) ? '?' : c)]);

    /// <summary>Sin Arial, la letra estándar del PDF no tiene tildes: «canción» → «cancion».</summary>
    private static string Plain(string s) => new([.. s.Normalize(System.Text.NormalizationForm.FormD)
        .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
        .Select(c => c < 128 ? c : '?')]);
}

/// <summary>Más formatos de imagen (Kroko 2): comprimir para mandar e ICO para iconos.</summary>
public static class ImageExtras
{
    /// <summary>JPG de calidad 75 y como mucho 1920 px de lado: pesa mucho menos y se ve igual en el celular.</summary>
    public static string Compress(string path)
    {
        var source = Load(path);
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest > 1920) source = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(1920.0 / longest, 1920.0 / longest));
        var enc = new JpegBitmapEncoder { QualityLevel = 75 };
        enc.Frames.Add(BitmapFrame.Create(source));
        var target = PathGuard.UniqueTarget(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + " (liviana).jpg");
        using var output = File.Create(target);
        enc.Save(output);
        return target;
    }

    /// <summary>Un icono .ico (PNG de hasta 256 px por dentro, como los de Windows 10/11).</summary>
    public static string ToIco(string path)
    {
        var source = Load(path);
        var k = 256.0 / Math.Max(source.PixelWidth, source.PixelHeight);
        if (k < 1) source = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(k, k));
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        png.Save(ms);
        var data = ms.ToArray();
        var target = PathGuard.UniqueTarget(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".ico");
        using var w = new BinaryWriter(File.Create(target));
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)1);
        w.Write((byte)(source.PixelWidth >= 256 ? 0 : source.PixelWidth));
        w.Write((byte)(source.PixelHeight >= 256 ? 0 : source.PixelHeight));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((short)1);
        w.Write((short)32);
        w.Write(data.Length);
        w.Write(22);
        w.Write(data);
        return target;
    }

    private static BitmapSource Load(string path)
    {
        using var s = File.OpenRead(path);
        return BitmapDecoder.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
    }
}
