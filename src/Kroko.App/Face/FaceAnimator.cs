using System.Diagnostics;
using System.Windows;

namespace Kroko.App.Face;

public enum DistractionKind { Fly, Ball, Clock }

public enum FrameNeed
{
    /// <summary>Nada se mueve (solo la respiración, muy lenta).</summary>
    None,
    /// <summary>Animación ambiental lenta: basta con 30 fps.</summary>
    Ambient,
    /// <summary>Movimiento visible: 60 fps.</summary>
    Full,
}

/// <summary>Objeto que cruza la escena durante una distracción (mosca o pelota).</summary>
public sealed record PropState(DistractionKind Kind, double X, double Y, double Alpha, double Time);

/// <summary>Todo lo necesario para dibujar un fotograma.</summary>
public sealed record FaceFrame(
    FaceParams Face,
    Vector Gaze,
    double BlinkFactor,
    double Breath,
    double Hover,
    double DragTilt,
    double Mic,
    double Speech,
    double Time,
    IReadOnlyList<KeyValuePair<string, double>> Extras,
    PropState? Prop,
    FrameNeed Need);

/// <summary>
/// Motor de la cara: decide qué cara mostrar en cada instante.
/// Combina la expresión activa (con sus keyframes), la transición desde la anterior
/// y las capas "vivas" (parpadeo, mirada, respiración, hover, distracciones).
/// No depende de WPF para dibujar: FaceView lo usa y FaceRenderer pinta el resultado.
/// </summary>
public sealed class FaceAnimator
{
    private const double MaxGaze = 6;

    private readonly ExpressionLibrary _library;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Func<double>? _externalClock;
    private readonly Random _rng = new();
    private readonly Dictionary<string, double> _extraAlpha = new(StringComparer.OrdinalIgnoreCase);

    private Expression _base;
    private double _baseStart;
    private Expression? _shot;
    private double _shotStart;

    private FaceParams _composed;
    private FaceParams _from;
    private double _transStart = -1;
    private double _transDur = 0.2;
    private Func<double, double> _ease = MathUtil.CubicOut;

    private double _last;
    private Vector _gaze;
    private Vector _gazeTarget;
    private Vector _lastCursor;
    private double _lastCursorMove = -100;
    private Vector _lookAround;
    private double _nextLook;

    private double _nextBlink = 2;
    private double _blinkStart = -10;
    private double _secondBlink = -1;

    private double _breathPhase;
    private double _hover;
    private double _dragTilt;
    private double _mic;
    private double _speech;

    private DistractionKind? _distraction;
    private double _distractionStart;

    /// <param name="clock">Reloj opcional en segundos (para pruebas y capturas). Por defecto, tiempo real.</param>
    public FaceAnimator(ExpressionLibrary library, Func<double>? clock = null)
    {
        _library = library;
        _externalClock = clock;
        _base = library.Get(ExpressionLibrary.FallbackKey);
        _composed = _base.Evaluate(0);
        _from = _composed;
        _library.Changed += OnLibraryChanged;
    }

    // ---------- Entradas (las rellenan la ventana, el audio o el comportamiento) ----------

    public bool FollowCursor { get; set; } = true;
    public bool EnableBlink { get; set; } = true;
    public bool EnableBreath { get; set; } = true;
    public bool Hover { get; set; }

    /// <summary>Velocidad horizontal al arrastrar (DIP/s): inclina el cuerpo por inercia.</summary>
    public double DragVelocityX { get; set; }

    /// <summary>Nivel del micrófono 0–1 (anillo de escucha).</summary>
    public double MicLevel { get; set; }

    /// <summary>Amplitud de la voz 0–1 (boca al hablar).</summary>
    public double SpeechLevel { get; set; }

    /// <summary>Dirección forzada de la mirada (unidades de cara), o null.</summary>
    public Vector? GazeOverride { get; set; }

    public string BaseKey => _base.Key;
    public string ActiveKey => (_shot ?? _base).Key;
    public bool IsPlayingOneShot => _shot is not null;
    public bool IsDistracted => _distraction is not null;

    public event Action<Expression>? ExpressionEntered;
    public event Action<Expression>? ExpressionExited;

    private double Now => _externalClock?.Invoke() ?? _stopwatch.Elapsed.TotalSeconds;

    // ---------- Control ----------

    /// <summary>Cambia el estado base (expresión que se mantiene: reposo, escucha, dormido…).</summary>
    public void SetBase(string key)
    {
        var exp = _library.Get(key);
        if (exp.Key == _base.Key) return;

        var now = Now;
        if (_shot is null)
        {
            ExpressionExited?.Invoke(_base);
            _base = exp;
            _baseStart = now;
            Retarget(exp, now);
            ExpressionEntered?.Invoke(exp);
        }
        else
        {
            _base = exp;
            _baseStart = now;
        }
    }

    /// <summary>Reproduce una expresión puntual; al terminar vuelve al estado base.</summary>
    public void Play(string key)
    {
        var exp = _library.Get(key);
        var now = Now;
        ExpressionExited?.Invoke(_shot ?? _base);
        _shot = exp;
        _shotStart = now;
        Retarget(exp, now);
        ExpressionEntered?.Invoke(exp);
    }

    public void StopOneShot()
    {
        if (_shot is null) return;
        var now = Now;
        ExpressionExited?.Invoke(_shot);
        _shot = null;
        Retarget(_base, now);
        ExpressionEntered?.Invoke(_base);
    }

    public void StartDistraction(DistractionKind kind)
    {
        _distraction = kind;
        _distractionStart = Now;
    }

    public void StopDistraction() => _distraction = null;

    private static double DistractionLength(DistractionKind k) => k switch
    {
        DistractionKind.Fly => 5.0,
        DistractionKind.Ball => 4.5,
        _ => 2.5,
    };

    private void Retarget(Expression exp, double now)
    {
        _from = _composed;
        _transStart = now;
        _transDur = exp.TransitionSeconds;
        _ease = exp.Easing;
    }

    private void OnLibraryChanged()
    {
        // Al editar expressions.json, reaplicamos las expresiones activas con sus nuevos valores.
        var now = Now;
        _base = _library.Get(_base.Key);
        if (_shot is not null) _shot = _library.Get(_shot.Key);
        Retarget(_shot ?? _base, now);
    }

    // ---------- Fotograma ----------

    /// <param name="cursor">Posición del ratón relativa al centro de la cara, en unidades de cara.</param>
    public FaceFrame Tick(Vector? cursor)
    {
        var now = Now;
        var dt = Math.Clamp(now - _last, 0, 0.1);
        _last = now;
        var need = FrameNeed.None;

        // 1) Fin de la expresión puntual
        if (_shot is not null && (now - _shotStart) * 1000 >= _shot.DurationMs)
        {
            ExpressionExited?.Invoke(_shot);
            _shot = null;
            Retarget(_base, now);
            ExpressionEntered?.Invoke(_base);
        }

        // 2) Cara objetivo (con keyframes) y transición desde la anterior
        var active = _shot ?? _base;
        var start = _shot is not null ? _shotStart : _baseStart;
        var goal = active.Evaluate((now - start) * 1000);

        FaceParams face;
        if (_transStart >= 0)
        {
            var p = (now - _transStart) / _transDur;
            if (p >= 1)
            {
                _transStart = -1;
                face = goal;
            }
            else
            {
                face = FaceParams.Lerp(_from, goal, _ease(p));
                need = FrameNeed.Full;
            }
        }
        else face = goal;

        _composed = face;
        if (_shot is not null) need = FrameNeed.Full;
        else if (active.IsAnimated) need = Max(need, FrameNeed.Ambient);

        // 3) Capas vivas sobre una copia (la cara compuesta nunca se modifica)
        var r = face.Clone();

        // Distracción (mosca, pelota, reloj)
        PropState? prop = null;
        Vector? propGaze = null;
        if (_distraction is { } kind)
        {
            var t = now - _distractionStart;
            var len = DistractionLength(kind);
            if (t >= len) _distraction = null;
            else
            {
                prop = ComputeProp(kind, t, len);
                propGaze = new Vector(prop.X - FaceRenderer.CenterX, prop.Y - FaceRenderer.CenterY);
                need = FrameNeed.Full;
            }
        }

        // Mirada
        double gazeWeight = r.GazeFollow;
        if (propGaze is { } pg)
        {
            _gazeTarget = GazeToward(pg);
            gazeWeight = 1;
        }
        else if (GazeOverride is { } go)
        {
            _gazeTarget = GazeToward(go);
            gazeWeight = 1;
        }
        else
        {
            if (cursor is { } c)
            {
                if ((c - _lastCursor).Length > 0.4) _lastCursorMove = now;
                _lastCursor = c;
            }

            if (FollowCursor && cursor is { } cur && now - _lastCursorMove < 5)
                _gazeTarget = GazeToward(cur);
            else
            {
                // Sin movimiento del ratón: mira a puntos al azar de vez en cuando.
                if (now >= _nextLook)
                {
                    _lookAround = _rng.NextDouble() < 0.4
                        ? new Vector()
                        : new Vector(Rand(-5, 5), Rand(-3, 3));
                    _nextLook = now + Rand(1.5, 5);
                }
                _gazeTarget = _lookAround;
            }
        }

        var prevGaze = _gaze;
        _gaze = new Vector(
            MathUtil.Approach(_gaze.X, _gazeTarget.X, 10, dt),
            MathUtil.Approach(_gaze.Y, _gazeTarget.Y, 10, dt));
        if ((_gaze - prevGaze).Length > 0.01) need = FrameNeed.Full;

        var g = _gaze * gazeWeight;
        r.Left.OffsetX += g.X;
        r.Left.OffsetY += g.Y;
        r.Right.OffsetX += g.X;
        r.Right.OffsetY += g.Y;

        // Parpadeo (aleatorio cada 2–6 s, a veces doble)
        double blink = 0;
        if (EnableBlink && r.Blink > 0.01)
        {
            if (now >= _nextBlink)
            {
                _blinkStart = now;
                _nextBlink = now + Rand(2, 6);
                _secondBlink = _rng.NextDouble() < 0.2 ? now + 0.2 : -1;
            }
            if (_secondBlink > 0 && now >= _secondBlink)
            {
                _blinkStart = now;
                _secondBlink = -1;
            }
            var bt = (now - _blinkStart) / 0.12;
            if (bt is >= 0 and < 1)
            {
                blink = Math.Sin(Math.PI * bt) * r.Blink;
                need = FrameNeed.Full;
            }
        }
        var blinkFactor = 1 - 0.92 * blink;

        // Respiración (escala 1.00 → 1.02)
        double breath = 0;
        if (EnableBreath)
        {
            _breathPhase += dt / Math.Max(0.5, r.BreathPeriod / 1000);
            _breathPhase %= 1;
            breath = 0.01 * (1 - Math.Cos(2 * Math.PI * _breathPhase));
        }

        // Hover y arrastre
        var hoverTarget = Hover ? 1.0 : 0.0;
        _hover = MathUtil.Approach(_hover, hoverTarget, 12, dt);
        if (Math.Abs(_hover - hoverTarget) > 0.005) need = FrameNeed.Full;

        var tiltTarget = Math.Clamp(-DragVelocityX / 90, -14, 14);
        _dragTilt = MathUtil.Approach(_dragTilt, tiltTarget, 10, dt);
        if (Math.Abs(_dragTilt) > 0.05 || Math.Abs(tiltTarget) > 0.05) need = FrameNeed.Full;

        // Niveles de audio: subida rápida, bajada suave
        _mic = Follow(_mic, Math.Clamp(MicLevel, 0, 1), dt);
        _speech = Follow(_speech, Math.Clamp(SpeechLevel, 0, 1), dt);
        r.MouthOpen = MathUtil.Lerp(r.MouthOpen, _speech, r.MouthSync);
        if ((_mic > 0.01 && r.Ring > 0.01) || (_speech > 0.01 && r.MouthSync > 0.01)) need = FrameNeed.Full;

        // Extras: aparecen y desaparecen con un fundido
        foreach (var name in r.Extras)
            _extraAlpha.TryAdd(name, 0);
        var extras = new List<KeyValuePair<string, double>>(_extraAlpha.Count);
        foreach (var key in _extraAlpha.Keys.ToArray())
        {
            var target = Array.IndexOf(r.Extras, key) >= 0 ? 1.0 : 0.0;
            var a = _extraAlpha[key];
            a = target > a ? Math.Min(1, a + dt / 0.28) : Math.Max(0, a - dt / 0.22);
            if (a <= 0 && target == 0)
            {
                _extraAlpha.Remove(key);
                continue;
            }
            if (a < 1) need = FrameNeed.Full;
            _extraAlpha[key] = a;
            extras.Add(new(key, a));
        }

        if (extras.Count > 0 || r.BodyShake > 0.01 || r.AntennaPulse > 0.01 || r.Ring > 0.01
            || r.Left.Shape is EyeShape.Spiral or EyeShape.Heart || r.Right.Shape is EyeShape.Spiral or EyeShape.Heart)
            need = Max(need, FrameNeed.Ambient);

        return new FaceFrame(r, g, blinkFactor, breath, _hover, _dragTilt, _mic, _speech, now, extras, prop, need);
    }

    private static Vector GazeToward(Vector v)
    {
        var d = v.Length;
        if (d < 0.001) return new Vector();
        var dir = v / d;
        var amount = MaxGaze * d / (d + 60);
        return new Vector(dir.X * amount, dir.Y * amount * 0.75);
    }

    private static PropState ComputeProp(DistractionKind kind, double t, double len)
    {
        var fade = Math.Min(1, Math.Min(t / 0.4, (len - t) / 0.4));
        switch (kind)
        {
            case DistractionKind.Fly:
            {
                // Vuelo en forma de ocho alrededor de la cabeza
                var th = Math.PI + t / len * 2 * Math.PI;
                var x = FaceRenderer.CenterX + 60 * Math.Cos(th);
                var y = 62 + 24 * Math.Sin(2 * th) + 2 * Math.Sin(t * 23);
                return new PropState(kind, x, y, fade, t);
            }
            case DistractionKind.Ball:
            {
                var p = t / len;
                var x = 12 + p * 126;
                var y = 153 - 28 * Math.Abs(Math.Sin(Math.PI * 3 * p)) * (1 - 0.5 * p);
                return new PropState(kind, x, y, fade, t);
            }
            default:
                // Mira hacia la esquina del reloj de Windows (abajo a la derecha)
                return new PropState(kind, FaceRenderer.CenterX + 90, FaceRenderer.CenterY + 80, 0, t);
        }
    }

    private static double Follow(double current, double target, double dt) =>
        MathUtil.Approach(current, target, target > current ? 30 : 9, dt);

    private static FrameNeed Max(FrameNeed a, FrameNeed b) => a > b ? a : b;

    private double Rand(double min, double max) => min + _rng.NextDouble() * (max - min);
}
