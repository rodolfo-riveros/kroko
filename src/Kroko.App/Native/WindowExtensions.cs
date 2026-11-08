using System.Windows;
using System.Windows.Interop;

namespace Kroko.App.Native;

internal static class WindowExtensions
{
    /// <summary>
    /// Convierte la ventana en "ventana de herramienta" que no roba el foco:
    /// fuera de Alt+Tab y sin activarse al mostrarse o al recibir clics.
    /// Debe llamarse en SourceInitialized (cuando ya existe el handle nativo).
    /// </summary>
    public static void MakeToolWindowNoActivate(this Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, style);
    }
}
