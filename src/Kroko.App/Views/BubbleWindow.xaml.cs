using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kroko.App.Face;
using Kroko.App.Native;
using Kroko.App.Services;
using Kroko.App.ViewModels;
using Microsoft.Extensions.Options;

namespace Kroko.App.Views;

/// <summary>
/// Ventana flotante de Kroko. Aquí solo hay lógica de ventana nativa:
/// estilos, posición por monitor, arrastre y escondite en pantalla completa.
/// El comportamiento vive en CompanionBehavior.
/// </summary>
public partial class BubbleWindow : Window
{
    private const double DragThresholdDip = 4;
    private const double FastDragDipPerSecond = 1600;

    private readonly BubbleViewModel _vm;
    private readonly KrokoOptions _options;
    private readonly WindowPlacementStore _placementStore;
    private readonly FullscreenWatcher _fullscreen;
    private readonly BubbleGeometry _geometry;
    private WindowPlacementStore.PlacementData _placement = new();

    private nint _hwnd;
    private bool _pressed;
    private bool _dragging;
    private NativeMethods.POINT _pressCursor;
    private NativeMethods.RECT _pressRect;
    private readonly Stopwatch _dragClock = new();
    private double _lastSampleTime;
    private NativeMethods.POINT _lastSample;
    private double _fastTime;
    private double _velX;

    public BubbleWindow(BubbleViewModel viewModel, IOptions<KrokoOptions> options, WindowPlacementStore placementStore,
        BubbleGeometry geometry)
    {
        InitializeComponent();
        _vm = viewModel;
        _options = options.Value;
        _placementStore = placementStore;
        _geometry = geometry;
        DataContext = viewModel;
        viewModel.VisibilityToggleRequested += ToggleUserVisibility;
        viewModel.Behavior.LongSleep += FadeAwayAsleep;
        viewModel.Behavior.Woke += ReturnFromSleep;
        viewModel.PanelShowRequested += _ => ReturnFromSleep();

        var scale = Math.Clamp(_options.BubbleScale, 0.75, 1.5);
        Width = FaceRenderer.CanvasWidth * scale;
        Height = FaceRenderer.CanvasHeight * scale;
        if (_options.IsIsland) SetUpIsland();

        _fullscreen = new FullscreenWatcher(() => _hwnd);
        _fullscreen.Changed += OnFullscreenChanged;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            this.MakeToolWindowNoActivate();
            RestorePlacement();
            UpdateGeometry();
        };
        Loaded += (_, _) =>
        {
            // Arranca escondido y dormido (decidido por Pol, 14/11/2026): aparece cuando lo llamas («Oye Kroko», el atajo,
            // la bandeja) o tiene un aviso. Se marca antes de mirar la pantalla completa, para que al salir de un juego
            // tampoco reaparezca solo.
            if (_options.StartHidden)
            {
                _hiddenAsleep = true;
                _vm.Behavior.StartAsleepHidden();
            }
            // Si ya hay un juego a pantalla completa, ni se asoma
            if (_options.HideOnFullscreen && _fullscreen.StartAndCheck())
            {
                EnterFullscreenHidden();
                return;
            }
            if (_hiddenAsleep && !_options.IsIsland)
            {
                Hide();
                return;
            }
            _hiddenAsleep = false;
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
        };
        Closed += (_, _) => _fullscreen.Dispose();
        LocationChanged += (_, _) => UpdateGeometry();
        DpiChanged += (_, _) => UpdateGeometry();
    }

    // ---------------------------------------------------------------- posición

    private double BodyUnitPx(double monitorScale) => Math.Clamp(_options.BubbleScale, 0.75, 1.5) * monitorScale;

    private void RestorePlacement()
    {
        if (_options.IsIsland)
        {
            PlaceIsland();
            return;
        }
        _placement = _placementStore.Load();

        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT(_placement.LastCenterX, _placement.LastCenterY), NativeMethods.MONITOR_DEFAULTTONULL);
        if (monitor == 0 || _placement.LastMonitor is null)
            monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT(0, 0), NativeMethods.MONITOR_DEFAULTTOPRIMARY);

        if (!NativeMethods.TryGetMonitorInfo(monitor, out var mi)) return;
        var ms = NativeMethods.GetMonitorScale(monitor);
        var unit = BodyUnitPx(ms);
        var work = mi.rcWork;

        int left, top;
        if (_placement.Monitors.TryGetValue(mi.szDevice, out var saved))
        {
            var winW = FaceRenderer.CanvasWidth * unit;
            var winH = FaceRenderer.CanvasHeight * unit;
            left = (int)Math.Round(work.Right - saved.FromRight * ms - winW);
            top = (int)Math.Round(work.Bottom - saved.FromBottom * ms - winH);
        }
        else
        {
            // Por defecto: esquina inferior derecha, con el CUERPO a 24 px del borde
            var bodyRight = (FaceRenderer.CenterX + FaceRenderer.BodyHalfWidth) * unit;
            var bodyBottom = (FaceRenderer.CenterY + FaceRenderer.Radius) * unit;
            left = (int)Math.Round(work.Right - _options.ScreenMargin * ms - bodyRight);
            top = (int)Math.Round(work.Bottom - _options.ScreenMargin * ms - bodyBottom);
        }

        // Que el cuerpo nunca quede fuera del área visible
        var minLeft = work.Left - (int)((FaceRenderer.CenterX - FaceRenderer.BodyHalfWidth) * unit);
        var maxLeft = work.Right - (int)((FaceRenderer.CenterX + FaceRenderer.BodyHalfWidth) * unit);
        var minTop = work.Top - (int)((FaceRenderer.BodyTop - 4) * unit);
        var maxTop = work.Bottom - (int)((FaceRenderer.CenterY + FaceRenderer.Radius) * unit);
        left = Math.Clamp(left, minLeft, Math.Max(minLeft, maxLeft));
        top = Math.Clamp(top, minTop, Math.Max(minTop, maxTop));

        NativeMethods.SetWindowPos(_hwnd, 0, left, top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private void SavePlacement()
    {
        if (!NativeMethods.GetWindowRect(_hwnd, out var r)) return;
        var center = new NativeMethods.POINT((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        var monitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (!NativeMethods.TryGetMonitorInfo(monitor, out var mi)) return;
        var ms = NativeMethods.GetMonitorScale(monitor);

        _placement.LastMonitor = mi.szDevice;
        _placement.LastCenterX = center.X;
        _placement.LastCenterY = center.Y;
        _placement.Monitors[mi.szDevice] = new WindowPlacementStore.MonitorPlacement
        {
            FromRight = (mi.rcWork.Right - r.Right) / ms,
            FromBottom = (mi.rcWork.Bottom - r.Bottom) / ms,
        };
        _placementStore.Save(_placement);
    }


    /// <summary>Publica dónde está el cuerpo (para que el panel se coloque encima).</summary>
    private void UpdateGeometry()
    {
        if (_hwnd == 0 || !NativeMethods.GetWindowRect(_hwnd, out var r) || r.Width <= 0) return;
        var monitor = NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (!NativeMethods.TryGetMonitorInfo(monitor, out var mi)) return;
        if (_options.IsIsland)
        {
            PublishIslandGeometry(r, mi, NativeMethods.GetMonitorScale(monitor));
            return;
        }

        var unit = r.Width / FaceRenderer.CanvasWidth;
        var body = new NativeMethods.RECT
        {
            Left = r.Left + (int)Math.Round((FaceRenderer.CenterX - FaceRenderer.BodyHalfWidth) * unit),
            Top = r.Top + (int)Math.Round(FaceRenderer.BodyTop * unit),
            Right = r.Left + (int)Math.Round((FaceRenderer.CenterX + FaceRenderer.BodyHalfWidth) * unit),
            Bottom = r.Top + (int)Math.Round((FaceRenderer.CenterY + FaceRenderer.Radius) * unit),
        };
        _geometry.Update(body, mi.rcWork, NativeMethods.GetMonitorScale(monitor), unit);
    }

    // ---------------------------------------------------------------- arrastre propio

    private void Face_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            _pressed = false;
            _vm.OnDoubleClick();
            return;
        }

        NativeMethods.GetCursorPos(out _pressCursor);
        NativeMethods.GetWindowRect(_hwnd, out _pressRect);
        _pressed = true;
        _dragging = false;
        _fastTime = 0;
        _lastSample = _pressCursor;
        _dragClock.Restart();
        _lastSampleTime = 0;
        Face.CaptureMouse();
    }

    private void Face_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed) return;
        NativeMethods.GetCursorPos(out var c);
        var monitorScale = NativeMethods.GetMonitorScale(
            NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST));

        var dx = c.X - _pressCursor.X;
        var dy = c.Y - _pressCursor.Y;
        if (!_dragging)
        {
            if (Math.Abs(dx) < DragThresholdDip * monitorScale && Math.Abs(dy) < DragThresholdDip * monitorScale) return;
            _dragging = true;
            _vm.Behavior.OnDragStarted();
        }

        NativeMethods.SetWindowPos(_hwnd, 0, _pressRect.Left + dx, _pressRect.Top + dy, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // Velocidad del arrastre (en DIP/s) para inclinar el cuerpo y detectar mareo
        var now = _dragClock.Elapsed.TotalSeconds;
        var dt = now - _lastSampleTime;
        if (dt < 0.012) return;
        var vx = (c.X - _lastSample.X) / monitorScale / dt;
        var vy = (c.Y - _lastSample.Y) / monitorScale / dt;
        _lastSample = c;
        _lastSampleTime = now;

        _velX = _velX * 0.6 + vx * 0.4;
        _vm.Face.DragVelocityX = _velX;

        var speed = Math.Sqrt(vx * vx + vy * vy);
        _fastTime = speed > FastDragDipPerSecond ? _fastTime + dt : Math.Max(0, _fastTime - dt * 0.5);
        if (_fastTime > 0.35)
        {
            _fastTime = 0;
            _vm.Behavior.OnFastDrag();
        }
    }

    private void Face_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        e.Handled = true;
        var wasDragging = _dragging;
        EndPress();
        if (wasDragging)
        {
            SavePlacement();
            _vm.Behavior.OnDragEnded();
        }
        else
        {
            _vm.OnClick();
        }
    }

    private void Face_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_pressed && _dragging) SavePlacement();
        _pressed = false;
        _dragging = false;
        _vm.Face.DragVelocityX = 0;
    }

    private void EndPress()
    {
        _pressed = false;
        _dragging = false;
        _velX = 0;
        _vm.Face.DragVelocityX = 0;
        if (Face.IsMouseCaptured) Face.ReleaseMouseCapture();
    }

    private void Face_MouseEnter(object sender, MouseEventArgs e) => _vm.Behavior.OnHoverChanged(true);

    private void Face_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_pressed) _vm.Behavior.OnHoverChanged(false);
    }

    // ---------------------------------------------------------------- pantalla completa

    private bool _hiddenByUser;
    private bool _hiddenAsleep;
    private bool _inFullscreen;

    /// <summary>Dormido un buen rato: se desvanece. Sigue escuchando «Oye Kroko» y los atajos.</summary>
    private void FadeAwayAsleep()
    {
        if (_options.IsIsland) return; // la isla no desaparece: se queda chiquita y dormida
        if (!IsVisible || _hiddenByUser || _hiddenAsleep) return;
        _hiddenAsleep = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(700)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            if (_hiddenAsleep) Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Lo llamaron («Oye Kroko», atajo, bandeja) o tiene algo que decir: vuelve a aparecer despertando.</summary>
    private void ReturnFromSleep()
    {
        if (!_hiddenAsleep) return;
        _hiddenAsleep = false;
        _vm.Behavior.WakeUp();
        if (_inFullscreen || _hiddenByUser) return;
        Opacity = 0;
        Show();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>"Mostrar / ocultar" desde la bandeja: Kroko sigue funcionando (atajos, recordatorios) aunque no se vea.</summary>
    private void ToggleUserVisibility()
    {
        if (_hiddenAsleep)
        {
            ReturnFromSleep();
            return;
        }
        _hiddenByUser = IsVisible;
        if (_hiddenByUser)
        {
            _vm.SetPanelSuppressed(true);
            Hide();
        }
        else
        {
            Opacity = 0;
            Show();
            _vm.SetPanelSuppressed(false);
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
        }
    }

    private void EnterFullscreenHidden()
    {
        _inFullscreen = true;
        _vm.Behavior.SetPaused(true);
        _vm.SetFullscreen(true);
        Hide();
    }

    private void OnFullscreenChanged(bool fullscreen)
    {
        var fade = new DoubleAnimation(fullscreen ? 0 : 1, TimeSpan.FromMilliseconds(fullscreen ? 150 : 250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        if (fullscreen)
        {
            fade.Completed += (_, _) => EnterFullscreenHidden();
        }
        else
        {
            _inFullscreen = false;
            _vm.Behavior.SetPaused(false);
            _vm.SetFullscreen(false);
            if (_hiddenByUser || _hiddenAsleep)
            {
                if (_hiddenByUser) _vm.SetPanelSuppressed(true);
                return; // escondido a propósito o dormido: no reaparece solo
            }
            Opacity = 0;
            Show();
        }
        BeginAnimation(OpacityProperty, fade);
    }

    // ---------------------------------------------------------------- isla de arriba (17/11/2026, como Troko Desk)

    private const double IslandWindowWidth = 340;
    private const double IslandWindowHeight = 40;
    private const double IslandIdleWidth = 150;
    private const double IslandBusyWidth = 250;
    private const double IslandHoverWidth = 280;
    private DispatcherTimer? _islandTimer;
    private bool _islandHover;
    private double _islandTarget = IslandIdleWidth;

    private void SetUpIsland()
    {
        Face.Visibility = Visibility.Collapsed;
        Island.Visibility = Visibility.Visible;
        Width = IslandWindowWidth;
        Height = IslandWindowHeight;

        Island.RenderTransform = _islandSlide;
        if (_options.IslandAutoHide)
        {
            // Empieza escondida: sale cuando la llamas o pasas el cursor por arriba
            _islandSlide.Y = -IslandWindowHeight;
            Island.Opacity = 0;
            _islandShown = false;
        }

        _islandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _islandTimer.Tick += (_, _) => RefreshIsland();
        _islandTimer.Start();
        RefreshIsland();
    }

    // ---- isla inteligente (17/11/2026, como Troko Desk): sale cuando la llamas, cuando hace algo o al pasar el cursor
    // por el borde de arriba; se esconde sola un momento después de terminar.

    private const double IslandHideDelaySeconds = 2.5;
    private readonly System.Windows.Media.TranslateTransform _islandSlide = new();
    private bool _islandShown = true;
    private DateTime _islandLastWanted = DateTime.Now;

    private void RefreshIsland()
    {
        _vm.RefreshIsland();
        UpdateIslandVisibility();

        var target = _islandHover ? IslandHoverWidth : _vm.IslandBusy ? IslandBusyWidth : IslandIdleWidth;
        if (Math.Abs(target - _islandTarget) < 0.5) return;
        _islandTarget = target;
        Island.BeginAnimation(WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }


    private void UpdateIslandVisibility()
    {
        if (!_options.IslandAutoHide)
        {
            if (!_islandShown) SlideIsland(true);
            return;
        }
        var now = DateTime.Now;
        if (_vm.IslandWanted || _islandHover || CursorAtTopCenter()) _islandLastWanted = now;
        var show = (now - _islandLastWanted).TotalSeconds < IslandHideDelaySeconds;
        if (show != _islandShown) SlideIsland(show);
    }

    /// <summary>El cursor está en el borde de arriba, a la altura de la isla (aunque esté escondida).</summary>
    private bool CursorAtTopCenter()
    {
        if (_hwnd == 0 || !NativeMethods.GetCursorPos(out var c) || !NativeMethods.GetWindowRect(_hwnd, out var r)) return false;
        var ms = r.Width / IslandWindowWidth;
        var grab = (int)Math.Round((_islandShown ? IslandWindowHeight : 6) * ms); // escondida: solo el filo de arriba
        return c.X >= r.Left && c.X <= r.Right && c.Y >= r.Top && c.Y <= r.Top + grab;
    }

    /// <summary>Baja desde el borde (aparece) o sube y se va (desaparece).</summary>
    private void SlideIsland(bool show)
    {
        _islandShown = show;
        var ease = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };
        var time = TimeSpan.FromMilliseconds(show ? 240 : 200);
        _islandSlide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(show ? 0 : -IslandWindowHeight, time) { EasingFunction = ease });
        Island.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, time));
        Island.IsHitTestVisible = show;
    }
    /// <summary>Arriba al centro del monitor principal, pegada al borde (encima de todo, como un notch).</summary>
    private void PlaceIsland()
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT(0, 0), NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (!NativeMethods.TryGetMonitorInfo(monitor, out var mi)) return;
        var ms = NativeMethods.GetMonitorScale(monitor);
        var widthPx = (int)Math.Round(IslandWindowWidth * ms);
        var left = mi.rcMonitor.Left + (mi.rcMonitor.Right - mi.rcMonitor.Left - widthPx) / 2;
        NativeMethods.SetWindowPos(_hwnd, 0, left, mi.rcMonitor.Top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>El "cuerpo" para los demás paneles es la isla desplegada (ancho máximo), centrada arriba.</summary>
    private void PublishIslandGeometry(NativeMethods.RECT window, NativeMethods.MONITORINFOEX mi, double ms)
    {
        var center = (window.Left + window.Right) / 2;
        var half = (int)Math.Round(IslandHoverWidth / 2 * ms);
        var body = new NativeMethods.RECT
        {
            Left = center - half,
            Right = center + half,
            Top = window.Top,
            Bottom = window.Top + (int)Math.Round(Island.Height * ms),
        };
        // Debajo de la isla: el área de trabajo empieza en su borde de abajo
        var work = mi.rcWork;
        if (work.Top < body.Bottom) work.Top = body.Bottom;
        _geometry.Update(body, work, ms, ms * 0.3, island: true);
    }

    private void Island_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _vm.OpenIsland();
    }

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _islandHover = true;
        _vm.Behavior.OnHoverChanged(true);
        RefreshIsland();
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        _islandHover = false;
        _vm.Behavior.OnHoverChanged(false);
        RefreshIsland();
    }
}
