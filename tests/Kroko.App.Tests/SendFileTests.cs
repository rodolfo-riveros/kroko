using System.IO;
using System.Text.Json;
using Kroko.App.Tools;

namespace Kroko.App.Tests;

public class SendFileTests
{
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    [Theory]
    [InlineData(@"C:\Users\Pol\Documents\proyecto\.env")]
    [InlineData(@"C:\Users\Pol\Documents\proyecto\.env.production")]
    [InlineData(@"C:\Users\Pol\.ssh\id_rsa")]
    [InlineData(@"C:\Users\Pol\Desktop\id_ed25519")]
    [InlineData(@"C:\Users\Pol\Documents\claves.kdbx")]
    [InlineData(@"C:\Users\Pol\Documents\certificado.pfx")]
    [InlineData(@"C:\Users\Pol\Documents\server.pem")]
    public void Nunca_envia_contrasenas_ni_llaves(string path) =>
        Assert.Throws<ToolException>(() => FileSendGuard.EnsureSendable(path));

    [Fact]
    public void Nunca_envia_datos_de_Kroko_ni_de_los_navegadores()
    {
        Assert.Throws<ToolException>(() => FileSendGuard.EnsureSendable(Path.Combine(Local, "Kroko", "kroko.db")));
        Assert.Throws<ToolException>(() => FileSendGuard.EnsureSendable(Path.Combine(Local, "Google", "Chrome", "User Data", "Default", "Login Data")));
        Assert.Throws<ToolException>(() => FileSendGuard.EnsureSendable(Path.Combine(Local, "Microsoft", "Edge", "User Data", "Default", "Bookmarks")));
    }

    [Theory]
    [InlineData(@"C:\Users\Pol\Documents\contrato.pdf")]
    [InlineData(@"C:\Users\Pol\Desktop\foto vacaciones.jpg")]
    [InlineData(@"C:\Users\Pol\Downloads\informe.xlsx")]
    [InlineData(@"C:\Users\Pol\Documents\environment.docx")] // "env" en el nombre no basta para bloquear
    public void Los_documentos_normales_si_se_envian(string path) => FileSendGuard.EnsureSendable(path);

    [Fact]
    public async Task Lee_el_archivo_y_lo_entrega_para_Telegram()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kroko-envio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "informe.pdf");
            await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
            var args = new ToolArgs(JsonSerializer.SerializeToElement(new { ruta = file, mensaje = "El informe" }));

            var outcome = await new EnviarArchivoTool().RunAsync(args, default);
            Assert.True(outcome.Ok);
            var json = JsonSerializer.SerializeToElement(outcome.Data);
            Assert.Equal("informe.pdf", json.GetProperty("nombre").GetString());
            Assert.Equal("application/pdf", json.GetProperty("mimeType").GetString());
            Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }), json.GetProperty("archivoBase64").GetString());

            // Una carpeta no se envía: pide el archivo concreto
            var folder = new ToolArgs(JsonSerializer.SerializeToElement(new { ruta = dir }));
            await Assert.ThrowsAsync<ToolException>(() => new EnviarArchivoTool().RunAsync(folder, default));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
