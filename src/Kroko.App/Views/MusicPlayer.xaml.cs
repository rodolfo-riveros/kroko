using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Kroko.App.Native;
using Kroko.App.Services;
using Kroko.App.ViewModels;

namespace Kroko.App.Views;

/// <summary>El reproductor grande que cuelga de la isla (17/11/2026). Se cierra con Esc o al hacer clic fuera.</summary>
public partial class MusicPlayer : Window
{
    /// <summary>Progreso 0..1 × ancho de la pista = ancho de la barra lima.</summary>
    public static readonly IMultiValueConverter WidthOf = new ProgressWidth();

    private readonly MusicPlayerViewModel _vm;
    private readonly BubbleGeometry _geometry;
    private readonly DateTime _openedAt = DateTime.Now;
    private bool _closing;

    public MusicPlayer(MusicPlayerViewModel viewModel, BubbleGeometry geometry)
    {
        InitializeComponent();
        _vm = viewModel;
        _geometry = geometry;
        DataContext = viewModel;
        SourceInitialized += (_, _) => NativeMethods.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            HangingPanel.Place(this, _geometry);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ((ScaleTransform)Frame.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            Frame.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        };
        SizeChanged += (_, _) => HangingPanel.Place(this, _geometry);
        Deactivated += (_, _) =>
        {
            if ((DateTime.Now - _openedAt).TotalMilliseconds > 400) CloseAnimated();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CloseAnimated();
        };
        Closed += (_, _) => _vm.Dispose();
    }

    public void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
        fade.Completed += (_, _) => Close();
        Frame.BeginAnimation(OpacityProperty, fade);
    }

    private sealed class ProgressWidth : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
            values is [double p, double w] ? Math.Clamp(p, 0, 1) * w : 0.0;

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
