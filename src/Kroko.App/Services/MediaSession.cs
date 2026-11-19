using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using NAudio.CoreAudioApi;

namespace Kroko.App.Services;

/// <summary>
/// Lo que suena en Windows (17/11/2026, especificación «música en la isla»): Spotify, YouTube Music, el navegador…
/// a través del control multimedia del sistema (el mismo del panel de volumen de Windows). Da título, artista, portada,
/// progreso y los botones sin pedir cuentas a nadie. Si Windows no lo ofrece, Kroko sigue con las teclas multimedia.
/// </summary>
public sealed class MediaSessionService(ILogger<MediaSessionService> log) : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly Lock _gate = new();

    public sealed record Snapshot(string Title, string Artist, string App, bool Playing, BitmapSource? Cover,
        TimeSpan Position, TimeSpan Duration)
    {
        public static readonly Snapshot None = new("", "", "", false, null, TimeSpan.Zero, TimeSpan.Zero);
        public bool HasMedia => Title.Length > 0;
        public string Line => Artist.Length > 0 ? $"{Title} — {Artist}" : Title;
    }

    public Snapshot Now { get; private set; } = Snapshot.None;

    /// <summary>Cambió algo (canción, pausa, portada). Puede llegar desde cualquier hilo.</summary>
    public event Action? Changed;

    /// <summary>Empezó otra canción (para que la isla salga unos segundos).</summary>
    public event Action? TrackChanged;

    public bool Available => _manager is not null;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => Attach();
            Attach();
            log.LogInformation("Control multimedia de Windows listo");
        }
        catch (Exception ex)
        {
            // Windows sin el control multimedia (N/KN, servidores…): la herramienta musica usa teclas
            log.LogWarning("Sin control multimedia de Windows: {Error}", ex.Message);
        }
    }

    private void Attach()
    {
        var session = _manager?.GetCurrentSession();
        lock (_gate)
        {
            if (ReferenceEquals(session, _session)) return;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnMedia;
                _session.PlaybackInfoChanged -= OnPlayback;
                _session.TimelinePropertiesChanged -= OnTimeline;
            }
            _session = session;
            if (session is not null)
            {
                session.MediaPropertiesChanged += OnMedia;
                session.PlaybackInfoChanged += OnPlayback;
                session.TimelinePropertiesChanged += OnTimeline;
            }
        }
        _ = RefreshAsync(newTrack: true);
    }

    private void OnMedia(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = RefreshAsync(newTrack: true);
    private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _ = RefreshAsync(newTrack: false);
    private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => _ = RefreshAsync(newTrack: false);

    private async Task RefreshAsync(bool newTrack)
    {
        try
        {
            var session = _session;
            if (session is null)
            {
                Publish(Snapshot.None, false);
                return;
            }
            var props = await session.TryGetMediaPropertiesAsync();
            var playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var timeline = session.GetTimelineProperties();
            var title = props?.Title?.Trim() ?? "";
            var artist = props?.Artist?.Trim() ?? "";
            var sameTrack = title == Now.Title && artist == Now.Artist;
            var cover = sameTrack ? Now.Cover : await ReadCoverAsync(props?.Thumbnail);
            var position = timeline is null ? TimeSpan.Zero : timeline.Position + (playing ? DateTimeOffset.Now - timeline.LastUpdatedTime : TimeSpan.Zero);
            var duration = timeline is null ? TimeSpan.Zero : timeline.EndTime - timeline.StartTime;
            if (duration > TimeSpan.Zero && position > duration) position = duration;
            Publish(new Snapshot(title, artist, AppName(session.SourceAppUserModelId), playing, cover, position, duration),
                newTrack && !sameTrack && title.Length > 0);
        }
        catch (Exception ex)
        {
            log.LogDebug("No pude leer la música: {Error}", ex.Message);
        }
    }

    /// <summary>La posición avanza sola mientras suena; la isla lo pide cada tanto para la barra de progreso.</summary>
    public Task PollAsync() => _session is null ? Task.CompletedTask : RefreshAsync(newTrack: false);

    private void Publish(Snapshot snapshot, bool newTrack)
    {
        Now = snapshot;
        Changed?.Invoke();
        if (newTrack)
        {
            RememberTrack(snapshot);
            TrackChanged?.Invoke();
        }
    }

    private static async Task<BitmapSource?> ReadCoverAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            using var net = stream.AsStreamForRead();
            using var copy = new MemoryStream();
            await net.CopyToAsync(copy);
            copy.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = copy;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null; // sin portada: la isla pone una nota musical
        }
    }

    internal static string AppName(string? id)
    {
        var s = (id ?? "").ToLowerInvariant();
        return s switch
        {
            _ when s.Contains("spotify") => "Spotify",
            _ when s.Contains("chrome") => "Chrome",
            _ when s.Contains("msedge") || s.Contains("edge") => "Edge",
            _ when s.Contains("firefox") => "Firefox",
            _ when s.Contains("zune") || s.Contains("media") => "Reproductor multimedia",
            _ when s.Contains("youtube") => "YouTube Music",
            _ => s.Length == 0 ? "" : Path.GetFileNameWithoutExtension(s.Split('!')[0]),
        };
    }

    // ---------------------------------------------------------------- botones

    /// <summary>true si Windows lo hizo; false si no hay sesión (quien llama usa las teclas multimedia).</summary>
    public async Task<bool> PlayPauseAsync() => _session is { } s && await s.TryTogglePlayPauseAsync();
    public async Task<bool> NextAsync() => _session is { } s && await s.TrySkipNextAsync();
    public async Task<bool> PreviousAsync() => _session is { } s && await s.TrySkipPreviousAsync();
    public async Task<bool> StopAsync() => _session is { } s && await s.TryStopAsync();
    public async Task<bool> PauseAsync() => _session is { } s && await s.TryPauseAsync();
    public async Task<bool> ShuffleAsync(bool on) => _session is { } s && await s.TryChangeShuffleActiveAsync(on);
    public async Task<bool> RepeatAsync(bool on) => _session is { } s && await s.TryChangeAutoRepeatModeAsync(on
        ? Windows.Media.MediaPlaybackAutoRepeatMode.List : Windows.Media.MediaPlaybackAutoRepeatMode.None);

    // ---------------------------------------------------------------- recientes y «Apagar en» (Kroko 2, 18/11/2026)

    /// <summary>Lo que sonó (Spotify no comparte su cola con Windows: esto es lo que Kroko vio pasar).</summary>
    public System.Collections.ObjectModel.ObservableCollection<Snapshot> Recent { get; } = [];

    private System.Windows.Threading.DispatcherTimer? _sleep;
    private bool _sleepAtEnd;

    /// <summary>"no", "15", "30" (minutos) o "fin" (al terminar la canción).</summary>
    public string SleepMode { get; private set; } = "no";

    public void SetSleep(string mode)
    {
        _sleep?.Stop();
        _sleepAtEnd = mode == "fin";
        SleepMode = mode;
        if (int.TryParse(mode, out var minutes))
        {
            _sleep = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
            _sleep.Tick += async (_, _) =>
            {
                _sleep?.Stop();
                SleepMode = "no";
                await PauseAsync();
            };
            _sleep.Start();
        }
    }

    private void RememberTrack(Snapshot s)
    {
        if (_sleepAtEnd && Recent.Count > 0)
        {
            _sleepAtEnd = false;
            SleepMode = "no";
            _ = PauseAsync();
        }
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Recent.Insert(0, s);
            while (Recent.Count > 8) Recent.RemoveAt(Recent.Count - 1);
        });
    }

    /// <summary>Volumen general de Windows (0–100).</summary>
    public static int Volume
    {
        get
        {
            try
            {
                using var e = new MMDeviceEnumerator();
                using var d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return (int)Math.Round(d.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            }
            catch (Exception) { return 50; }
        }
        set
        {
            try
            {
                using var e = new MMDeviceEnumerator();
                using var d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0, 100) / 100f;
                if (value > 0 && d.AudioEndpointVolume.Mute) d.AudioEndpointVolume.Mute = false;
            }
            catch (Exception) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_session is null) return;
            _session.MediaPropertiesChanged -= OnMedia;
            _session.PlaybackInfoChanged -= OnPlayback;
            _session.TimelinePropertiesChanged -= OnTimeline;
            _session = null;
        }
    }
}
