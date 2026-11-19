using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kroko.App.Services;
using Kroko.App.Tools.FileActions;

namespace Kroko.App.Tests;

/// <summary>Conversiones de documentos e imágenes de Kroko 2 (18/11/2026).</summary>
[Collection("Archivos")]
public sealed class DocumentConvertTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kroko-docs-" + Guid.NewGuid().ToString("N"));

    public DocumentConvertTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Text(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Texto_a_PDF_y_de_vuelta_a_texto()
    {
        var txt = Text("notas.txt", "Hola Pol, mañana revisamos el informe.\nSegunda línea con ñ y tildes: canción.");
        var pdf = DocumentConvert.TextToPdf(txt);
        Assert.Equal(".pdf", Path.GetExtension(pdf));
        var back = DocumentConvert.ToText(pdf);
        Assert.Contains("informe", File.ReadAllText(back));
    }

    [Fact]
    public void Unir_y_separar_PDFs()
    {
        var a = DocumentConvert.TextToPdf(Text("a.txt", "uno"));
        var b = DocumentConvert.TextToPdf(Text("b.txt", string.Join('\n', Enumerable.Repeat("linea", 120)))); // 3 páginas
        var merged = DocumentConvert.MergePdfs([a, b]);
        using (var doc = UglyToad.PdfPig.PdfDocument.Open(merged)) Assert.Equal(4, doc.NumberOfPages);
        var folder = DocumentConvert.SplitPdf(merged);
        Assert.Equal(4, Directory.GetFiles(folder, "*.pdf").Length);
    }

    [Fact]
    public void Corta_las_lineas_largas_sin_partir_palabras()
    {
        var lines = DocumentConvert.Wrap(string.Join(' ', Enumerable.Repeat("palabra", 30)), 40);
        Assert.All(lines, l => Assert.True(l.Length <= 40));
        Assert.All(lines, l => Assert.DoesNotContain("palab ", l));
    }

    [Fact]
    public void Imagen_a_icono_y_comprimida()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(Brushes.SeaGreen, null, new System.Windows.Rect(0, 0, 300, 200));
        var bmp = new RenderTargetBitmap(300, 200, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var png = Path.Combine(_dir, "logo.png");
        using (var f = File.Create(png))
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            enc.Save(f);
        }
        var ico = ImageExtras.ToIco(png);
        var head = File.ReadAllBytes(ico)[..4];
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, head); // cabecera de icono
        Assert.EndsWith("(liviana).jpg", ImageExtras.Compress(png));
    }

    [Fact]
    public void Los_bolsillos_de_documentos()
    {
        Assert.Equal(["resumir", "texto", "bandeja"], QuickFiles.PocketsFor(["informe.pdf"]).Select(p => p.Key));
        Assert.Equal("docpdf", QuickFiles.PocketsFor(["informe.docx"])[0].Key);
        Assert.Equal("unir", QuickFiles.PocketsFor(["a.pdf", "b.pdf"])[0].Key);
    }
}
