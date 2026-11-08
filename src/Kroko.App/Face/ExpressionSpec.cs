using System.Windows.Media;

namespace Kroko.App.Face;

// Estas clases reflejan expressions.json. Todo es opcional: lo que no se indica se hereda.

public sealed class EyeSpec
{
    public double? W { get; set; }
    public double? H { get; set; }
    public double? R { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Rot { get; set; }
    public double? Upper { get; set; }
    public double? Lower { get; set; }
    public EyeShape? Shape { get; set; }
    public string? Color { get; set; }

    public void ApplyTo(EyeParams e)
    {
        if (W is { } w) e.Width = w;
        if (H is { } h) e.Height = h;
        if (R is { } r) e.CornerRadius = r;
        if (X is { } x) e.OffsetX = x;
        if (Y is { } y) e.OffsetY = y;
        if (Rot is { } rot) e.Rotation = rot;
        if (Upper is { } up) e.UpperLid = up;
        if (Lower is { } lo) e.LowerLid = lo;
        if (Shape is { } s) { e.Shape = s; e.ShapeFrom = s; e.ShapeMix = 1; }
        if (Color is { } c) e.Color = ColorParser.Parse(c, e.Color);
    }
}

public sealed class FaceSpec
{
    public EyeSpec? Eyes { get; set; }
    public EyeSpec? LeftEye { get; set; }
    public EyeSpec? RightEye { get; set; }
    public double? EyeSpacing { get; set; }
    public double? EyeY { get; set; }

    public MouthShape? Mouth { get; set; }
    public double? MouthOpen { get; set; }
    public double? MouthWidth { get; set; }
    public double? MouthY { get; set; }
    public bool? MouthSync { get; set; }

    public double? Blush { get; set; }
    public string[]? Extras { get; set; }
    public string? ExtraColor { get; set; }

    public double? BodyScale { get; set; }
    public double? BodySquash { get; set; }
    public double? BodyTilt { get; set; }
    public double? BodyY { get; set; }
    public double? BodyShake { get; set; }

    public string? AntennaColor { get; set; }
    public double? AntennaGlow { get; set; }
    public double? AntennaPulse { get; set; }
    public bool? AntennaSync { get; set; }

    public double? Ring { get; set; }
    public double? GazeFollow { get; set; }
    public bool? Blink { get; set; }
    public double? BreathPeriod { get; set; }

    public void ApplyTo(FaceParams f)
    {
        Eyes?.ApplyTo(f.Left);
        Eyes?.ApplyTo(f.Right);
        LeftEye?.ApplyTo(f.Left);
        RightEye?.ApplyTo(f.Right);
        if (EyeSpacing is { } es) f.EyeSpacing = es;
        if (EyeY is { } ey) f.EyeY = ey;

        if (Mouth is { } m) { f.Mouth = m; f.MouthFrom = m; f.MouthMix = 1; }
        if (MouthOpen is { } mo) f.MouthOpen = mo;
        if (MouthWidth is { } mw) f.MouthWidth = mw;
        if (MouthY is { } my) f.MouthY = my;
        if (MouthSync is { } ms) f.MouthSync = ms ? 1 : 0;

        if (Blush is { } b) f.Blush = b;
        if (Extras is { } ex) f.Extras = ex;
        if (ExtraColor is { } xc) f.ExtraColor = ColorParser.Parse(xc, f.ExtraColor);

        if (BodyScale is { } bs) f.BodyScale = bs;
        if (BodySquash is { } bq) f.BodySquash = bq;
        if (BodyTilt is { } bt) f.BodyTilt = bt;
        if (BodyY is { } by) f.BodyY = by;
        if (BodyShake is { } bk) f.BodyShake = bk;

        if (AntennaColor is { } ac) f.AntennaColor = ColorParser.Parse(ac, f.AntennaColor);
        if (AntennaGlow is { } ag) f.AntennaGlow = ag;
        if (AntennaPulse is { } ap) f.AntennaPulse = ap;
        if (AntennaSync is { } asy) f.AntennaSync = asy ? 1 : 0;

        if (Ring is { } ring) f.Ring = ring;
        if (GazeFollow is { } gf) f.GazeFollow = gf;
        if (Blink is { } bl) f.Blink = bl ? 1 : 0;
        if (BreathPeriod is { } bp) f.BreathPeriod = bp;
    }
}

public sealed class KeyframeSpec
{
    public int AtMs { get; set; }

    /// <summary>"smooth" (por defecto), "linear" o "step".</summary>
    public string Ease { get; set; } = "smooth";

    public FaceSpec? Face { get; set; }
}

public sealed class ExpressionSpec
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>"loop" (estado que se mantiene) u "oneShot" (dura DurationMs y vuelve al estado base).</summary>
    public string Mode { get; set; } = "loop";

    public int DurationMs { get; set; } = 1500;
    public int TransitionMs { get; set; } = 220;

    /// <summary>"cubic" (suave) o "back" (con pequeño rebote).</summary>
    public string Easing { get; set; } = "cubic";

    public FaceSpec? Face { get; set; }
    public List<KeyframeSpec>? Keyframes { get; set; }

    /// <summary>Si se indica, los keyframes se repiten cada CycleMs.</summary>
    public int? CycleMs { get; set; }

    public string? Sound { get; set; }
    public string? ExitSound { get; set; }

    /// <summary>Expresiones internas (no forman parte del catálogo principal).</summary>
    public bool Internal { get; set; }
}

public sealed class ExpressionFile
{
    public List<ExpressionSpec> Expressions { get; set; } = [];
}

internal static class ColorParser
{
    public static Color Parse(string text, Color fallback)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(text);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }
}
