using System.Windows;
using System.Windows.Input;
using Kroko.App.ViewModels;

namespace Kroko.App.Views;

/// <summary>
/// Configuración estilo macOS (17/11/2026). Aquí solo hay lógica de ventana: arrastrar, los tres botones de color y
/// cargar los datos al abrir. Todo lo demás vive en SettingsViewModel (y en OnboardingViewModel para perfil y cuentas).
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            await viewModel.Account.LoadForSettingsAsync();
            await viewModel.CheckBrainAsync();
            await viewModel.LoadNoticesAsync();
            await viewModel.LoadUsageAsync();
            await viewModel.Connectors.StartAsync();
        };
        Closed += (_, _) => viewModel.Dispose();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private void Drag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Maximize_Click(sender, e);
        else if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Profile_Click(object sender, RoutedEventArgs e) =>
        ((SettingsViewModel)DataContext).SelectedSection = Array.IndexOf(SettingsViewModel.Sections, "perfil");
}
