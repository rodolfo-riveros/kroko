using System.Runtime.InteropServices;
using System.Text;

namespace Kroko.App.Native;

/// <summary>
/// Llamadas directas a Windows (user32, shcore, kernel32) que WPF no expone.
/// Todas las coordenadas aquí son píxeles físicos de pantalla.
/// </summary>
public static partial class NativeMethods
{
    public const int GWL_EXSTYLE = -20;

    /// <summary>No aparece en Alt+Tab ni en la barra de tareas.</summary>
    public const long WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>No roba el foco al aparecer ni al hacer clic.</summary>
    public const long WS_EX_NOACTIVATE = 0x08000000;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    public const uint MONITOR_DEFAULTTONULL = 0;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong32(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    public static long GetWindowLong(nint hWnd, int index) =>
        Environment.Is64BitProcess ? GetWindowLongPtr64(hWnd, index) : GetWindowLong32(hWnd, index);

    public static void SetWindowLong(nint hWnd, int index, long value)
    {
        if (Environment.Is64BitProcess)
            SetWindowLongPtr64(hWnd, index, (nint)value);
        else
            SetWindowLong32(hWnd, index, (int)value);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(POINT pt, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    public static partial nint GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder name, int max);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static bool TryGetMonitorInfo(nint hMonitor, out MONITORINFOEX info)
    {
        info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
        return hMonitor != 0 && GetMonitorInfo(hMonitor, ref info);
    }

    /// <summary>Escala de un monitor (1.0 = 100 %, 1.5 = 150 %…).</summary>
    public static double GetMonitorScale(nint hMonitor)
    {
        try
        {
            if (GetDpiForMonitor(hMonitor, 0, out var dx, out _) == 0 && dx > 0)
                return dx / 96.0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 1.0;
    }

    public static string GetClassName(nint hWnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }
}
