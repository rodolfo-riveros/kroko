using System.IO;
using System.Windows.Threading;
using Kroko.App.Audio;
using Kroko.App.ViewModels;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kroko.App.Services;

/// <summary>
/// Conversación por voz en tiempo real (Gemini Live a través del cerebro). Tu voz sale en vivo y la de Kroko
/// suena a medida que llega. Sin cancelación de eco, mientras Kroko habla no se le manda el micrófono (para que
/// no se escuche a sí mismo por los parlantes); se le interrumpe con «Oye Kroko» o el atajo.
/// Termina cuando te quedas callado un rato, dices «gracias, eso es todo» o pulsas el atajo.
/// </summary>
public sealed class LiveConversation : IDisposable
{
    private const string MicUser = "tiempo-real";
    private const int SendChunkBytes = 3200; // 100 ms a 16 kHz

    private readonly BrainConnection _brain;
    private readonly MicrophoneStream _mic;
    private readonly LivePlayer _player;
    private readonly PanelViewModel _panel;
    private readonly CompanionBehavior _behavior;
    private readonly VoiceSettings _opt;
    private readonly ILogger<LiveConversation> _log;
    private readonly DispatcherTimer _tick;
    private readonly MemoryStream _pending = new();
    private readonly List<byte[]> _preroll = [];
    private bool _started;
    private bool _endRequested;
    private bool _unavailable;
    private DateTime _lastActivity;
    private string _lastHeard = "";

    public LiveConversation(BrainConnection brain, MicrophoneStream mic, LivePlayer player, PanelViewModel panel, CompanionBehavior behavior,
        IOptions<KrokoOptions> options, ILogger<LiveConversation> log)
    {
        _brain = brain;
        _mic = mic;
        _player = player;
        _panel = panel;
        _behavior = behavior;
        _opt = options.Value.Voice;
        _log = log;
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => OnTick();
        _player.Level += level => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _behavior.Face.SpeechLevel = level);
        _brain.MessageReceived += m =>
        {
            if (m is LiveEventMessage or LiveAudioMessage) System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnBrainMessage(m));
        };
        _brain.ConnectionChanged += connected =>
        {
            if (!connected) System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Stop(null));
        };
    }

    public bool IsActive { get; private set; }

    /// <summary>¿Se usa la voz en tiempo real? (activada, con cerebro y sin haber fallado en esta sesión)</summary>
    public bool CanUse => _opt.Realtime && _opt.RepliesWithVoice && !_unavailable && _brain.IsConnected && MicrophoneStream.HasMicrophone;

    /// <summary>No se pudo abrir: el controlador de voz vuelve al modo clásico.</summary>
    public event Action? Failed;

    /// <summary>Abre la conversación. preroll = lo dicho junto a «Oye Kroko» (para no perder la orden).</summary>
    public bool Start(byte[]? preroll)
    {
        if (IsActive) return true;
        try
        {
            _mic.Data += OnMicData;
            _mic.Acquire(MicUser, _opt.MicrophoneDevice);
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            _mic.Data -= OnMicData;
            _log.LogWarning("No pude abrir el micrófono para la voz en tiempo real: {Message}", ex.Message);
            return false;
        }

        IsActive = true;
        _started = false;
        _endRequested = false;
        _lastHeard = "";
        _lastActivity = DateTime.Now;
        _pending.SetLength(0);
        _preroll.Clear();
        if (preroll is { Length: > 0 }) _preroll.Add(preroll);
        _behavior.WakeUp();
        _behavior.SetBrainState(FaceStates.Listening);
        _panel.ShowListening();
        _panel.ShowLiveListening();
        _ = _brain.SendAsync(new LiveStartMessage());
        _tick.Start();
        _log.LogInformation("Voz en tiempo real: empezando");
        return true;
    }

    /// <summary>«Oye Kroko» o el atajo mientras habla: se calla y te escucha.</summary>
    public void Interrupt()
    {
        if (!IsActive) return;
        _player.Clear();
        _lastActivity = DateTime.Now;
        _behavior.SetBrainState(FaceStates.Listening);
        _panel.ShowLiveListening();
    }

    public void Stop(string? farewell = "Aquí estaré. Dime «Oye Kroko» cuando me necesites.")
    {
        if (!IsActive) return;
        IsActive = false;
        _tick.Stop();
        _mic.Data -= OnMicData;
        _mic.Release(MicUser);
        _player.Close();
        _ = _brain.SendAsync(new LiveStopMessage());
        _behavior.Face.MicLevel = 0;
        _behavior.Face.SpeechLevel = 0;
        _behavior.SetBrainState(FaceStates.Idle);
        _panel.MicLevel = 0;
        _panel.EndConversation(farewell);
        _log.LogInformation("Voz en tiempo real: terminada");
    }

    private void OnMicData(byte[] buffer, int count)
    {
        var level = MicrophoneStream.LevelOf(buffer, count);
        _behavior.Face.MicLevel = level;
        _panel.MicLevel = level;
        if (level >= _opt.SpeechThreshold) _lastActivity = DateTime.Now;

        // Mientras Kroko habla, el micrófono no va a Gemini (evita que se escuche a sí mismo)
        if (_player.IsSpeaking || _panel.IsConfirming) return;

        if (!_started)
        {
            // Hasta que el cerebro confirme, se guardan unos segundos para no perder el principio
            if (_preroll.Sum(p => p.Length) < 16000 * 2 * 4) _preroll.Add(buffer[..count]);
            return;
        }

        _pending.Write(buffer, 0, count);
        if (_pending.Length >= SendChunkBytes) Flush();
    }

    private void Flush()
    {
        if (_pending.Length == 0) return;
        var data = Convert.ToBase64String(_pending.GetBuffer(), 0, (int)_pending.Length);
        _pending.SetLength(0);
        _ = _brain.SendAsync(new LiveAudioMessage { Data = data });
    }

    private void OnBrainMessage(KrokoMessage m)
    {
        if (!IsActive) return;
        switch (m)
        {
            case LiveEventMessage { Kind: "started" }:
                _started = true;
                foreach (var chunk in _preroll)
                    for (var i = 0; i < chunk.Length; i += SendChunkBytes)
                        _ = _brain.SendAsync(new LiveAudioMessage { Data = Convert.ToBase64String(chunk, i, Math.Min(SendChunkBytes, chunk.Length - i)) });
                _preroll.Clear();
                break;

            case LiveEventMessage { Kind: "you", Text: { } heard }:
                _lastHeard = heard;
                _lastActivity = DateTime.Now;
                _panel.ShowLiveListening();
                _panel.ShowCaption(heard);
                var said = Audio.WakeWordDetector.Normalize(heard);
                if (_opt.EffectiveEndPhrases.Any(p => said.Contains(Audio.WakeWordDetector.Normalize(p), StringComparison.Ordinal)))
                    _endRequested = true;
                break;

            case LiveEventMessage { Kind: "kroko", Text: { } text }:
                _lastActivity = DateTime.Now;
                _panel.ShowLiveReply(text);
                break;

            case LiveEventMessage { Kind: "turn" }:
                _lastActivity = DateTime.Now;
                break;

            case LiveEventMessage { Kind: "interrupted" }:
                _player.Clear();
                break;

            case LiveEventMessage { Kind: "error" or "ended", Text: var reason }:
                _log.LogWarning("Voz en tiempo real: {Reason}", reason);
                var wasStarted = _started;
                Stop(null);
                if (!wasStarted)
                {
                    _unavailable = true; // no reintentar en esta sesión: el modo clásico sigue funcionando
                    Failed?.Invoke();
                }
                break;

            case LiveAudioMessage audio:
                try
                {
                    _player.Enqueue(Convert.FromBase64String(audio.Data));
                }
                catch (FormatException)
                {
                    break;
                }
                _lastActivity = DateTime.Now;
                _behavior.SetBrainState(FaceStates.Speaking);
                break;
        }
    }

    private void OnTick()
    {
        if (!IsActive) return;
        if (_player.IsSpeaking)
        {
            _lastActivity = DateTime.Now;
            return;
        }
        if (_behavior.Face.BaseKey == FaceStates.Speaking) _behavior.SetBrainState(FaceStates.Listening);

        // Dijiste «gracias, eso es todo»: cuando Kroko termina de despedirse, se cierra
        if (_endRequested && DateTime.Now - _player.LastAudio > TimeSpan.FromSeconds(1.5))
        {
            Stop(null);
            return;
        }
        // En silencio un rato (y sin una tarjeta pendiente): se cierra solo
        if (!_panel.IsConfirming && DateTime.Now - _lastActivity > TimeSpan.FromSeconds(Math.Max(5, _opt.FollowUpSeconds + 4)))
            Stop();
    }

    public void Dispose()
    {
        Stop(null);
        _player.Dispose();
    }
}
