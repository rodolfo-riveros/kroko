using System.IO;
using System.IO.Compression;
using System.Text;
using Kroko.App.Services;

namespace Kroko.App.Tests;

/// <summary>Lectura de documentos e índice de búsqueda por significado (con vectores de prueba).</summary>
public class DocumentSearchTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "kroko-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Lee_el_texto_de_un_Word()
    {
        var path = Path.Combine(TempDir(), "contrato.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("word/document.xml");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write("""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Contrato de alquiler</w:t></w:r></w:p><w:p><w:r><w:t>Ana Quispe</w:t></w:r></w:p></w:body></w:document>""");
        }
        var text = DocumentText.Extract(path);
        Assert.Contains("Contrato de alquiler", text);
        Assert.Contains("Ana Quispe", text);
    }

    [Fact]
    public void Parte_los_textos_largos_en_trozos_con_solape()
    {
        var text = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"Línea número {i} con algo de contenido para llenar."));
        var chunks = DocumentText.Chunk(text);
        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.True(c.Length <= 1500));
        Assert.Contains("Línea número 1 ", chunks[0]);
    }

    [Fact]
    public void Encuentra_el_archivo_mas_parecido_y_se_actualiza()
    {
        var index = new DocumentIndex(Path.Combine(TempDir(), "docs.db"));
        float[] V(params float[] v) => v;
        index.Save(@"C:\docs\contrato-ana.docx", 1, 10, ["contrato de alquiler"], [V(1, 0, 0)]);
        index.Save(@"C:\docs\receta.txt", 1, 10, ["ají de gallina"], [V(0, 1, 0)]);
        index.Save(@"C:\docs\vacio.pdf", 1, 10, [], []); // sin texto útil: registrado pero sin trozos

        var hits = index.Search(V(0.9f, 0.1f, 0), "acuerdo alquiler");
        Assert.Equal("contrato-ana.docx", hits[0].Name);
        Assert.Equal((3, 2), index.Count());

        index.Remove(@"C:\docs\contrato-ana.docx");
        Assert.Equal("receta.txt", index.Search(V(0.9f, 0.1f, 0), "x")[0].Name);
        Assert.DoesNotContain(@"C:\docs\contrato-ana.docx", index.Files().Keys);
    }
}
