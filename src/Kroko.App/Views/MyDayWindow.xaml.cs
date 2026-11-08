using System.Windows;
using Kroko.App.ViewModels;

namespace Kroko.App.Views;

public partial class MyDayWindow : Window
{
    public MyDayWindow(MyDayViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadAsync();
    }
}
