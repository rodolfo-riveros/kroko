using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Kroko.Brain.Agent;
using Kroko.Brain.Providers;
using Kroko.Brain.Tts;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Productivity;

/// <summary>Sección "Productivity" de appsettings.json.</summary>
public sealed class ProductivityOptions
{
    public const string Section = "Productivity";

    /// <summary>Hora (0–23) del resumen automático del día; -1 = desactivado.</summary>
    public int DailySummaryHour { get; set; } = 8;

    /// <summary>Decir los recordatorios en voz alta (además de mostrarlos).</summary>
    public bool SpeakNotifications { get; set; } = true;
}

/// <summary>Apps de escritorio conectadas ahora mismo (para avisos y para que Telegram use sus "manos").</summary>
public sealed class AppConnections
{
    private readonly ConcurrentDictionary<Guid, IAgentChannel> _channels = new();
    private Guid? _latest;

    public bool Any => !_channels.IsEmpty;

    /// <summary>La App conectada más recientemente (la que ejecuta herramientas pedidas desde Telegram).</summary>
    public IAgentChannel? Primary => _latest is { } id && _channels.TryGetValue(id, out var c) ? c : _channels.Values.FirstOrDefault();

    public Guid Add(IAgentChannel channel)
    {
        var id = Guid.NewGuid();
        _channels[id] = channel;
        _latest = id;
        return id;
    }

    public void Remove(Guid id) => _channels.TryRemove(id, out _);

    public async Task<bool> BroadcastAsync(KrokoMessage message, CancellationToken ct)
    {
        var delivered = false;
        foreach (var c in _channels.Values)
        {
            try
            {
                await c.SendAsync(message with { SessionId = c.SessionId }, ct);
                delivered = true;
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                // Esa conexión se está cerrando; las demás siguen
            }
        }
        return delivered;
    }
}

/// <summary>Un canal por el que Kroko puede avisar a su dueño (la App, Telegram…).</summary>
public interface INotifier
{
    /// <summary>¿Hay ahora mismo a quién avisar por este canal?</summary>
    bool IsAvailable { get; }

    Task<bool> NotifyAsync(NotifyMessage message, CancellationToken ct);
}

public sealed class AppNotifier(AppConnections apps) : INotifier
{
    public bool IsAvailable => apps.Any;

    public Task<bool> NotifyAsync(NotifyMessage message, CancellationToken ct) => apps.BroadcastAsync(message, ct);
}

/// <summary>
/// Revisa cada 15 s los recordatorios vencidos y, una vez al día, prepara el resumen de la mañana.
/// Si no hay a quién avisar (App apagada y sin Telegram), espera: nada se pierde.
/// </summary>
public sealed class ReminderScheduler(
    ProductivityStore store,
    IEnumerable<INotifier> notifiers,
    IServiceProvider services,
    IOptionsMonitor<ProductivityOptions> options,
    TimeProvider time,
    Owner.OwnerProfileStore owner,
    ILogger<ReminderScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Error revisando recordatorios");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        // Sin nadie a quien avisar, no se gasta nada: lo pendiente se entrega al reconectar
        if (!notifiers.Any(n => n.IsAvailable)) return;

        foreach (var r in store.DueReminders())
        {
            var msg = new NotifyMessage
            {
                Kind = "recordatorio",
                Title = "Recordatorio",
                Text = r.Text,
                Expression = "surprised",
                AudioBase64 = await SpeakAsync($"Oye{Greeting()}, te recuerdo: {r.Text}", ct),
            };
            if (await SendAsync(msg, ct))
            {
                store.MarkDelivered(r);
                log.LogInformation("Recordatorio {Id} entregado", r.Id);
            }
        }

        await MaybeDailySummaryAsync(ct);
    }

    private async Task MaybeDailySummaryAsync(CancellationToken ct)
    {
        var hour = options.CurrentValue.DailySummaryHour;
        var now = time.GetLocalNow();
        var today = DateOnly.FromDateTime(now.DateTime).ToString("yyyy-MM-dd");
        if (hour < 0 || now.Hour < hour || store.GetValue("resumen:ultimo") == today) return;

        var data = JsonSerializer.Serialize(ResumenDelDiaTool.Build(store, DateOnly.FromDateTime(now.DateTime), now));
        string text;
        try
        {
            using var scope = services.CreateScope();
            var llm = scope.ServiceProvider.GetRequiredService<IConfiguredLlm>();
            var res = await llm.ChatAsync(
                Agent.SystemPrompt.Personalize("Eres Kroko, compañero de escritorio de {nombre}{ubicacion}. Español {acento}, cálido y breve.", owner.Get()),
                [ChatMessage.User($"Escribe el resumen de buenos días para {Who()} en 2–4 frases, sin listas largas ni Markdown, con lo más importante de hoy. Datos: {data}")],
                [], ct);
            text = ExpressionTag.Extract(res.Text).Text;
        }
        catch (Exception ex) when (ex is LlmProviderException or NoLlmConfiguredException)
        {
            text = $"¡Buenos días{Greeting()}! Hoy no pude preparar el resumen con la IA, pero tus tareas siguen en tu lista.";
        }

        var msg = new NotifyMessage
        {
            Kind = "resumen",
            Title = "Resumen del día",
            Text = text,
            Expression = "happy",
            AudioBase64 = await SpeakAsync(text, ct),
        };
        if (await SendAsync(msg, ct))
        {
            store.SetValue("resumen:ultimo", today);
            log.LogInformation("Resumen del día entregado");
        }
    }

    private async Task<bool> SendAsync(NotifyMessage msg, CancellationToken ct)
    {
        var delivered = false;
        foreach (var n in notifiers)
            delivered |= await n.NotifyAsync(msg, ct);
        return delivered;
    }

    /// <summary>", Rodolfo" o nada si todavía no sabe el nombre.</summary>
    private string Greeting() => owner.Get() is { IsComplete: true } p ? ", " + p.Nickname : "";

    private string Who() => owner.Get() is { IsComplete: true } p ? p.Nickname : "tu dueño";

    private async Task<string?> SpeakAsync(string text, CancellationToken ct)
    {
        if (!options.CurrentValue.SpeakNotifications) return null;
        using var scope = services.CreateScope();
        var tts = scope.ServiceProvider.GetRequiredService<ITtsProvider>();
        var wav = await tts.SynthesizeAsync(text, ct);
        return wav is null ? null : Convert.ToBase64String(wav);
    }
}
