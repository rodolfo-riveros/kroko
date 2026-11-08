using System.Windows.Interop;
using Kroko.App.Native;
using Microsoft.Extensions.Logging;

namespace Kroko.App.Services;

/// <summary>
/// Interruptor de emergencia (sección 8): Ctrl+Alt+K desde cualquier programa detiene toda acción
/// en curso y pone a Kroko en pausa. Pulsarlo otra vez lo reanuda.
/// Usa una ventana invisible solo para mensajes (no aparece en ningún sitio).
/// </summary>
public sealed class EmergencyHotkey(ToolExecutor executor, ILogger<EmergencyHotkey> log) : IDisposable
{
    private const int HotkeyId = 0x4B52;
    private const uint VK_K = 0x4B;

    private HwndSource? _source;

    public bool IsRegistered { get; private set; }

    public void Start()
    {
        _source = new HwndSource(new HwndSourceParameters("KrokoHotkeys")
        {
            ParentWindow = new nint(-3), // HWND_MESSAGE: ventana solo de mensajes
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        });
        _source.AddHook(WndProc);

        IsRegistered = NativeMethods.RegisterHotKey(_source.Handle, HotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, VK_K);
        if (IsRegistered) log.LogInformation("Interruptor de emergencia listo: Ctrl+Alt+K");
        else log.LogWarning("No se pudo registrar Ctrl+Alt+K (otro programa lo usa). Usa el menú de Kroko para pausar.");
    }

    /// <summary>Lo mismo que pulsar Ctrl+Alt+K (también desde el menú).</summary>
    public void Toggle()
    {
        if (executor.IsPaused) executor.Resume();
        else _ = executor.EmergencyStopAsync();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam == HotkeyId)
        {
            handled = true;
            Toggle();
        }
        return 0;
    }

    public void Dispose()
    {
        if (_source is null) return;
        if (IsRegistered) NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
    }
}
