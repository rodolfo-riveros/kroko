using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Kroko.Brain.Apps;
using Kroko.Brain.Email;
using Kroko.Shared.Protocol;
using MailKit;
using MailKit.Search;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Productivity;

/// <summary>Sección "Proactive": avisos que Kroko da por su cuenta.</summary>
public sealed class ProactiveOptions
{
    public const string Section = "Proactive";

    public bool Enabled { get; set; } = true;

    /// <summary>Minutos antes de un evento del calendario para avisar (0 = no avisar).</summary>
    public int MeetingMinutesBefore { get; set; } = 10;

    /// <summary>Avisar de correos nuevos que Gmail marca como importantes.</summary>
    public bool ImportantEmails { get; set; } = true;

    public int EmailCheckMinutes { get; set; } = 5;

    /// <summary>Hora (0–23) del aviso diario de tareas vencidas; -1 = no avisar.</summary>
    public int OverdueHour { get; set; } = 10;

    /// <summary>Horario de silencio (no avisa de correos ni vencidas; las reuniones sí).</summary>
    public int QuietStartHour { get; set; } = 22;
    public int QuietEndHour { get; set; } = 7;
}

/// <summary>
/// Kroko avisa sin que se lo pidan: reuniones que están por empezar, correos importantes que acaban de llegar
/// y tareas vencidas (las suyas, Google Tasks y Trello). Por la App y por Telegram.
/// Cada fuente es opcional: si una cuenta no está conectada, simplemente no se revisa.
/// </summary>
public sealed class ProactiveService(
    IOptionsMonitor<ProactiveOptions> options,
    IEnumerable<INotifier> notifiers,
    ProductivityStore store,
    GoogleAccount google,
    TrelloClient trello,
    EmailService email,
    TimeProvider time,
    ILogger<ProactiveService> log) : BackgroundService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _notified = new();
    private List<(string Key, string Title, DateTimeOffset Start, string? Place)> _events = [];
    private DateTimeOffset _eventsFetched = DateTimeOffset.MinValue;
    private DateTimeOffset _emailChecked = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            if (!options.CurrentValue.Enabled || !notifiers.Any(n => n.IsAvailable)) continue;
            await Safe("calendario", () => CheckMeetingsAsync(stoppingToken));
            await Safe("correo", () => CheckImportantEmailAsync(stoppingToken));
            await Safe("vencidas", () => CheckOverdueAsync(stoppingToken));
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task Safe(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug("Avisos ({What}): {Message}", what, ex.Message);
        }
    }

    private bool IsQuiet(DateTimeOffset now)
    {
        var o = options.CurrentValue;
        var h = now.Hour;
        return o.QuietStartHour > o.QuietEndHour ? h >= o.QuietStartHour || h < o.QuietEndHour : h >= o.QuietStartHour && h < o.QuietEndHour;
    }

    // ------------------------------------------------------------ reuniones

    private async Task CheckMeetingsAsync(CancellationToken ct)
    {
        var minutes = options.CurrentValue.MeetingMinutesBefore;
        if (minutes <= 0 || !google.IsConnected) return;
        var now = time.GetLocalNow();

        // La agenda de las próximas 3 horas se pide cada 5 minutos; el aviso se revisa cada minuto
        if (now - _eventsFetched > TimeSpan.FromMinutes(5))
        {
            _eventsFetched = now;
            var json = await google.GetAsync("https://www.googleapis.com/calendar/v3/calendars/primary/events?singleEvents=true&orderBy=startTime&maxResults=20" +
                $"&timeMin={Uri.EscapeDataString(now.ToString("o"))}&timeMax={Uri.EscapeDataString(now.AddHours(3).ToString("o"))}", ct);
            _events = (json.TryGetProperty("items", out var items) ? items.EnumerateArray() : Enumerable.Empty<JsonElement>())
                .Where(e => e.TryGetProperty("start", out var s) && s.TryGetProperty("dateTime", out _)) // los de todo el día no
                .Select(e => (
                    Key: "evento:" + e.GetProperty("id").GetString(),
                    Title: e.TryGetProperty("summary", out var t) ? t.GetString() ?? "(sin título)" : "(sin título)",
                    Start: DateTimeOffset.Parse(e.GetProperty("start").GetProperty("dateTime").GetString()!, CultureInfo.InvariantCulture),
                    Place: e.TryGetProperty("location", out var l) ? l.GetString() : null))
                .ToList();
        }

        foreach (var ev in _events)
        {
            var left = ev.Start - now;
            if (left < TimeSpan.Zero || left > TimeSpan.FromMinutes(minutes) || !_notified.TryAdd(ev.Key + ev.Start, now)) continue;
            var mins = Math.Max(1, (int)Math.Round(left.TotalMinutes));
            await NotifyAsync("Reunión", $"«{ev.Title}» empieza en {mins} minuto{(mins == 1 ? "" : "s")}" +
                (ev.Place is { Length: > 0 } p ? $" ({p})." : "."), "surprised", ct);
        }
        Cleanup(now);
    }

    // ------------------------------------------------------------ correos importantes

    private async Task CheckImportantEmailAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.ImportantEmails || !email.IsConfigured) return;
        var now = time.GetLocalNow();
        if (now - _emailChecked < TimeSpan.FromMinutes(Math.Max(2, o.EmailCheckMinutes)) || IsQuiet(now)) return;
        _emailChecked = now;

        const string key = "proactive.lastImportantUid";
        var last = uint.TryParse(store.GetValue(key), out var l) ? l : 0u;
        var fresh = await email.WithImapAsync(async client =>
        {
            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
            var uids = await inbox.SearchAsync(SearchQuery.GMailRawSearch("is:important is:unread newer_than:1d"), ct);
            var newest = uids.Count == 0 ? last : uids.Max(u => u.Id);
            var newOnes = last == 0 ? [] : uids.Where(u => u.Id > last).OrderBy(u => u.Id).TakeLast(3).ToList();
            var summaries = newOnes.Count == 0 ? [] : await inbox.FetchAsync(newOnes, MessageSummaryItems.Envelope, ct);
            return (newest, summaries.Select(s => (From: s.Envelope?.From.Mailboxes.FirstOrDefault()?.Name is { Length: > 0 } n ? n
                : s.Envelope?.From.Mailboxes.FirstOrDefault()?.Address ?? "alguien", Subject: s.Envelope?.Subject ?? "(sin asunto)")).ToList());
        }, ct);

        // La primera vez solo se toma nota (no se avisa de correos viejos)
        store.SetValue(key, fresh.newest.ToString(CultureInfo.InvariantCulture));
        foreach (var (from, subject) in fresh.Item2)
            await NotifyAsync("Correo importante", $"Llegó un correo de {from}: «{subject}».", "surprised", ct);
    }

    // ------------------------------------------------------------ tareas vencidas

    private async Task CheckOverdueAsync(CancellationToken ct)
    {
        var hour = options.CurrentValue.OverdueHour;
        var now = time.GetLocalNow();
        if (hour < 0 || now.Hour < hour || IsQuiet(now)) return;
        const string key = "proactive.overdueDay";
        var today = DateOnly.FromDateTime(now.DateTime);
        if (store.GetValue(key) == today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) return;
        store.SetValue(key, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var overdue = store.Tasks(includeDone: false).Where(t => t.DueDate < today).Select(t => t.Text).ToList();

        if (google.IsConnected)
        {
            var lists = await google.GetAsync("https://tasks.googleapis.com/tasks/v1/users/@me/lists?maxResults=100", ct);
            foreach (var list in lists.TryGetProperty("items", out var li) ? li.EnumerateArray() : Enumerable.Empty<JsonElement>())
            {
                var due = Uri.EscapeDataString(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToString("o"));
                var tasks = await google.GetAsync($"https://tasks.googleapis.com/tasks/v1/lists/{Uri.EscapeDataString(list.GetProperty("id").GetString()!)}/tasks?showCompleted=false&dueMax={due}&maxResults=50", ct);
                overdue.AddRange((tasks.TryGetProperty("items", out var ti) ? ti.EnumerateArray() : Enumerable.Empty<JsonElement>())
                    .Where(t => t.TryGetProperty("due", out _)).Select(t => t.GetProperty("title").GetString() ?? ""));
            }
        }

        if (trello.IsConfigured)
        {
            var cards = await trello.GetAsync("members/me/cards", new() { ["filter"] = "open", ["fields"] = "name,due,dueComplete" }, ct);
            overdue.AddRange(cards.EnumerateArray()
                .Where(c => c.TryGetProperty("due", out var d) && d.ValueKind == JsonValueKind.String
                            && DateTimeOffset.Parse(d.GetString()!, CultureInfo.InvariantCulture) < now
                            && !(c.TryGetProperty("dueComplete", out var dc) && dc.ValueKind == JsonValueKind.True))
                .Select(c => c.GetProperty("name").GetString() ?? ""));
        }

        overdue = overdue.Where(t => t.Length > 0).Distinct().ToList();
        if (overdue.Count == 0) return;
        var list3 = string.Join(", ", overdue.Take(3).Select(t => $"«{t}»"));
        await NotifyAsync("Tareas vencidas",
            $"Tienes {overdue.Count} tarea{(overdue.Count == 1 ? "" : "s")} vencida{(overdue.Count == 1 ? "" : "s")}: {list3}{(overdue.Count > 3 ? " y más" : "")}.",
            "oops", ct);
    }

    // ------------------------------------------------------------ utilidades

    private async Task NotifyAsync(string title, string text, string expression, CancellationToken ct)
    {
        log.LogInformation("Aviso proactivo: {Title}", title);
        var msg = new NotifyMessage { Kind = "aviso", Title = title, Text = text, Expression = expression };
        foreach (var n in notifiers.Where(n => n.IsAvailable))
        {
            try
            {
                await n.NotifyAsync(msg, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogDebug("No se pudo avisar por {Channel}: {Message}", n.GetType().Name, ex.Message);
            }
        }
    }

    private void Cleanup(DateTimeOffset now)
    {
        foreach (var old in _notified.Where(kv => now - kv.Value > TimeSpan.FromDays(1)).Select(kv => kv.Key).ToList())
            _notified.TryRemove(old, out _);
    }
}
