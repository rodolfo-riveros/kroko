using Velopack;

namespace Kroko.App;

/// <summary>
/// Punto de entrada. Velopack (instalador y autoactualización, sección 13) tiene que ejecutarse
/// antes que nada: gestiona la instalación, desinstalación y aplicación de actualizaciones.
/// </summary>
public static class Program
{
    /// <summary>Versión a la que se acaba de actualizar (para que Kroko lo cuente al arrancar).</summary>
    public static string? JustUpdatedTo { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnRestarted(v => JustUpdatedTo = v.ToString())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
