using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Kroko.App.Native;
using Kroko.App.Services;
using Kroko.App.ViewModels;

namespace Kroko.App.Views;

/// <summary>
/// Panel de respuesta encima de la burbuja. El code-behind solo hace cosas de ventana:
/// colocarse junto a Kroko, animarse al aparecer/desaparecer y dar el foco a la caja de texto.
/// </summary>
public partial class ResponsePanel : Window
{
    private readonly PanelViewModel _vm;
    private readonly BubbleGeometry _geometry;
    private nint _hwnd;
    private bool _hiding;

    private ResponsePanel()
    {
        InitializeComponent();
        _vm = null!; // sin lógica: los manejadores comprueban null
        _geometry = null!;
    }

    /// <summary>Instancia sin lógica (solo para dibujar el panel en --snapshot).</summary>
    internal static ResponsePanel CreateForSnapshot() => new();

    public ResponsePanel(PanelViewModel viewModel, BubbleGeometry geometry)
    {
        InitializeComponent();
        _vm = viewModel;
        _geometry = geometry;
        DataContext = viewModel;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // Fuera de Alt+Tab, pero SÍ puede recibir el foco (para escribir)
            var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, style | NativeMethods.WS_EX_TOOLWINDOW);
        };
        SizeChanged += (_, _) => Reposition();
        _geometry.Changed += () => { if (IsVisible) Reposition(); };

        _vm.ShowRequested += ShowPanel;
        _vm.HideRequested += HidePanel;
        _vm.ConfirmShown += () => Dispatcher.BeginInvoke(() =>
        {
            ApproveButton.Focus();
            Keyboard.Focus(ApproveButton);
        }, System.Windows.Threading.DispatcherPriority.Input);

        PreviewKeyDown += OnPreviewKeyDown;
        MouseEnter += (_, _) => { _vm.IsPointerOver = true; _vm.NotifyActivity(); };
        MouseLeave += (_, _) => { _vm.IsPointerOver = false; _vm.NotifyActivity(); };
        ReplyScroll.ScrollChanged += (_, e) =>
        {
            // Mientras escribe, la respuesta larga se va desplazando sola hasta el final
            if (e.ExtentHeightChange > 0) ReplyScroll.ScrollToEnd();
        };
    }

    private void ShowPanel(bool activate)
    {
        _hiding = false;
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            Reposition();
            Animate(show: true);
        }
        else if (Opacity < 1)
        {
            Animate(show: true);
        }

        if (activate)
        {
            Activate();
            Input.Focus();
            Keyboard.Focus(Input);
        }
    }

    private void HidePanel()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        Animate(show: false);
    }

    private void Animate(bool show)
    {
        var duration = TimeSpan.FromMilliseconds(show ? 180 : 130);
        var ease = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };

        var fade = new DoubleAnimation(show ? 1 : 0, duration) { EasingFunction = ease };
        var scale = new DoubleAnimation(show ? 0.94 : 1, show ? 1 : 0.96, duration) { EasingFunction = ease };
        if (!show)
        {
            fade.Completed += (_, _) =>
            {
                if (!_hiding) return; // se volvió a pedir mostrar durante la animación
                Hide();
                _hiding = false;
            };
        }

        BeginAnimation(OpacityProperty, fade);
        CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scale);
        CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scale);
    }

    /// <summary>Encima del cuerpo de Kroko, alineado a su derecha; si no cabe arriba, debajo.</summary>
    private void Reposition()
    {
        if (_hwnd == 0 || !_geometry.IsKnown || !NativeMethods.GetWindowRect(_hwnd, out var me)) return;

        var body = _geometry.Body;
        var work = _geometry.WorkArea;
        var s = _geometry.MonitorScale;
        var shadowMargin = (int)Math.Round(10 * s);
        var gap = (int)Math.Round(6 * s);
        var antenna = (int)Math.Round(20 * _geometry.UnitPx);

        var left = body.Right + shadowMargin - me.Width;
        var top = body.Top - antenna - gap - me.Height + shadowMargin;
        if (top < work.Top) top = body.Bottom + gap - shadowMargin;

        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - me.Width));
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - me.Height));

        NativeMethods.SetWindowPos(_hwnd, 0, left, top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _vm.NotifyActivity();
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_vm.IsConfirming) _vm.DenyCommand.Execute(null);
            else
            {
                _vm.StopSpeaking(); // Esc también calla a Kroko
                _vm.Hide();
            }
        }
    }

    private void Input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _vm?.NotifyActivity();
}
