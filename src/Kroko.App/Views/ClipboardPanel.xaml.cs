using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using FlaUI.Core.WindowsAPI;
using Kroko.App.Native;
using Kroko.App.Services;
using Kroko.App.Tools;
using Kroko.App.ViewModels;

namespace Kroko.App.Views;

/// <summary>
/// Portapapeles (Kroko 2, 18/11/2026): lo que copiaste (solo en memoria) con Pegar, Resumir, Traducir, Extraer texto y
/// Al celular. Pegar vuelve a la ventana donde estabas y pega ahí (Ctrl+V).
/// </summary>
public partial class ClipboardPanel : Window
{
    private readonly ClipboardWatcher _clipboard;
    private readonly IslandViewModel _island;
    private readonly ForegroundTracker _foreground;
    private readonly BubbleGeometry _geometry;
    private bool _closing;

    public ClipboardPanel(ClipboardWatcher clipboard, IslandViewModel island, ForegroundTracker foreground, BubbleGeometry geometry)
    {
        InitializeComponent();
        _clipboard = clipboard;
        _island = island;
        _foreground = foreground;
        _geometry = geometry;
        List.ItemsSource = clipboard.History;
        Empty.Visibility = clipboard.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SourceInitialized += (_, _) => NativeMethods.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => HangingPanel.Place(this, _geometry);
        SizeChanged += (_, _) => HangingPanel.Place(this, _geometry);
        Deactivated += (_, _) => SafeClose();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            SafeClose();
        };
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.BeginInvoke(Close);
    }

    private static ClipItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipItem;

    private void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { TemplatedParent: System.Windows.Controls.Button }) return;
        if (ItemOf(sender) is { } item) Paste(item);
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemOf(sender) is { } item) Paste(item);
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemOf(sender) is not { } item || (sender as FrameworkElement)?.Tag is not string action) return;
        SafeClose();
        _island.RunClip(item, action);
    }

    /// <summary>Lo pone en el portapapeles, vuelve a tu ventana y pega.</summary>
    private void Paste(ClipItem item)
    {
        _clipboard.IgnoreNext();
        if (item.Image is { } img) Clipboard.SetImage(img);
        else Clipboard.SetText(item.Text);
        SafeClose();
        var target = _foreground.LastExternalWindow;
        if (target == 0) return;
        ForegroundTracker.SetForegroundWindow(target);
        _ = Task.Delay(150).ContinueWith(_ => FlaUI.Core.Input.Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V), TaskScheduler.Default);
    }
}
