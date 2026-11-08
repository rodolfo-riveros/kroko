using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kroko.App.Services;
using Microsoft.Extensions.Logging;

namespace Kroko.App.ViewModels;

/// <summary>
/// "Lo que Kroko sabe de ti": recuerdos, rutinas y buzón de mejoras. Todo se puede borrar.
/// </summary>
public partial class MemoryViewModel(BrainApiClient api, ILogger<MemoryViewModel> log) : ObservableObject
{
    private string _suggestionsFile = "";

    public ObservableCollection<MemoryDto> Recuerdos { get; } = [];
    public ObservableCollection<RoutineDto> Rutinas { get; } = [];
    public ObservableCollection<SuggestionDto> Mejoras { get; } = [];

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        Status = "Cargando…";
        try
        {
            var snap = await api.GetMemoryAsync();
            Replace(Recuerdos, snap.Recuerdos);
            Replace(Rutinas, snap.Rutinas);
            Replace(Mejoras, snap.Mejoras);
            _suggestionsFile = snap.Buzon;
            Status = $"{Recuerdos.Count} recuerdos · {Rutinas.Count} rutinas · {Mejoras.Count(m => !m.IsDone)} mejoras pendientes";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("No se pudo leer la memoria del Brain: {Message}", ex.Message);
            Status = "No pude hablar con el cerebro de Kroko. ¿Está encendido?";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task ForgetAsync(MemoryDto item) => DeleteAsync("recuerdos", item.Id, () => Recuerdos.Remove(item));

    [RelayCommand]
    private Task DeleteRoutineAsync(RoutineDto item) => DeleteAsync("rutinas", item.Id, () => Rutinas.Remove(item));

    [RelayCommand]
    private Task DeleteSuggestionAsync(SuggestionDto item) => DeleteAsync("mejoras", item.Id, () => Mejoras.Remove(item));

    [RelayCommand]
    private async Task MarkDoneAsync(SuggestionDto item)
    {
        try
        {
            await api.MarkDoneAsync(item.Id);
            await LoadAsync();
        }
        catch (HttpRequestException ex)
        {
            Status = $"No se pudo marcar: {ex.Message}";
        }
    }

    /// <summary>Kroko olvida la conversación (los recuerdos y rutinas se quedan).</summary>
    [RelayCommand]
    private async Task ClearConversationAsync()
    {
        try
        {
            await api.ClearConversationAsync();
            Status = "Conversación olvidada. Los recuerdos y rutinas se mantienen.";
        }
        catch (HttpRequestException ex)
        {
            Status = $"No se pudo borrar: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenSuggestionsFile()
    {
        if (File.Exists(_suggestionsFile))
            Process.Start(new ProcessStartInfo(_suggestionsFile) { UseShellExecute = true })?.Dispose();
        else
            Status = "Todavía no hay buzón: Kroko aún no ha anotado ninguna mejora.";
    }

    private async Task DeleteAsync(string kind, long id, Action removeLocal)
    {
        try
        {
            await api.DeleteAsync(kind, id);
            removeLocal();
            Status = "Borrado.";
        }
        catch (HttpRequestException ex)
        {
            Status = $"No se pudo borrar: {ex.Message}";
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}
