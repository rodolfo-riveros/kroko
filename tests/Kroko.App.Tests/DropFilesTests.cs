using System.IO;
using System.Net;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kroko.App.Services.LanShare;
using Kroko.App.Tools;
using Kroko.App.Tools.FileActions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kroko.App.Tests;

/// <summary>Soltar archivos sobre Kroko (17/11/2026): convertir, juntar en PDF, ordenar con deshacer y el enlace al celular.</summary>
[Collection("Archivos")]
public sealed class DropFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kroko-soltar-" + Guid.NewGuid().ToString("N"));

    public DropFilesTests()
    {
        Directory.CreateDirectory(_dir);
        OrderUndo.LogDirOverride = Path.Combine(_dir, "_registros");
    }

    public void Dispose()
    {
        OrderUndo.LogDirOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string MakePng(string name, int w = 40, int h = 30)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(Brushes.OrangeRed, null, new System.Windows.Rect(0, 0, w, h));
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        var path = Path.Combine(_dir, name);
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }

    [Fact]
    public void Convierte_una_imagen_sin_tocar_el_original()
    {
        var png = MakePng("foto.png");
        var jpg = FileConvert.ImageTo(png, "jpg");

        Assert.Equal(Path.Combine(_dir, "foto.jpg"), jpg);
        Assert.True(File.Exists(png));
        var bytes = File.ReadAllBytes(jpg);
        Assert.Equal(0xFF, bytes[0]); // cabecera JPEG
        Assert.Equal(0xD8, bytes[1]);

        // Si ya existe, no la pisa
        Assert.Equal(Path.Combine(_dir, "foto (2).jpg"), FileConvert.ImageTo(png, "jpg"));
    }

    [Fact]
    public void Junta_varias_imagenes_en_un_PDF()
    {
        var pdf = FileConvert.ImagesToPdf([MakePng("a.png"), MakePng("b.png", 30, 60)]);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(pdf), 0, 4));
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.Equal(2, doc.NumberOfPages);
    }

    [Fact]
    public void Formato_desconocido_se_explica() =>
        Assert.Throws<ArgumentException>(() => FileConvert.ImageTo(MakePng("x.png"), "psd"));

    [Fact]
    public void Ordena_por_tipo_y_se_puede_deshacer()
    {
        var foto = MakePng("vacaciones.png");
        var nota = Path.Combine(_dir, "nota.txt");
        File.WriteAllText(nota, "hola");

        var plan = FileOrganize.Plan([foto, nota], byDate: false);
        Assert.All(plan, m => Assert.Equal(_dir, Path.GetDirectoryName(Path.GetDirectoryName(m.To))));
        Assert.Equal(2, FileOrganize.Apply(plan));
        Assert.False(File.Exists(foto));
        Assert.False(File.Exists(nota));

        var undo = OrderUndo.UndoLast();
        Assert.NotNull(undo);
        Assert.Equal(2, undo.Devueltos);
        Assert.True(File.Exists(foto));
        Assert.True(File.Exists(nota));
        Assert.Null(OrderUndo.UndoLast()); // no se deshace dos veces
    }

    [Fact]
    public void Ordena_por_fecha_en_carpeta_anio_mes()
    {
        var nota = Path.Combine(_dir, "nota.txt");
        File.WriteAllText(nota, "hola");
        File.SetLastWriteTime(nota, new DateTime(2025, 3, 9));
        var plan = FileOrganize.Plan([nota], byDate: true);
        Assert.Equal(Path.Combine(_dir, "2025-03", "nota.txt"), plan[0].To);
    }

    [Fact]
    public void El_enlace_al_celular_sirve_una_sola_vez_y_vence()
    {
        using var share = new PhoneShare(NullLogger<PhoneShare>.Instance);
        var now = DateTime.UtcNow;
        share.UseForTest(new PhoneShare.Offer("abc123", "x", "x", false, now.AddMinutes(5), "", null!));

        Assert.Null(share.Take("otro", now));        // token equivocado
        Assert.NotNull(share.Take("abc123", now));   // primera vez
        Assert.Null(share.Take("abc123", now));      // segunda vez: ya se usó

        share.UseForTest(new PhoneShare.Offer("def456", "x", "x", false, now.AddMinutes(5), "", null!));
        Assert.Null(share.Take("def456", now.AddMinutes(6))); // vencido
    }

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.3.4", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    public void Solo_comparte_en_la_red_de_casa(string ip, bool privada) =>
        Assert.Equal(privada, PhoneShare.IsPrivate(IPAddress.Parse(ip)));
}

public class FreeKrokoTests
{
    private static Kroko.App.Native.NativeMethods.RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    [Fact]
    public void Se_pega_al_borde_si_queda_a_12_px_o_menos()
    {
        var work = R(0, 0, 1920, 1040);
        Assert.Equal((-10, 0), Kroko.App.Services.EdgeSnap.Offset(R(10, 500, 118, 576), work, 12));       // izquierda
        Assert.Equal((5, 6), Kroko.App.Services.EdgeSnap.Offset(R(1807, 958, 1915, 1034), work, 12));     // esquina abajo a la derecha
        Assert.Equal((0, 0), Kroko.App.Services.EdgeSnap.Offset(R(400, 400, 508, 476), work, 12));         // en medio: no se mueve
    }

    [Theory]
    [InlineData("isla", true, false)]
    [InlineData("burbuja", false, false)]
    [InlineData("libre", false, true)]
    [InlineData("Libre ", false, true)]
    public void Donde_vive_Kroko(string look, bool isla, bool libre)
    {
        var o = new Kroko.App.Services.KrokoOptions { Look = look };
        Assert.Equal(isla, o.IsIsland);
        Assert.Equal(libre, o.IsFree);
    }

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData("Chrome", "Chrome")]
    [InlineData("MSEdge", "Edge")]
    [InlineData("", "")]
    public void Nombre_de_la_app_que_suena(string id, string nombre) =>
        Assert.Equal(nombre, Kroko.App.Services.MediaSessionService.AppName(id));
}
