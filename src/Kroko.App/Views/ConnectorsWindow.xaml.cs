using System.Windows;
using Kroko.App.ViewModels;

namespace Kroko.App.Views;

public partial class ConnectorsWindow : Window
{
    public ConnectorsWindow(ConnectorsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.StartAsync();
        Closed += (_, _) => viewModel.Dispose();
    }
}
