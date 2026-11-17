using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Kroko.App.Views;

/// <summary>
/// Vidrio de verdad para las ventanas de Kroko (17/11/2026, pedido por Pol: «el estilo Mac es más Liquid Glass»).
/// En Windows 11 (22H2 o más nuevo) se usa el fondo acrílico del sistema: el escritorio se ve detrás, desenfocado, y
/// encima va un tinte oscuro (<c>Kroko.GlassTint</c>) para que las letras blancas siempre se lean. En Windows 10, o si
/// el sistema no lo permite, el tinte pasa a ser negro sólido. Uso: <c>Glass.Enabled="True"</c> (lo pone Kroko.MacWindow).
/// </summary>
public static partial class Glass
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(Glass), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    /// <summary>Negro sólido cuando no hay vidrio (Windows 10).</summary>
    private static readonly Brush SolidTint = Freeze(new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x14)));

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window w || e.NewValue is not true) return;
        if (new WindowInteropHelper(w).Handle != 0) Apply(w);
        else w.SourceInitialized += (_, _) => Apply(w);
    }

    private static void Apply(Window w)
    {
        // Una ventana con transparencia por píxel (la del panel colgado de la isla) no puede llevar el fondo del sistema
        if (w.AllowsTransparency || !TryUseAcrylic(w)) w.Resources["Kroko.GlassTint"] = SolidTint;
    }

    /// <summary>Pide a Windows el fondo acrílico, modo oscuro y esquinas redondeadas. false = no se pudo.</summary>
    public static bool TryUseAcrylic(Window w)
    {
        if (Environment.OSVersion.Version.Build < 22621) return false; // el fondo del sistema llegó en Windows 11 22H2
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == 0 || HwndSource.FromHwnd(hwnd) is not { CompositionTarget: { } target }) return false;
        try
        {
            target.BackgroundColor = Colors.Transparent;
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0) return false;
            var dark = 1;
            _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));       // DWMWA_USE_IMMERSIVE_DARK_MODE
            var round = 2;
            _ = DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));      // DWMWA_WINDOW_CORNER_PREFERENCE = redondas
            var acrylic = 3;
            return DwmSetWindowAttribute(hwnd, 38, ref acrylic, sizeof(int)) == 0; // DWMWA_SYSTEMBACKDROP_TYPE = acrílico
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
