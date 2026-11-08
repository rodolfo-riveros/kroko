using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kroko.App.Services;
using Kroko.App.ViewModels;
using Microsoft.Extensions.Options;

namespace Kroko.App.Views;

/// <summary>Herramienta de desarrollo (--snapshot): dibuja la ventana "Conectores" con datos de ejemplo.</summary>
public static class ConnectorsWindowSnapshot
{
    public static string Render(string outputDir)
    {
        using var api = new BrainApiClient(Options.Create(new KrokoOptions()));
        var vm = new ConnectorsViewModel(api) { Status = "1 de 3 conectados" };
        vm.Connectors.Add(new ConnectorDto("navegador", "Navegador web", "Conectado", 21, null, false));
        vm.Connectors.Add(new ConnectorDto("notion", "Notion", "NecesitaPermiso", 0, "Pulsa \"Conectar\" y da permiso en el navegador.", true));
        vm.Connectors.Add(new ConnectorDto("todoist", "Todoist", "NecesitaPermiso", 0, "Pulsa \"Conectar\" y da permiso en el navegador.", true));

        var window = new ConnectorsWindow(vm);
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new System.Windows.Controls.Border { Background = (Brush)Application.Current.Resources["Kroko.Window"], Child = content };
        host.Resources.MergedDictionaries.Add(window.Resources);
        host.DataContext = vm;
        System.Windows.Documents.TextElement.SetForeground(host, window.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);
        host.Measure(new Size(640, 520));
        host.Arrange(new Rect(0, 0, 640, 520));
        host.UpdateLayout();

        var bmp = new RenderTargetBitmap(640, 520, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        window.Close();
        vm.Dispose();

        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "conectores.png");
        using var fs = File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        enc.Save(fs);
        return path;
    }
}
