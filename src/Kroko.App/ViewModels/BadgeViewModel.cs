using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Kroko.App.Services;
using Kroko.Shared.Protocol;

namespace Kroko.App.ViewModels;

/// <summary>
/// Lo que se ve justo debajo de Kroko (14/11/2026, como en el Troko-Desk): un check verde con una palabra después de cada
/// acción («Anotado», «Agendado»…) y la cuenta atrás del enfoque o temporizador aunque el panel esté cerrado.
/// </summary>
public sealed partial class BadgeViewModel : ObservableObject
{
    private readonly DispatcherTimer _clear;
    private readonly PanelViewModel _panel;

    public BadgeViewModel(BrainConnection brain, TimerService timers, PanelViewModel panel)
    {
        Timers = timers;
        _panel = panel;
        _clear = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _clear.Tick += (_, _) =>
        {
            _clear.Stop();
            BadgeText = "";
        };
        brain.MessageReceived += m =>
        {
            if (m is BadgeMessage { Text.Length: > 0 } b)
                Application.Current?.Dispatcher.BeginInvoke(() => ShowBadge(b.Text));
        };
    }

    public TimerService Timers { get; }

    [ObservableProperty]
    public partial string BadgeText { get; set; } = "";

    /// <summary>Escondido por el usuario o en pantalla completa: no se muestra nada.</summary>
    public bool IsSuppressed => _panel.IsSuppressed;

    public bool HasAnything => (BadgeText.Length > 0 || Timers.IsActive) && !IsSuppressed;

    public void ShowBadge(string text)
    {
        BadgeText = text;
        _clear.Stop();
        _clear.Start();
    }
}
