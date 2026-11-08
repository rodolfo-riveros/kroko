using System.Windows.Threading;
using Kroko.App.Native;

namespace Kroko.App.Services;

/// <summary>
/// Detecta si hay una app a pantalla completa (juego, presentación, vídeo) en el mismo monitor
/// que Kroko, para que se esconda y vuelva al salir.
/// </summary>
public sealed class FullscreenWatcher : IDisposable
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    private readonly Func<nint> _ownWindow;
    private readonly DispatcherTimer _timer;
    private bool _isFullscreen;

    public FullscreenWatcher(Func<nint> ownWindow)
    {
        _ownWindow = ownWindow;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(800) };
        _timer.Tick += (_, _) => Check();
    }

    /// <summary>true = hay pantalla completa en el monitor de Kroko.</summary>
    public event Action<bool>? Changed;

    /// <summary>Empieza a vigilar y devuelve si YA hay pantalla completa (para no aparecer ni un instante).</summary>
    public bool StartAndCheck()
    {
        _isFullscreen = Detect();
        _timer.Start();
        return _isFullscreen;
    }

    /// <summary>Comprobación puntual, sin empezar a vigilar.</summary>
    public bool IsFullscreenNow() => Detect();

    private void Check()
    {
        var now = Detect();
        if (now == _isFullscreen) return;
        _isFullscreen = now;
        Changed?.Invoke(now);
    }

    private bool Detect()
    {
        var fg = NativeMethods.GetForegroundWindow();
        var own = _ownWindow();
        if (fg == 0 || fg == own || fg == NativeMethods.GetShellWindow() || fg == NativeMethods.GetDesktopWindow())
            return false;
        if (ShellClasses.Contains(NativeMethods.GetClassName(fg)))
            return false;

        var fgMonitor = NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONULL);
        if (fgMonitor == 0) return false;
        if (own != 0 && NativeMethods.MonitorFromWindow(own, NativeMethods.MONITOR_DEFAULTTONEAREST) != fgMonitor)
            return false;

        if (!NativeMethods.GetWindowRect(fg, out var r) || !NativeMethods.TryGetMonitorInfo(fgMonitor, out var mi))
            return false;

        var m = mi.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    public void Dispose() => _timer.Stop();
}
