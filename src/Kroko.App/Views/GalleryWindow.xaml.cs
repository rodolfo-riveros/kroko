using System.Windows;
using Kroko.App.ViewModels;

namespace Kroko.App.Views;

public partial class GalleryWindow : Window
{
    public GalleryWindow(GalleryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }
}
