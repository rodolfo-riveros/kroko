using System.IO;
using System.Windows;
using System.Windows.Threading;
using Kroko.App.Audio;
using Kroko.App.Face;
using Kroko.App.Services;
using Kroko.App.Tools;
using Kroko.App.ViewModels;
using Kroko.App.Views;
using Kroko.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Kroko.App;

/// <summary>
/// Punto de arranque: monta el contenedor de dependencias, los logs y muestra la burbuja.
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            await StartCoreAsync(e);
        }
        catch (Exception ex)
        {
            // Si el arranque falla, nunca quedarse abierto e invisible: avisar y salir.
            Log.Fatal(ex, "Kroko no pudo arrancar");
            if (!e.Args.Contains("--snapshot"))
            {
                MessageBox.Show("Kroko no pudo arrancar. Los detalles están en %LOCALAPPDATA%\\Kroko\\logs.",
                    "Kroko", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Shutdown(1);
        }
    }

    private async Task StartCoreAsync(StartupEventArgs e)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", "logs");

        _host = Host.CreateDefaultBuilder(e.Args)
            .UseContentRoot(AppContext.BaseDirectory)
            // Ajustes de Pol (ventana de Ajustes) por encima de appsettings.json
            .ConfigureAppConfiguration(c => c.AddJsonFile(UserSettings.FilePath, optional: true, reloadOnChange: false))
            .UseSerilog((ctx, cfg) => cfg
                .ReadFrom.Configuration(ctx.Configuration)
                .Enrich.FromLogContext()
                .WriteTo.File(Path.Combine(logDir, "app-.log"),
                    rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14))
            .ConfigureServices((ctx, services) =>
            {
                services.Configure<KrokoOptions>(ctx.Configuration.GetSection(KrokoOptions.Section));
                // La dirección del cerebro se escribe a mano (Ajustes → Cerebro): siempre se usa en su forma canónica
                services.PostConfigure<KrokoOptions>(o =>
                {
                    var address = BrainAddress.Normalize(o.BrainUrl);
                    o.BrainUrl = address.Ok ? address.Url : KrokoDefaults.BrainUrl;
                });
                services.AddSingleton<ExpressionLibrary>();
                services.AddSingleton<ISoundService, SoundService>();
                services.AddSingleton<WindowPlacementStore>();
                services.AddSingleton<BubbleGeometry>();
                services.AddHostedService<BrainLauncher>();
                services.AddSingleton<BrainConnection>();
                services.AddHostedService(sp => sp.GetRequiredService<BrainConnection>());
                services.AddSingleton<MicrophoneStream>();
                services.AddSingleton<VoskModelManager>();
                services.AddSingleton<WakeWordService>();
                services.AddSingleton<VoiceRecorder>();
                services.AddSingleton<SpeechPlayer>();
                services.AddSingleton<LocalTts>();
                services.AddSingleton<VoiceSession>();
                services.AddSingleton<TimerService>();
                services.AddSingleton<FocusService>();
                services.AddSingleton<PanelViewModel>();
                services.AddSingleton<LivePlayer>();
                services.AddSingleton<LiveConversation>();
                services.AddSingleton<VoiceController>();
                services.AddSingleton<AuditLog>();
                services.AddSingleton<ProgramLocator>();
                services.AddSingleton<ForegroundTracker>();
                services.AddSingleton<UiAutomationSession>();
                foreach (var type in typeof(ILocalTool).Assembly.GetTypes()
                             .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ILocalTool).IsAssignableFrom(t)))
                    services.AddSingleton(typeof(ILocalTool), type);
                services.AddSingleton<DocumentIndex>();
                services.AddSingleton<DocumentIndexer>();
                services.AddHostedService(sp => sp.GetRequiredService<DocumentIndexer>());
                services.AddSingleton<ToolExecutor>();
                services.AddSingleton<EmergencyHotkey>();
                services.AddTransient<AuditWindow>();
                services.AddSingleton<BrainApiClient>();
                services.AddTransient<MemoryViewModel>();
                services.AddTransient<MemoryWindow>();
                services.AddTransient<ConnectorsViewModel>();
                services.AddTransient<ConnectorsWindow>();
                services.AddTransient<OnboardingViewModel>();
                services.AddTransient<OnboardingWindow>();
                services.AddTransient<MyDayViewModel>();
                services.AddTransient<MyDayWindow>();
                services.AddHostedService<OnboardingLauncher>();
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<SettingsWindow>();
                services.AddSingleton<TrayService>();
                services.AddSingleton<UpdateService>();
                services.AddSingleton<ResponsePanel>();
                services.AddSingleton<CompanionBehavior>();
                services.AddSingleton<WindowService>();
                services.AddSingleton<BubbleViewModel>();
                services.AddSingleton<BubbleWindow>();
                services.AddTransient<GalleryViewModel>();
                services.AddTransient<GalleryWindow>();
            })
            .Build();

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        await _host.StartAsync();
        Log.Information("Kroko.App arrancando");

        // Herramienta de desarrollo: --snapshot <carpeta> [segundos...] dibuja todas las caras en PNG y sale.
        var snap = Array.IndexOf(e.Args, "--snapshot");
        if (snap >= 0)
        {
            var dir = e.Args.Length > snap + 1 ? e.Args[snap + 1] : Path.Combine(AppContext.BaseDirectory, "snapshots");
            var lib = _host.Services.GetRequiredService<ExpressionLibrary>();
            var times = e.Args.Skip(snap + 2)
                .Select(a => double.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1)
                .Where(v => v >= 0).DefaultIfEmpty(0.6);
            foreach (var t in times)
                Log.Information("Captura guardada en {Path}", FaceSnapshot.RenderSheet(lib, dir, t));
            Log.Information("Captura guardada en {Path}", PanelSnapshot.Render(dir));
            Log.Information("Captura guardada en {Path}", MemoryWindowSnapshot.Render(dir));
            Log.Information("Captura guardada en {Path}", ConnectorsWindowSnapshot.Render(dir));
            Log.Information("Captura guardada en {Path}", MyDayWindowSnapshot.Render(dir));
            foreach (var p in OnboardingWindowSnapshot.Render(_host.Services, dir)) Log.Information("Captura guardada en {Path}", p);
            foreach (var p in SettingsWindowSnapshot.Render(_host.Services, dir)) Log.Information("Captura guardada en {Path}", p);
            Shutdown();
            return;
        }

        var bubble = _host.Services.GetRequiredService<BubbleWindow>();
        bubble.Show();
        _host.Services.GetRequiredService<ResponsePanel>(); // se suscribe al panel; se muestra cuando haga falta
        _host.Services.GetRequiredService<ToolExecutor>();  // escucha las herramientas que pide el Brain
        _host.Services.GetRequiredService<ForegroundTracker>(); // recuerda tu última ventana (Fase 4)
        _host.Services.GetRequiredService<AuditLog>().LoadRecent();
        _host.Services.GetRequiredService<EmergencyHotkey>().Start();
        _host.Services.GetRequiredService<VoiceController>().Start();

        var bubbleVm = _host.Services.GetRequiredService<BubbleViewModel>();
        _host.Services.GetRequiredService<TrayService>().Start(bubbleVm.TalkFromTray, bubbleVm.ToggleVisible,
            bubbleVm.TogglePauseFromTray, () => _host.Services.GetRequiredService<WindowService>().ShowSettings());
        _ = _host.Services.GetRequiredService<UpdateService>().RunAsync(_host.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().ApplicationStopping);
        if (Program.JustUpdatedTo is { } version)
            _host.Services.GetRequiredService<PanelViewModel>().ShowNotification("Kroko", $"¡Me actualicé a la versión {version}! Ya tengo las mejoras nuevas.", "success", null);
        _host.Services.GetRequiredService<CompanionBehavior>().Start();

        if (e.Args.Contains("--gallery"))
            _host.Services.GetRequiredService<WindowService>().ShowGallery();

        // Herramienta de desarrollo: --say "texto" envía una orden en cuanto haya conexión con el Brain.
        var say = Array.IndexOf(e.Args, "--say");
        if (say >= 0 && e.Args.Length > say + 1)
        {
            var text = e.Args[say + 1];
            var brain = _host.Services.GetRequiredService<BrainConnection>();
            var panel = _host.Services.GetRequiredService<PanelViewModel>();
            for (var i = 0; i < 60 && !brain.IsConnected; i++) await Task.Delay(250);
            panel.InputText = text;
            if (panel.SendCommand.CanExecute(null)) await panel.SendCommand.ExecuteAsync(null);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("Kroko.App cerrándose");
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Nunca mostramos excepciones crudas al usuario: se registran y Kroko sigue vivo.
        Log.Error(e.Exception, "Error no controlado en la interfaz");
        e.Handled = true;
    }
}
