using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kroko.App.Face;

/// <summary>
/// El icono de Kroko: su cara dibujada con el mismo motor (bandeja, programa e instalador).
/// <c>Kroko.App.exe --icon &lt;archivo.ico&gt;</c> genera el .ico con varios tamaños (assets/kroko.ico).
/// </summary>
public static class KrokoIcon
{
    public static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    /// <summary>La cara en reposo, cuadrada, con el cuerpo ocupando casi todo el lado.</summary>
    public static BitmapSource Render(ExpressionLibrary library, int size, string expression = "idle")
    {
        var anim = new FaceAnimator(library, () => 1.0) { EnableBlink = false, FollowCursor = false, EnableBreath = false };
        anim.SetBase(expression);
        var frame = anim.Tick(null);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Encuadre en el cuerpo: los tamaños chicos van más llenos para que se lea la cara
            var fill = size <= 24 ? 0.98 : 0.92;
            var scale = size * fill / (FaceRenderer.BodyHalfWidth * 2 + 2);
            var c = FaceRenderer.BodyCenter;
            FaceRenderer.Render(dc, frame, scale, size / 2.0 - c.X * scale, size / 2.0 - c.Y * scale, shadow: false);
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Escribe un .ico con todos los tamaños (cada uno como PNG dentro, válido desde Windows Vista).</summary>
    public static void WriteIco(ExpressionLibrary library, string path)
    {
        var images = IcoSizes.Select(s => (Size: s, Png: Png(Render(library, s)))).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
    }

    public static void WritePng(BitmapSource image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, Png(image));
    }

    private static byte[] Png(BitmapSource image)
    {
        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(ms);
        return ms.ToArray();
    }
}
