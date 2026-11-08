using System.Windows.Media;

namespace Kroko.App.Face;

public enum EyeShape { Normal, Arc, Line, Heart, Star, Spiral, X }

public enum MouthShape { None, Smile, Open, O, Wave, Flat }

/// <summary>
/// Parámetros de un ojo, en "unidades de cara" (el cuerpo mide 84 unidades de diámetro).
/// </summary>
public sealed class EyeParams
{
    public double Width = 20;
    public double Height = 24;
    public double CornerRadius = 9;
    public double OffsetX;
    public double OffsetY;
    public double Rotation;
    public double UpperLid;
    public double LowerLid;
    public EyeShape Shape = EyeShape.Normal;
    public Color Color = Colors.White;

    /// <summary>Forma de la que viene durante un cambio de forma (se transforma como un parpadeo).</summary>
    public EyeShape ShapeFrom = EyeShape.Normal;

    /// <summary>0 = todavía forma anterior, 1 = forma nueva completa.</summary>
    public double ShapeMix = 1;

    public EyeParams Clone() => (EyeParams)MemberwiseClone();

    public static EyeParams Lerp(EyeParams a, EyeParams b, double t)
    {
        var r = new EyeParams
        {
            Width = MathUtil.Lerp(a.Width, b.Width, t),
            Height = MathUtil.Lerp(a.Height, b.Height, t),
            CornerRadius = MathUtil.Lerp(a.CornerRadius, b.CornerRadius, t),
            OffsetX = MathUtil.Lerp(a.OffsetX, b.OffsetX, t),
            OffsetY = MathUtil.Lerp(a.OffsetY, b.OffsetY, t),
            Rotation = MathUtil.Lerp(a.Rotation, b.Rotation, t),
            UpperLid = MathUtil.Lerp(a.UpperLid, b.UpperLid, t),
            LowerLid = MathUtil.Lerp(a.LowerLid, b.LowerLid, t),
            Color = MathUtil.Lerp(a.Color, b.Color, t),
            Shape = b.Shape,
        };

        if (a.Shape == b.Shape)
        {
            r.ShapeFrom = a.ShapeFrom;
            r.ShapeMix = a.ShapeMix + (1 - a.ShapeMix) * MathUtil.Clamp01(t);
        }
        else
        {
            // Si "a" estaba a mitad de un cambio, partimos de la forma que se veía en ese momento.
            r.ShapeFrom = a.ShapeMix < 0.5 ? a.ShapeFrom : a.Shape;
            r.ShapeMix = MathUtil.Clamp01(t);
            if (r.ShapeFrom == r.Shape) r.ShapeMix = 1;
        }
        return r;
    }
}

/// <summary>
/// Todo lo que define una cara en un instante. Se interpola entre expresiones.
/// </summary>
public sealed class FaceParams
{
    public EyeParams Left = new();
    public EyeParams Right = new();
    public double EyeSpacing = 13;
    public double EyeY = 2;

    public MouthShape Mouth = MouthShape.None;
    public MouthShape MouthFrom = MouthShape.None;
    public double MouthMix = 1;
    public double MouthOpen;
    public double MouthWidth = 1;
    public double MouthY = 19;
    public double MouthSync;

    public double Blush;
    public string[] Extras = [];
    public Color ExtraColor = Palette.Accent;

    public double BodyScale = 1;
    public double BodySquash;
    public double BodyTilt;
    public double BodyY;
    public double BodyShake;

    public Color AntennaColor = Palette.Accent;
    public double AntennaGlow = 0.6;
    public double AntennaPulse;
    public double AntennaSync;

    public double Ring;
    public double GazeFollow = 1;
    public double Blink = 1;
    public double BreathPeriod = 4000;

    public FaceParams Clone()
    {
        var c = (FaceParams)MemberwiseClone();
        c.Left = Left.Clone();
        c.Right = Right.Clone();
        return c;
    }

    public static FaceParams Lerp(FaceParams a, FaceParams b, double t)
    {
        var r = new FaceParams
        {
            Left = EyeParams.Lerp(a.Left, b.Left, t),
            Right = EyeParams.Lerp(a.Right, b.Right, t),
            EyeSpacing = MathUtil.Lerp(a.EyeSpacing, b.EyeSpacing, t),
            EyeY = MathUtil.Lerp(a.EyeY, b.EyeY, t),
            MouthOpen = MathUtil.Lerp(a.MouthOpen, b.MouthOpen, t),
            MouthWidth = MathUtil.Lerp(a.MouthWidth, b.MouthWidth, t),
            MouthY = MathUtil.Lerp(a.MouthY, b.MouthY, t),
            MouthSync = MathUtil.Lerp(a.MouthSync, b.MouthSync, t),
            Blush = MathUtil.Lerp(a.Blush, b.Blush, t),
            Extras = b.Extras,
            ExtraColor = MathUtil.Lerp(a.ExtraColor, b.ExtraColor, t),
            BodyScale = MathUtil.Lerp(a.BodyScale, b.BodyScale, t),
            BodySquash = MathUtil.Lerp(a.BodySquash, b.BodySquash, t),
            BodyTilt = MathUtil.Lerp(a.BodyTilt, b.BodyTilt, t),
            BodyY = MathUtil.Lerp(a.BodyY, b.BodyY, t),
            BodyShake = MathUtil.Lerp(a.BodyShake, b.BodyShake, t),
            AntennaColor = MathUtil.Lerp(a.AntennaColor, b.AntennaColor, t),
            AntennaGlow = MathUtil.Lerp(a.AntennaGlow, b.AntennaGlow, t),
            AntennaPulse = MathUtil.Lerp(a.AntennaPulse, b.AntennaPulse, t),
            AntennaSync = MathUtil.Lerp(a.AntennaSync, b.AntennaSync, t),
            Ring = MathUtil.Lerp(a.Ring, b.Ring, t),
            GazeFollow = MathUtil.Lerp(a.GazeFollow, b.GazeFollow, t),
            Blink = MathUtil.Lerp(a.Blink, b.Blink, t),
            BreathPeriod = MathUtil.Lerp(a.BreathPeriod, b.BreathPeriod, t),
            Mouth = b.Mouth,
        };

        if (a.Mouth == b.Mouth)
        {
            r.MouthFrom = a.MouthFrom;
            r.MouthMix = a.MouthMix + (1 - a.MouthMix) * MathUtil.Clamp01(t);
        }
        else
        {
            r.MouthFrom = a.MouthMix < 0.5 ? a.MouthFrom : a.Mouth;
            r.MouthMix = MathUtil.Clamp01(t);
        }
        return r;
    }
}

/// <summary>Colores fijos del motor (deben coincidir con Theme/KrokoTheme.xaml).</summary>
public static class Palette
{
    public static readonly Color Body = Color.FromRgb(0x0F, 0x0F, 0x0F);
    public static readonly Color BodyEdge = Color.FromRgb(0x2A, 0x2A, 0x2A);
    public static readonly Color Accent = Color.FromRgb(0xA3, 0xFF, 0x6A);
    public static readonly Color Love = Color.FromRgb(0xFF, 0x7E, 0xB6);
    public static readonly Color Tear = Color.FromRgb(0x4D, 0xA3, 0xFF);
    public static readonly Color Alert = Color.FromRgb(0xFF, 0xB5, 0x47);
    public static readonly Color Error = Color.FromRgb(0xFF, 0x5C, 0x6C);
}

public static class MathUtil
{
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    public static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    public static Color Lerp(Color a, Color b, double t)
    {
        t = Clamp01(t);
        return Color.FromArgb(
            (byte)Math.Round(Lerp(a.A, b.A, t)),
            (byte)Math.Round(Lerp(a.R, b.R, t)),
            (byte)Math.Round(Lerp(a.G, b.G, t)),
            (byte)Math.Round(Lerp(a.B, b.B, t)));
    }

    public static double SmoothStep(double t)
    {
        t = Clamp01(t);
        return t * t * (3 - 2 * t);
    }

    public static double CubicOut(double t)
    {
        t = Clamp01(t);
        var u = 1 - t;
        return 1 - u * u * u;
    }

    public static double BackOut(double t)
    {
        t = Clamp01(t);
        const double c1 = 1.4;
        const double c3 = c1 + 1;
        var u = t - 1;
        return 1 + c3 * u * u * u + c1 * u * u;
    }

    /// <summary>Suavizado exponencial independiente de los fps.</summary>
    public static double Approach(double current, double target, double speed, double dt) =>
        current + (target - current) * (1 - Math.Exp(-speed * dt));
}
