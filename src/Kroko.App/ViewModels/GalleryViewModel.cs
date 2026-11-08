using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kroko.App.Audio;
using Kroko.App.Face;
using Kroko.App.Services;

namespace Kroko.App.ViewModels;

public sealed record ExpressionItem(string Key, string Name, string Description, bool IsLoop, bool IsInternal)
{
    public string ModeLabel => IsInternal ? "interna" : IsLoop ? "estado" : "puntual";
}

/// <summary>
/// Galería para ver y probar todas las expresiones. Tiene su propia cara (independiente de la burbuja)
/// y opcionalmente reproduce también en la burbuja.
/// </summary>
public partial class GalleryViewModel : ObservableObject, IDisposable
{
    private readonly ExpressionLibrary _library;
    private readonly CompanionBehavior _behavior;
    private readonly ISoundService _sounds;
    private readonly DispatcherTimer _audioSim;
    private readonly Stopwatch _simClock = Stopwatch.StartNew();

    public GalleryViewModel(ExpressionLibrary library, CompanionBehavior behavior, ISoundService sounds)
    {
        _library = library;
        _behavior = behavior;
        _sounds = sounds;
        Face = new FaceAnimator(library);
        Face.ExpressionEntered += e => { if (PlaySounds && e.Sound is { } s) _sounds.Play(s); };
        Face.ExpressionExited += e => { if (PlaySounds && e.ExitSound is { } s) _sounds.Play(s); };

        _audioSim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _audioSim.Tick += (_, _) => SimulateAudioTick();

        _library.Changed += Refresh;
        Refresh();
        Selected = Items.FirstOrDefault();
    }

    public FaceAnimator Face { get; }

    public ObservableCollection<ExpressionItem> Items { get; } = [];

    public string ExpressionsPath => _library.ActivePath;

    [ObservableProperty]
    public partial ExpressionItem? Selected { get; set; }

    [ObservableProperty]
    public partial bool FollowCursor { get; set; } = true;

    [ObservableProperty]
    public partial bool Blink { get; set; } = true;

    [ObservableProperty]
    public partial bool Breath { get; set; } = true;

    [ObservableProperty]
    public partial bool MirrorToBubble { get; set; }

    [ObservableProperty]
    public partial bool PlaySounds { get; set; } = true;

    [ObservableProperty]
    public partial bool SimulateAudio { get; set; }

    [ObservableProperty]
    public partial double MicLevel { get; set; }

    [ObservableProperty]
    public partial double SpeechLevel { get; set; }

    partial void OnSelectedChanged(ExpressionItem? value)
    {
        if (value is null) return;
        Face.StopDistraction();
        if (value.IsLoop)
        {
            Face.StopOneShot();
            Face.SetBase(value.Key);
        }
        else
        {
            Face.Play(value.Key);
        }
        if (MirrorToBubble) _behavior.Preview(value.Key, value.IsLoop);
    }

    partial void OnFollowCursorChanged(bool value) => Face.FollowCursor = value;
    partial void OnBlinkChanged(bool value) => Face.EnableBlink = value;
    partial void OnBreathChanged(bool value) => Face.EnableBreath = value;
    partial void OnMicLevelChanged(double value) => Face.MicLevel = value;
    partial void OnSpeechLevelChanged(double value) => Face.SpeechLevel = value;

    partial void OnSimulateAudioChanged(bool value)
    {
        if (value) _audioSim.Start();
        else
        {
            _audioSim.Stop();
            MicLevel = 0;
            SpeechLevel = 0;
        }
    }

    [RelayCommand]
    private void Replay()
    {
        if (Selected is { } s) OnSelectedChanged(s);
    }

    [RelayCommand]
    private void Fly() => Distract(DistractionKind.Fly);

    [RelayCommand]
    private void Ball() => Distract(DistractionKind.Ball);

    [RelayCommand]
    private void Clock() => Distract(DistractionKind.Clock);

    [RelayCommand]
    private void Yawn()
    {
        Face.Play("yawn");
        if (MirrorToBubble) _behavior.Preview("yawn", false);
    }

    [RelayCommand]
    private void EditExpressions()
    {
        var path = _library.EnsureUserCopy();
        OnPropertyChanged(nameof(ExpressionsPath));
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void Distract(DistractionKind kind)
    {
        Face.StopOneShot();
        Face.StartDistraction(kind);
        if (MirrorToBubble) _behavior.Face.StartDistraction(kind);
    }

    private void SimulateAudioTick()
    {
        // Voz falsa: sílabas rápidas con pausas, para probar anillo y boca sin micrófono
        var t = _simClock.Elapsed.TotalSeconds;
        var syllable = Math.Max(0, Math.Sin(t * 13)) * (0.5 + 0.5 * Math.Sin(t * 2.3));
        var pause = Math.Sin(t * 0.9) > -0.6 ? 1 : 0;
        var level = Math.Clamp(syllable * pause + Random.Shared.NextDouble() * 0.08, 0, 1);
        MicLevel = level;
        SpeechLevel = level;
        if (MirrorToBubble)
        {
            _behavior.Face.MicLevel = level;
            _behavior.Face.SpeechLevel = level;
        }
    }

    private void Refresh()
    {
        var selectedKey = Selected?.Key;
        Items.Clear();
        foreach (var e in _library.All)
            Items.Add(new ExpressionItem(e.Key, e.Name, e.Description, e.IsLoop, e.IsInternal));
        OnPropertyChanged(nameof(ExpressionsPath));
        if (selectedKey is not null) Selected = Items.FirstOrDefault(i => i.Key == selectedKey);
    }

    public void Dispose()
    {
        _audioSim.Stop();
        _library.Changed -= Refresh;
        if (MirrorToBubble)
        {
            _behavior.Face.MicLevel = 0;
            _behavior.Face.SpeechLevel = 0;
            _behavior.Preview("idle", true);
        }
    }
}
