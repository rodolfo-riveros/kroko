using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kroko.App.Services;
using Kroko.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kroko.App.Views;

/// <summary>Herramienta de desarrollo (--snapshot): dibuja "Lo que Kroko sabe de ti" con datos de ejemplo.</summary>
public static class MemoryWindowSnapshot
{
    public static string Render(string outputDir)
    {
        using var api = new BrainApiClient(Options.Create(new KrokoOptions()));
        var vm = new MemoryViewModel(api, NullLogger<MemoryViewModel>.Instance) { Status = "3 recuerdos · 1 rutinas · 1 mejoras pendientes" };
        vm.Recuerdos.Add(new MemoryDto(1, "preferencia", "Su navegador favorito es Brave", DateTimeOffset.Now));
        vm.Recuerdos.Add(new MemoryDto(2, "dato", "Guarda las facturas en Documentos, carpeta Facturas", DateTimeOffset.Now));
        vm.Recuerdos.Add(new MemoryDto(3, "persona", "Su hermana se llama Ana", DateTimeOffset.Now));
        vm.Rutinas.Add(new RoutineDto(1, "modo estudio", ["modo estudio", "a estudiar"], ["abrir Notion", "silenciar el sonido", "abrir la carpeta de documentos"], DateTimeOffset.Now));
        vm.Mejoras.Add(new SuggestionDto(1, "idea", "Leer mis correos de Gmail", "Pol preguntó si tenía correos nuevos.", 2, "nueva", DateTimeOffset.Now));

        var window = new MemoryWindow(vm);
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new System.Windows.Controls.Border { Background = (Brush)Application.Current.Resources["Kroko.Window"], Child = content };
        host.Resources.MergedDictionaries.Add(window.Resources);
        host.DataContext = vm;
        System.Windows.Documents.TextElement.SetForeground(host, window.Foreground); // lo que la ventana real hereda
        System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);
        host.Measure(new Size(760, 900));
        host.Arrange(new Rect(0, 0, 760, 900));
        host.UpdateLayout();

        var bmp = new RenderTargetBitmap(760, 900, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        window.Close();

        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "memoria.png");
        using var fs = File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        enc.Save(fs);
        return path;
    }
}
