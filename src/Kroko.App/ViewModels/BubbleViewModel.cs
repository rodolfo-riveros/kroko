using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kroko.App.Face;
using Kroko.App.Services;

namespace Kroko.App.ViewModels;

/// <summary>
/// Estado y acciones de la burbuja. La cara (FaceAnimator) la gobierna CompanionBehavior;
/// la conversación, PanelViewModel.
/// </summary>
public partial class BubbleViewModel(CompanionBehavior behavior, PanelViewModel panel, WindowService windows,
    EmergencyHotkey emergency, VoiceController voice)
    : ObservableObject
{
    public CompanionBehavior Behavior { get; } = behavior;

    public FaceAnimator Face => Behavior.Face;

    public void OnClick()
    {
        // Sin cerebro, al tocarlo explica qué pasa (sección 12)
        if (!Behavior.IsBrainAvailable) panel.ExplainOffline();
        Behavior.OnClick();
    }

    /// <summary>Escondido por el usuario: el panel no sale (Kroko sigue escuchando «Oye Kroko»).</summary>
    public void SetPanelSuppressed(bool suppressed) => panel.SetSuppressed(suppressed);

    /// <summary>Pantalla completa: el panel se esconde y «Oye Kroko» descansa (salvo que se pida lo contrario).</summary>
    public void SetFullscreen(bool fullscreen)
    {
        panel.SetSuppressed(fullscreen);
        voice.SetFullscreen(fullscreen);
    }

    /// <summary>El panel quiere mostrarse (respuesta, aviso, recordatorio): la burbuja debe estar visible.</summary>
    public event Action<bool> PanelShowRequested
    {
        add => panel.ShowRequested += value;
        remove => panel.ShowRequested -= value;
    }

    /// <summary>Doble clic: abre el panel para escribirle.</summary>
    public void OnDoubleClick()
    {
        Behavior.WakeUp();
        panel.OpenForInput();
    }

    /// <summary>Lo mismo que el atajo de voz.</summary>
    [RelayCommand]
    private void Talk() => voice.Toggle();

    [RelayCommand]
    private void Write() => OnDoubleClick();

    [RelayCommand]
    private void OpenGallery() => windows.ShowGallery();

    [RelayCommand]
    private void OpenAudit() => windows.ShowAudit();

    [RelayCommand]
    private void OpenMemory() => windows.ShowMemory();

    [RelayCommand]
    private void OpenConnectors() => windows.ShowConnectors();

    [RelayCommand]
    private void OpenOnboarding() => windows.ShowOnboarding();

    [RelayCommand]
    private void OpenMyDay() => windows.ShowMyDay();

    public void OpenMyDayFromTray() => windows.ShowMyDay();

    [RelayCommand]
    private void OpenSettings() => windows.ShowSettings();

    /// <summary>Mostrar/ocultar la burbuja (bandeja del sistema).</summary>
    public event Action? VisibilityToggleRequested;

    public void ToggleVisible() => VisibilityToggleRequested?.Invoke();

    public void TalkFromTray() => voice.Toggle();

    public void TogglePauseFromTray() => emergency.Toggle();

    /// <summary>Igual que Ctrl+Alt+K: pausa (o reanuda) a Kroko.</summary>
    [RelayCommand]
    private void TogglePause() => emergency.Toggle();

    [RelayCommand]
    private void Sleep() => Behavior.SleepNow();

    [RelayCommand]
    private void Wake() => Behavior.WakeUp();

    [RelayCommand]
    private static void Exit() => Application.Current.Shutdown();
}
