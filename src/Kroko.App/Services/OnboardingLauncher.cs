using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kroko.App.Services;

/// <summary>
/// La primera vez (mientras Kroko no sepa quién es su dueño) abre solo el asistente "Configurar Kroko"
/// en cuanto el cerebro conecta. Espera si hay un juego o vídeo a pantalla completa para no interrumpir.
/// </summary>
public sealed class OnboardingLauncher(BrainConnection brain, BrainApiClient api, WindowService windows,
    ILogger<OnboardingLauncher> log) : IHostedService
{
    private int _checked;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        brain.ConnectionChanged += OnConnectionChanged;
        if (brain.IsConnected) OnConnectionChanged(true);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        brain.ConnectionChanged -= OnConnectionChanged;
        return Task.CompletedTask;
    }

    private void OnConnectionChanged(bool connected)
    {
        // Una sola comprobación por arranque
        if (!connected || Interlocked.Exchange(ref _checked, 1) == 1) return;
        _ = Task.Run(CheckAsync);
    }

    private async Task CheckAsync()
    {
        try
        {
            var profile = await api.GetProfileAsync();
            if (profile.Completo) return;

            log.LogInformation("Kroko aún no conoce a su dueño: abriendo el asistente de bienvenida");
            var watcher = Application.Current.Dispatcher.Invoke(() => new FullscreenWatcher(() => 0));
            while (Application.Current.Dispatcher.Invoke(watcher.IsFullscreenNow))
                await Task.Delay(TimeSpan.FromSeconds(5));
            watcher.Dispose();

            await Task.Delay(TimeSpan.FromSeconds(2)); // que Kroko termine de despertar
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (!windows.IsOnboardingOpen) windows.ShowOnboarding();
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogInformation("No se pudo comprobar el perfil del dueño ({Message})", ex.Message);
            Interlocked.Exchange(ref _checked, 0); // se reintenta en la próxima conexión
        }
    }
}
