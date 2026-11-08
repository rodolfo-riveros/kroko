using System.Diagnostics;
using System.IO;
using System.Windows;
using Kroko.App.Services;

namespace Kroko.App.Views;

public partial class AuditWindow : Window
{
    public AuditWindow(AuditLog audit)
    {
        InitializeComponent();
        DataContext = audit;
        Loaded += (_, _) => audit.LoadRecent(); // incluye lo último que hizo el Brain
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AuditLog.FolderPath);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AuditLog.FolderPath}\"") { UseShellExecute = true })?.Dispose();
    }
}
