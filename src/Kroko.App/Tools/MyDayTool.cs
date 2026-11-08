using System.Windows;
using Kroko.App.Services;

namespace Kroko.App.Tools;

/// <summary>"Muéstrame mi día": abre la ventana Mi día en la PC.</summary>
public sealed class MostrarMiDiaTool(WindowService windows) : ILocalTool
{
    public string Name => "mostrar_mi_dia";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        Application.Current.Dispatcher.Invoke(windows.ShowMyDay);
        return Task.FromResult(ToolOutcome.Success(new { abierta = "Mi día" }));
    }
}
