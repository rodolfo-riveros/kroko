using System.Windows;
using Kroko.App.Services;
using Kroko.App.ViewModels;

namespace Kroko.App.Tools;

/// <summary>"Abre tu configuración", "quiero vincular mi Google", "¿qué sabes de mí?": abre la ventana de Kroko que toca.</summary>
public sealed class AbrirVentanaTool(WindowService windows) : ILocalTool
{
    public string Name => "abrir_ventana";

    private static readonly Dictionary<string, (string Title, Action<WindowService> Open)> Windows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["asistente"] = ("Configurar Kroko", w => w.ShowOnboarding()),
        ["conectores"] = ("Conectores", w => w.ShowConnectors()),
        ["memoria"] = ("Lo que sabe de ti", w => w.ShowMemory()),
        ["registro"] = ("Registro de acciones", w => w.ShowAudit()),
        ["galeria"] = ("Galería de expresiones", w => w.ShowGallery()),
        ["mi_dia"] = ("Mi día", w => w.ShowMyDay()),
    };

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var which = args.String("ventana").Trim();
        string title;
        Action open;

        if (which.Equals("configuracion", StringComparison.OrdinalIgnoreCase))
        {
            var section = args.OptionalString("seccion")?.Trim();
            int? index = null;
            if (!string.IsNullOrEmpty(section))
            {
                var i = Array.FindIndex(SettingsViewModel.Sections, s => s.Equals(section, StringComparison.OrdinalIgnoreCase));
                if (i < 0) throw new ToolException($"No tengo la sección \"{section}\". Hay: {string.Join(", ", SettingsViewModel.Sections)}.");
                index = i;
            }
            title = index is { } n ? $"Ajustes ({SettingsViewModel.Sections[n]})" : "Ajustes";
            open = () => windows.ShowSettings(index);
        }
        else if (Windows.TryGetValue(which, out var entry))
        {
            title = entry.Title;
            open = () => entry.Open(windows);
        }
        else
        {
            throw new ToolException($"No conozco la ventana \"{which}\".");
        }

        Application.Current.Dispatcher.Invoke(open);
        return Task.FromResult(ToolOutcome.Success(new { abierta = title }));
    }
}
