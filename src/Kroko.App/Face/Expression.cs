namespace Kroko.App.Face;

/// <summary>
/// Expresión ya "compilada" a partir de expressions.json, lista para evaluarse a 60 fps.
/// </summary>
public sealed class Expression
{
    private readonly Keyframe[] _frames;

    public string Key { get; }
    public string Name { get; }
    public string Description { get; }
    public bool IsLoop { get; }
    public bool IsInternal { get; }
    public int DurationMs { get; }
    public double TransitionSeconds { get; }
    public Func<double, double> Easing { get; }
    public int? CycleMs { get; }
    public string? Sound { get; }
    public string? ExitSound { get; }

    /// <summary>True si la cara cambia sola con el tiempo (keyframes).</summary>
    public bool IsAnimated => _frames.Length > 1;

    private Expression(ExpressionSpec spec, Keyframe[] frames)
    {
        Key = spec.Key;
        Name = string.IsNullOrWhiteSpace(spec.Name) ? spec.Key : spec.Name;
        Description = spec.Description ?? "";
        IsLoop = !string.Equals(spec.Mode, "oneShot", StringComparison.OrdinalIgnoreCase);
        IsInternal = spec.Internal;
        DurationMs = Math.Max(100, spec.DurationMs);
        TransitionSeconds = Math.Max(0.01, spec.TransitionMs / 1000.0);
        Easing = string.Equals(spec.Easing, "back", StringComparison.OrdinalIgnoreCase)
            ? MathUtil.BackOut
            : MathUtil.CubicOut;
        CycleMs = spec.CycleMs is > 0 ? spec.CycleMs : null;
        Sound = spec.Sound;
        ExitSound = spec.ExitSound;
        _frames = frames;
    }

    public static Expression Build(ExpressionSpec spec)
    {
        var baseFace = new FaceParams();
        spec.Face?.ApplyTo(baseFace);

        var frames = new List<Keyframe> { new(0, "smooth", baseFace) };
        var previous = baseFace;

        // Cada keyframe hereda del anterior: solo hay que escribir lo que cambia.
        foreach (var k in (spec.Keyframes ?? []).OrderBy(k => k.AtMs))
        {
            var face = previous.Clone();
            k.Face?.ApplyTo(face);
            if (k.AtMs <= 0)
                frames[0] = new Keyframe(0, k.Ease, face);
            else
                frames.Add(new Keyframe(k.AtMs, k.Ease, face));
            previous = face;
        }

        return new Expression(spec, [.. frames]);
    }

    /// <summary>Cara de esta expresión a los <paramref name="ms"/> milisegundos de empezar.</summary>
    public FaceParams Evaluate(double ms)
    {
        if (_frames.Length == 1) return _frames[0].Face;

        double t = ms;
        if (CycleMs is { } cycle) t %= cycle;

        int i = _frames.Length - 1;
        while (i > 0 && _frames[i].AtMs > t) i--;

        var a = _frames[i];
        Keyframe? b = null;
        double end;
        if (i + 1 < _frames.Length)
        {
            b = _frames[i + 1];
            end = b.AtMs;
        }
        else if (CycleMs is { } c && c > a.AtMs)
        {
            b = _frames[0];
            end = c;
        }
        else
        {
            return a.Face;
        }

        var u = (t - a.AtMs) / Math.Max(1, end - a.AtMs);
        u = b.Ease switch
        {
            "linear" => MathUtil.Clamp01(u),
            "step" => 0,
            _ => MathUtil.SmoothStep(u),
        };
        return FaceParams.Lerp(a.Face, b.Face, u);
    }

    private sealed record Keyframe(double AtMs, string Ease, FaceParams Face);
}
