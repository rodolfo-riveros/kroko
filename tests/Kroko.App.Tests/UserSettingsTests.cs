using System.IO;
using System.Text.Json.Nodes;
using Kroko.App.Services;

namespace Kroko.App.Tests;

/// <summary>
/// "Guardar y reiniciar" en Ajustes fallaba siempre que settings.json ya existía ("The node already has a parent"),
/// así que no se guardaba nada (08/11/2026).
/// </summary>
public class UserSettingsTests
{
    [Fact]
    public void Se_puede_guardar_varias_veces_seguidas()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kroko-ajustes-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "settings.json");
        try
        {
            var k = UserSettings.LoadKrokoSection(file); // aún no existe
            k["BrainUrl"] = "http://localhost:5080";
            UserSettings.SaveKrokoSection(k, file);

            // Segunda vez: se lee lo guardado, se cambia y se guarda (antes aquí reventaba)
            var again = UserSettings.LoadKrokoSection(file);
            again["BrainUrl"] = "https://kroko.midominio.com";
            again["Voice"] = new JsonObject { ["RepliesWithVoice"] = false };
            UserSettings.SaveKrokoSection(again, file);
            UserSettings.SaveKrokoSection(again, file); // y otra vez con el mismo objeto

            var saved = UserSettings.LoadKrokoSection(file);
            Assert.Equal("https://kroko.midominio.com", saved["BrainUrl"]!.GetValue<string>());
            Assert.False(saved["Voice"]!["RepliesWithVoice"]!.GetValue<bool>());
            Assert.False(File.Exists(file + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Un_archivo_roto_no_impide_guardar()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kroko-ajustes-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "settings.json");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(file, "{ esto no es json");
            var k = UserSettings.LoadKrokoSection(file);
            Assert.Empty(k);
            k["SoundsEnabled"] = true;
            UserSettings.SaveKrokoSection(k, file);
            Assert.True(UserSettings.LoadKrokoSection(file)["SoundsEnabled"]!.GetValue<bool>());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
