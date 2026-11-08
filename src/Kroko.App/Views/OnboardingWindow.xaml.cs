using System.Windows;
using Kroko.App.ViewModels;

namespace Kroko.App.Views;

public partial class OnboardingWindow : Window
{
    public OnboardingWindow(OnboardingViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
        Loaded += async (_, _) => await viewModel.StartAsync();
        Closed += (_, _) => viewModel.Dispose();
    }
}
