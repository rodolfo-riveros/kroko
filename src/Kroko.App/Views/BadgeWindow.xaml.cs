using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Kroko.App.Native;
using Kroko.App.Services;
using Kroko.App.ViewModels;

namespace Kroko.App.Views;

/// <summary>
/// La ventanita bajo Kroko: el check verde tras cada acción y la cuenta atrás del enfoque. No recibe clics ni foco,
/// no sale en Alt+Tab y se coloca debajo del cuerpo de Kroko (o a su izquierda si abajo no cabe).
/// </summary>
public partial class BadgeWindow : Window
{
    private readonly BadgeViewModel _vm;
    private readonly BubbleGeometry _geometry;
    private readonly DispatcherTimer _tick;
    private nint _hwnd;

    public BadgeWindow(BadgeViewModel viewModel, BubbleGeometry geometry)
    {
        InitializeComponent();
        _vm = viewModel;
        _geometry = geometry;
        DataContext = viewModel;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            this.MakeToolWindowNoActivate();
            var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, style | NativeMethods.WS_EX_TRANSPARENT); // los clics pasan de largo
        };
        SizeChanged += (_, _) => Reposition();
        _geometry.Changed += () => { if (IsVisible) Reposition(); };

        // Cada medio segundo: mostrar u ocultar según haya algo que enseñar (check o cuenta atrás)
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tick.Tick += (_, _) => Update();
        _tick.Start();
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BadgeViewModel.BadgeText)) Update(); };
    }

    private void Update()
    {
        var show = _vm.HasAnything && _geometry.IsKnown;
        if (show && !IsVisible)
        {
            Show();
            Reposition();
        }
        else if (!show && IsVisible) Hide();
    }

    private void Reposition()
    {
        if (_hwnd == 0 || !_geometry.IsKnown || !NativeMethods.GetWindowRect(_hwnd, out var me)) return;
        var body = _geometry.Body;
        var work = _geometry.WorkArea;
        var gap = (int)Math.Round(2 * _geometry.MonitorScale);

        // Debajo de Kroko, centrado; si no cabe (suele estar en la esquina de abajo), a su izquierda
        var left = (body.Left + body.Right) / 2 - me.Width / 2;
        var top = body.Bottom + gap;
        if (top + me.Height > work.Bottom)
        {
            left = body.Left - me.Width - gap;
            top = (body.Top + body.Bottom) / 2 - me.Height / 2;
        }
        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - me.Width));
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - me.Height));
        NativeMethods.SetWindowPos(_hwnd, 0, left, top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }
}
