using System.Windows;
using System.Windows.Interop;
using Kroko.App.Native;
using Kroko.App.Services;

namespace Kroko.App.Views;

/// <summary>Dónde se abren los paneles que cuelgan de Kroko (soltar archivos, reproductor).</summary>
public static class HangingPanel
{
    /// <summary>
    /// Con la isla: debajo y centrado. Con la burbuja o Kroko libre: hacia el lado con más espacio (arriba o abajo)
    /// y sin salirse del área de trabajo.
    /// </summary>
    public static void Place(Window window, BubbleGeometry geometry)
    {
        if (!geometry.IsKnown) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        var s = geometry.MonitorScale;
        var w = (int)Math.Round(window.ActualWidth * s);
        var h = (int)Math.Round(window.ActualHeight * s);
        var body = geometry.Body;
        var work = geometry.WorkArea;
        var gap = (int)Math.Round(8 * s);
        var left = (body.Left + body.Right) / 2 - w / 2;
        int top;
        if (geometry.IsIsland) top = body.Bottom + (int)Math.Round(6 * s);
        else
        {
            var above = body.Top - work.Top;
            var below = work.Bottom - body.Bottom;
            top = above >= h + gap || above >= below ? body.Top - h - gap : body.Bottom + gap;
        }
        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - w));
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - h));
        NativeMethods.SetWindowPos(hwnd, 0, left, top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }
}
