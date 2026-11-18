using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Kroko.App.Native;
using Kroko.App.Services;
using Kroko.App.ViewModels;

namespace Kroko.App.Views;

/// <summary>
/// Panel que sale al soltar archivos sobre Kroko (17/11/2026, especificación «soltar archivos»): cuelga de la isla
/// (o sale encima de la burbuja), se cierra con Esc, al hacer clic fuera o al terminar.
/// </summary>
public partial class DropPanel : Window
{
    private readonly DropPanelViewModel _vm;
    private readonly BubbleGeometry _geometry;
    private DateTime _openedAt;
    private bool _closing;

    public DropPanel(DropPanelViewModel viewModel, BubbleGeometry geometry)
    {
        InitializeComponent();
        _vm = viewModel;
        _geometry = geometry;
        DataContext = viewModel;
        viewModel.CloseRequested += CloseAnimated;
        SourceInitialized += (_, _) => NativeMethods.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            Place();
            AnimateOpen();
        };
        SizeChanged += (_, _) => Place();
        Deactivated += (_, _) =>
        {
            // No cerrarse mientras trabaja ni mientras se muestra el QR (el dueño mira su celular)
            if (_vm.Busy || _vm.Mode == "celular") return;
            if ((DateTime.Now - _openedAt).TotalMilliseconds > 400) CloseAnimated();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CloseAnimated();
        };
    }

    public DropPanelViewModel ViewModel => _vm;

    public void Open(IEnumerable<string> files)
    {
        _vm.Load(files);
        _openedAt = DateTime.Now;
        if (!IsLoaded) Show();
        Activate();
    }

    private void Place() => HangingPanel.Place(this, _geometry);

    private void AnimateOpen()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = (ScaleTransform)Frame.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        Frame.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    public void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
        fade.Completed += (_, _) => Close();
        ((ScaleTransform)Frame.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.9, TimeSpan.FromMilliseconds(130)));
        Frame.BeginAnimation(OpacityProperty, fade);
    }
}
