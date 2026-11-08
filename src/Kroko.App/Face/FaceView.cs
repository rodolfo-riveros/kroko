using System.Windows;
using System.Windows.Media;
using Kroko.App.Native;

namespace Kroko.App.Face;

/// <summary>
/// Control WPF que muestra una cara animada. Sincronizado con el refresco de pantalla
/// (CompositionTarget.Rendering), pero solo redibuja cuando algo cambia de verdad:
/// 60 fps en movimiento, 30 fps en animaciones lentas y casi nada en reposo.
/// </summary>
public sealed class FaceView : FrameworkElement
{
    public static readonly DependencyProperty AnimatorProperty = DependencyProperty.Register(
        nameof(Animator), typeof(FaceAnimator), typeof(FaceView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private FaceFrame? _frame;
    private bool _subscribed;
    private TimeSpan _lastRenderingTime;
    private double _lastDrawTime;
    private double _lastDrawBreath;

    public FaceView()
    {
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => UpdateSubscription();
        IsVisibleChanged += (_, _) => UpdateSubscription();
    }

    public FaceAnimator? Animator
    {
        get => (FaceAnimator?)GetValue(AnimatorProperty);
        set => SetValue(AnimatorProperty, value);
    }

    /// <summary>Escala lógica actual (píxeles por unidad de cara).</summary>
    public double FaceScale => Math.Min(ActualWidth / FaceRenderer.CanvasWidth, ActualHeight / FaceRenderer.CanvasHeight);

    private void UpdateSubscription()
    {
        var want = IsLoaded && IsVisible;
        if (want == _subscribed) return;
        if (want) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
        _subscribed = want;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs re)
        {
            if (re.RenderingTime == _lastRenderingTime) return; // mismo fotograma, ya procesado
            _lastRenderingTime = re.RenderingTime;
        }

        var animator = Animator;
        if (animator is null || ActualWidth <= 0) return;

        var frame = animator.Tick(CursorInFaceUnits());

        var draw = frame.Need switch
        {
            FrameNeed.Full => true,
            FrameNeed.Ambient => frame.Time - _lastDrawTime >= 1 / 31.0,
            _ => Math.Abs(frame.Breath - _lastDrawBreath) > 0.0012 || _frame is null,
        };

        _frame = frame;
        if (!draw) return;

        _lastDrawTime = frame.Time;
        _lastDrawBreath = frame.Breath;
        InvalidateVisual();
    }

    private Vector? CursorInFaceUnits()
    {
        if (PresentationSource.FromVisual(this) is null) return null;
        if (!NativeMethods.GetCursorPos(out var cursor)) return null;

        try
        {
            var s = FaceScale;
            var (ox, oy) = Offset(s);
            var center = PointToScreen(new Point(ox + FaceRenderer.CenterX * s, oy + FaceRenderer.CenterY * s));
            var dpi = VisualTreeHelper.GetDpi(this);
            var unitPx = s * dpi.DpiScaleX;
            if (unitPx <= 0) return null;
            return new Vector((cursor.X - center.X) / unitPx, (cursor.Y - center.Y) / unitPx);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private (double X, double Y) Offset(double s) =>
        ((ActualWidth - FaceRenderer.CanvasWidth * s) / 2, (ActualHeight - FaceRenderer.CanvasHeight * s) / 2);

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? FaceRenderer.CanvasWidth : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? FaceRenderer.CanvasHeight : availableSize.Height;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_frame is null) return;
        var s = FaceScale;
        var (ox, oy) = Offset(s);
        FaceRenderer.Render(dc, _frame, s, ox, oy);
    }

    /// <summary>Solo el cuerpo (y la antena) cuentan para el ratón; lo demás deja pasar el clic.</summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        var s = FaceScale;
        var (ox, oy) = Offset(s);
        var p = hitTestParameters.HitPoint;
        var u = new Point((p.X - ox) / s, (p.Y - oy) / s);
        var inBody = (u - new Point(FaceRenderer.CenterX, FaceRenderer.CenterY)).Length <= FaceRenderer.Radius + 2;
        var inAntenna = Math.Abs(u.X - FaceRenderer.CenterX) <= 7
                        && u.Y >= FaceRenderer.CenterY - FaceRenderer.Radius - 20
                        && u.Y <= FaceRenderer.CenterY - FaceRenderer.Radius;
        return inBody || inAntenna ? new PointHitTestResult(this, p) : null;
    }
}
