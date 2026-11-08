using System.Diagnostics;

namespace Kroko.Brain.Agent;

/// <summary>
/// Cuando la App lanza el Brain como proceso hijo (--parent-pid N), el Brain se cierra solo
/// si la App desaparece (incluso si se colgó), para no dejar procesos huérfanos.
/// </summary>
public sealed class ParentProcessWatcher(IConfiguration config, IHostApplicationLifetime lifetime, ILogger<ParentProcessWatcher> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!int.TryParse(config["parent-pid"], out var pid)) return;

        Process parent;
        try
        {
            parent = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            log.LogWarning("El proceso padre {Pid} ya no existe; cerrando", pid);
            lifetime.StopApplication();
            return;
        }

        log.LogInformation("Vigilando a la App (pid {Pid})", pid);
        try
        {
            await parent.WaitForExitAsync(stoppingToken);
            log.LogInformation("La App se cerró; el Brain también se cierra");
            lifetime.StopApplication();
        }
        catch (OperationCanceledException) { }
        finally
        {
            parent.Dispose();
        }
    }
}
