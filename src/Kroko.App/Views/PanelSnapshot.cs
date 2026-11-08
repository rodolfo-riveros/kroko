using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kroko.App.Views;

/// <summary>
/// Herramienta de desarrollo (parte de --snapshot): dibuja el panel de respuesta con datos de
/// ejemplo en un PNG, sin mostrar ninguna ventana.
/// </summary>
public static class PanelSnapshot
{
    private sealed record Sample(string UserText, string ReplyText, bool IsReplyError, string InputText,
        bool IsThinking, bool IsConnected, string StatusText)
    {
        public object? SendCommand => null;
        public bool IsConfirming { get; init; }
        public string ConfirmText { get; init; } = "";
        public object Timers { get; } = new { IsActive = false };
        public bool IsListening { get; init; }
        public string ListeningHint { get; init; } = "";
        public double MicLevel { get; init; }
        public bool IsConversation { get; init; }
        public bool HasCard => CardItems.Count > 0;
        public string CardTitle { get; init; } = "";
        public List<Kroko.Shared.Protocol.CardItem> CardItems { get; init; } = [];
    }

    public static string Render(string outputDir)
    {
        Sample[] samples =
        [
            new("hola", "¡Hola, Rodolfo! Aquí estoy, listo para ayudarte. ¿Qué hacemos hoy?", false, "", false, true, "Conectado"),
            new("¿qué hora es?", "", false, "", true, true, "Conectado"),
            new("", "No me puedo conectar con mi cerebro (Kroko.Brain). Lo sigo intentando solo; si no vuelve, revisa que esté encendido.",
                true, "", false, false, "Sin conexión con el cerebro · reintentando"),
            new("", "", false, "ábreme el Excel de ventas", false, true, "Conectado"),
            new("Abre la carpeta de descargas y", "", false, "", false, true, "Conectado")
                { IsListening = true, ListeningHint = "¿Algo más? Te escucho · di «gracias» para terminar", MicLevel = 0.55, IsConversation = true },
            new("¿qué tareas tengo hoy?", "Tienes tres pendientes; la más urgente es enviar la cotización.", false, "", false, true, "Conectado")
            {
                IsConversation = true, CardTitle = "Tareas",
                CardItems = [new("Enviar la cotización a Ana", "para el 7 de noviembre", false), new("Comprar tóner", null, false), new("Llamar al contador", null, true)],
            },
            new("apaga la PC en 10 minutos", "", false, "", false, true, "Conectado")
                { IsConfirming = true, ConfirmText = "Apagar la PC dentro de 10 minutos" },
            new("¿cómo va mi PC?", "Todo bien: batería al 76 % y cargando, poca memoria usada.", false, "", false, true, "Conectado")
            {
                CardTitle = "Estado del equipo",
                CardItems = [new("Batería", "76 % · cargando", Value: 0.76), new("Procesador", "12 %", Value: 0.12), new("Memoria", "48 % · 8.2 GB libres", Value: 0.48), new("Disco C:", "120 GB libres", Value: 0.74), new("Internet", "conectado", Done: true)],
            },
        ];

        const double width = 372;
        var images = new List<RenderTargetBitmap>();
        foreach (var s in samples)
        {
            var window = new ResponsePanelTemplate();
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            content.DataContext = s;
            content.Measure(new Size(width, double.PositiveInfinity));
            content.Arrange(new Rect(0, 0, width, content.DesiredSize.Height));
            content.UpdateLayout();

            var bmp = new RenderTargetBitmap((int)width, (int)Math.Ceiling(content.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
            bmp.Render(content);
            images.Add(bmp);
            window.Close();
        }

        var total = images.Sum(i => i.PixelHeight) + 16 * (images.Count + 1);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5A, 0x6B, 0x7A)), null, new Rect(0, 0, width + 32, total));
            double y = 16;
            foreach (var img in images)
            {
                dc.DrawImage(img, new Rect(16, y, img.PixelWidth, img.PixelHeight));
                y += img.PixelHeight + 16;
            }
        }

        var sheet = new RenderTargetBitmap((int)width + 32, total, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual);
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "panel.png");
        using var fs = File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(sheet));
        enc.Save(fs);
        return path;
    }

    /// <summary>Solo para dibujar el panel fuera de pantalla (--snapshot).</summary>
    private sealed class ResponsePanelTemplate : Window
    {
        public ResponsePanelTemplate()
        {
            var panel = ResponsePanel.CreateForSnapshot();
            Content = panel.Content;
            panel.Content = null;
            foreach (var key in panel.Resources.Keys) Resources[key] = panel.Resources[key];
            panel.Close();
        }
    }
}
