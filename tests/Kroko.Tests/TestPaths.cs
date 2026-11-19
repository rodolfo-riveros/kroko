using System.Runtime.CompilerServices;

namespace Kroko.Tests;

/// <summary>Rutas temporales para que las pruebas nunca toquen la memoria real de Kroko.</summary>
internal static class TestPaths
{
    /// <summary>
    /// Se ejecuta al cargar el ensamblado de pruebas (antes que cualquier prueba): nunca se lee el .env real
    /// ni el archivo de claves cifradas real del dueño.
    /// </summary>
    [ModuleInitializer]
    internal static void Isolate()
    {
        Environment.SetEnvironmentVariable("KROKO_DISABLE_DOTENV", "1");
        Environment.SetEnvironmentVariable("KROKO_SECRETS_FILE",
            Path.Combine(Path.GetTempPath(), "kroko-brain-tests", "secretos-" + Guid.NewGuid().ToString("N") + ".dat"));
    }

    /// <summary>La carpeta del repositorio (donde está Kroko.slnx).</summary>
    public static string RepoRoot => FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kroko.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("No encontré Kroko.slnx");
        }
    }

    public static Dictionary<string, string?> IsolatedMemory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kroko-brain-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new Dictionary<string, string?>
        {
            ["Memory:DatabasePath"] = Path.Combine(dir, "kroko.db"),
            ["Memory:SuggestionsFile"] = Path.Combine(dir, "buzon.md"),
            ["Logging:WriteToFile"] = "false",
            ["Mcp:Enabled"] = "false", // las pruebas nunca lanzan navegadores ni conectan servicios reales
            ["Productivity:DailySummaryHour"] = "-1", // ni mandan el resumen del día en mitad de una prueba
            ["Proactive:Enabled"] = "false", // ni revisan calendarios o correos reales
            ["Productivity:SeedCoach"] = "false", // ni corre el asistente de las 7:30, 13:00 y 20:00 en mitad de una prueba
        };
    }

    /// <summary>Perfil del dueño en una base de datos temporal.</summary>
    public static Kroko.Brain.Owner.OwnerProfileStore Owner() =>
        new(new Kroko.Brain.Memory.KrokoDatabase(
                Microsoft.Extensions.Options.Options.Create(new Kroko.Brain.Memory.MemoryOptions { DatabasePath = IsolatedMemory()["Memory:DatabasePath"] }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Kroko.Brain.Memory.KrokoDatabase>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Kroko.Brain.Owner.OwnerProfileStore>.Instance);
}
