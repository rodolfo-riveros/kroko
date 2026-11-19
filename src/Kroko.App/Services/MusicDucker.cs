using System.Windows.Threading;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace Kroko.App.Services;

/// <summary>
/// Música y Kroko conviven (diseño Kroko 2, 18/11/2026): mientras Kroko habla baja el volumen de las demás apps (Spotify,
/// el navegador…) al 30 % y lo devuelve al terminar; y al acabar un enfoque pausa la música. Se apaga en el reproductor
/// («Bajar el volumen cuando Kroko habla…»). Nunca toca el volumen general ni el de Kroko.
/// </summary>
public sealed class MusicDucker : IDisposable
{
    private readonly CompanionBehavior _behavior;
    private readonly MediaSessionService _media;
    private readonly ILogger<MusicDucker> _log;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Dictionary<uint, float> _saved = [];
    private bool _ducked;

    public MusicDucker(CompanionBehavior behavior, MediaSessionService media, TimerService timers, ILogger<MusicDucker> log)
    {
        _behavior = behavior;
        _media = media;
        _log = log;
        Enabled = Services.UserSettings.LoadKrokoSection()["MusicDucking"]?.GetValue<bool>() ?? true;
        _tick.Tick += (_, _) => Check();
        _tick.Start();
        timers.PomodoroPhaseChanged += work =>
        {
            // Terminó el bloque de trabajo (empieza el descanso o se acabó): pausa la música
            if (Enabled && work != true && _media.Now.Playing) _ = _media.PauseAsync();
        };
    }

    public bool Enabled { get; private set; }

    public void SetEnabled(bool on)
    {
        Enabled = on;
        var k = Services.UserSettings.LoadKrokoSection();
        k["MusicDucking"] = on;
        Services.UserSettings.SaveKrokoSection(k);
        if (!on) Restore();
    }

    private void Check()
    {
        var speaking = _behavior.Face.BaseKey == FaceStates.Speaking;
        if (speaking && Enabled && !_ducked && _media.Now.Playing) Duck();
        else if (!speaking && _ducked) Restore();
    }

    private void Duck()
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            using var device = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            var me = (uint)Environment.ProcessId;
            for (var i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.GetProcessID == me || s.GetProcessID == 0 || s.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                _saved[s.GetProcessID] = s.SimpleAudioVolume.Volume;
                s.SimpleAudioVolume.Volume = s.SimpleAudioVolume.Volume * 0.3f;
            }
            _ducked = true;
        }
        catch (Exception ex)
        {
            _log.LogDebug("No pude bajar la música: {Error}", ex.Message);
        }
    }

    private void Restore()
    {
        _ducked = false;
        if (_saved.Count == 0) return;
        try
        {
            using var e = new MMDeviceEnumerator();
            using var device = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
                if (_saved.TryGetValue(sessions[i].GetProcessID, out var v)) sessions[i].SimpleAudioVolume.Volume = v;
        }
        catch (Exception ex)
        {
            _log.LogDebug("No pude devolver el volumen: {Error}", ex.Message);
        }
        _saved.Clear();
    }

    public void Dispose()
    {
        _tick.Stop();
        Restore();
    }
}
