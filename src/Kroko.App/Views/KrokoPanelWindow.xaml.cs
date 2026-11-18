using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Shell;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Kroko.App.Native;
using Kroko.App.Services;
using Kroko.App.ViewModels;

namespace Kroko.App.Views;

/// <summary>El panel «Kroko»: Inicio, Tareas, Hábitos, Enfoque, Mi PC y Ajustes (14/11/2026).</summary>
public partial class KrokoPanelWindow : Window
{
    private bool _attached;
    private bool _closing;
    private DateTime _openedAt;

    public KrokoPanelWindow(KrokoPanelViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            await viewModel.RefreshAsync();

        };
        Activated += async (_, _) => await viewModel.RefreshAsync();
        viewModel.ChatUpdated += () => Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd()); // el chat siempre muestra lo último
    }

    public KrokoPanelViewModel ViewModel => (KrokoPanelViewModel)DataContext;

    // ---------------------------------------------------------------- Bandeja y archivos (Kroko 2, 18/11/2026)

    private static string[] Files(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] f ? f : [];

    private void Tray_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = Files(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Tray_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ViewModel.AddToTray(Files(e));
    }

    /// <summary>Soltar un archivo en el cuadro de «Escribe…»: se abre el panel de archivos (Convertir, Analizar…).</summary>
    private void AskBox_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (Files(e).Length == 0) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void AskBox_PreviewDrop(object sender, DragEventArgs e)
    {
        if (Files(e) is not { Length: > 0 } files) return;
        e.Handled = true;
        ViewModel.OpenDrop(files);
    }

    private Point _trayPress;
    private Services.TrayFile? _trayPressed;

    private void TrayTile_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _trayPress = e.GetPosition(this);
        _trayPressed = (sender as FrameworkElement)?.DataContext as Services.TrayFile;
    }

    /// <summary>Arrastrar un archivo de la bandeja a cualquier app (pasados 4 px, así el doble clic sigue abriéndolo).</summary>
    private void TrayTile_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _trayPressed is null) return;
        var d = e.GetPosition(this) - _trayPress;
        if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
        var file = _trayPressed;
        _trayPressed = null;
        Services.FileTray.DragOut((DependencyObject)sender, file);
    }

    public bool IsAttached => _attached;

    public bool IsClosing => _closing;

    /// <summary>
    /// Modo isla (17/11/2026, como Troko Desk): sin marco, colgando de la isla de arriba, con esquinas redondeadas.
    /// Se cierra al hacer clic fuera o con Esc. Hay que llamarlo antes de Show().
    /// </summary>
    public void AttachToIsland(BubbleGeometry geometry)
    {
        _attached = true;
        // Sin el marco macOS de ventana normal: aquí cuelga de la isla, sin título ni semáforo
        var bare = new FrameworkElementFactory(typeof(AdornerDecorator));
        bare.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        Template = new ControlTemplate(typeof(Window)) { VisualTree = bare };
        WindowChrome.SetWindowChrome(this, null);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 1080;
        Height = 690;
        MinWidth = 0;
        MinHeight = 0;

        var surface = (Brush)FindResource("Kroko.Window");
        Background = Brushes.Transparent;
        var content = (UIElement)Content;
        Content = null;
        var frame = new Border
        {
            Background = surface,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Margin = new Thickness(14, 0, 14, 18), // espacio para la sombra
            Child = content,
            RenderTransformOrigin = new Point(0.5, 0),
            RenderTransform = new ScaleTransform(1, 1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.45, Color = Colors.Black,
            },
        };
        Content = frame;

        SourceInitialized += (_, _) => PlaceUnder(geometry);
        Loaded += (_, _) => AnimateOpen(frame);
        Deactivated += (_, _) =>
        {
            // El clic que lo abrió puede quitarle el foco un instante: no cerrarse en ese primer momento
            if ((DateTime.Now - _openedAt).TotalMilliseconds > 350) CloseAnimated();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CloseAnimated();
        };
        _openedAt = DateTime.Now;
    }

    private void PlaceUnder(BubbleGeometry geometry)
    {
        if (!geometry.IsKnown) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        var s = geometry.MonitorScale;
        var widthPx = (int)Math.Round(Width * s);
        var body = geometry.Body;
        var left = (body.Left + body.Right) / 2 - widthPx / 2;
        var top = body.Bottom + (int)Math.Round(6 * s);
        NativeMethods.SetWindowPos(hwnd, 0, left, top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private static void AnimateOpen(Border frame)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = (ScaleTransform)frame.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        frame.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    /// <summary>Se recoge hacia la isla y se cierra.</summary>
    public void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        if (!_attached || Content is not Border frame)
        {
            Close();
            return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
        fade.Completed += (_, _) => Close();
        ((ScaleTransform)frame.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.9, TimeSpan.FromMilliseconds(130)));
        frame.BeginAnimation(OpacityProperty, fade);
    }
}
