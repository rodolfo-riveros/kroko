using System.IO;
using System.Text.Json;
using Kroko.App.Tools;

namespace Kroko.App.Tests;

/// <summary>Herramientas de archivos sobre una carpeta temporal (nunca toca tus archivos reales).</summary>
public sealed class FileToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kroko-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public FileToolTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static ToolArgs Args(object o) => new(JsonSerializer.SerializeToElement(o));

    private static JsonElement Data(ToolOutcome o)
    {
        Assert.True(o.Ok, o.Error);
        return JsonSerializer.SerializeToElement(o.Data);
    }

    private string Touch(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Rutas_conocidas_se_resuelven()
    {
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), PathResolver.Resolve("escritorio"));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "facturas"),
            PathResolver.Resolve("Documentos\\facturas"));
        Assert.Throws<ToolException>(() => PathResolver.Resolve("carpeta-sin-ruta"));
    }

    [Fact]
    public void Zonas_protegidas_se_rechazan()
    {
        Assert.Throws<ToolException>(() => PathGuard.EnsureWritable(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.Throws<ToolException>(() => PathGuard.EnsureWritable(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "algo")));
        Assert.Throws<ToolException>(() => PathGuard.EnsureWritable("C:\\"));
        PathGuard.EnsureWritable(_root); // carpeta normal: OK
    }

    [Fact]
    public async Task Crear_renombrar_y_listar()
    {
        await new CrearCarpetaTool().RunAsync(Args(new { ruta = Path.Combine(_root, "Nueva") }), default);
        var f = Touch("informe.docx");

        var r = Data(await new RenombrarTool().RunAsync(Args(new { ruta = f, nuevo_nombre = "informe final" }), default));
        Assert.Equal("informe final.docx", r.GetProperty("ahora").GetString()); // conserva la extensión

        var list = Data(await new ListarArchivosTool().RunAsync(Args(new { ruta = _root }), default));
        Assert.Equal(1, list.GetProperty("totalCarpetas").GetInt32());
        Assert.Equal(1, list.GetProperty("totalArchivos").GetInt32());
    }

    [Fact]
    public async Task Mover_nunca_sobrescribe()
    {
        Touch("destino/foto.jpg", "vieja");
        var nueva = Touch("foto.jpg", "nueva");

        Data(await new MoverArchivosTool().RunAsync(Args(new { origenes = new[] { nueva }, destino = Path.Combine(_root, "destino") }), default));

        Assert.Equal("vieja", File.ReadAllText(Path.Combine(_root, "destino", "foto.jpg")));
        Assert.Equal("nueva", File.ReadAllText(Path.Combine(_root, "destino", "foto (2).jpg")));
        Assert.False(File.Exists(nueva));
    }

    [Fact]
    public async Task Copiar_carpeta_completa()
    {
        Touch("proyecto/a.txt");
        Touch("proyecto/sub/b.txt");

        Data(await new CopiarArchivosTool().RunAsync(Args(new { origenes = new[] { Path.Combine(_root, "proyecto") }, destino = Path.Combine(_root, "copia") }), default));

        Assert.True(File.Exists(Path.Combine(_root, "copia", "proyecto", "sub", "b.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "proyecto", "a.txt"))); // el original sigue ahí
    }

    [Fact]
    public async Task No_mete_una_carpeta_dentro_de_si_misma()
    {
        Touch("caja/a.txt");
        var res = await new MoverArchivosTool().RunAsync(Args(new { origenes = new[] { Path.Combine(_root, "caja") }, destino = Path.Combine(_root, "caja", "dentro") }), default);
        Assert.False(res.Ok);
    }

    [Fact]
    public async Task Buscar_por_nombre_y_por_contenido()
    {
        Touch("docs/factura-enero.txt", "Total: 150 soles");
        Touch("docs/otra/factura-febrero.txt", "Total: 99 dólares");
        Touch("docs/notas.txt", "nada");

        var porNombre = Data(await new BuscarArchivosTool().RunAsync(Args(new { carpeta = _root, patron = "factura" }), default));
        Assert.Equal(2, porNombre.GetProperty("encontrados").GetInt32());

        var porContenido = Data(await new BuscarArchivosTool().RunAsync(Args(new { carpeta = _root, patron = "*.txt", contenido = "soles" }), default));
        Assert.Equal(1, porContenido.GetProperty("encontrados").GetInt32());
    }

    [Fact]
    public async Task Ordenar_por_tipo_deja_accesos_directos_y_registra()
    {
        Touch("foto.png");
        Touch("tesis.pdf");
        Touch("cancion.mp3");
        Touch("raro.xyz");
        Touch("Chrome.lnk");

        var res = Data(await new OrdenarCarpetaTool().RunAsync(Args(new { ruta = _root, criterio = "tipo" }), default));

        Assert.Equal(4, res.GetProperty("movidos").GetInt32());
        Assert.True(File.Exists(Path.Combine(_root, "Imágenes", "foto.png")));
        Assert.True(File.Exists(Path.Combine(_root, "Documentos", "tesis.pdf")));
        Assert.True(File.Exists(Path.Combine(_root, "Música", "cancion.mp3")));
        Assert.True(File.Exists(Path.Combine(_root, "Otros", "raro.xyz")));
        Assert.True(File.Exists(Path.Combine(_root, "Chrome.lnk"))); // los accesos directos se quedan

        var registro = res.GetProperty("registro").GetString()!;
        Assert.True(File.Exists(registro));
        File.Delete(registro);
    }

    [Fact]
    public async Task Abrir_archivo_no_ejecuta_programas()
    {
        var exe = Touch("instalador.exe");
        var res = await new AbrirArchivoTool().RunAsync(Args(new { ruta = exe }), default);
        Assert.False(res.Ok);
    }

    [Theory]
    [InlineData("excel", "microsoft excel", 80)]
    [InlineData("excel", "excel", 100)]
    [InlineData("spotify", "spotify music", 85)]
    [InlineData("word", "wordpad", 65)]
    [InlineData("xyz", "excel", 0)]
    public void Puntuacion_de_nombres_de_programa(string query, string candidate, int expected) =>
        Assert.Equal(expected, ProgramLocator.Score(ProgramLocator.Normalize(query), ProgramLocator.Normalize(candidate)));
}
