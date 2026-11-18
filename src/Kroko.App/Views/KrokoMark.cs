using System.Windows;
using System.Windows.Media;

namespace Kroko.App.Views;

/// <summary>
/// La carita mínima de Kroko (diseño «Kroko 2», 18/11/2026): cuerpo mochi negro con borde, luz lima arriba y dos ojos
/// blancos. Para barras de título, la barra lateral y la isla en chiquito. Se dibuja con vectores y escala sin perder nitidez.
/// </summary>
public sealed class KrokoMark : FrameworkElement
{
    public static readonly DependencyProperty EyeBrushProperty = DependencyProperty.Register(nameof(EyeBrush), typeof(Brush),
        typeof(KrokoMark), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LightBrushProperty = DependencyProperty.Register(nameof(LightBrush), typeof(Brush),
        typeof(KrokoMark), new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xB6, 0xF2, 0x3A)), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Color de los ojos (ámbar en recordatorios, blanco el resto).</summary>
    public Brush EyeBrush
    {
        get => (Brush)GetValue(EyeBrushProperty);
        set => SetValue(EyeBrushProperty, value);
    }

    /// <summary>Color de la luz de arriba (lima conectado, gris sin cerebro).</summary>
    public Brush LightBrush
    {
        get => (Brush)GetValue(LightBrushProperty);
        set => SetValue(LightBrushProperty, value);
    }

    private static readonly Brush Body = Frozen(Color.FromRgb(0x0F, 0x0F, 0x0F));
    private static readonly Pen Edge = new(Frozen(Color.FromRgb(0x2E, 0x2E, 0x2E)), 1);

    static KrokoMark() => Edge.Freeze();

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Mochi: arriba más redondo, abajo un poco más ancho (como el cuerpo grande de Kroko)
        var top = h * 0.02;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(w * 0.16, h), true, true);
            c.LineTo(new Point(w * 0.84, h), true, false);
            c.QuadraticBezierTo(new Point(w, h), new Point(w * 0.98, h * 0.72), true, false);
            c.BezierTo(new Point(w * 0.93, h * 0.18), new Point(w * 0.78, top), new Point(w * 0.5, top), true, false);
            c.BezierTo(new Point(w * 0.22, top), new Point(w * 0.07, h * 0.18), new Point(w * 0.02, h * 0.72), true, false);
            c.QuadraticBezierTo(new Point(0, h), new Point(w * 0.16, h), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(Body, Edge, g);

        // Luz de estado
        var lw = w * 0.2;
        dc.DrawRoundedRectangle(LightBrush, null, new Rect(w / 2 - lw / 2, h * 0.1, lw, Math.Max(1, h * 0.055)), h * 0.03, h * 0.03);

        // Ojos
        var ew = w * 0.2;
        var eh = h * 0.31;
        var ey = h * 0.42;
        var r = ew * 0.45;
        dc.DrawRoundedRectangle(EyeBrush, null, new Rect(w * 0.5 - w * 0.06 - ew, ey, ew, eh), r, r);
        dc.DrawRoundedRectangle(EyeBrush, null, new Rect(w * 0.5 + w * 0.06, ey, ew, eh), r, r);
    }
}
