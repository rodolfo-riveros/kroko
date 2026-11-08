using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kroko.App.Audio;
using Kroko.App.Services;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kroko.App.ViewModels;

/// <summary>
/// Panel de conversación encima de la burbuja: lo que escribes (gris), la respuesta de Kroko
/// (blanco, con efecto máquina de escribir) y la caja para escribirle.
/// También traduce los mensajes del Brain a la cara (pensando, hablando, expresiones).
/// </summary>
public partial class PanelViewModel : ObservableObject
{
    private readonly BrainConnection _brain;
    private readonly SpeechPlayer _player;
    private readonly LocalTts _localTts;
    private readonly VoiceSession _voice;
    private bool _audioDriven;
    private readonly FocusService _focus;
    private readonly DispatcherTimer _notifyRetry;
    private readonly Queue<(string Title, string Text, string? Expression, string? Audio)> _notifications = new();
    private readonly ToolExecutor _tools;
    private readonly DispatcherTimer _confirmTimeout;
    private ConfirmRequestMessage? _pendingConfirm;
    private readonly CompanionBehavior _behavior;
    private readonly KrokoOptions _opt;
    private readonly ILogger<PanelViewModel> _log;
    private readonly DispatcherTimer _typing;
    private readonly DispatcherTimer _autoHide;

    private string _fullReply = "";
    private double _typed;
    private DateTime _lastTypingTick;
    private string? _pendingExpression;
    private double _speech;

    public PanelViewModel(BrainConnection brain, ToolExecutor tools, CompanionBehavior behavior, IOptions<KrokoOptions> options,
        SpeechPlayer player, LocalTts localTts, VoiceSession voice, TimerService timers, FocusService focus,
        ILogger<PanelViewModel> log)
    {
        Timers = timers;
        _focus = focus;
        timers.Finished += (title, text, expression) => ShowNotification(title, text, expression, null);
        timers.PomodoroPhaseChanged += _ => UpdateFocusFace();
        focus.Changed += _ => UpdateFocusFace();
        _notifyRetry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _notifyRetry.Tick += (_, _) => FlushNotifications();
        _player = player;
        _localTts = localTts;
        _voice = voice;
        // La boca y la antena siguen la amplitud real de la voz (sección 6)
        _player.Level += level => _behavior!.Face.SpeechLevel = level;
        _player.Finished += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(OnSpeechFinished);
        _brain = brain;
        _tools = tools;
        _tools.PausedChanged += OnPausedChanged;
        _confirmTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(58) };
        _confirmTimeout.Tick += (_, _) => Decide(false);
        _behavior = behavior;
        _opt = options.Value;
        _log = log;

        _typing = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _typing.Tick += (_, _) => TypingTick();

        _autoHide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(2, _opt.PanelAutoHideSeconds)) };
        _autoHide.Tick += (_, _) => AutoHideTick();

        _tools.CardReady += card => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ShowCard(card));
        _brain.MessageReceived += OnBrainMessage;
        _brain.ConnectionChanged += OnConnectionChanged;
        UpdateStatus();
    }

    /// <summary>La vista se muestra (activate = true cuando hay que escribir).</summary>
    public event Action<bool>? ShowRequested;

    public event Action? HideRequested;

    [ObservableProperty]
    public partial string UserText { get; set; } = "";

    [ObservableProperty]
    public partial string ReplyText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsReplyError { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string InputText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsThinking { get; set; }

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    /// <summary>La vista lo pone a true mientras el ratón está encima (no se esconde).</summary>
    public bool IsPointerOver { get; set; }

    public bool IsTyping => _typing.IsEnabled;

    // ---------------------------------------------------------------- acciones de la burbuja

    /// <summary>Doble clic en Kroko: abrir el panel para escribir.</summary>
    public void OpenForInput()
    {
        RequestShow(true);
        RestartAutoHide();
    }

    /// <summary>Clic en Kroko sin cerebro: explicar qué pasa (sección 12).</summary>
    public void ExplainOffline()
    {
        UserText = "";
        IsReplyError = true;
        ReplyText = "No me puedo conectar con mi cerebro (Kroko.Brain). Lo sigo intentando solo; " +
                    "si no vuelve, revisa que esté encendido.";
        RequestShow(false);
        RestartAutoHide();
    }

    public void Hide()
    {
        _autoHide.Stop();
        HideRequested?.Invoke();
    }

    private bool _suppressed;

    /// <summary>
    /// Mientras hay un juego o presentación a pantalla completa, el panel no aparece
    /// (Kroko también está escondido). Las confirmaciones pendientes caducan solas.
    /// </summary>
    public void SetSuppressed(bool suppressed)
    {
        _suppressed = suppressed;
        if (suppressed)
        {
            HideRequested?.Invoke();
            return;
        }
        if (IsConfirming || IsTyping) ShowRequested?.Invoke(IsConfirming); // al volver, que no se pierda una tarjeta pendiente
        FlushNotifications(); // y los avisos que esperaban, ahora sí

    }

    private void RequestShow(bool activate)
    {
        if (!_suppressed) ShowRequested?.Invoke(activate);
    }

    /// <summary>Llamado por la vista con cualquier actividad (teclear, mover el ratón…).</summary>
    public void NotifyActivity() => RestartAutoHide();

    // ---------------------------------------------------------------- enviar

    private bool CanSend() => !string.IsNullOrWhiteSpace(InputText) && !IsPaused;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0) return;
        NoteOwnerOrder();

        InputText = "";
        StopTyping(applyPending: false);
        ClearCard();
        UserText = text;
        ReplyText = "";
        IsReplyError = false;
        RestartAutoHide();

        var sent = await _brain.SendAsync(new UserTextMessage { Text = text, Speak = _opt.Voice.WantsVoice(spoken: false) });
        if (!sent)
        {
            _log.LogInformation("Mensaje no enviado: sin conexión con el Brain");
            IsReplyError = true;
            ReplyText = "Ahorita no tengo conexión con mi cerebro. Espera unos segundos y vuelve a intentarlo.";
            _behavior.PlayExpression("sad");
            return;
        }
        IsThinking = true;
    }

    // ---------------------------------------------------------------- mensajes del Brain

    private void OnBrainMessage(KrokoMessage message)
    {
        switch (message)
        {
            case StateMessage s:
                if (s.State == FaceStates.Thinking)
                {
                    IsThinking = true;
                    _behavior.SetBrainState(FaceStates.Thinking);
                }
                else if (s.State == FaceStates.Idle)
                {
                    IsThinking = false;
                    if (!IsTyping) _behavior.SetBrainState(FaceStates.Idle);
                }
                else
                {
                    _behavior.SetBrainState(s.State);
                }
                break;

            case ReplyMessage r:
                IsThinking = false;
                _ = SpeakReplyAsync(r);
                break;

            case ExpressionMessage e:
                if (IsTyping || _audioDriven) _pendingExpression = e.Name;
                else _behavior.PlayExpression(e.Name);
                break;

            case CardMessage card:
                ShowCard(card);
                break;

            case NotifyMessage n:
                ShowNotification(n.Title, n.Text, n.Expression, n.AudioBase64);
                break;

            case ConfirmRequestMessage c:
                ShowConfirm(c);
                break;

            case TranscriptMessage t:
                UserText = t.Text;
                break;
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        _behavior.SetBrainConnected(connected);
        if (!connected && _pendingConfirm is not null)
        {
            // El Brain ya no espera la respuesta: la tarjeta deja de tener sentido
            _confirmTimeout.Stop();
            _pendingConfirm = null;
            IsConfirming = false;
            _behavior.SetConfirming(false);
        }
        if (!connected && IsThinking)
        {
            IsThinking = false;
            IsReplyError = true;
            ReplyText = "Se cortó la conexión con mi cerebro mientras pensaba. Vuelve a preguntarme en un ratito.";
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        IsConnected = _brain.IsConnected;
        StatusText = IsPaused ? "En pausa · Ctrl+Alt+K para reanudar"
            : IsConnected ? "Conectado" : "Sin conexión con el cerebro · reintentando";
    }


    // ---------------------------------------------------------------- confirmación de acciones peligrosas

    [ObservableProperty]
    public partial bool IsConfirming { get; set; }

    [ObservableProperty]
    public partial string ConfirmText { get; set; } = "";

    /// <summary>La vista enfoca el botón "Sí, hazlo" cuando aparece la tarjeta.</summary>
    public event Action? ConfirmShown;

    private void ShowConfirm(ConfirmRequestMessage request)
    {
        if (_pendingConfirm is not null) Decide(false); // nunca dos tarjetas a la vez
        if (IsPaused)
        {
            _ = _brain.SendAsync(new ConfirmResponseMessage { CallId = request.CallId, Approved = false });
            return;
        }

        // Lo pidió el dueño hace un momento y es de las acciones que eligió hacer sin preguntar (apagar, ordenar…):
        // se aprueba sola, por el mismo camino que el botón «Sí, hazlo» (el ejecutor la sigue verificando).
        if (_opt.CanAutoApprove(request.Tool, _lastOwnerOrder, DateTime.Now))
        {
            _log.LogInformation("Aprobada sin tarjeta (la pidió el dueño): {Tool}", request.Tool);
            _tools.RecordDecision(request.CallId, request.Tool, request.Text, approved: true);
            _ = _brain.SendAsync(new ConfirmResponseMessage { CallId = request.CallId, Approved = true });
            return;
        }


        _pendingConfirm = request;
        ConfirmText = request.Text;
        IsConfirming = true;
        IsThinking = false;
        _behavior.SetConfirming(true);
        _confirmTimeout.Stop();
        _confirmTimeout.Start();
        RequestShow(true);
        ConfirmShown?.Invoke();
        RestartAutoHide();
    }

    private DateTime? _lastOwnerOrder;

    /// <summary>El dueño acaba de dar una orden (escrita, por voz o en la conversación en tiempo real).</summary>
    public void NoteOwnerOrder() => _lastOwnerOrder = DateTime.Now;

    /// <summary>Herramienta y id de la tarjeta pendiente (para aprobar por voz solo lo permitido).</summary>
    public string? PendingConfirmTool => _pendingConfirm?.Tool;

    public string? PendingConfirmId => _pendingConfirm?.CallId;

    /// <summary>Indicación bajo la tarjeta cuando Kroko escucha la respuesta («sí, hazlo» o «cancela»).</summary>
    [ObservableProperty]
    public partial string ConfirmVoiceHint { get; set; } = "";

    /// <summary>Respuesta dicha en voz alta: solo vale si la tarjeta sigue siendo la misma.</summary>
    public void AnswerByVoice(string callId, bool approved)
    {
        ConfirmVoiceHint = "";
        if (_pendingConfirm?.CallId == callId) Decide(approved);
    }

    [RelayCommand]
    private void Approve() => Decide(true);

    [RelayCommand]
    private void Deny() => Decide(false);

    private void Decide(bool approved)
    {
        ConfirmVoiceHint = "";
        _confirmTimeout.Stop();
        var request = _pendingConfirm;
        if (request is null) return;
        _pendingConfirm = null;
        IsConfirming = false;

        // Primero se anota en el ejecutor (segunda barrera de seguridad) y luego se avisa al Brain
        _tools.RecordDecision(request.CallId, request.Tool, request.Text, approved);
        _ = _brain.SendAsync(new ConfirmResponseMessage { CallId = request.CallId, Approved = approved });

        _behavior.SetConfirming(false);
        IsThinking = true;
        RestartAutoHide();
    }

    // ---------------------------------------------------------------- pausa de emergencia

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsPaused { get; set; }

    private void OnPausedChanged(bool paused)
    {
        IsPaused = paused;
        UpdateStatus();
        _behavior.SetEmergencyPaused(paused);
        if (paused)
        {
            if (_pendingConfirm is not null) Decide(false);
            StopTyping(applyPending: false);
            IsThinking = false;
            IsReplyError = true;
            UserText = "";
            ReplyText = "Me detuve por completo (interruptor de emergencia). No haré ninguna acción hasta que me reanudes con Ctrl+Alt+K.";
            RequestShow(false);
        }
        else
        {
            IsReplyError = false;
            ReplyText = "¡De vuelta! Ya puedo ayudarte otra vez.";
            RequestShow(false);
        }
        RestartAutoHide();
    }

    // ---------------------------------------------------------------- productividad (Fase 2)

    /// <summary>Temporizador/pomodoro en marcha (tarjeta con anillo en el panel).</summary>
    public TimerService Timers { get; }

    [RelayCommand]
    private void CancelTimer() => Timers.Cancel();

    private void UpdateFocusFace() =>
        _behavior.SetFocusMode(_focus.IsActive || (Timers.IsPomodoro && Timers.IsWorkPhase));

    /// <summary>
    /// Aviso que no responde a una orden (recordatorio, fin del temporizador, resumen del día).
    /// Si Kroko está ocupado o hay un juego a pantalla completa, espera su turno.
    /// </summary>
    public void ShowNotification(string title, string text, string? expression, string? audioBase64)
    {
        _notifications.Enqueue((title, text, expression, audioBase64));
        FlushNotifications();
    }

    private bool IsBusy => IsThinking || IsTyping || IsConfirming || IsListening || _audioDriven || _suppressed;

    private async void FlushNotifications()
    {
        if (_notifications.Count == 0)
        {
            _notifyRetry.Stop();
            return;
        }
        if (IsBusy)
        {
            _notifyRetry.Start();
            return;
        }

        _notifyRetry.Stop();
        var (title, text, expression, audio) = _notifications.Dequeue();
        _behavior.WakeUp();
        IsReplyError = false;
        UserText = "🔔 " + title;
        await SpeakReplyAsync(new ReplyMessage { Text = text, AudioBase64 = audio }, notification: true);
        _pendingExpression = expression;
        if (!IsTyping && !_audioDriven && expression is not null) _behavior.PlayExpression(expression);
        if (_notifications.Count > 0) _notifyRetry.Start();
    }

    // ---------------------------------------------------------------- voz (Fase 1C)

    private double _cps;

    [ObservableProperty]
    public partial bool IsListening { get; set; }

    /// <summary>Hay una conversación por voz en curso (Kroko vuelve a escuchar tras responder).</summary>
    [ObservableProperty]
    public partial bool IsConversation { get; set; }

    /// <summary>Indicación bajo los subtítulos mientras escucha.</summary>
    [ObservableProperty]
    public partial string ListeningHint { get; set; } = "";

    /// <summary>Terminó de hablar una respuesta (interrupted = la cortaste). El controlador de voz decide si sigue escuchando.</summary>
    public event Action<bool>? ReplyFinished;

    private bool _interrupted;

    /// <summary>Empieza a escuchar: el panel muestra "Te escucho…" (o "¿Algo más?" si sigue la conversación).</summary>
    public void ShowListening(bool followUp = false)
    {
        StopSpeaking();
        IsListening = true;
        IsReplyError = false;
        ClearCard();
        UserText = "";
        ListeningHint = followUp ? "¿Algo más? Te escucho · di «gracias» para terminar" : "Te escucho…";
        ReplyText = "";
        RequestShow(false);
        RestartAutoHide();
    }

    /// <summary>Terminó de escuchar y mandó el audio: ahora piensa.</summary>
    public void OnVoiceSent()
    {
        NoteOwnerOrder();
        IsListening = false;
        ListeningHint = "";
        if (UserText.Length == 0) UserText = "…";
        IsThinking = true;
        RestartAutoHide();
    }

    // ---------------------------------------------------------------- tarjetas (lista / estado del equipo)

    public ObservableCollection<CardItem> CardItems { get; } = [];

    [ObservableProperty]
    public partial string CardTitle { get; set; } = "";

    [ObservableProperty]
    public partial bool HasCard { get; set; }

    [ObservableProperty]
    public partial bool IsEquipmentCard { get; set; }

    /// <summary>Nivel del micrófono (0–1) para la onda de voz del panel.</summary>
    [ObservableProperty]
    public partial double MicLevel { get; set; }

    public void ShowCard(CardMessage card)
    {
        CardItems.Clear();
        foreach (var item in card.Items) CardItems.Add(item);
        CardTitle = card.Title;
        IsEquipmentCard = card.Kind == "equipo";
        HasCard = CardItems.Count > 0;
        RequestShow(false);
        RestartAutoHide();
    }

    private void ClearCard()
    {
        HasCard = false;
        CardItems.Clear();
    }

    /// <summary>Subtítulo en vivo de lo que estás diciendo (reconocimiento local).</summary>
    public void ShowCaption(string text)
    {
        if (!IsListening) return;
        UserText = text;
        RestartAutoHide();
    }

    /// <summary>Voz en tiempo real: lo que Kroko va diciendo aparece mientras habla.</summary>
    public void ShowLiveReply(string text)
    {
        StopTyping(applyPending: false);
        IsThinking = false;
        IsListening = false;
        ListeningHint = "";
        IsReplyError = false;
        ReplyText = text;
        RequestShow(false);
        RestartAutoHide();
    }

    /// <summary>Voz en tiempo real: vuelve a escuchar (sin borrar lo último que dijo Kroko).</summary>
    public void ShowLiveListening()
    {
        IsListening = true;
        IsConversation = true;
        ListeningHint = "En conversación · habla cuando quieras";
        RequestShow(false);
        RestartAutoHide();
    }

    /// <summary>Se acabó la conversación (te callaste o dijiste «gracias»).</summary>
    public void EndConversation(string? farewell = null)
    {
        IsConversation = false;
        IsListening = false;
        ListeningHint = "";
        if (farewell is not null)
        {
            UserText = "";
            IsReplyError = false;
            ReplyText = farewell;
        }
        RestartAutoHide();
    }

    /// <summary>Un aviso corto en el panel (sin micrófono, no te escuché…).</summary>
    public void ShowNotice(string text)
    {
        IsListening = false;
        ListeningHint = "";
        IsThinking = false;
        UserText = "";
        IsReplyError = true;
        ReplyText = text;
        RequestShow(false);
        RestartAutoHide();
    }

    /// <summary>Corta la voz (Esc o el atajo mientras habla).</summary>
    public void StopSpeaking()
    {
        if (!_player.IsPlaying && !_audioDriven) return;
        _interrupted = true;
        _player.Stop();
        OnSpeechFinished();
    }

    /// <param name="notification">Aviso que no responde a una orden: solo depende de si Kroko habla o está en "solo chat".</param>
    private async Task SpeakReplyAsync(ReplyMessage r, bool notification = false)
    {
        var spoken = !notification && _voice.LastRequestWasVoice;
        if (!notification) _voice.LastRequestWasVoice = false;
        // Modo "solo chat" (Ajustes o «cállate»): nunca suena, aunque el Brain haya mandado audio
        var wantsVoice = notification ? _opt.Voice.RepliesWithVoice : _opt.Voice.WantsVoice(spoken);

        byte[]? wav = null;
        if (wantsVoice && !string.IsNullOrEmpty(r.AudioBase64))
        {
            try { wav = Convert.FromBase64String(r.AudioBase64); }
            catch (FormatException) { wav = null; }
        }
        // Respaldo: la voz de Windows si el Brain no mandó audio. Solo cuando le hablaste o es un aviso: a lo que
        // escribes, sin la voz de Gemini (cerebro antiguo, cuota agotada…) se responde solo por escrito.
        if (wav is null && wantsVoice && (spoken || notification) && _opt.Voice.LocalTtsFallback)
            wav = await Task.Run(() => _localTts.Synthesize(r.Text));

        var duration = wav is null ? null : _player.Play(wav);
        if (duration is { TotalSeconds: > 0.3 } d)
        {
            _audioDriven = true;
            // El texto se escribe al ritmo de la voz
            StartTyping(r.Text, r.IsError, r.Text.Length / Math.Max(0.5, d.TotalSeconds * 0.92));
        }
        else
        {
            _audioDriven = false;
            StartTyping(r.Text, r.IsError);
            // Respuesta sin voz a una orden hablada: la conversación por voz termina (no se queda esperando)
            if (spoken) ReplyFinished?.Invoke(true);
        }
    }

    private void OnSpeechFinished()
    {
        if (!_audioDriven) return;
        _audioDriven = false;
        if (_typing.IsEnabled)
        {
            _typing.Stop();
            ReplyText = _fullReply;
        }
        _behavior.Face.SpeechLevel = 0;
        _behavior.SetBrainState(FaceStates.Idle);
        if (_pendingExpression is { } expr) _behavior.PlayExpression(expr);
        _pendingExpression = null;
        RestartAutoHide();
        var interrupted = _interrupted;
        _interrupted = false;
        ReplyFinished?.Invoke(interrupted);
    }

    // ---------------------------------------------------------------- efecto máquina de escribir

    private void StartTyping(string text, bool isError, double? charsPerSecond = null)
    {
        _cps = charsPerSecond ?? Math.Max(5, _opt.TypingCharsPerSecond);
        _fullReply = text;
        _typed = 0;
        _lastTypingTick = DateTime.Now;
        _pendingExpression = null;
        IsReplyError = isError;
        ReplyText = "";
        RequestShow(false);
        _behavior.SetBrainState(FaceStates.Speaking);
        _typing.Start();
        RestartAutoHide();
    }

    private void TypingTick()
    {
        var now = DateTime.Now;
        var dt = (now - _lastTypingTick).TotalSeconds;
        _lastTypingTick = now;

        var before = (int)_typed;
        _typed = Math.Min(_fullReply.Length, _typed + dt * _cps);
        var after = (int)_typed;

        // Sin audio, la boca se mueve con las vocales; con audio la mueve la amplitud real
        if (!_audioDriven)
        {
            for (var i = before; i < after; i++)
                _speech = "aeiouáéíóúAEIOUÁÉÍÓÚ".Contains(_fullReply[i]) ? 0.9
                    : char.IsLetterOrDigit(_fullReply[i]) ? 0.4 : 0.05;
            _speech *= Math.Exp(-dt * 6);
            _behavior.Face.SpeechLevel = _speech;
        }

        if (after != before) ReplyText = _fullReply[..after];
        if (after >= _fullReply.Length) StopTyping(applyPending: true);
    }

    private void StopTyping(bool applyPending)
    {
        if (!_typing.IsEnabled) return;
        _typing.Stop();
        ReplyText = _fullReply;
        if (_audioDriven && _player.IsPlaying)
        {
            RestartAutoHide();
            return; // la cara sigue "hablando" hasta que termine la voz
        }
        _behavior.Face.SpeechLevel = 0;
        _behavior.SetBrainState(FaceStates.Idle);
        if (applyPending && _pendingExpression is { } expr) _behavior.PlayExpression(expr);
        _pendingExpression = null;
        RestartAutoHide();
    }

    // ---------------------------------------------------------------- auto-ocultar

    private void RestartAutoHide()
    {
        _autoHide.Stop();
        _autoHide.Start();
    }

    private void AutoHideTick()
    {
        // No se esconde mientras piensa, escribe, tienes texto a medio escribir o el ratón está encima.
        if (IsThinking || IsTyping || _player.IsPlaying || IsListening || IsConfirming || !string.IsNullOrWhiteSpace(InputText) || IsPointerOver) return;
        Hide();
    }
}
