using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kroko.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Kroko.App.Views;

/// <summary>Herramienta de desarrollo (--snapshot): dibuja cada paso del asistente "Configurar Kroko".</summary>
public static class OnboardingWindowSnapshot
{
    private const int W = 880, H = 700;

    public static IEnumerable<string> Render(IServiceProvider services, string outputDir)
    {
        var vm = services.GetRequiredService<OnboardingViewModel>();
        vm.Name = "Ana Quispe";
        vm.City = "Cusco";
        vm.GeminiState = "✓ Ya tengo una clave de Gemini (desde user-secrets). Escribe otra solo si quieres cambiarla.";
        vm.TavilyState = "Opcional: mejora las búsquedas y la lectura de páginas.";
        vm.DeepSeekState = "Opcional: respaldo si Gemini falla.";
        vm.MailState = "Opcional: así puedo leer, buscar y (con tu permiso) enviar correos.";
        vm.TelegramState = "Bot @kroko_ana_bot conectado. Falta vincular tu chat (paso 2).";

        var window = new OnboardingWindow(vm);
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new System.Windows.Controls.Border { Background = (Brush)Application.Current.Resources["Kroko.Window"], Child = content };
        host.Resources.MergedDictionaries.Add(window.Resources);
        host.DataContext = vm;
        System.Windows.Documents.TextElement.SetForeground(host, window.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(host, window.FontSize);
        Directory.CreateDirectory(outputDir);

        var paths = new List<string>();
        for (var step = 0; step <= OnboardingViewModel.DoneStep; step++)
        {
            vm.Preview(step);
            if (step == OnboardingViewModel.TelegramStep) vm.PairingCode = "482913";
            if (step == OnboardingViewModel.DoneStep)
            {
                vm.Summary.Clear();
                foreach (var s in new[] { "✓ Sé quién eres", "✓ Cerebro de IA + búsqueda Tavily", "✓ Gmail: ana@gmail.com", "· Telegram: sin vincular" })
                    vm.Summary.Add(s);
            }
            if (step == OnboardingViewModel.MailStep) { vm.MessageIsError = true; vm.Message = "Gmail rechazó la contraseña de aplicación. Hay que generarla de nuevo."; }
            if (step == OnboardingViewModel.ExtrasStep) { vm.MessageIsError = false; vm.Message = ""; vm.TrelloState = "Tableros, listas y tarjetas."; vm.GoogleState = "Necesita un cliente OAuth de tu proyecto de Google Cloud (pasos abajo)."; }

            host.Measure(new Size(W, H));
            host.Arrange(new Rect(0, 0, W, H));
            host.UpdateLayout();
            var bmp = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(host);

            var path = Path.Combine(outputDir, $"bienvenida-{step}.png");
            using (var fs = File.Create(path))
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                enc.Save(fs);
            }
            paths.Add(path);
        }
        window.Close();
        vm.Dispose();
        return paths;
    }
}
