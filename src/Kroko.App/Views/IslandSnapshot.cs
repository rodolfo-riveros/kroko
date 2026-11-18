using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kroko.App.Face;
using Kroko.App.Services;

namespace Kroko.App.Views;

/// <summary>
/// Herramienta de desarrollo (parte de --snapshot): dibuja la isla «Kroko 2» en sus diez estados y los pasos de soltar
/// archivos, sobre una franja que imita la parte de arriba de la pantalla, sin mostrar ninguna ventana.
/// </summary>
public static class IslandSnapshot
{
    /// <summary>Datos de ejemplo con las mismas propiedades que IslandViewModel (sin servicios).</summary>
    public sealed class Sample
    {
        public required FaceAnimator Face { get; init; }
        public required string State { get; init; }
        public bool Hover { get; init; }
        public string Clock { get; init; } = "18:10";
        public object Panel { get; init; } = new { UserText = "abre mi Notion y crea una página…", MicLevel = 0.7 };
        public string ThinkingText { get; init; } = "Abriendo Notion";
        public string ToolChip { get; init; } = "abrir_app";
        public string DoneText { get; init; } = "Ordené 24 archivos";
        public bool DoneCanUndo { get; init; } = true;
        public object Timers { get; init; } = new { RemainingText = "24:12", Label = "Informe del proyecto", Progress = 0.65 };
        public string MusicTitle { get; init; } = "Lofi para enfocarse";
        public string MusicArtist { get; init; } = "Spotify";
        public bool MusicPlaying { get; init; } = true;
        public ImageSource? MusicCover { get; init; }
        public string ReminderText { get; init; } = "Llamar al dentista";
        public List<Pocket> Pockets { get; init; } = [.. QuickFiles.PocketsFor(["a.png", "b.png", "c.png"])];
        public string NearDetail { get; init; } = "3 imágenes · 4,2 MB";
        public string ClipText { get; init; } = "Copiaste un enlace";
        public object ClipItem { get; init; } = new { IsLink = true, IsImage = false, IsText = false };
        public string WorkTitle { get; init; } = "Convirtiendo 2 de 3";
        public string WorkDetail { get; init; } = "foto-02.jpg → fotos.pdf";
        public double WorkProgress { get; init; } = 0.62;
        public string WorkPercent { get; init; } = "62 %";
        public QuickResult? ResultData { get; init; } = new("PDF listo", @"C:\Users\Pol\Downloads\fotos.pdf", "3 páginas · 3,1 MB · en Descargas", () => "");
        public string ResultError { get; init; } = "";
        public bool ResultHasFile { get; init; } = true;
        public string ResultFileName { get; init; } = "fotos.pdf";
        public string ResultKind { get; init; } = "PDF";
    }

    public static string Render(ExpressionLibrary library, string outputDir)
    {
        FaceAnimator Face(string expression)
        {
            var a = new FaceAnimator(library, () => 1.0) { EnableBlink = false, FollowCursor = false, EnableBreath = false };
            a.SetBase(expression);
            return a;
        }

        (string Label, Sample Data)[] samples =
        [
            ("Reposo", new Sample { Face = Face("idle"), State = "reposo" }),
            ("Reposo + ratón", new Sample { Face = Face("idle"), State = "reposo", Hover = true }),
            ("Escuchando", new Sample { Face = Face("listening"), State = "escuchando" }),
            ("Pensando", new Sample { Face = Face("thinking"), State = "pensando" }),
            ("Listo + Deshacer", new Sample { Face = Face("success"), State = "listo" }),
            ("Enfoque", new Sample { Face = Face("focus"), State = "enfoque" }),
            ("Música", new Sample { Face = Face("music"), State = "musica" }),
            ("Recordatorio", new Sample { Face = Face("alert"), State = "recordatorio" }),
            ("Archivo cerca", new Sample { Face = Face("surprised"), State = "cerca" }),
            ("Portapapeles", new Sample { Face = Face("idle"), State = "portapapeles" }),
            ("Sin conexión", new Sample { Face = Face("offline"), State = "sin-conexion" }),
            ("Convirtiendo", new Sample { Face = Face("thinking"), State = "trabajando" }),
            ("Listo (archivo)", new Sample { Face = Face("success"), State = "resultado" }),
        ];

        const double stripWidth = 720, labelWidth = 130;
        var images = new List<RenderTargetBitmap>();
        foreach (var (_, s) in samples)
        {
            var island = new IslandView { DataContext = s };
            island.Measure(new Size(600, 300));
            island.Arrange(new Rect(island.DesiredSize));
            // Que se apliquen los enlaces (qué estado se ve) antes de medir de verdad
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            island.InvalidateMeasure();
            island.Measure(new Size(600, 300));
            var size = island.DesiredSize;
            island.Arrange(new Rect(0, 0, size.Width, size.Height));
            island.UpdateLayout();
            island.RenderFaceNow();
            island.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
            bmp.Render(island);
            images.Add(bmp);
        }

        var rows = images.Select(i => Math.Max(56, i.PixelHeight + 22.0)).ToList();
        var total = rows.Sum();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x1B, 0x1F)), null, new Rect(0, 0, stripWidth, total));
            var y = 0.0;
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            for (var i = 0; i < images.Count; i++)
            {
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), 1), new Point(0, y), new Point(stripWidth, y));
                var label = new FormattedText(samples[i].Label, System.Globalization.CultureInfo.GetCultureInfo("es-PE"), FlowDirection.LeftToRight,
                    typeface, 12, Brushes.White, 1.0);
                dc.DrawText(label, new Point(14, y + 10));
                var x = labelWidth + (stripWidth - labelWidth - images[i].PixelWidth) / 2;
                dc.DrawImage(images[i], new Rect(x, y, images[i].PixelWidth, images[i].PixelHeight));
                y += rows[i];
            }
        }

        var sheet = new RenderTargetBitmap((int)stripWidth, (int)total, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual);
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "isla.png");
        using var file = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(sheet));
        encoder.Save(file);
        return path;
    }
}
