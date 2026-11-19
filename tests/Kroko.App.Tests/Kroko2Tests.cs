using System.IO;
using System.Text.Json;
using Kroko.App.Audio;
using Kroko.App.Services;
using Kroko.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kroko.App.Tests;

/// <summary>Kroko 2 (18/11/2026): isla, bandeja, portapapeles, recordatorios por contexto, reunión y actividad.</summary>
public class Kroko2Tests
{
    [Theory]
    [InlineData("Excel", "EXCEL", true)]
    [InlineData("Word", "WINWORD", true)]
    [InlineData("Chrome", "chrome", true)]
    [InlineData("Excel", "chrome", false)]
    [InlineData("PowerPoint", "POWERPNT", true)]
    public void Recordatorio_al_abrir_una_app(string app, string process, bool fires) =>
        Assert.Equal(fires, ContextReminders.Matches(new ContextRule("1", "app", app, "x", true), "app", process));

    [Fact]
    public void Recordatorio_al_conectar_audifonos_no_espera_una_app() =>
        Assert.True(ContextReminders.Matches(new ContextRule("1", "audifonos", "", "x", true), "audifonos", ""));

    [Theory]
    [InlineData("Pa$$w0rd!2026", true)]
    [InlineData("4111 1111 1111 1111", true)]
    [InlineData("sk-proj-abcdefghijklmnop", true)]
    [InlineData("https://www.notion.so/pagina-1", false)]
    [InlineData("Hola, nos vemos mañana a las 10", false)]
    public void El_portapapeles_no_guarda_lo_que_parece_secreto(string text, bool secret) =>
        Assert.Equal(secret, ClipboardWatcher.LooksSecret(text));

    [Theory]
    [InlineData("notion-create-pages", "Abriendo Notion")]
    [InlineData("buscar_en_internet", "Buscando en internet")]
    [InlineData("ordenar_carpeta", "Ordenando archivos")]
    [InlineData("algo_nuevo", "Trabajando")]
    public void La_isla_dice_que_herramienta_usa(string tool, string text) =>
        Assert.Equal(text, IslandViewModel.FriendlyTool(tool));

    [Fact]
    public void Los_bolsillos_cambian_segun_el_tipo()
    {
        Assert.Equal("pdf", QuickFiles.PocketsFor(["a.png", "b.jpg"])[0].Key);
        Assert.Equal(["mp3", "transcribir", "bandeja"], QuickFiles.PocketsFor(["nota.m4a"]).Select(p => p.Key));
        Assert.Contains(QuickFiles.PocketsFor(["informe.docx"]), p => p.Key == "resumir");
        Assert.Contains(QuickFiles.PocketsFor(["x.zip"]), p => p.Key == "bandeja");
        Assert.All(QuickFiles.PocketsFor(["a.png"]), p => Assert.Single(p.GlyphText));
    }

    [Fact]
    public void La_bandeja_se_vacia_a_las_24_horas_sin_borrar_los_archivos()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kroko-bandeja-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "fotos.pdf");
            File.WriteAllText(file, "x");
            var tray = new FileTray(NullLogger<FileTray>.Instance, Path.Combine(dir, "bandeja.json"));
            Assert.Equal(1, tray.Add([file]));
            Assert.Equal(0, tray.Add([file])); // sin repetidos
            Assert.Equal("PDF", tray.Items[0].Kind);

            // Se recuerda entre reinicios
            Assert.Single(new FileTray(NullLogger<FileTray>.Instance, Path.Combine(dir, "bandeja.json")).Items);

            tray.Prune(DateTime.Now.AddHours(25));
            Assert.Empty(tray.Items);
            Assert.True(File.Exists(file)); // el original sigue ahí
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Lo_que_hizo_Kroko_ordenar_se_puede_deshacer()
    {
        var args = JsonSerializer.SerializeToElement(new { ruta = @"C:\Users\Pol\Downloads" });
        var data = JsonSerializer.SerializeToElement(new { movidos = 24, registro = @"C:\x\orden-1.json" });
        var item = ActivityFeed.Describe("ordenar_carpeta", args, data);
        Assert.NotNull(item);
        Assert.Equal("Ordenó 24 archivos de Downloads", item.Text);
        Assert.Equal("Deshacer", item.ActionLabel);
        Assert.True(item.CanUndo);
        Assert.Null(ActivityFeed.Describe("estado_equipo", args, data)); // las consultas no se anotan
    }

    [Fact]
    public void La_reunion_se_parte_en_trozos_que_Gemini_acepta()
    {
        var pcm = new byte[16000 * 2 * 60 * 20]; // 20 minutos de silencio
        var parts = MeetingRecorder.Split(Wav.FromPcm16(pcm, 16000), 8);
        Assert.Equal(3, parts.Count);
        Assert.All(parts, p => Assert.True(p.Length < 18 * 1024 * 1024));
    }

    [Fact]
    public void La_reunion_mezcla_el_microfono_aunque_no_haya_audio_del_sistema()
    {
        var mic = Wav.FromPcm16(new byte[16000 * 2], 16000); // 1 s
        var mixed = MeetingRecorder.Mix(mic, null, null);
        Assert.Equal(44 + 16000 * 2, mixed.Length);
    }
}
