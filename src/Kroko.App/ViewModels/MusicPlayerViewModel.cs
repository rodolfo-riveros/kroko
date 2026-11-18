using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kroko.App.Services;

namespace Kroko.App.ViewModels;

/// <summary>El reproductor grande que cuelga de la isla (17/11/2026): portada, título, progreso, botones y volumen.</summary>
public sealed partial class MusicPlayerViewModel : ObservableObject, IDisposable
{
    private readonly MediaSessionService _media;
    private readonly DispatcherTimer _tick;
    private bool _settingVolume;

    public MusicPlayerViewModel(MediaSessionService media)
    {
        _media = media;
        _media.Changed += OnChanged;
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += async (_, _) => await _media.PollAsync();
        _tick.Start();
        _settingVolume = true;
        Volume = MediaSessionService.Volume;
        _settingVolume = false;
        Apply();
    }

    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Artist { get; set; } = "";
    [ObservableProperty] public partial string App { get; set; } = "";
    [ObservableProperty] public partial bool Playing { get; set; }
    [ObservableProperty] public partial bool HasMedia { get; set; }
    [ObservableProperty] public partial System.Windows.Media.ImageSource? Cover { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string PositionText { get; set; } = "";
    [ObservableProperty] public partial string DurationText { get; set; } = "";
    [ObservableProperty] public partial int Volume { get; set; }

    partial void OnVolumeChanged(int value)
    {
        if (!_settingVolume) MediaSessionService.Volume = value;
    }

    private void OnChanged() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(Apply);

    private void Apply()
    {
        var n = _media.Now;
        HasMedia = n.HasMedia;
        Title = n.HasMedia ? n.Title : "No suena nada";
        Artist = n.Artist;
        App = n.App;
        Playing = n.Playing;
        Cover = n.Cover;
        Progress = n.Duration > TimeSpan.Zero ? Math.Clamp(n.Position / n.Duration, 0, 1) : 0;
        PositionText = n.Duration > TimeSpan.Zero ? Time(n.Position) : "";
        DurationText = n.Duration > TimeSpan.Zero ? Time(n.Duration) : "";
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

    public void Dispose()
    {
        _tick.Stop();
        _media.Changed -= OnChanged;
    }
}
