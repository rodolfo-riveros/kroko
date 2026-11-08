using System.Windows;
using Kroko.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Kroko.App.Services;

/// <summary>Abre ventanas secundarias sin que los ViewModels conozcan las vistas.</summary>
public sealed class WindowService(IServiceProvider services)
{
    private GalleryWindow? _gallery;

    private AuditWindow? _audit;
    private MemoryWindow? _memory;
    private ConnectorsWindow? _connectors;
    private SettingsWindow? _settings;
    private OnboardingWindow? _onboarding;
    private MyDayWindow? _myDay;

    public void ShowMyDay()
    {
        if (_myDay is { IsLoaded: true })
        {
            _myDay.Activate();
            _ = ((ViewModels.MyDayViewModel)_myDay.DataContext).LoadAsync();
            return;
        }
        _myDay = services.GetRequiredService<MyDayWindow>();
        _myDay.Closed += (_, _) => _myDay = null;
        _myDay.Show();
        _myDay.Activate();
    }

    public bool IsOnboardingOpen => _onboarding is { IsLoaded: true };

    public void ShowOnboarding()
    {
        if (_onboarding is { IsLoaded: true })
        {
            _onboarding.Activate();
            return;
        }
        _onboarding = services.GetRequiredService<OnboardingWindow>();
        _onboarding.Closed += (_, _) => _onboarding = null;
        _onboarding.Show();
        _onboarding.Activate();
    }

    /// <param name="section">Pestaña a mostrar (0 General … 5 Cuentas y apps); null = la de siempre.</param>
    public void ShowSettings(int? section = null)
    {
        if (_settings is { IsLoaded: true })
        {
            if (section is { } open) ((ViewModels.SettingsViewModel)_settings.DataContext).SelectedSection = open;
            if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
            _settings.Activate();
            return;
        }
        _settings = services.GetRequiredService<SettingsWindow>();
        if (section is { } s) ((ViewModels.SettingsViewModel)_settings.DataContext).SelectedSection = s;
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    public void ShowConnectors()
    {
        if (_connectors is { IsLoaded: true })
        {
            _connectors.Activate();
            return;
        }
        _connectors = services.GetRequiredService<ConnectorsWindow>();
        _connectors.Closed += (_, _) => _connectors = null;
        _connectors.Show();
        _connectors.Activate();
    }

    public void ShowMemory()
    {
        if (_memory is { IsLoaded: true })
        {
            _memory.Activate();
            return;
        }
        _memory = services.GetRequiredService<MemoryWindow>();
        _memory.Closed += (_, _) => _memory = null;
        _memory.Show();
        _memory.Activate();
    }

    public void ShowAudit()
    {
        if (_audit is { IsLoaded: true })
        {
            _audit.Activate();
            return;
        }
        _audit = services.GetRequiredService<AuditWindow>();
        _audit.Closed += (_, _) => _audit = null;
        _audit.Show();
        _audit.Activate();
    }

    public void ShowGallery()
    {
        if (_gallery is { IsLoaded: true })
        {
            if (_gallery.WindowState == WindowState.Minimized) _gallery.WindowState = WindowState.Normal;
            _gallery.Activate();
            return;
        }

        _gallery = services.GetRequiredService<GalleryWindow>();
        _gallery.Closed += (_, _) => _gallery = null;
        _gallery.Show();
        _gallery.Activate();
    }
}
