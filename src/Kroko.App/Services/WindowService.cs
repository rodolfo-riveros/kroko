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
    private SettingsWindow? _settings;
    private MyDayWindow? _myDay;
    private KrokoPanelWindow? _panel;
    private DropPanel? _drop;

    private MusicPlayer? _player;

    public bool IsPlayerOpen => _player is { IsLoaded: true };

    /// <summary>El reproductor grande que cuelga de la isla (clic en la portada).</summary>
    public void ShowPlayer()
    {
        if (_player is { IsLoaded: true })
        {
            _player.CloseAnimated();
            return;
        }
        _player = services.GetRequiredService<MusicPlayer>();
        _player.Closed += (_, _) => _player = null;
        _player.Show();
        _player.Activate();
    }

    /// <summary>Soltaste archivos sobre Kroko: el panel Convertir / Analizar / Ordenar / Enviar.</summary>
    public void ShowDrop(IEnumerable<string> files, string? mode = null) => Drop().Open(files, mode);

    /// <summary>Lo que copiaste, al celular: enlace o texto en un QR; una imagen, como archivo por tu Wi-Fi.</summary>
    public void ShowClipboardPhone(ClipItem clip)
    {
        if (clip.Image is { } img) ShowDrop([ClipboardWatcher.SaveImage(img)], "celular");
        else Drop().OpenText(clip.Kind == "enlace" ? "Enlace al celular" : "Texto al celular", clip.Text);
    }

    private DropPanel Drop()
    {
        if (_drop is not { IsLoaded: true })
        {
            _drop = services.GetRequiredService<DropPanel>();
            _drop.Closed += (_, _) => _drop = null;
        }
        return _drop;
    }

    /// <summary>
    /// El panel «Kroko» (14/11/2026): inicio, tareas, hábitos, enfoque, mi PC y ajustes simples. section = una de
    /// KrokoPanelViewModel.Sections (null = inicio).
    /// </summary>
    public void ShowPanel(string? section = null)
    {
        var geometry = services.GetRequiredService<BubbleGeometry>();
        if (_panel is not { IsLoaded: true })
        {
            _panel = services.GetRequiredService<KrokoPanelWindow>();
            // Kroko vive en la isla de arriba: el panel se despliega desde ella (17/11/2026)
            if (geometry.IsIsland) _panel.AttachToIsland(geometry);
            _panel.Closed += (_, _) =>
            {
                _panelClosedAt = DateTime.Now;
                _panel?.ViewModel.Dispose();
                _panel = null;
            };
            _panel.ViewModel.OpenSection(section);
            _panel.Show();
        }
        else
        {
            _panel.ViewModel.OpenSection(section);
            if (_panel.WindowState == WindowState.Minimized) _panel.WindowState = WindowState.Normal;
        }
        _panel.Activate();
    }

    private DateTime _panelClosedAt;

    /// <summary>El panel «Kroko» está desplegado desde la isla.</summary>
    public bool IsIslandPanelOpen => _panel is { IsLoaded: true, IsAttached: true, IsClosing: false };
    public bool IsDropOpen => _drop is { IsLoaded: true };

    /// <summary>Clic en la isla: abre el panel o, si ya está desplegado, lo recoge.</summary>
    public void ToggleIslandPanel()
    {
        if (_panel is { IsLoaded: true, IsAttached: true } open)
        {
            if (!open.IsClosing) open.CloseAnimated();
            return;
        }
        // El mismo clic en la isla acaba de cerrarlo (perdió el foco): no volver a abrirlo
        if ((DateTime.Now - _panelClosedAt).TotalMilliseconds < 400) return;
        ShowPanel("inicio");
    }

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

    // Kroko 2 (18/11/2026): ya no hay asistente ni ventana de conectores aparte; todo vive en Configuración.
    public const int SectionProfile = 4, SectionAccounts = 5, SectionAiKeys = 6, SectionBrain = 8;

    public bool IsSettingsOpen => _settings is { IsLoaded: true };

    /// <summary>Lo que antes abría el asistente «Configurar Kroko»: Configuración en Tu perfil.</summary>
    public void ShowOnboarding() => ShowSettings(SectionProfile);

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

    /// <summary>Notion, Todoist y navegador: Configuración → Cuentas y apps.</summary>
    public void ShowConnectors() => ShowSettings(SectionAccounts);

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
