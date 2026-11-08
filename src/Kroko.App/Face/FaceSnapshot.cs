using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kroko.App.Face;

/// <summary>
/// Herramienta de desarrollo: dibuja todas las expresiones en una hoja PNG sin abrir ventanas.
/// Uso: Kroko.App.exe --snapshot "C:\ruta\carpeta"
/// </summary>
public static class FaceSnapshot
{
    private const double CellScale = 1.6;
    private const int Columns = 7;

    public static string RenderSheet(ExpressionLibrary library, string outputDir, double atSeconds = 0.6)
    {
        Directory.CreateDirectory(outputDir);
        var items = library.All;
        var cellW = FaceRenderer.CanvasWidth * CellScale;
        var cellH = FaceRenderer.CanvasHeight * CellScale + 22;
        var rows = (int)Math.Ceiling(items.Count / (double)Columns);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)), null,
                new Rect(0, 0, cellW * Columns, cellH * rows));

            for (int i = 0; i < items.Count; i++)
            {
                var exp = items[i];
                var x = i % Columns * cellW;
                var y = i / Columns * cellH;
                var frame = Simulate(library, exp, atSeconds);
                FaceRenderer.Render(dc, frame, CellScale, x, y);

                var label = new FormattedText($"{exp.Name} ({exp.Key})", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, 1.0);
                dc.DrawText(label, new Point(x + 6, y + cellH - 20));
            }
        }

        var bmp = new RenderTargetBitmap((int)(cellW * Columns), (int)(cellH * rows), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var path = Path.Combine(outputDir, $"expresiones_{(int)(atSeconds * 1000)}ms.png");
        using var fs = File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        enc.Save(fs);
        return path;
    }

    /// <summary>Avanza un animador con reloj simulado hasta <paramref name="atSeconds"/>.</summary>
    private static FaceFrame Simulate(ExpressionLibrary library, Expression exp, double atSeconds)
    {
        double now = 0;
        var anim = new FaceAnimator(library, () => now)
        {
            EnableBlink = false,
            FollowCursor = false,
            MicLevel = 0.6,
            SpeechLevel = 0.6,
        };
        if (exp.IsLoop) anim.SetBase(exp.Key);
        else anim.Play(exp.Key);

        FaceFrame frame = anim.Tick(null);
        for (now = 1 / 60.0; now <= atSeconds; now += 1 / 60.0)
            frame = anim.Tick(null);
        return frame;
    }
}
