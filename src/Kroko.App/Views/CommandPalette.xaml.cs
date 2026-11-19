using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Kroko.App.Native;
using Kroko.App.Services;
using Kroko.App.ViewModels;

namespace Kroko.App.Views;

/// <summary>Barra de comandos (Kroko 2): sale arriba al centro, bajo la isla; Esc o clic fuera la cierran.</summary>
public partial class CommandPalette : Window
{
    private readonly CommandPaletteViewModel _vm;
    private readonly BubbleGeometry _geometry;
    private bool _closing;

    public CommandPalette(CommandPaletteViewModel viewModel, BubbleGeometry geometry)
    {
        InitializeComponent();
        _vm = viewModel;
        _geometry = geometry;
        DataContext = viewModel;
        viewModel.CloseRequested += SafeClose;
        SourceInitialized += (_, _) => NativeMethods.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            HangingPanel.Place(this, _geometry);
            QueryBox.Focus();
        };
        SizeChanged += (_, _) => HangingPanel.Place(this, _geometry);
        Deactivated += (_, _) => SafeClose();
    }

    public void Open()
    {
        _vm.Open();
        Show();
        Activate();
        QueryBox.Focus();
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.BeginInvoke(Close);
    }

    private void QueryBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: SafeClose(); break;
            case Key.Down: _vm.Move(1); break;
            case Key.Up: _vm.Move(-1); break;
            case Key.Enter: _vm.Execute(); break;
            case Key.Tab: _vm.AskTyped(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CommandRow row) _vm.Execute(row);
    }
}
