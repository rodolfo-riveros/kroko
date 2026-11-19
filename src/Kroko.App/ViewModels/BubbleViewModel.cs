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

    // La isla «Kroko 2» vive en IslandViewModel (18/11/2026); aquí queda la burbuja y lo común.

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

    /// <summary>Clic derecho → «Volver arriba al centro» (Kroko libre o burbuja): vuelve a la isla y se reinicia.</summary>
    [RelayCommand]
    private static void ReturnToTop()
    {
        var k = UserSettings.LoadKrokoSection();
        k["Look"] = "isla";
        UserSettings.SaveKrokoSection(k);
        AppRestart.Now();
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
        // En la isla no hay burbuja para escribir: se escribe en el chat del panel Kroko
        if (windows.IsIslandMode) windows.ShowPanel("chat");
        else panel.OpenForInput();
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
