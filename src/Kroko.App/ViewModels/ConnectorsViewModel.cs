using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kroko.App.Services;

namespace Kroko.App.ViewModels;

/// <summary>
/// Conectores de Kroko (navegador, Notion, Google…): estado y botón para conectarlos.
/// Se actualiza solo cada 2 segundos mientras la ventana está abierta.
/// </summary>
public partial class ConnectorsViewModel : ObservableObject, IDisposable
{
    private readonly BrainApiClient _api;
    private readonly DispatcherTimer _refresh;
    private readonly HashSet<string> _awaitingAuth = new(StringComparer.OrdinalIgnoreCase);

    public ConnectorsViewModel(BrainApiClient api)
    {
        _api = api;
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refresh.Tick += async (_, _) => await LoadAsync();
    }

    public ObservableCollection<ConnectorDto> Connectors { get; } = [];

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    public async Task StartAsync()
    {
        await LoadAsync();
        _refresh.Start();
    }

    private async Task LoadAsync()
    {
        try
        {
            var list = await _api.GetConnectorsAsync();
            // Modo servidor: el permiso se da en el navegador de esta PC
            foreach (var c in list.Where(c => c.AuthUrl is { Length: > 0 } && _awaitingAuth.Contains(c.Nombre)))
            {
                _awaitingAuth.Remove(c.Nombre);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(c.AuthUrl!) { UseShellExecute = true })?.Dispose();
            }
            Connectors.Clear();
            foreach (var c in list) Connectors.Add(c);
            Status = $"{list.Count(c => c.IsConnected)} de {list.Count} conectados";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Status = "No pude hablar con el cerebro de Kroko. ¿Está encendido?";
        }
    }

    [RelayCommand]
    private async Task ConnectAsync(ConnectorDto c)
    {
        try
        {
            _awaitingAuth.Add(c.Nombre);
            await _api.ConnectAsync(c.Nombre);
            Status = $"Conectando {c.Titulo}… si se abre el navegador, pulsa \"Permitir\".";
        }
        catch (HttpRequestException ex)
        {
            Status = $"No se pudo conectar: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync(ConnectorDto c)
    {
        try
        {
            await _api.DisconnectAsync(c.Nombre);
            await LoadAsync();
        }
        catch (HttpRequestException ex)
        {
            Status = $"No se pudo desconectar: {ex.Message}";
        }
    }

    public void Dispose() => _refresh.Stop();
}
