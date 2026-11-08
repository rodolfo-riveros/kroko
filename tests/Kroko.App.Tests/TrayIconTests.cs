using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kroko.App.Services;

namespace Kroko.App.Tests;

public class TrayIconTests
{
    /// <summary>La bandeja fallaba al arrancar: H.NotifyIcon no convierte un RenderTargetBitmap en icono.</summary>
    [Fact]
    public void La_cara_dibujada_se_convierte_en_icono_de_Windows()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawEllipse(Brushes.Black, null, new System.Windows.Point(32, 32), 30, 30);
        var bmp = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();

        using var icon = TrayIconImage.ToIcon(bmp);
        Assert.Equal(64, icon.Width);
        Assert.Equal(64, icon.Height);
        using var asBitmap = icon.ToBitmap();
        Assert.Equal(255, asBitmap.GetPixel(32, 32).A); // el centro del círculo es opaco
        Assert.Equal(0, asBitmap.GetPixel(0, 0).A);     // la esquina, transparente
    }
}
