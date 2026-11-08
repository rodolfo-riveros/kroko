using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kroko.App.Services;
using Kroko.App.ViewModels;
using Microsoft.Extensions.Options;

namespace Kroko.App.Views;

/// <summary>Herramienta de desarrollo (--snapshot): dibuja "Mi día" con datos de ejemplo.</summary>
public static class MyDayWindowSnapshot
{
    public static string Render(string outputDir)
    {
        using var api = new BrainApiClient(Options.Create(new KrokoOptions()));
        var vm = new MyDayViewModel(api);
        vm.Apply(new MyDayDto("Lunes 9 de noviembre", "Buenos días, Rodolfo",
            new MyDayWeatherDto("Urubamba", 17.4, 21, 7, 40, "Parcialmente nublado"),
            [new("09:00", "Reunión con el equipo", "Google Meet"), new("13:30", "Almuerzo con Ana", "Restaurante El Huacatay"), new("Todo el día", "Cumpleaños de mamá", null)],
            [new("Enviar la cotización a Ana", "Kroko", "7 nov", true), new("Revisar propuesta", "Trello", "9 nov 18:00", false), new("Comprar tóner", "Google", null, false)],
            [new("16:00", "Llamar al contador")],
            [new("Tomar 2 litros de agua", true, 5), new("Leer 20 minutos", false, 0)],
            ["Conecta tu cuenta de Google para ver tu agenda y Google Tasks."]));

        var window = new MyDayWindow(vm);
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new System.Windows.Controls.Border { Background = (Brush)Application.Current.Resources["Kroko.Window"], Child = content };
        host.Resources.MergedDictionaries.Add(window.Resources);
        host.DataContext = vm;
        System.Windows.Documents.TextElement.SetForeground(host, window.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(host, window.FontSize);
        host.Measure(new Size(820, 640));
        host.Arrange(new Rect(0, 0, 820, 640));
        host.UpdateLayout();
        var bmp = new RenderTargetBitmap(820, 640, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        window.Close();

        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "mi-dia.png");
        using var fs = File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        enc.Save(fs);
        return path;
    }
}
