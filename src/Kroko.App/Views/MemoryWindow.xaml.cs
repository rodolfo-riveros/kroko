using System.Windows;
using Kroko.App.ViewModels;

namespace Kroko.App.Views;

public partial class MemoryWindow : Window
{
    public MemoryWindow(MemoryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadAsync();
    }
}
