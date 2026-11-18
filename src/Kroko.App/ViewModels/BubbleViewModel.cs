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
    EmergencyHotkey emergency, VoiceController voice, BadgeViewModel badge)
    : ObservableObject
{
    public CompanionBehavior Behavior { get; } = behavior;

    public FaceAnimator Face => Behavior.Face;

    // ---------------------------------------------------------------- la isla de arriba (17/11/2026)

    /// <summary>Lo que dice la isla al lado de la cara: «Listo», «Te escucho…», «✓ Anotado», «12:30 · Enfoque»…</summary>
    [ObservableProperty]
    public partial string IslandText { get; set; } = "Listo";

    /// <summary>La línea es un check verde (acaba de hacer algo).</summary>
    [ObservableProperty]
    public partial bool IslandCheck { get; set; }

    /// <summary>Hay algo que contar (no está en reposo): la isla se ensancha para que se lea.</summary>
    [ObservableProperty]
    public partial bool IslandBusy { get; set; }

    /// <summary>Kroko tiene algo que mostrar ahora (escucha, piensa, habla, un check, el enfoque, su panel): la isla sale.</summary>
    public bool IslandWanted { get; private set; }

    /// <summary>Hay conexión con el cerebro (la luz de la isla, lima o gris).</summary>
    [ObservableProperty]
    public partial bool IslandOnline { get; set; }

    /// <summary>Se llama varias veces por segundo desde la isla.</summary>
    public void RefreshIsland()
    {
        var (text, check, busy) = IslandStatus();
        if (IslandText != text) IslandText = text;
        if (IslandCheck != check) IslandCheck = check;
        if (IslandBusy != busy) IslandBusy = busy;
        if (IslandOnline != Behavior.IsBrainAvailable) IslandOnline = Behavior.IsBrainAvailable;
        IslandWanted = panel.IsActiveForIsland || badge.BadgeText.Length > 0 || badge.Timers.IsActive || windows.IsIslandPanelOpen || windows.IsDropOpen || DragOver;
    }

    private (string Text, bool Check, bool Busy) IslandStatus()
    {
        if (DragOver) return ("Suéltalo aquí", false, true);
        if (badge.BadgeText.Length > 0) return (badge.BadgeText, true, true);
        if (panel.IsListening) return ("Te escucho…", false, true);
        if (panel.IsThinking) return ("Pensando…", false, true);
        if (panel.IsTyping) return ("Hablando", false, true);
        if (badge.Timers.IsActive)
            return (string.IsNullOrWhiteSpace(badge.Timers.Label) ? badge.Timers.RemainingText : $"{badge.Timers.RemainingText} · {badge.Timers.Label}", false, true);
        if (!Behavior.IsBrainAvailable) return ("Sin conexión", false, true);
        if (Behavior.IsSleeping) return ("Durmiendo", false, false);
        return ("Listo", false, false);
    }

    /// <summary>Hay archivos arrastrándose sobre Kroko (la isla se ensancha y dice «Suéltalo aquí»).</summary>
    public bool DragOver { get; set; }

    /// <summary>Soltaste archivos sobre Kroko: abre el panel Convertir / Analizar / Ordenar / Enviar.</summary>
    public void DropFiles(IReadOnlyList<string> files)
    {
        DragOver = false;
        if (files.Count == 0) return;
        Behavior.WakeUp();
        Behavior.PlayExpression("surprised");
        windows.ShowDrop(files);
    }

    /// <summary>Clic en la isla: se despliega el panel «Kroko» pegado a ella.</summary>
    public void OpenIsland()
    {
        Behavior.WakeUp();
        if (!Behavior.IsBrainAvailable) panel.ExplainOffline();
        windows.ToggleIslandPanel();
    }

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

    /// <summary>El panel «Kroko» en una sección (inicio, habitos, tareas, enfoque, mipc, ajustes).</summary>
    [RelayCommand]
    private void OpenPanel(string? section) => windows.ShowPanel(section);

    /// <summary>Lo mismo que decirle «descansa»: se calla y se esconde.</summary>
    [RelayCommand]
    private void Rest() => voice.Rest();

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
