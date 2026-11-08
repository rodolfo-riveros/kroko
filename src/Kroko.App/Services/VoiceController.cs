using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Kroko.App.Audio;
using Kroko.App.Native;
using Kroko.App.ViewModels;
using Kroko.Shared;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kroko.App.Services;

/// <summary>
/// Hablar con Kroko (sección 6), de tres formas:
/// · atajo global (Ctrl+Espacio): pulsas, hablas y se detiene solo al callarte (o al soltar en "pulsar para hablar");
/// · «Oye Kroko» / «Hola Kroko» (detector local con Vosk): despierta y te escucha; si lo dijiste junto con la orden
///   ("oye kroko, abre Excel") la manda de una vez;
/// · conversación: tras responder hablando vuelve a escucharte unos segundos sin repetir la frase; termina si te
///   callas o dices «gracias, eso es todo».
/// El atajo o «Oye Kroko» mientras habla lo interrumpen.
/// </summary>
public sealed partial class VoiceController : IDisposable
{
    private const int HotkeyId = 0x4B56;

    private enum ListenReason { Hotkey, WakeWord, FollowUp }

    private readonly VoiceRecorder _recorder;
    private readonly SpeechPlayer _player;
    private readonly BrainConnection _brain;
    private readonly CompanionBehavior _behavior;
    private readonly PanelViewModel _panel;
    private readonly ToolExecutor _tools;
    private readonly VoiceSession _session;
    private readonly WakeWordService _wake;
    private readonly LiveConversation _live;
    private readonly VoiceSettings _opt;
    private readonly ILogger<VoiceController> _log;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _followUpDelay;

    private HwndSource? _source;
    private uint _modifiers;
    private uint _vk;
    private DateTime _started;
    private DateTime _lastVoice;
    private double _speechSeconds;
    private bool _speechSeen;
    private ListenReason _reason;

    // Conversación en curso: tras cada respuesta hablada se vuelve a escuchar
    private bool _conversation;
    private bool _endRequested;

    public VoiceController(VoiceRecorder recorder, SpeechPlayer player, BrainConnection brain, CompanionBehavior behavior,
        PanelViewModel panel, ToolExecutor tools, VoiceSession session, WakeWordService wake, LiveConversation live, IOptions<KrokoOptions> options,
        ILogger<VoiceController> log)
    {
        _recorder = recorder;
        _player = player;
        _brain = brain;
        _behavior = behavior;
        _panel = panel;
        _tools = tools;
        _session = session;
        _wake = wake;
        _live = live;
        // Si la voz en tiempo real no se pudo abrir, se escucha con el modo clásico (grabar y enviar)
        _live.Failed += () => StartListening(ListenReason.WakeWord);
        _opt = options.Value.Voice;
        _log = log;
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _tick.Tick += (_, _) => OnTick();
        _followUpDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _followUpDelay.Tick += (_, _) =>
        {
            _followUpDelay.Stop();
            ContinueConversation();
        };

        _recorder.Level += level => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnLevel(level));
        _wake.Detected += OnWakeWord;
        _wake.Caption += _panel.ShowCaption;
        _panel.ReplyFinished += OnReplyFinished;
        _panel.ConfirmShown += OnConfirmShown;
        _brain.MessageReceived += m =>
        {
            if (m is TranscriptMessage t) System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnTranscript(t.Text));
        };
        _brain.ConnectionChanged += connected =>
        {
            if (!connected) System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => EndConversation(quiet: true));
        };
    }

    public string HotkeyText => _opt.Hotkey;

    public bool IsRegistered { get; private set; }

    public bool InConversation => _conversation;

    public void SetFullscreen(bool fullscreen) => _wake.SetFullscreen(fullscreen);

    public void Start()
    {
        _wake.Start();
        if (!TryParseHotkey(_opt.Hotkey, out _modifiers, out _vk))
        {
            _log.LogWarning("Atajo de voz inválido: {Hotkey}", _opt.Hotkey);
            return;
        }
        _source = new HwndSource(new HwndSourceParameters("KrokoVoiceHotkey") { ParentWindow = new nint(-3), Width = 0, Height = 0 });
        _source.AddHook(WndProc);
        IsRegistered = NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, _modifiers | NativeMethods.MOD_NOREPEAT, _vk);
        if (IsRegistered) _log.LogInformation("Atajo de voz listo: {Hotkey}", _opt.Hotkey);
        else _log.LogWarning("No se pudo registrar {Hotkey} (otro programa lo usa). Cambia Voice:Hotkey en Ajustes", _opt.Hotkey);
    }

    /// <summary>Lo mismo que pulsar el atajo (también desde el menú "Hablar").</summary>
    public void Toggle()
    {
        // 0) Voz en tiempo real: el atajo la interrumpe si habla, o la cierra si te escucha
        if (_live.IsActive)
        {
            if (_player.IsPlaying) _panel.StopSpeaking();
            _live.Stop();
            return;
        }

        // 1) Si Kroko está hablando, el atajo lo interrumpe (y se acaba la conversación)
        if (_player.IsPlaying)
        {
            EndConversation(quiet: true);
            _panel.StopSpeaking();
            return;
        }

        // 2) Si ya está escuchando, el atajo termina y envía (modo toque)
        if (_recorder.IsRecording)
        {
            Finish(send: true);
            return;
        }

        if (!_tools.IsPaused && _live.CanUse && _live.Start(null)) return;
        StartListening(ListenReason.Hotkey);
    }

    // ---------------------------------------------------------------- «Oye Kroko»

    private void OnWakeWord(WakeDetection hit)
    {
        if (_recorder.IsRecording || _tools.IsPaused || _panel.IsConfirming) return;

        // En plena conversación en tiempo real, «Oye Kroko» lo calla y te escucha
        if (_live.IsActive)
        {
            _live.Interrupt();
            return;
        }

        // Interrumpe si estaba hablando
        if (_player.IsPlaying)
        {
            _followUpDelay.Stop();
            _panel.StopSpeaking();
        }

        if (!_brain.IsConnected)
        {
            _behavior.WakeUp();
            _panel.ExplainOffline();
            return;
        }

        // Voz en tiempo real: lo dicho junto a «Oye Kroko» va como inicio de la conversación
        if (_live.CanUse && _live.Start(hit.HasCommand ? hit.UtterancePcm : null)) return;

        _conversation = _opt.Conversation;
        _endRequested = false;
        _behavior.WakeUp();

        if (hit.HasCommand)
        {
            // "oye kroko, abre Excel" dicho de corrido: se manda la frase completa tal cual
            _panel.ShowListening();
            _panel.ShowCaption(Capitalize(KrokoName.Fix(hit.Heard)));
            Send(Wav.FromPcm16(hit.UtterancePcm, VoiceRecorder.Format.SampleRate));
            return;
        }
        StartListening(ListenReason.WakeWord);
    }

    // ---------------------------------------------------------------- escuchar

    private void StartListening(ListenReason reason)
    {
        _followUpDelay.Stop();
        if (_tools.IsPaused)
        {
            if (reason != ListenReason.FollowUp) _panel.ShowNotice("Estoy en pausa (Ctrl+Alt+K para reanudarme).");
            EndConversation(quiet: true);
            return;
        }
        if (!VoiceRecorder.HasMicrophone)
        {
            _panel.ShowNotice("No encontré ningún micrófono. Conéctalo y vuelve a pulsar " + _opt.Hotkey + ".");
            _behavior.PlayExpression("confused");
            EndConversation(quiet: true);
            return;
        }
        if (!_brain.IsConnected)
        {
            _panel.ExplainOffline();
            EndConversation(quiet: true);
            return;
        }

        try
        {
            _behavior.WakeUp();
            _recorder.Start(_opt.MicrophoneDevice);
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            _log.LogWarning(ex, "No se pudo abrir el micrófono");
            _panel.ShowNotice("No pude abrir el micrófono. ¿Lo está usando otro programa?");
            EndConversation(quiet: true);
            return;
        }

        if (reason == ListenReason.Hotkey)
        {
            _conversation = _opt.Conversation;
            _endRequested = false;
        }
        _reason = reason;
        _started = _lastVoice = DateTime.Now;
        _speechSeen = false;
        _speechSeconds = 0;
        _wake.BeginCaptions();
        _behavior.SetBrainState(FaceStates.Listening);
        _panel.IsConversation = _conversation;
        _panel.ShowListening(followUp: reason == ListenReason.FollowUp);
        _tick.Start();
    }

    private void OnLevel(double level)
    {
        if (!_recorder.IsRecording) return;
        _behavior.Face.MicLevel = level;
        _panel.MicLevel = level;
        if (level >= _opt.SpeechThreshold)
        {
            _speechSeconds += 0.05;
            // En la conversación hace falta un poco más de voz (que un ruido suelto no cuente como respuesta)
            if (_reason != ListenReason.FollowUp || _speechSeconds >= 0.3) _speechSeen = true;
            _lastVoice = DateTime.Now;
        }
    }

    private void OnTick()
    {
        if (!_recorder.IsRecording)
        {
            _tick.Stop();
            return;
        }

        var now = DateTime.Now;
        var elapsed = (now - _started).TotalSeconds;

        if (_reason == ListenReason.Hotkey && string.Equals(_opt.Mode, "PushToTalk", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsHotkeyHeld()) Finish(send: true);
        }
        else
        {
            var wait = _reason == ListenReason.FollowUp ? Math.Max(3, _opt.FollowUpSeconds) : 6;
            if (_speechSeen && (now - _lastVoice).TotalSeconds >= _opt.SilenceSeconds) Finish(send: true);
            else if (!_speechSeen && elapsed >= wait) Finish(send: false);
        }

        if (_recorder.IsRecording && elapsed >= _opt.MaxSeconds) Finish(send: true);
    }

    private void Finish(bool send)
    {
        _tick.Stop();
        var wav = _recorder.Stop();
        _wake.EndCaptions();
        _behavior.Face.MicLevel = 0;
        _panel.MicLevel = 0;

        if (!send || wav is null || !_speechSeen)
        {
            _behavior.SetBrainState(FaceStates.Idle);
            if (_reason == ListenReason.FollowUp)
            {
                // Te callaste: la conversación termina sin molestar
                EndConversation(quiet: false);
                return;
            }
            EndConversation(quiet: true);
            _panel.ShowNotice(_reason == ListenReason.WakeWord
                ? "¿Me llamaste? No te escuché bien. Dime «Oye Kroko» otra vez."
                : "No te escuché. Pulsa " + _opt.Hotkey + " y habla cerca del micrófono.");
            return;
        }
        Send(wav);
    }

    private void Send(byte[] wav)
    {
        _session.LastRequestWasVoice = true;
        _panel.OnVoiceSent();
        _behavior.SetBrainState(FaceStates.Thinking);
        _ = SendAsync(wav);
    }

    private async Task SendAsync(byte[] wav)
    {
        if (!await _brain.SendAsync(new UserAudioMessage { AudioBase64 = Convert.ToBase64String(wav), Speak = _opt.WantsVoice(spoken: true) }))
        {
            _session.LastRequestWasVoice = false;
            EndConversation(quiet: true);
            _behavior.SetBrainState(FaceStates.Idle);
            _panel.ShowNotice("Se cortó la conexión con mi cerebro. Inténtalo otra vez en unos segundos.");
        }
    }

    // ---------------------------------------------------------------- conversación

    /// <summary>
    /// En una conversación por voz, las acciones medianas (crear un evento, mover archivos…) se pueden aprobar
    /// diciendo «sí, hazlo» o cancelar con «cancela». Lo grave sigue pidiendo clic o Enter.
    /// </summary>
    private async void OnConfirmShown()
    {
        var tool = _panel.PendingConfirmTool;
        var callId = _panel.PendingConfirmId;
        if (!(_conversation || _live.IsActive) || callId is null || !Kroko.Shared.Tools.VoiceConfirm.IsAllowed(tool)) return;
        _panel.ConfirmVoiceHint = "Te escucho: di «sí, hazlo» o «cancela».";
        var answer = await _wake.AskYesNoAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        if (answer is { } approved) _panel.AnswerByVoice(callId, approved);
        else if (_panel.PendingConfirmId == callId) _panel.ConfirmVoiceHint = "No te escuché: usa los botones.";
    }

    private void OnTranscript(string text)
    {
        if (!_conversation) return;
        var said = WakeWordDetector.Normalize(text);
        if (_opt.EffectiveEndPhrases.Any(p => said.Contains(WakeWordDetector.Normalize(p), StringComparison.Ordinal)))
            _endRequested = true; // Kroko responde (se despide) y ya no vuelve a escuchar
    }

    private void OnReplyFinished(bool interrupted)
    {
        if (!_conversation) return;
        if (interrupted || _endRequested)
        {
            EndConversation(quiet: true);
            return;
        }
        // Un respiro para no oír el final de su propia voz
        _followUpDelay.Stop();
        _followUpDelay.Start();
    }

    private void ContinueConversation()
    {
        if (!_conversation || _recorder.IsRecording || _player.IsPlaying) return;
        // Si hay una tarjeta de confirmación, se espera a que la respondas (con el ratón o Enter/Esc)
        if (_panel.IsConfirming)
        {
            _followUpDelay.Start();
            return;
        }
        StartListening(ListenReason.FollowUp);
    }

    private void EndConversation(bool quiet)
    {
        _followUpDelay.Stop();
        var was = _conversation;
        _conversation = false;
        _endRequested = false;
        if (!was) return;
        _panel.EndConversation(quiet ? null : "Aquí estaré. Dime «Oye Kroko» cuando me necesites.");
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    // ---------------------------------------------------------------- atajo

    private bool IsHotkeyHeld()
    {
        bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        if (!Down((int)_vk)) return false;
        if ((_modifiers & NativeMethods.MOD_CONTROL) != 0 && !Down(0x11)) return false;
        if ((_modifiers & NativeMethods.MOD_ALT) != 0 && !Down(0x12)) return false;
        if ((_modifiers & MOD_SHIFT) != 0 && !Down(0x10)) return false;
        return true;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam == HotkeyId)
        {
            handled = true;
            Toggle();
        }
        return 0;
    }

    private const uint MOD_SHIFT = 0x0004;

    /// <summary>"Ctrl+Space", "Ctrl+Alt+J", "Shift+F2"… → modificadores + tecla virtual.</summary>
    internal static bool TryParseHotkey(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= NativeMethods.MOD_CONTROL; break;
                case "alt": modifiers |= NativeMethods.MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                default:
                    if (!Enum.TryParse<Key>(part, ignoreCase: true, out var key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    public void Dispose()
    {
        _tick.Stop();
        _followUpDelay.Stop();
        _recorder.Stop();
        _wake.Dispose();
        if (_source is null) return;
        if (IsRegistered) NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
