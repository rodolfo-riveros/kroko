using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kroko.App.Services;
using NAudio.CoreAudioApi;

namespace Kroko.App.ViewModels;

/// <summary>Una barrita de la onda de progreso (lima lo que ya sonó).</summary>
public sealed partial class WaveBar(double height) : ObservableObject
{
    public double Height { get; } = height;
    [ObservableProperty] public partial bool Played { get; set; }
}

/// <summary>Una salida de audio (parlantes, audífonos…).</summary>
public sealed record AudioOutput(string Name, bool IsDefault);

/// <summary>
/// El reproductor grande que cuelga de la isla (diseño Kroko 2, 18/11/2026): portada, título, onda de progreso,
/// aleatorio / anterior / play / siguiente / repetir, volumen; a la derecha Recientes y Salida de audio, «Apagar en» y
/// «Bajar el volumen cuando Kroko habla». Spotify no comparte su cola ni las letras con Windows: no se inventan.
/// </summary>
public sealed partial class MusicPlayerViewModel : ObservableObject, IDisposable
{
    private readonly MediaSessionService _media;
    private readonly MusicDucker _ducker;
    private readonly DispatcherTimer _tick;
    private bool _settingVolume;
    private string? _barsFor;

    public MusicPlayerViewModel(MediaSessionService media, MusicDucker ducker)
    {
        _media = media;
        _ducker = ducker;
        _media.Changed += OnChanged;
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += async (_, _) => await _media.PollAsync();
        _tick.Start();
        _settingVolume = true;
        Volume = MediaSessionService.Volume;
        _settingVolume = false;
        Ducking = ducker.Enabled;
        SleepMode = media.SleepMode;
        LoadOutputs();
        Apply();
    }

    public ObservableCollection<MediaSessionService.Snapshot> Recent => _media.Recent;
    public ObservableCollection<WaveBar> Bars { get; } = [];
    public ObservableCollection<AudioOutput> Outputs { get; } = [];

    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Artist { get; set; } = "";
    [ObservableProperty] public partial bool Playing { get; set; }
    [ObservableProperty] public partial bool HasMedia { get; set; }
    [ObservableProperty] public partial System.Windows.Media.ImageSource? Cover { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string PositionText { get; set; } = "";
    [ObservableProperty] public partial string DurationText { get; set; } = "";
    [ObservableProperty] public partial int Volume { get; set; }
    [ObservableProperty] public partial bool Shuffle { get; set; }
    [ObservableProperty] public partial bool Repeat { get; set; }
    [ObservableProperty] public partial string Tab { get; set; } = "recientes";
    [ObservableProperty] public partial string SleepMode { get; set; } = "no";
    [ObservableProperty] public partial bool Ducking { get; set; }
    public bool HasNoRecent => Recent.Count == 0;

    partial void OnVolumeChanged(int value)
    {
        if (!_settingVolume) MediaSessionService.Volume = value;
    }

    partial void OnDuckingChanged(bool value)
    {
        if (value != _ducker.Enabled) _ducker.SetEnabled(value);
    }

    private void OnChanged() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(Apply);

    private void Apply()
    {
        var n = _media.Now;
        HasMedia = n.HasMedia;
        Title = n.HasMedia ? n.Title : "No suena nada";
        Artist = n.HasMedia ? string.Join(" · ", new[] { n.Artist, n.App }.Where(s => s.Length > 0)) : "Pon algo en Spotify, YouTube o el navegador";
        Playing = n.Playing;
        Cover = n.Cover;
        Progress = n.Duration > TimeSpan.Zero ? Math.Clamp(n.Position / n.Duration, 0, 1) : 0;
        PositionText = n.Duration > TimeSpan.Zero ? Time(n.Position) : "";
        DurationText = n.Duration > TimeSpan.Zero ? Time(n.Duration) : "";
        if (_barsFor != n.Title) MakeBars(n.Title);
        for (var i = 0; i < Bars.Count; i++) Bars[i].Played = (i + 0.5) / Bars.Count <= Progress;
        OnPropertyChanged(nameof(HasNoRecent));
    }

    /// <summary>La onda es decorativa (Windows no da la forma real del audio): siempre la misma para la misma canción.</summary>
    private void MakeBars(string title)
    {
        _barsFor = title;
        Bars.Clear();
        var rnd = new Random(title.GetHashCode(StringComparison.Ordinal));
        for (var i = 0; i < 46; i++) Bars.Add(new WaveBar(5 + rnd.NextDouble() * 17 * (0.5 + 0.5 * Math.Sin(i / 5.0) * Math.Sin(i / 5.0))));
    }

    private void LoadOutputs()
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            var def = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                Outputs.Add(new AudioOutput(d.FriendlyName, d.ID == def));
        }
        catch (Exception) { } // sin audio: la lista queda vacía
    }

    private static string Time(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        if (!await _media.PlayPauseAsync()) Tools.MusicKeys.PlayPause();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (!await _media.NextAsync()) Tools.MusicKeys.Next();
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        if (!await _media.PreviousAsync()) Tools.MusicKeys.Previous();
    }

    [RelayCommand]
    private async Task ToggleShuffleAsync()
    {
        if (await _media.ShuffleAsync(!Shuffle)) Shuffle = !Shuffle;
    }

    [RelayCommand]
    private async Task ToggleRepeatAsync()
    {
        if (await _media.RepeatAsync(!Repeat)) Repeat = !Repeat;
    }

    [RelayCommand]
    private void ShowTab(string tab) => Tab = tab;

    [RelayCommand]
    private void Sleep(string mode)
    {
        _media.SetSleep(mode);
        SleepMode = mode;
    }

    /// <summary>Windows no deja cambiar la salida desde otra app sin trucos: se abre su configuración de sonido.</summary>
    [RelayCommand]
    private static void OpenSoundSettings() => ActivityFeed.OpenShell("ms-settings:sound");

    public void Dispose()
    {
        _tick.Stop();
        _media.Changed -= OnChanged;
    }
}
