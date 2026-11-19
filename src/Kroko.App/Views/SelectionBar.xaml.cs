using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Kroko.App.Native;
using Kroko.App.ViewModels;

namespace Kroko.App.Views;

/// <summary>Mini barra junto al cursor para el texto que seleccionaste (Ctrl+Shift+K). La respuesta sale en el panel de Kroko.</summary>
public partial class SelectionBar : Window
{
    private readonly PanelViewModel _chat;
    private string _text = "";
    private bool _closing;

    public SelectionBar(PanelViewModel chat)
    {
        InitializeComponent();
        _chat = chat;
        SourceInitialized += (_, _) => NativeMethods.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => PlaceAtCursor();
        Deactivated += (_, _) => SafeClose();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            SafeClose();
        };
    }

    public void Open(string text)
    {
        _text = text;
        Preview.Text = "«" + text.ReplaceLineEndings(" ") + "»";
        Show();
        Activate();
    }

    private void PlaceAtCursor()
    {
        if (!NativeMethods.GetCursorPos(out var c)) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(hwnd, 0, c.X - 40, c.Y + 18, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.BeginInvoke(Close);
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        var prompt = ((FrameworkElement)sender).Tag switch
        {
            "traducir" => "Tradúceme este texto al español (o al inglés si ya está en español). Solo la traducción:",
            "explicar" => "Explícame en palabras simples este texto:",
            "corregir" => "Corrige la ortografía y la redacción de este texto sin cambiar lo que dice. Devuélvemelo corregido y nada más:",
            _ => "Redáctame una respuesta breve y amable a este mensaje:",
        };
        SafeClose();
        _chat.InputText = $"{prompt}\n\n{_text}";
        if (_chat.SendCommand.CanExecute(null)) _chat.SendCommand.Execute(null);
    }
}
