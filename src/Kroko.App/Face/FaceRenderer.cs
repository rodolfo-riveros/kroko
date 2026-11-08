using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Kroko.App.Face;

/// <summary>
/// Dibuja un fotograma de Kroko con vectores (sin imágenes). Lienzo lógico de 150 × 160 unidades;
/// el cuerpo es un círculo de 84 unidades centrado en (75, 92).
/// </summary>
public static class FaceRenderer
{
    public const double CanvasWidth = 150;
    public const double CanvasHeight = 160;
    public const double CenterX = 75;
    public const double CenterY = 92;
    public const double Radius = 42;

    private static readonly Brush BodyBrush = Frozen(new SolidColorBrush(Palette.Body));
    private static readonly Pen BodyEdgePen = Frozen(new Pen(Frozen(new SolidColorBrush(Palette.BodyEdge)), 1));
    private static readonly Brush StemBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)));
    private static readonly Pen StemPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A))), 0.8));
    private static readonly Brush WhiteBrush = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Brush TearBrush = Frozen(new SolidColorBrush(Palette.Tear));
    private static readonly Brush LoveBrush = Frozen(new SolidColorBrush(Palette.Love));
    private static readonly Brush LensBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)));
    private static readonly Brush FlyBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)));

    private static readonly Brush ShadowBrush = Frozen(new RadialGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromArgb(0x70, 0, 0, 0), 0.0),
            new(Color.FromArgb(0x38, 0, 0, 0), 0.75),
            new(Color.FromArgb(0x00, 0, 0, 0), 1.0),
        }));

    private static readonly Brush GlassBrush = Frozen(new LinearGradientBrush(
        Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 90));

    private static readonly Dictionary<string, Geometry> GlyphCache = [];

    public static void Render(DrawingContext dc, FaceFrame fr, double scale, double offsetX = 0, double offsetY = 0)
    {
        var f = fr.Face;
        var t = fr.Time;

        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, offsetX, offsetY));

        // Sombra en el "suelo": se achica cuando Kroko salta
        var jump = Math.Max(0, -f.BodyY);
        var shadowR = Radius + 7 - jump * 0.3;
        dc.PushOpacity(Math.Clamp(1 - jump / 25, 0.3, 1));
        dc.DrawEllipse(ShadowBrush, null, new Point(CenterX, CenterY + 4), shadowR, shadowR);
        dc.Pop();

        // Transformación del cuerpo: temblor, salto, inclinación y escala (apoyado en la base)
        var shakeX = f.BodyShake * Math.Sin(t * 47);
        var shakeY = f.BodyShake * 0.6 * Math.Cos(t * 59);
        var s = f.BodyScale * (1 + fr.Breath) * (1 + 0.05 * fr.Hover);
        var sx = s * (1 - f.BodySquash * 0.5);
        var sy = s * (1 + f.BodySquash);
        var pivot = new Point(CenterX, CenterY + Radius);

        var body = new TransformGroup();
        body.Children.Add(new ScaleTransform(sx, sy, pivot.X, pivot.Y));
        body.Children.Add(new RotateTransform(f.BodyTilt + fr.DragTilt, CenterX, CenterY + Radius * 0.6));
        body.Children.Add(new TranslateTransform(shakeX, f.BodyY + shakeY));
        dc.PushTransform(body);

        DrawRing(dc, f, fr.Mic, t);
        DrawAntenna(dc, f, fr, t);

        dc.DrawEllipse(BodyBrush, BodyEdgePen, new Point(CenterX, CenterY), Radius, Radius);
        dc.DrawEllipse(GlassBrush, null, new Point(CenterX, CenterY - 25), 28, 13);

        var eyeY = CenterY + f.EyeY;
        var lx = CenterX - f.EyeSpacing;
        var rx = CenterX + f.EyeSpacing;

        if (f.Blush > 0.01) DrawBlush(dc, f, lx, rx, eyeY);

        DrawEye(dc, f.Left, lx, eyeY, fr.BlinkFactor, t);
        DrawEye(dc, f.Right, rx, eyeY, fr.BlinkFactor, t);

        DrawMouth(dc, f, fr.Gaze, t);

        foreach (var (name, alpha) in fr.Extras)
            DrawExtra(dc, name, alpha, f, lx, rx, eyeY, t);

        dc.Pop(); // cuerpo

        if (fr.Prop is { Kind: not DistractionKind.Clock } prop)
            DrawProp(dc, prop);

        dc.Pop(); // escala
    }

    // ---------------------------------------------------------------- partes del cuerpo

    private static void DrawRing(DrawingContext dc, FaceParams f, double mic, double t)
    {
        if (f.Ring <= 0.01) return;
        var idle = 0.15 * (1 + Math.Sin(t * 3)) / 2;
        var level = Math.Max(mic, idle);
        var r1 = Radius + 4 + level * 7;
        dc.DrawEllipse(null, new Pen(Brush(Palette.Accent, (0.35 + 0.55 * level) * f.Ring), 2),
            new Point(CenterX, CenterY), r1, r1);
        var r2 = Radius + 5 + level * 14;
        dc.DrawEllipse(null, new Pen(Brush(Palette.Accent, 0.18 * level * f.Ring), 1.5),
            new Point(CenterX, CenterY), r2, r2);
    }

    private static void DrawAntenna(DrawingContext dc, FaceParams f, FaceFrame fr, double t)
    {
        var top = CenterY - Radius;
        dc.DrawRoundedRectangle(StemBrush, StemPen, new Rect(CenterX - 1.75, top - 10, 3.5, 11), 1.75, 1.75);

        var pulse = f.AntennaPulse * 0.5 * (1 - Math.Sin(t * 6));
        var level = Math.Max(fr.Mic, fr.Speech) * f.AntennaSync;
        var glow = Math.Clamp(f.AntennaGlow * (1 - pulse) + level * 0.6, 0, 1);
        var tip = new Point(CenterX, top - 13);

        if (glow > 0.02)
        {
            var gr = 4.5 + 8 * glow;
            var gb = new RadialGradientBrush(
                Color.FromArgb((byte)(150 * glow), f.AntennaColor.R, f.AntennaColor.G, f.AntennaColor.B),
                Color.FromArgb(0, f.AntennaColor.R, f.AntennaColor.G, f.AntennaColor.B));
            gb.Freeze();
            dc.DrawEllipse(gb, null, tip, gr, gr);
        }
        var tipColor = MathUtil.Lerp(Palette.BodyEdge, f.AntennaColor, 0.35 + 0.65 * Math.Max(glow, 0.4));
        dc.DrawEllipse(Brush(tipColor), null, tip, 4.5, 4.5);
    }

    private static void DrawBlush(DrawingContext dc, FaceParams f, double lx, double rx, double eyeY)
    {
        var a = MathUtil.Clamp01(f.Blush);
        var soft = Brush(Palette.Love, 0.28 * a);
        var pen = new Pen(Brush(Palette.Love, 0.95 * a), 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        foreach (var x in new[] { lx - 3, rx + 3 })
        {
            var y = eyeY + 16;
            dc.DrawEllipse(soft, null, new Point(x, y), 7, 3.5);
            for (int i = -1; i <= 1; i++)
                dc.DrawLine(pen, new Point(x + i * 3.2 - 1.2, y + 2), new Point(x + i * 3.2 + 1.2, y - 2));
        }
    }

    // ---------------------------------------------------------------- ojos

    private static void DrawEye(DrawingContext dc, EyeParams e, double cx, double cy, double blink, double t)
    {
        if (e.ShapeFrom != e.Shape && e.ShapeMix < 1)
        {
            // Cambio de forma: la forma vieja se cierra y la nueva se abre (como un parpadeo)
            if (e.ShapeMix < 0.5) DrawEyeShape(dc, e, e.ShapeFrom, cx, cy, 1 - e.ShapeMix * 2, blink, t);
            else DrawEyeShape(dc, e, e.Shape, cx, cy, e.ShapeMix * 2 - 1, blink, t);
        }
        else DrawEyeShape(dc, e, e.Shape, cx, cy, 1, blink, t);
    }

    private static void DrawEyeShape(DrawingContext dc, EyeParams e, EyeShape shape, double cx, double cy,
        double vScale, double blink, double t)
    {
        var x = cx + e.OffsetX;
        var y = cy + e.OffsetY;
        var w = Math.Max(1, e.Width);
        var h = Math.Max(1, e.Height);
        var sy = Math.Max(0.08, vScale * (shape == EyeShape.Line ? 1 : blink));
        var brush = Brush(e.Color);

        dc.PushTransform(new RotateTransform(e.Rotation, x, y));
        dc.PushTransform(new ScaleTransform(1, sy, x, y));

        switch (shape)
        {
            case EyeShape.Normal:
            {
                var up = MathUtil.Clamp01(e.UpperLid);
                var lo = MathUtil.Clamp01(e.LowerLid);
                var clipped = up > 0.001 || lo > 0.001;
                if (clipped)
                {
                    var visible = Math.Max(0.5, h * (1 - up - lo));
                    dc.PushClip(new RectangleGeometry(new Rect(x - w / 2 - 1, y - h / 2 + up * h, w + 2, visible)));
                }
                var r = Math.Min(e.CornerRadius, Math.Min(w, h) / 2);
                dc.DrawRoundedRectangle(brush, null, new Rect(x - w / 2, y - h / 2, w, h), r, r);
                if (clipped) dc.Pop();
                break;
            }
            case EyeShape.Arc:
            {
                var thick = Math.Max(3.5, w * 0.28);
                var half = w / 2 - thick / 2;
                var yb = y + h * 0.35;
                var k = h * 0.7 / 0.75;
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(x - half, yb), false, false);
                    c.BezierTo(new Point(x - half, yb - k), new Point(x + half, yb - k), new Point(x + half, yb), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(null, RoundPen(brush, thick), g);
                break;
            }
            case EyeShape.Line:
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(x - w / 2 + 2, y), false, false);
                    c.QuadraticBezierTo(new Point(x, y + 3), new Point(x + w / 2 - 2, y), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(null, RoundPen(brush, 3.6), g);
                break;
            }
            case EyeShape.Heart:
            {
                var beat = 1 + 0.07 * Math.Max(0, Math.Sin(t * 7));
                dc.PushTransform(new ScaleTransform(beat, beat, x, y));
                dc.DrawGeometry(brush, null, Heart(x, y, w, h));
                dc.Pop();
                break;
            }
            case EyeShape.Star:
            {
                dc.PushTransform(new RotateTransform(8 * Math.Sin(t * 3), x, y));
                dc.DrawGeometry(brush, null, Sparkle(x, y, w / 2, h / 2));
                dc.Pop();
                break;
            }
            case EyeShape.Spiral:
            {
                var g = new StreamGeometry();
                var maxR = Math.Min(w, h) / 2;
                using (var c = g.Open())
                {
                    const int steps = 60;
                    const double turns = 2.6;
                    c.BeginFigure(new Point(x, y), false, false);
                    for (int i = 1; i <= steps; i++)
                    {
                        var a = i / (double)steps * turns * 2 * Math.PI;
                        var rr = maxR * i / steps;
                        c.LineTo(new Point(x + rr * Math.Cos(a), y + rr * Math.Sin(a)), true, true);
                    }
                }
                g.Freeze();
                dc.PushTransform(new RotateTransform(-t * 420 % 360, x, y));
                dc.DrawGeometry(null, RoundPen(brush, 2.6), g);
                dc.Pop();
                break;
            }
            case EyeShape.X:
            {
                var pen = RoundPen(brush, 4);
                var a = w / 2 - 2;
                var b = h / 2 - 2;
                dc.DrawLine(pen, new Point(x - a, y - b), new Point(x + a, y + b));
                dc.DrawLine(pen, new Point(x - a, y + b), new Point(x + a, y - b));
                break;
            }
        }

        dc.Pop();
        dc.Pop();
    }

    // ---------------------------------------------------------------- boca

    private static void DrawMouth(DrawingContext dc, FaceParams f, Vector gaze, double t)
    {
        if (f.MouthFrom != f.Mouth && f.MouthMix < 1)
        {
            DrawMouthShape(dc, f, f.MouthFrom, 1 - f.MouthMix, gaze, t);
            DrawMouthShape(dc, f, f.Mouth, f.MouthMix, gaze, t);
        }
        else DrawMouthShape(dc, f, f.Mouth, 1, gaze, t);
    }

    private static void DrawMouthShape(DrawingContext dc, FaceParams f, MouthShape shape, double alpha, Vector gaze, double t)
    {
        if (shape == MouthShape.None || alpha <= 0.01) return;

        var x = CenterX + gaze.X * 0.5;
        var y = CenterY + f.MouthY + gaze.Y * 0.4;
        var w = 12 * f.MouthWidth;
        var open = MathUtil.Clamp01(f.MouthOpen);

        dc.PushOpacity(alpha);
        switch (shape)
        {
            case MouthShape.Smile:
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(x - w / 2, y - 1), false, false);
                    c.QuadraticBezierTo(new Point(x, y + 5), new Point(x + w / 2, y - 1), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(null, RoundPen(WhiteBrush, 2.4), g);
                break;
            }
            case MouthShape.Open:
            {
                var h = 1.6 + 6.5 * open;
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(x - w / 2, y - 1), true, true);
                    c.LineTo(new Point(x + w / 2, y - 1), true, true);
                    c.BezierTo(new Point(x + w / 2, y - 1 + h * 1.33), new Point(x - w / 2, y - 1 + h * 1.33),
                        new Point(x - w / 2, y - 1), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(WhiteBrush, null, g);
                break;
            }
            case MouthShape.O:
                dc.DrawEllipse(WhiteBrush, null, new Point(x, y + 2), 2.4 + 1.6 * open, 2.4 + 3.2 * open);
                break;
            case MouthShape.Wave:
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    const int segs = 4;
                    c.BeginFigure(new Point(x - w / 2, y), false, false);
                    for (int i = 1; i <= segs; i++)
                        c.LineTo(new Point(x - w / 2 + w * i / segs, y + (i % 2 == 1 ? -1.6 : 1.6)), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(null, RoundPen(WhiteBrush, 2), g);
                break;
            }
            case MouthShape.Flat:
                dc.DrawLine(RoundPen(WhiteBrush, 2.4), new Point(x - w * 0.35, y), new Point(x + w * 0.35, y));
                break;
        }
        dc.Pop();
    }

    // ---------------------------------------------------------------- extras

    private static void DrawExtra(DrawingContext dc, string name, double alpha, FaceParams f,
        double lx, double rx, double eyeY, double t)
    {
        var c = f.ExtraColor;
        dc.PushOpacity(alpha);
        switch (name.ToLowerInvariant())
        {
            case "sparkles":
            {
                var pts = new[] { (CenterX - 36, CenterY - 30), (CenterX + 34, CenterY - 36), (CenterX + 40, CenterY - 4) };
                for (int i = 0; i < pts.Length; i++)
                {
                    var k = 0.55 + 0.45 * Math.Sin(t * 8 + i * 2.1);
                    dc.DrawGeometry(Brush(c), null, Sparkle(pts[i].Item1, pts[i].Item2, 4.5 * k, 4.5 * k));
                }
                break;
            }
            case "sparkle":
            {
                var k = 0.7 + 0.3 * Math.Sin(t * 6);
                dc.DrawGeometry(Brush(c), null, Sparkle(CenterX + 31, CenterY - 31, 5.5 * k, 5.5 * k));
                break;
            }
            case "rays":
            {
                var pen = RoundPen(Brush(c), 1.8);
                var len = 3 + 1.2 * Math.Sin(t * 9);
                foreach (var ex in new[] { lx, rx })
                {
                    var o = new Point(ex, eyeY - 10);
                    foreach (var deg in new[] { -50.0, 0, 50 })
                    {
                        var a = (deg - 90) * Math.PI / 180;
                        var d = new Vector(Math.Cos(a), Math.Sin(a));
                        dc.DrawLine(pen, o + d * 10, o + d * (10 + len));
                    }
                }
                break;
            }
            case "dots":
            {
                var o = new Point(CenterX + 30, CenterY - 36);
                for (int i = 0; i < 3; i++)
                {
                    var a = t * 3 + i * 2 * Math.PI / 3;
                    var p = new Point(o.X + 7 * Math.Cos(a), o.Y + 4 * Math.Sin(a));
                    dc.DrawEllipse(Brush(c, 0.7 + 0.3 * Math.Sin(a)), null, p, 2.7, 2.7);
                }
                break;
            }
            case "sweat":
            {
                var p = t * 0.6 % 1;
                dc.PushOpacity(1 - p * 0.6);
                dc.DrawGeometry(TearBrush, null, Drop(CenterX + 31, CenterY - 26 + p * 8, 4.2));
                dc.Pop();
                break;
            }
            case "tear":
            {
                var p = t * 0.7 % 1;
                dc.PushOpacity(1 - p);
                dc.DrawGeometry(TearBrush, null, Drop(lx - 4, eyeY + 12 + p * 20, 3.4));
                dc.Pop();
                break;
            }
            case "tears":
            {
                foreach (var ex in new[] { lx, rx })
                {
                    var top = eyeY + 4;
                    var bottom = CenterY + Radius - 4;
                    dc.DrawRoundedRectangle(Brush(Palette.Tear, 0.85), null, new Rect(ex - 3, top, 6, bottom - top), 3, 3);
                    for (int i = 0; i < 2; i++)
                    {
                        var p = (t * 1.2 + i * 0.5) % 1;
                        dc.DrawEllipse(Brush(Color.FromRgb(0xB8, 0xDB, 0xFF), 0.9 * (1 - p)), null,
                            new Point(ex, top + 3 + p * (bottom - top - 6)), 1.6, 2.4);
                    }
                }
                break;
            }
            case "zzz":
            {
                for (int i = 0; i < 3; i++)
                {
                    var p = (t * 0.32 + i / 3.0) % 1;
                    var size = 7 + p * 7;
                    dc.PushOpacity(Math.Sin(p * Math.PI));
                    DrawGlyph(dc, "z", CenterX + 26 + p * 18, CenterY - 34 - p * 26, size, Brush(c));
                    dc.Pop();
                }
                break;
            }
            case "question":
                dc.PushTransform(new RotateTransform(12, CenterX + 33, CenterY - 40));
                DrawGlyph(dc, "?", CenterX + 33, CenterY - 40 + 2 * Math.Sin(t * 4), 20, Brush(c));
                dc.Pop();
                break;
            case "exclamation":
            {
                var k = 1 + 0.08 * Math.Sin(t * 10);
                dc.PushTransform(new ScaleTransform(k, k, CenterX + 34, CenterY - 40));
                DrawGlyph(dc, "!", CenterX + 34, CenterY - 40, 20, Brush(c));
                dc.Pop();
                break;
            }
            case "anger":
            {
                var o = new Point(CenterX + 32, CenterY - 34);
                var k = 1 + 0.15 * Math.Max(0, Math.Sin(t * 10));
                var pen = RoundPen(Brush(c), 2.4);
                dc.PushTransform(new ScaleTransform(k, k, o.X, o.Y));
                for (int q = 0; q < 4; q++)
                {
                    dc.PushTransform(new RotateTransform(q * 90, o.X, o.Y));
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(new Point(o.X + 2, o.Y - 6.5), false, false);
                        ctx.QuadraticBezierTo(new Point(o.X + 2, o.Y - 2), new Point(o.X + 6.5, o.Y - 2), true, true);
                    }
                    g.Freeze();
                    dc.DrawGeometry(null, pen, g);
                    dc.Pop();
                }
                dc.Pop();
                break;
            }
            case "hearts":
            {
                for (int i = 0; i < 3; i++)
                {
                    var p = (t * 0.45 + i / 3.0) % 1;
                    var hx = CenterX - 32 + i * 32 + 4 * Math.Sin(t * 3 + i);
                    var hy = CenterY - 22 - p * 42;
                    dc.PushOpacity(Math.Sin(p * Math.PI));
                    dc.DrawGeometry(LoveBrush, null, Heart(hx, hy, 8, 7));
                    dc.Pop();
                }
                break;
            }
            case "glasses":
            {
                // Bajan desde arriba mientras aparecen
                var drop = -(1 - alpha) * 34;
                dc.PushTransform(new TranslateTransform(0, drop));
                var stroke = new Pen(WhiteBrush, 1.6);
                stroke.Freeze();
                foreach (var ex in new[] { lx, rx })
                {
                    var lens = new Rect(ex - 11, eyeY - 8, 22, 15);
                    dc.DrawRectangle(LensBrush, stroke, lens);
                    dc.DrawRectangle(WhiteBrush, null, new Rect(lens.X + 3, lens.Y + 3, 3, 3));
                    dc.DrawRectangle(WhiteBrush, null, new Rect(lens.X + 7, lens.Y + 3, 2, 2));
                }
                dc.DrawRectangle(WhiteBrush, null, new Rect(lx + 11, eyeY - 6, rx - lx - 22, 2.5));
                dc.Pop();
                break;
            }
            case "notes":
            {
                for (int i = 0; i < 2; i++)
                {
                    var p = (t * 0.4 + i * 0.5) % 1;
                    dc.PushOpacity(Math.Sin(p * Math.PI));
                    DrawGlyph(dc, i == 0 ? "♪" : "♫", CenterX + 28 + p * 12 + 3 * Math.Sin(t * 4 + i),
                        CenterY - 30 - p * 28, 14, Brush(c));
                    dc.Pop();
                }
                break;
            }
            case "bulb":
            {
                var o = new Point(CenterX + 27, CenterY - 52);
                var glow = 0.5 + 0.5 * Math.Sin(t * 5);
                var gb = new RadialGradientBrush(Color.FromArgb((byte)(110 * glow), c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
                gb.Freeze();
                dc.DrawEllipse(gb, null, o, 15, 15);
                dc.DrawEllipse(Brush(c), null, o, 6.5, 6.5);
                dc.DrawRoundedRectangle(WhiteBrush, null, new Rect(o.X - 3, o.Y + 5, 6, 5), 1.2, 1.2);
                var pen = RoundPen(Brush(c), 1.6);
                foreach (var deg in new[] { -150.0, -90, -30, 0, 180 })
                {
                    var a = deg * Math.PI / 180;
                    var d = new Vector(Math.Cos(a), Math.Sin(a));
                    dc.DrawLine(pen, o + d * 9.5, o + d * 12.5);
                }
                break;
            }
        }
        dc.Pop();
    }

    // ---------------------------------------------------------------- distracciones

    private static void DrawProp(DrawingContext dc, PropState p)
    {
        dc.PushOpacity(p.Alpha);
        if (p.Kind == DistractionKind.Fly)
        {
            // Mosca pixel: cuerpo de 3 × 3 "píxeles" con alas que aletean
            var flap = Math.Sin(p.Time * 60) > 0;
            var px = 1.6;
            var x = Math.Round(p.X / px) * px;
            var y = Math.Round(p.Y / px) * px;
            var wing = Brush(Colors.White, 0.85);
            if (flap)
            {
                dc.DrawRectangle(wing, null, new Rect(x - px * 2, y - px * 2, px * 2, px));
                dc.DrawRectangle(wing, null, new Rect(x + px, y - px * 2, px * 2, px));
            }
            else
            {
                dc.DrawRectangle(wing, null, new Rect(x - px * 2, y - px, px * 2, px));
                dc.DrawRectangle(wing, null, new Rect(x + px, y - px, px * 2, px));
            }
            dc.DrawRectangle(WhiteBrush, null, new Rect(x - px * 1.5 - 0.6, y - px * 0.5 - 0.6, px * 3 + 1.2, px * 2 + 1.2));
            dc.DrawRectangle(FlyBrush, null, new Rect(x - px * 1.5, y - px * 0.5, px * 3, px * 2));
        }
        else if (p.Kind == DistractionKind.Ball)
        {
            dc.DrawEllipse(Brush(Palette.Accent), null, new Point(p.X, p.Y), 4.5, 4.5);
            dc.DrawEllipse(Brush(Colors.White, 0.7), null, new Point(p.X - 1.5, p.Y - 1.5), 1.3, 1.3);
        }
        dc.Pop();
    }

    // ---------------------------------------------------------------- utilidades de geometría

    private static Geometry Heart(double cx, double cy, double w, double h)
    {
        var g = new StreamGeometry();
        double X(double u) => cx - w / 2 + u * w;
        double Y(double v) => cy - h / 2 + v * h;
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(X(0.5), Y(0.98)), true, true);
            c.BezierTo(new Point(X(0.15), Y(0.72)), new Point(X(0.0), Y(0.52)), new Point(X(0.0), Y(0.3)), true, true);
            c.BezierTo(new Point(X(0.0), Y(0.12)), new Point(X(0.13), Y(0.0)), new Point(X(0.28), Y(0.0)), true, true);
            c.BezierTo(new Point(X(0.39), Y(0.0)), new Point(X(0.47), Y(0.07)), new Point(X(0.5), Y(0.18)), true, true);
            c.BezierTo(new Point(X(0.53), Y(0.07)), new Point(X(0.61), Y(0.0)), new Point(X(0.72), Y(0.0)), true, true);
            c.BezierTo(new Point(X(0.87), Y(0.0)), new Point(X(1.0), Y(0.12)), new Point(X(1.0), Y(0.3)), true, true);
            c.BezierTo(new Point(X(1.0), Y(0.52)), new Point(X(0.85), Y(0.72)), new Point(X(0.5), Y(0.98)), true, true);
        }
        g.Freeze();
        return g;
    }

    private static Geometry Sparkle(double cx, double cy, double rx, double ry)
    {
        var g = new StreamGeometry();
        var c0 = new Point(cx, cy);
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(cx, cy - ry), true, true);
            c.QuadraticBezierTo(c0, new Point(cx + rx, cy), true, true);
            c.QuadraticBezierTo(c0, new Point(cx, cy + ry), true, true);
            c.QuadraticBezierTo(c0, new Point(cx - rx, cy), true, true);
            c.QuadraticBezierTo(c0, new Point(cx, cy - ry), true, true);
        }
        g.Freeze();
        return g;
    }

    private static Geometry Drop(double cx, double cy, double r)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(cx, cy - r * 1.7), true, true);
            c.BezierTo(new Point(cx + r * 0.4, cy - r), new Point(cx + r, cy - r * 0.3), new Point(cx + r, cy + r * 0.2), true, true);
            c.ArcTo(new Point(cx - r, cy + r * 0.2), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            c.BezierTo(new Point(cx - r, cy - r * 0.3), new Point(cx - r * 0.4, cy - r), new Point(cx, cy - r * 1.7), true, true);
        }
        g.Freeze();
        return g;
    }

    private static void DrawGlyph(DrawingContext dc, string text, double cx, double cy, double size, Brush brush)
    {
        if (!GlyphCache.TryGetValue(text, out var geo))
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal),
                100, Brushes.White, 1.0);
            geo = ft.BuildGeometry(new Point(0, 0));
            geo.Freeze();
            GlyphCache[text] = geo;
        }

        var b = geo.Bounds;
        if (b.IsEmpty) return;
        var k = size / 100;
        dc.PushTransform(new MatrixTransform(k, 0, 0, k, cx - (b.X + b.Width / 2) * k, cy - (b.Y + b.Height / 2) * k));
        dc.DrawGeometry(brush, null, geo);
        dc.Pop();
    }

    private static Pen RoundPen(Brush brush, double thickness)
    {
        var p = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        p.Freeze();
        return p;
    }

    private static SolidColorBrush Brush(Color c, double opacity = 1)
    {
        var b = new SolidColorBrush(c) { Opacity = MathUtil.Clamp01(opacity) };
        b.Freeze();
        return b;
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
