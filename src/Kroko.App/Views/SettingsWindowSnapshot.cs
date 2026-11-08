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
    private const int W = 920, H = 680;

    public static IEnumerable<string> Render(IServiceProvider services, string outputDir)
    {
        var vm = services.GetRequiredService<SettingsViewModel>();
        var window = new SettingsWindow(vm);
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new Border { Background = (Brush)Application.Current.Resources["Kroko.Window"], Child = content };
        host.Resources.MergedDictionaries.Add(window.Resources);
        host.DataContext = vm;
        System.Windows.Documents.TextElement.SetForeground(host, window.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(host, window.FontSize);
        Directory.CreateDirectory(outputDir);

        var tabs = FindTabControl(content);
        var paths = new List<string>();
        for (var i = 0; tabs is not null && i < tabs.Items.Count; i++)
        {
            tabs.SelectedIndex = i;
            host.Measure(new Size(W, H));
            host.Arrange(new Rect(0, 0, W, H));
            host.UpdateLayout();
            var bmp = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(host);
            var path = Path.Combine(outputDir, $"ajustes-{i}.png");
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
                var lower = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
                lower.Render(host);
                var lowerPath = Path.Combine(outputDir, $"ajustes-{i}-abajo.png");
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
        vm.Dispose();
        return paths;
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
