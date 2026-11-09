using System.Windows;
using Kroko.App.ViewModels;

namespace Kroko.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            await viewModel.CheckBrainAsync();
            await viewModel.LoadUsageAsync();
        };
        Closed += (_, _) => viewModel.Dispose();
    }
}
