using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kroko.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Kroko.App.Views;

/// <summary>Herramienta de desarrollo (--snapshot): dibuja cada pestaña de Ajustes.</summary>
public static class SettingsWindowSnapshot
{
    private const int W = 980, H = 700;

    public static IEnumerable<string> Render(IServiceProvider services, string outputDir)
    {
        var vm = services.GetRequiredService<SettingsViewModel>();
        var paths = RenderTabs(new SettingsWindow(vm), vm, "ajustes", outputDir).ToList();
        vm.Dispose();

        // El panel «Kroko» con datos de muestra (sin cerebro)
        var panelVm = services.GetRequiredService<KrokoPanelViewModel>();
        panelVm.ShowSample();
        paths.AddRange(RenderTabs(new KrokoPanelWindow(panelVm), panelVm, "panel", outputDir));

        // El mismo panel desplegado desde la isla (sin marco, como lo usa el dueño)
        var islandVm = services.GetRequiredService<KrokoPanelViewModel>();
        islandVm.ShowSample();
        var attached = new KrokoPanelWindow(islandVm);
        attached.AttachToIsland(new Kroko.App.Services.BubbleGeometry());
        paths.AddRange(RenderTabs(attached, islandVm, "isla-panel", outputDir));
        islandVm.Dispose();
        panelVm.Dispose();
        return paths;
    }

    /// <summary>Dibuja cada pestaña de una ventana con navegación lateral (y otra vez desplazada si es larga).</summary>
    internal static IEnumerable<string> RenderTabs(Window window, object vm, string prefix, string outputDir)
    {
        // La ventana de verdad, abierta fuera de la pantalla: así los estilos de Windows se aplican igual que en uso real
        // (17/11/2026: dibujar el contenido suelto escondía letras negras sobre fondo oscuro).
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -30000;
        window.Top = -30000;
        window.Width = W;
        window.Height = H;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        var host = (FrameworkElement)window.Content;
        var content = host;
        Directory.CreateDirectory(outputDir);

        var tabs = FindTabControl(content);
        var paths = new List<string>();
        for (var i = 0; tabs is not null && i < tabs.Items.Count; i++)
        {
            tabs.SelectedIndex = i;
            window.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            var bmp = Capture(window, host);
            var path = Path.Combine(outputDir, $"{prefix}-{i}.png");
            using (var fs = File.Create(path))
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                enc.Save(fs);
            }
            paths.Add(path);

            // Pestañas largas: otra captura con el contenido desplazado hasta abajo
            if (FindVisible<ScrollViewer>(host) is { ScrollableHeight: > 0 } scroll)
            {
                scroll.ScrollToEnd();
                host.UpdateLayout();
                var lower = Capture(window, host);
                var lowerPath = Path.Combine(outputDir, $"{prefix}-{i}-abajo.png");
                using (var fs = File.Create(lowerPath))
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(lower));
                    enc.Save(fs);
                }
                paths.Add(lowerPath);
                scroll.ScrollToHome();
            }
        }
        window.Close();
        return paths;
    }

    /// <summary>El contenido con el fondo de la ventana detrás (como se ve en pantalla).</summary>
    private static RenderTargetBitmap Capture(Window window, FrameworkElement content)
    {
        // La ventana entera con su marco (semáforo y título) si lo tiene
        if (VisualTreeHelper.GetChildrenCount(window) > 0 && VisualTreeHelper.GetChild(window, 0) is FrameworkElement root) content = root;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.AllowsTransparency ? (Brush)Application.Current.Resources["Kroko.Window"] : window.Background, null, new Rect(0, 0, W, H));
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null,
                new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        var bmp = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        return bmp;
    }

    /// <summary>El primer elemento T visible (la pestaña oculta no cuenta).</summary>
    private static T? FindVisible<T>(DependencyObject root) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Visibility == Visibility.Visible) return match;
            if (child is UIElement { Visibility: not Visibility.Visible }) continue;
            if (FindVisible<T>(child) is { } found) return found;
        }
        return null;
    }

    private static TabControl? FindTabControl(DependencyObject root)
    {
        if (root is TabControl t) return t;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (FindTabControl(child) is { } found) return found;
        return null;
    }
}
