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
    private const int W = 1160, H = 760;

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

        // Soltar archivos y el reproductor (17/11/2026): cada vista del panel
        var sample = Path.Combine(Path.GetTempPath(), "Foto de muestra.png");
        if (!File.Exists(sample)) WriteSamplePng(sample);
        var drop = services.GetRequiredService<DropPanel>();
        drop.ViewModel.Load([sample]);
        foreach (var mode in new[] { "inicio", "convertir", "analizar", "ordenar", "enviar" })
        {
            drop.ViewModel.ShowCommand.Execute(mode);
            paths.Add(RenderLoose(drop, $"soltar-{mode}", outputDir));
        }
        drop.Close();
        // Barra de comandos y portapapeles (Kroko 2)
        var palette = services.GetRequiredService<CommandPalette>();
        var pvm = (CommandPaletteViewModel)palette.DataContext;
        pvm.Open();
        pvm.Query = "conv";
        paths.Add(RenderLoose(palette, "comandos", outputDir));
        palette.Close();
        var clips = services.GetRequiredService<Kroko.App.Services.ClipboardWatcher>();
        clips.History.Add(new Kroko.App.Services.ClipItem { Kind = "enlace", Text = "https://www.notion.so/kroko/pendientes-del-lunes" });
        clips.History.Add(new Kroko.App.Services.ClipItem { Kind = "texto", Text = "Reunión con Ana el viernes a las 10, llevar el informe impreso", Time = DateTime.Now.AddMinutes(-5) });
        var clipPanel = services.GetRequiredService<ClipboardPanel>();
        paths.Add(RenderLoose(clipPanel, "portapapeles", outputDir));
        clipPanel.Close();
        var player = services.GetRequiredService<MusicPlayer>();
        paths.Add(RenderLoose(player, "musica", outputDir));
        player.Close();
        return paths;
    }

    /// <summary>Una ventana que cuelga de Kroko (tamaño propio), abierta fuera de la pantalla.</summary>
    private static string RenderLoose(Window window, string name, string outputDir)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -30000;
        window.Top = -30000;
        window.ShowActivated = false;
        if (!window.IsVisible) window.Show();
        window.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        var full = Capture(window, (FrameworkElement)window.Content);
        // Solo la ventana (con un poco de escritorio alrededor), no el lienzo entero
        var w = Math.Min(W, (int)Math.Ceiling(window.ActualWidth) + 16);
        var h = Math.Min(H, (int)Math.Ceiling(window.ActualHeight) + 16);
        var bmp = new CroppedBitmap(full, new Int32Rect(0, 0, w, h));
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, name + ".png");
        using var fs = File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        enc.Save(fs);
        return path;
    }

    private static void WriteSamplePng(string path)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen()) dc.DrawRectangle(Brushes.SteelBlue, null, new Rect(0, 0, 64, 48));
        var bmp = new RenderTargetBitmap(64, 48, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        using var fs = File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        enc.Save(fs);
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

    /// <summary>Quita las animaciones de entrada (desvanecer, escalar) para capturar la ventana ya abierta del todo.</summary>
    private static void FinishAnimations(DependencyObject node)
    {
        if (node is UIElement el)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            if (el.RenderTransform is ScaleTransform s && !s.IsFrozen)
            {
                s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) FinishAnimations(VisualTreeHelper.GetChild(node, i));
        if (node is Window w) w.UpdateLayout();
    }

    /// <summary>El contenido con el fondo de la ventana detrás (como se ve en pantalla).</summary>
    private static RenderTargetBitmap Capture(Window window, FrameworkElement content)
    {
        FinishAnimations(window);
        // La ventana entera con su marco (semáforo y título) si lo tiene
        if (VisualTreeHelper.GetChildrenCount(window) > 0 && VisualTreeHelper.GetChild(window, 0) is FrameworkElement root) content = root;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Detrás, un escritorio de colores (el peor caso para el contraste del vidrio): el acrílico de Windows lo
            // desenfoca y oscurece aún más, así que si aquí se lee bien, en la pantalla también
            var desk = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(0x2D, 0x6C, 0xDF), 0), new(Color.FromRgb(0xB0, 0x4C, 0xD8), 0.5), new(Color.FromRgb(0xF2, 0x9B, 0x38), 1),
            }, new Point(0, 0), new Point(1, 1));
            dc.DrawRectangle(desk, null, new Rect(0, 0, W, H));
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
