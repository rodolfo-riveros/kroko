using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Kroko.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kroko.App.Services;

/// <summary>
/// Mientras el Brain no viva en un servidor, la App lo lanza como proceso hijo (sección 13).
/// Si ya hay un Brain respondiendo en la URL configurada, no lanza otro.
/// El Brain recibe --parent-pid y se cierra solo si la App desaparece.
/// </summary>
public sealed class BrainLauncher(IOptions<KrokoOptions> options, ILogger<BrainLauncher> log) : IHostedService
{
    private Process? _process;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var opt = options.Value;
        // La App instalada solo usa el cerebro del servidor; el local es para desarrollo
        if (!opt.LaunchLocalBrain || UpdateService.IsInstalled) return;

        var url = new Uri(opt.BrainUrl);
        if (!url.IsLoopback)
        {
            log.LogInformation("El Brain es remoto ({Url}); no se lanza uno local", url);
            return;
        }

        if (await IsHealthyAsync(url, cancellationToken))
        {
            log.LogInformation("Ya hay un Brain funcionando en {Url}", url);
            return;
        }

        var exe = FindBrainExecutable(opt.BrainExecutablePath);
        if (exe is null)
        {
            log.LogWarning("No encontré Kroko.Brain.exe. Arráncalo a mano o indica BrainExecutablePath en appsettings.json");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--parent-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add("--urls");
            psi.ArgumentList.Add(url.GetLeftPart(UriPartial.Authority));

            _process = Process.Start(psi);
            log.LogInformation("Brain local lanzado: {Exe} (pid {Pid})", exe, _process?.Id);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.LogError(ex, "No se pudo lanzar el Brain local");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                log.LogDebug("El Brain ya se había cerrado: {Message}", ex.Message);
            }
        }
        _process?.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<bool> IsHealthyAsync(Uri baseUrl, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };
        try
        {
            using var res = await http.GetAsync(new Uri(baseUrl, KrokoDefaults.HealthPath), ct);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Busca el ejecutable: ruta configurada → carpeta "Brain" junto a la App (instalación) →
    /// carpeta de compilación del repo (desarrollo).
    /// </summary>
    private static string? FindBrainExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured), AppContext.BaseDirectory);
            return File.Exists(full) ? full : null;
        }

        var installed = Path.Combine(AppContext.BaseDirectory, "Brain", "Kroko.Brain.exe");
        if (File.Exists(installed)) return installed;

        // Desarrollo: ...\src\Kroko.App\bin\<Config>\<tfm>\  →  ...\src\Kroko.Brain\bin\<Config>\<tfm sin -windows>\
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var tfm = dir.Name.Replace("-windows", "", StringComparison.OrdinalIgnoreCase);
        var config = dir.Parent?.Name;
        var src = dir.Parent?.Parent?.Parent?.Parent;
        if (src is not null && config is not null)
        {
            var dev = Path.Combine(src.FullName, "Kroko.Brain", "bin", config, tfm, "Kroko.Brain.exe");
            if (File.Exists(dev)) return dev;
        }
        return null;
    }
}
