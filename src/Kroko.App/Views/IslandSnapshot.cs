using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kroko.App.Face;

namespace Kroko.App.Views;

/// <summary>
/// Herramienta de desarrollo (parte de --snapshot): dibuja la isla de arriba en sus estados (reposo, escucha, check,
/// enfoque, sin conexión) sobre una franja que imita la parte de arriba de la pantalla, sin mostrar ninguna ventana.
/// </summary>
public static class IslandSnapshot
{
    private sealed record Sample(FaceAnimator Face, string IslandText, bool IslandCheck, bool IslandBusy, bool IslandOnline, double Width);

    public static string Render(ExpressionLibrary library, string outputDir)
    {
        FaceAnimator Face(string expression)
        {
            var a = new FaceAnimator(library, () => 1.0) { EnableBlink = false, FollowCursor = false, EnableBreath = false };
            a.SetBase(expression);
            return a;
        }

        Sample[] samples =
        [
            new(Face("idle"), "Listo", false, false, true, 150),
            new(Face("idle"), "Listo", false, false, true, 280),
            new(Face("listening"), "Te escucho…", false, true, true, 250),
            new(Face("thinking"), "Pensando…", false, true, true, 250),
            new(Face("success"), "Anotado", true, true, true, 250),
            new(Face("focus"), "24:12 · Enfoque", false, true, true, 250),
            new(Face("sleeping"), "Durmiendo", false, false, true, 150),
            new(Face("offline"), "Sin conexión", false, true, false, 250),
        ];

        const double stripWidth = 420, rowHeight = 64;
        var visual = new DrawingVisual();
        var images = new List<RenderTargetBitmap>();
        foreach (var s in samples)
        {
            var island = new IslandView { DataContext = s, Width = s.Width };
            island.Measure(new Size(s.Width, 34));
            island.Arrange(new Rect(0, 0, s.Width, 34));
            island.UpdateLayout();
            island.RenderFaceNow();
            island.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)s.Width, 34, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(island);
            images.Add(bmp);
        }

        using (var dc = visual.RenderOpen())
        {
            var wallpaper = new LinearGradientBrush(Color.FromRgb(0x3B, 0x4C, 0x7A), Color.FromRgb(0x7A, 0x5C, 0x8F), 90);
            dc.DrawRectangle(wallpaper, null, new Rect(0, 0, stripWidth, rowHeight * images.Count));
            for (var i = 0; i < images.Count; i++)
            {
                var y = i * rowHeight;
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), 1), new Point(0, y), new Point(stripWidth, y));
                dc.DrawImage(images[i], new Rect((stripWidth - images[i].PixelWidth) / 2, y, images[i].PixelWidth, 34));
            }
        }

        var sheet = new RenderTargetBitmap((int)stripWidth, (int)(rowHeight * images.Count), 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual);
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "isla.png");
        using var file = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(sheet));
        encoder.Save(file);
        return path;
    }
}
