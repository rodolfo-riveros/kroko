using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kroko.App.Services;
using Kroko.App.ViewModels;

namespace Kroko.App.Views;

/// <summary>La isla «Kroko 2» (18/11/2026). Solo dibujo y arrastrar/soltar: el estado viene de IslandViewModel.</summary>
public partial class IslandView : UserControl
{
    public IslandView() => InitializeComponent();

    private IslandViewModel? Vm => DataContext as IslandViewModel;

    /// <summary>Para las capturas: dibuja la cara ya, sin esperar al refresco de pantalla.</summary>
    internal void RenderFaceNow() => Face.RenderNow();

    internal static string[] Files(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] f
            ? [.. f.Where(System.IO.File.Exists)]
            : [];

    // ---------------------------------------------------------------- bolsillos

    private static readonly Brush Lime = new SolidColorBrush(Color.FromRgb(0xB6, 0xF2, 0x3A));
    private static readonly Brush LimeBack = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1D, 0x12));
    private static readonly Brush Edge = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x32));
    private static readonly Brush Back = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0C));

    private void Pocket_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is Border b)
        {
            b.BorderBrush = Lime;
            b.Background = LimeBack;
        }
        Pocket_DragOver(sender, e);
    }

    private void Pocket_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = Files(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Pocket_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is not Border b) return;
        b.BorderBrush = Edge;
        b.Background = Back;
    }

    private async void Pocket_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        Pocket_DragLeave(sender, e);
        if (Vm is { } vm) await vm.DropAsync(Files(e), (sender as FrameworkElement)?.Tag as string);
    }

    // ---------------------------------------------------------------- «Arrástralo a donde quieras» (resultado)

    private Point _press;
    private bool _pressed;

    private void ResultFile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _press = e.GetPosition(this);
        _pressed = true;
    }

    private void ResultFile_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed || Vm?.ResultData?.Path is not { } path) return;
        var d = e.GetPosition(this) - _press;
        if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
        _pressed = false;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
    }
}
