using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kroko.App.Services;

namespace Kroko.App.ViewModels;

/// <summary>Ventana "Mi día": todo lo de hoy en una vista (agenda, tareas, recordatorios, hábitos y clima).</summary>
public sealed partial class MyDayViewModel(BrainApiClient api) : ObservableObject
{
    public ObservableCollection<MyDayEventDto> Agenda { get; } = [];
    public ObservableCollection<MyDayTaskDto> Tareas { get; } = [];
    public ObservableCollection<MyDayReminderDto> Recordatorios { get; } = [];
    public ObservableCollection<MyDayHabitDto> Habitos { get; } = [];
    public ObservableCollection<string> Avisos { get; } = [];

    [ObservableProperty]
    public partial string Fecha { get; set; } = "";

    [ObservableProperty]
    public partial string Saludo { get; set; } = "Cargando tu día…";

    [ObservableProperty]
    public partial MyDayWeatherDto? Clima { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            if (await api.GetMyDayAsync() is { } d) Apply(d);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Saludo = "No pude hablar con mi cerebro";
            Avisos.Clear();
            Avisos.Add("Revisa la conexión y pulsa Actualizar.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal void Apply(MyDayDto d)
    {
        Fecha = d.Fecha;
        Saludo = d.Saludo;
        Clima = d.Clima;
        Fill(Agenda, d.Agenda);
        Fill(Tareas, d.Tareas);
        Fill(Recordatorios, d.Recordatorios);
        Fill(Habitos, d.Habitos);
        Fill(Avisos, d.Avisos);
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}
