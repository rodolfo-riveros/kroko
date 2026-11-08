using System.Collections.Concurrent;
using Kroko.Brain.Agent;
using Kroko.Brain.Productivity;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Kroko.Brain.Telegram;

/// <summary>
/// Sección "Telegram". El token del bot NO va aquí: user-secrets, variable de entorno
/// Telegram__BotToken o TELEGRAM_BOT_TOKEN en el .env.
/// </summary>
public sealed class TelegramOptions
{
    public const string Section = "Telegram";

    public string? BotToken { get; set; }

    /// <summary>Solo estos chats pueden hablar con Kroko (el id se ve escribiéndole al bot).</summary>
    public List<long> AllowedChatIds { get; set; } = [];

    /// <summary>Mandar también los recordatorios y el resumen del día al móvil.</summary>
    public bool SendNotifications { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken);
}

/// <summary>
/// Bot de Telegram (Fase 5): el dueño le escribe (o le manda notas de voz) desde el móvil, Kroko responde con
/// las mismas herramientas y las mismas reglas: lo peligroso se confirma con botones "Sí, hazlo" / "Cancelar".
/// Lo que toca la PC lo ejecuta la App de escritorio conectada.
/// </summary>
public sealed class TelegramBot(
    IOptionsMonitor<TelegramOptions> options,
    IServiceProvider services,
    AppConnections apps,
    Config.SecretStore secrets,
    TimeProvider time,
    ILogger<TelegramBot> log) : BackgroundService, INotifier, ITelegramDelivery
{
    private static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(10);
    private readonly Lock _pairingLock = new();
    private (string Code, DateTimeOffset Expires)? _pairing;
    private volatile CancellationTokenSource? _restart;
    private volatile string? _activeToken;

    /// <summary>@usuario del bot cuando está conectado (null si no).</summary>
    public string? BotUsername { get; private set; }

    public bool IsRunning => _bot is not null;

    public IReadOnlyList<long> LinkedChats => options.CurrentValue.AllowedChatIds;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingConfirms = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();
    private TelegramBotClient? _bot;

    public bool IsAvailable => _bot is not null && options.CurrentValue.SendNotifications && options.CurrentValue.AllowedChatIds.Count > 0;

    public async Task<bool> NotifyAsync(NotifyMessage message, CancellationToken ct)
    {
        if (!IsAvailable) return false;
        var sent = false;
        foreach (var chat in options.CurrentValue.AllowedChatIds)
        {
            try
            {
                await _bot!.SendMessage(chat, $"🔔 {message.Title}\n{message.Text}", cancellationToken: ct);
                sent = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Telegram: no se pudo avisar a {Chat}: {Message}", chat, ex.Message);
            }
        }
        return sent;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Si el dueño cambia el token en "Configurar Kroko", el bot se reinicia solo con el nuevo
        using var watch = options.OnChange(o =>
        {
            if (string.Equals(o.BotToken, _activeToken, StringComparison.Ordinal)) return;
            try { _restart?.Cancel(); } catch (ObjectDisposedException) { }
        });

        while (!stoppingToken.IsCancellationRequested)
        {
            using var restart = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _restart = restart;
            _activeToken = options.CurrentValue.BotToken;
            try
            {
                await RunAsync(restart.Token);
                // Sin token o token inválido: se espera a que cambie
                await Task.Delay(Timeout.Infinite, restart.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogInformation("Telegram: el token cambió, reiniciando el bot");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            finally
            {
                _bot = null;
                BotUsername = null;
            }
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var opt = options.CurrentValue;
        if (!opt.IsConfigured)
        {
            log.LogInformation("Telegram no configurado (falta Telegram:BotToken)");
            return;
        }

        var bot = new TelegramBotClient(opt.BotToken!, cancellationToken: stoppingToken);
        try
        {
            var me = await bot.GetMe(stoppingToken);
            await bot.DeleteWebhook(cancellationToken: stoppingToken);
            BotUsername = me.Username;
            _bot = bot;
            log.LogInformation("Telegram listo: @{Bot}", me.Username);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError("Telegram: el token no funciona ({Message})", ex.Message);
            return;
        }

        int? offset = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            Update[] updates;
            try
            {
                updates = await bot.GetUpdates(offset, 50, 30, [UpdateType.Message, UpdateType.CallbackQuery], stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning("Telegram: error leyendo mensajes ({Message}); reintento en 10 s", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }

            foreach (var u in updates)
            {
                offset = u.Id + 1;
                try
                {
                    if (u.CallbackQuery is { } cb) await OnCallbackAsync(cb, stoppingToken);
                    else if (u.Message is { } m) await OnMessageAsync(m, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Telegram: error procesando un mensaje");
                }
            }
        }
    }

    private async Task OnMessageAsync(Message m, CancellationToken ct)
    {
        var chat = m.Chat.Id;
        if (!options.CurrentValue.AllowedChatIds.Contains(chat))
        {
            // Vinculación: el dueño abre el enlace del asistente ("/start 123456") o escribe el código
            if (TryConsumePairingCode(m.Text))
            {
                Link(chat);
                var name = m.From?.FirstName is { Length: > 0 } n ? $", {n}" : "";
                await _bot!.SendMessage(chat, $"¡Listo{name}! Ya estamos vinculados. Escríbeme o mándame notas de voz cuando quieras.", cancellationToken: ct);
                return;
            }
            // Nadie más puede usar a Kroko
            log.LogWarning("Telegram: mensaje de un chat no autorizado ({Chat})", chat);
            await _bot!.SendMessage(chat, "Hola. Todavía no estoy vinculado contigo. En tu PC: clic derecho en Kroko → Configurar Kroko → Telegram → Vincular.", cancellationToken: ct);
            return;
        }

        byte[]? audio = null;
        if (m.Voice is { } voice)
        {
            using var ms = new MemoryStream();
            var file = await _bot!.GetFile(voice.FileId, ct);
            await _bot!.DownloadFile(file.FilePath!, ms, ct);
            audio = ms.ToArray();
        }
        else if (string.IsNullOrWhiteSpace(m.Text))
        {
            return;
        }

        // Una orden nueva cancela la anterior de ese chat (igual que en la PC)
        if (_running.TryRemove(chat, out var previous)) await previous.CancelAsync();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[chat] = cts;

        var channel = new TelegramChannel(_bot!, chat, this, apps);
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = services.CreateScope();
                var agent = scope.ServiceProvider.GetRequiredService<AgentLoop>();
                if (audio is not null) await agent.HandleUserAudioAsync(audio, channel, cts.Token, "audio/ogg", speakReply: false);
                else await agent.HandleUserTextAsync(m.Text!, channel, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Telegram: error en el agente");
            }
            finally
            {
                _running.TryRemove(new KeyValuePair<long, CancellationTokenSource>(chat, cts));
                cts.Dispose();
            }
        }, CancellationToken.None);
    }

    private async Task OnCallbackAsync(CallbackQuery cb, CancellationToken ct)
    {
        if (cb.Message is null || !options.CurrentValue.AllowedChatIds.Contains(cb.Message.Chat.Id)) return;
        var data = cb.Data ?? "";
        var approved = data.StartsWith("si:", StringComparison.Ordinal);
        var key = data.Length > 3 ? data[3..] : "";
        if (_pendingConfirms.TryRemove(key, out var tcs))
        {
            tcs.TrySetResult(approved);
            await _bot!.AnswerCallbackQuery(cb.Id, approved ? "Hecho, voy." : "Cancelado.", cancellationToken: ct);
            await _bot!.EditMessageText(cb.Message.Chat.Id, cb.Message.Id,
                (cb.Message.Text ?? "") + (approved ? "\n\n✅ Aprobado" : "\n\n❌ Cancelado"), cancellationToken: ct);
        }
        else
        {
            await _bot!.AnswerCallbackQuery(cb.Id, "Esta pregunta ya caducó.", cancellationToken: ct);
        }
    }

    /// <summary>
    /// enviar_archivo: el archivo va al chat desde el que se pidió o, si se pidió en la PC, al chat del dueño.
    /// Nunca a un chat que no esté vinculado.
    /// </summary>
    public async Task<string?> SendFileAsync(IAgentChannel channel, FileAttachment file, CancellationToken ct)
    {
        if (_bot is not { } bot) return "Telegram no está conectado: pon el token del bot en Configurar Kroko → Telegram.";
        long? chat = channel is TelegramChannel tc ? tc.ChatId
            : options.CurrentValue.AllowedChatIds.Count > 0 ? options.CurrentValue.AllowedChatIds[0] : null;
        if (chat is not { } target || !options.CurrentValue.AllowedChatIds.Contains(target))
            return "Todavía no vinculaste tu Telegram con Kroko (Configurar Kroko → Telegram → Vincular).";
        try
        {
            using var stream = new MemoryStream(file.Data);
            var caption = string.IsNullOrWhiteSpace(file.Caption) ? null : file.Caption.Length > 1000 ? file.Caption[..1000] + "…" : file.Caption;
            await bot.SendChatAction(target, ChatAction.UploadDocument, cancellationToken: ct);
            await bot.SendDocument(target, InputFile.FromStream(stream, file.Name), caption: caption, cancellationToken: ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Telegram: no se pudo enviar el archivo {Name} ({Message})", file.Name, ex.Message);
            return $"Telegram no aceptó {file.Name} ({ex.Message}).";
        }
    }

    public async Task SendImageIfTelegramAsync(IAgentChannel channel, byte[] image, string? caption, CancellationToken ct)
    {
        if (_bot is not { } bot || channel is not TelegramChannel tc) return;
        try
        {
            using var stream = new MemoryStream(image);
            await bot.SendPhoto(tc.ChatId, InputFile.FromStream(stream, "pantalla.jpg"), caption: caption, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Telegram: no se pudo enviar la imagen ({Message})", ex.Message);
        }
    }

    /// <summary>Mensaje al dueño (primer chat vinculado). Telegram corta a 4096 caracteres.</summary>
    public async Task<bool> SendToOwnerAsync(string text, CancellationToken ct)
    {
        if (_bot is not { } bot || options.CurrentValue.AllowedChatIds.Count == 0) return false;
        try
        {
            await bot.SendMessage(options.CurrentValue.AllowedChatIds[0], text.Length > 4000 ? text[..4000] + "…" : text, cancellationToken: ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Telegram: no se pudo enviar ({Message})", ex.Message);
            return false;
        }
    }

    /// <summary>Pregunta "¿Lo hago?" al dueño con botones (null = no respondió).</summary>
    public Task<bool?> AskOwnerAsync(string text, CancellationToken ct) =>
        _bot is null || options.CurrentValue.AllowedChatIds.Count == 0
            ? Task.FromResult<bool?>(null)
            : AskAsync(options.CurrentValue.AllowedChatIds[0], text, ct);

    /// <summary>Pregunta con botones y espera la respuesta (60 s).</summary>
    internal async Task<bool?> AskAsync(long chat, string text, CancellationToken ct)
    {
        var key = Guid.NewGuid().ToString("N")[..16];
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingConfirms[key] = tcs;
        var buttons = new InlineKeyboardMarkup([
            [InlineKeyboardButton.WithCallbackData("Sí, hazlo", "si:" + key), InlineKeyboardButton.WithCallbackData("Cancelar", "no:" + key)],
        ]);
        await _bot!.SendMessage(chat, "¿Lo hago?\n" + text, replyMarkup: buttons, cancellationToken: ct);
        try
        {
            return await tcs.Task.WaitAsync(ToolConfirmation.Timeout, ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _pendingConfirms.TryRemove(key, out _);
        }
    }

    /// <summary>Crea un código de 6 cifras (válido 10 min) para vincular el Telegram del dueño.</summary>
    public (string Code, DateTimeOffset Expires) StartPairing()
    {
        var code = System.Security.Cryptography.RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();
        var pairing = (code, time.GetUtcNow() + PairingLifetime);
        lock (_pairingLock) _pairing = pairing;
        return pairing;
    }

    internal bool TryConsumePairingCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var given = text.Trim();
        if (given.StartsWith("/start", StringComparison.OrdinalIgnoreCase)) given = given[6..].Trim();
        lock (_pairingLock)
        {
            if (_pairing is not { } p || time.GetUtcNow() > p.Expires) return false;
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(given), System.Text.Encoding.ASCII.GetBytes(p.Code)))
                return false;
            _pairing = null; // un solo uso
            return true;
        }
    }

    /// <summary>Agrega el chat a la lista permitida y lo guarda (cifrado) para siempre.</summary>
    private void Link(long chat)
    {
        var chats = options.CurrentValue.AllowedChatIds.Append(chat).Distinct().Select(c => c.ToString());
        secrets.ReplaceList("Telegram:AllowedChatIds", chats);
        log.LogInformation("Telegram vinculado con el chat {Chat}", chat);
    }

    /// <summary>Quita todos los chats vinculados.</summary>
    public void Unlink() => secrets.ReplaceList("Telegram:AllowedChatIds", []);

    public bool PairingPending
    {
        get { lock (_pairingLock) return _pairing is { } p && time.GetUtcNow() <= p.Expires; }
    }
}

/// <summary>
/// Canal del agente para un chat de Telegram: las respuestas van al chat, las confirmaciones son botones
/// y las herramientas de la PC se piden a la App conectada.
/// </summary>
internal sealed class TelegramChannel(TelegramBotClient bot, long chat, TelegramBot owner, AppConnections apps) : IAgentChannel
{
    public long ChatId => chat;

    private readonly ConcurrentDictionary<string, bool> _approved = new();

    public string SessionId => $"telegram-{chat}";

    public async Task SendAsync(KrokoMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case ReplyMessage r:
                await bot.SendMessage(chat, r.Text, cancellationToken: ct);
                break;
            case CardMessage card when card.Items.Count > 0:
                // Las tarjetas del panel (tareas, hábitos, estado del equipo) también llegan al chat, como texto
                await bot.SendMessage(chat, TelegramFormat.Card(card), cancellationToken: ct);
                break;
            case StateMessage { State: FaceStates.Thinking }:
                await bot.SendChatAction(chat, ChatAction.Typing, cancellationToken: ct);
                break;
        }
    }

    public async Task<TResponse?> RequestAsync<TResponse>(KrokoMessage request, string callId, TimeSpan timeout, CancellationToken ct)
        where TResponse : KrokoMessage
    {
        switch (request)
        {
            case ConfirmRequestMessage confirm:
            {
                var answer = await owner.AskAsync(chat, confirm.Text, ct);
                if (answer is null) return null;
                if (answer.Value) _approved[callId] = true;
                return new ConfirmResponseMessage { CallId = callId, Approved = answer.Value } as TResponse;
            }
            case ToolCallMessage call:
            {
                // Las "manos" están en la PC: sin App conectada no se puede
                if (apps.Primary is not { } app)
                {
                    return new ToolResultMessage
                    {
                        CallId = callId,
                        Ok = false,
                        Error = "Kroko no está abierto en la PC (o la PC está apagada): no puedo hacer eso desde aquí.",
                    } as TResponse;
                }
                var forwarded = _approved.TryRemove(callId, out _) ? call with { ApprovedRemotely = "Telegram" } : call;
                return await app.RequestAsync<TResponse>(forwarded with { SessionId = app.SessionId }, callId, timeout, ct);
            }
            default:
                return null;
        }
    }
}

/// <summary>Cómo se ven en el chat las cosas que en la PC son tarjetas del panel.</summary>
public static class TelegramFormat
{
    public static string Card(CardMessage card)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(card.Title)) sb.AppendLine((card.Kind == "equipo" ? "💻 " : "📋 ") + card.Title.Trim());
        foreach (var item in card.Items.Take(40))
        {
            var mark = item.Done switch { true => "✅", false => "⬜", _ => "•" };
            var line = $"{mark} {item.Text}";
            if (item.Value is { } v) line += $"  {Bar(v)} {Math.Round(Math.Clamp(v, 0, 1) * 100)}%";
            if (!string.IsNullOrWhiteSpace(item.Detail)) line += $" — {item.Detail}";
            sb.AppendLine(line);
        }
        if (card.Items.Count > 40) sb.AppendLine($"… y {card.Items.Count - 40} más");
        return sb.ToString().TrimEnd();
    }

    private static string Bar(double value)
    {
        var filled = (int)Math.Round(Math.Clamp(value, 0, 1) * 10);
        return new string('▰', filled) + new string('▱', 10 - filled);
    }
}
