using System.Windows;
using Kroko.App.ViewModels;

namespace Kroko.App.Views;

/// <summary>El panel «Kroko»: Inicio, Tareas, Hábitos, Enfoque, Mi PC y Ajustes (14/11/2026).</summary>
public partial class KrokoPanelWindow : Window
{
    public KrokoPanelWindow(KrokoPanelViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            await viewModel.RefreshAsync();
            await viewModel.LoadNoticesAsync();
        };
        Activated += async (_, _) => await viewModel.RefreshAsync();
    }

    public KrokoPanelViewModel ViewModel => (KrokoPanelViewModel)DataContext;
}
