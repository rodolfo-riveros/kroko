using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Kroko.Brain.Agent;
using Kroko.Brain.Apps;
using Kroko.Brain.Owner;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Productivity;

public sealed record MyDayWeather(string Ciudad, double? Temperatura, double? Maxima, double? Minima, int? Lluvia, string Descripcion);
public sealed record MyDayEvent(string Hora, string Titulo, string? Lugar);
public sealed record MyDayTask(string Texto, string Fuente, string? Vence, bool Vencida);
public sealed record MyDayReminder(string Hora, string Texto);
public sealed record MyDayHabit(string Nombre, bool Hecho, int Racha);

public sealed record MyDay(string Fecha, string Saludo, MyDayWeather? Clima, IReadOnlyList<MyDayEvent> Agenda,
    IReadOnlyList<MyDayTask> Tareas, IReadOnlyList<MyDayReminder> Recordatorios, IReadOnlyList<MyDayHabit> Habitos,
    IReadOnlyList<string> Avisos);

/// <summary>
/// "Mi día": agenda, tareas (Kroko, Google Tasks, Trello), recordatorios, hábitos y clima en una sola respuesta.
/// Cada fuente es opcional y un fallo en una no estropea las demás (se anota en "avisos").
/// </summary>
public sealed class MyDayService(ProductivityStore store, GoogleAccount google, TrelloClient trello, OwnerProfileStore owner,
    IHttpClientFactory httpFactory, TimeProvider time, ILogger<MyDayService> log)
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-PE");
    private (string City, MyDayWeather Weather, DateTimeOffset At)? _weatherCache;

    public async Task<MyDay> BuildAsync(CancellationToken ct)
    {
        var now = time.GetLocalNow();
        var today = DateOnly.FromDateTime(now.DateTime);
        var notes = new List<string>();
        var profile = owner.Get();
        var name = profile.IsComplete ? ", " + profile.Nickname : "";
        var greeting = now.Hour switch { < 12 => $"Buenos días{name}", < 19 => $"Buenas tardes{name}", _ => $"Buenas noches{name}" };

        // El clima puede no existir (sin ciudad en el perfil): se acepta null
        var weatherTask = Try<MyDayWeather>(async () => (await WeatherAsync(profile.City, ct))!, "clima", notes);
        var agendaTask = google.IsConnected ? Try(() => AgendaAsync(now, ct), "Google Calendar", notes) : Task.FromResult<List<MyDayEvent>?>(null);
        var googleTasks = google.IsConnected ? Try(() => GoogleTasksAsync(today, ct), "Google Tasks", notes) : Task.FromResult<List<MyDayTask>?>(null);
        var trelloTasks = trello.IsConfigured ? Try(() => TrelloAsync(now, ct), "Trello", notes) : Task.FromResult<List<MyDayTask>?>(null);
        await Task.WhenAll(weatherTask, agendaTask, googleTasks, trelloTasks);

        var tasks = store.Tasks(includeDone: false)
            .Where(t => t.DueDate is null || t.DueDate <= today)
            .Select(t => new MyDayTask(t.Text, "Kroko", t.DueDate?.ToString("d MMM", Es), t.DueDate < today))
            .Concat(googleTasks.Result ?? [])
            .Concat(trelloTasks.Result ?? [])
            .OrderByDescending(t => t.Vencida).ThenBy(t => t.Vence is null)
            .Take(20).ToList();

        var reminders = store.PendingReminders()
            .Where(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.Due, time.LocalTimeZone).DateTime) == today)
            .Select(r => new MyDayReminder(TimeZoneInfo.ConvertTime(r.Due, time.LocalTimeZone).ToString("HH:mm"), r.Text)).ToList();

        var habits = store.Habits().Select(h => new MyDayHabit(h.Name, h.DoneToday, h.Streak)).ToList();

        if (!google.IsConnected) notes.Add("Conecta tu cuenta de Google para ver tu agenda y Google Tasks.");
        return new MyDay(Capital(now.ToString("dddd d 'de' MMMM", Es)), greeting, weatherTask.Result, agendaTask.Result ?? [], tasks,
            reminders, habits, notes);
    }

    private async Task<T?> Try<T>(Func<Task<T>> action, string source, List<string> notes) where T : class
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug("Mi día: {Source} falló ({Message})", source, ex.Message);
            lock (notes) notes.Add($"No pude leer {source} ahora.");
            return null;
        }
    }

    private async Task<List<MyDayEvent>> AgendaAsync(DateTimeOffset now, CancellationToken ct)
    {
        var start = new DateTimeOffset(now.Date, now.Offset);
        var json = await google.GetAsync("https://www.googleapis.com/calendar/v3/calendars/primary/events?singleEvents=true&orderBy=startTime&maxResults=30" +
            $"&timeMin={Uri.EscapeDataString(start.ToString("o"))}&timeMax={Uri.EscapeDataString(start.AddDays(1).ToString("o"))}", ct);
        return (json.TryGetProperty("items", out var items) ? items.EnumerateArray() : Enumerable.Empty<JsonElement>()).Select(e =>
        {
            var s = e.GetProperty("start");
            var hour = s.TryGetProperty("dateTime", out var dt)
                ? TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(dt.GetString()!, CultureInfo.InvariantCulture), time.LocalTimeZone).ToString("HH:mm")
                : "Todo el día";
            return new MyDayEvent(hour, e.TryGetProperty("summary", out var t) ? t.GetString() ?? "(sin título)" : "(sin título)",
                e.TryGetProperty("location", out var l) ? l.GetString() : null);
        }).ToList();
    }

    private async Task<List<MyDayTask>> GoogleTasksAsync(DateOnly today, CancellationToken ct)
    {
        var result = new List<MyDayTask>();
        var lists = await google.GetAsync("https://tasks.googleapis.com/tasks/v1/users/@me/lists?maxResults=100", ct);
        foreach (var l in lists.TryGetProperty("items", out var li) ? li.EnumerateArray() : Enumerable.Empty<JsonElement>())
        {
            var tasks = await google.GetAsync($"https://tasks.googleapis.com/tasks/v1/lists/{Uri.EscapeDataString(l.GetProperty("id").GetString()!)}/tasks?showCompleted=false&maxResults=50", ct);
            foreach (var t in tasks.TryGetProperty("items", out var ti) ? ti.EnumerateArray() : Enumerable.Empty<JsonElement>())
            {
                DateOnly? due = t.TryGetProperty("due", out var d) && DateOnly.TryParseExact(d.GetString()?[..10], "yyyy-MM-dd", out var dd) ? dd : null;
                if (due > today) continue;
                result.Add(new MyDayTask(t.GetProperty("title").GetString() ?? "", "Google", due?.ToString("d MMM", Es), due < today));
            }
        }
        return result;
    }

    private async Task<List<MyDayTask>> TrelloAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cards = await trello.GetAsync("members/me/cards", new() { ["filter"] = "open", ["fields"] = "name,due,dueComplete" }, ct);
        return cards.EnumerateArray()
            .Where(c => c.TryGetProperty("due", out var d) && d.ValueKind == JsonValueKind.String
                        && !(c.TryGetProperty("dueComplete", out var dc) && dc.ValueKind == JsonValueKind.True))
            .Select(c => (Name: c.GetProperty("name").GetString() ?? "", Due: TimeZoneInfo.ConvertTime(
                DateTimeOffset.Parse(c.GetProperty("due").GetString()!, CultureInfo.InvariantCulture), time.LocalTimeZone)))
            .Where(c => c.Due.Date <= now.Date)
            .Select(c => new MyDayTask(c.Name, "Trello", c.Due.ToString("d MMM HH:mm", Es), c.Due < now))
            .ToList();
    }

    /// <summary>Clima con Open-Meteo (gratis, sin clave): ciudad del perfil → coordenadas → pronóstico de hoy. Se guarda 30 min.</summary>
    private async Task<MyDayWeather?> WeatherAsync(string city, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(city)) return null;
        if (_weatherCache is { } c && c.City == city && time.GetUtcNow() - c.At < TimeSpan.FromMinutes(30)) return c.Weather;

        var http = httpFactory.CreateClient("weather");
        var geo = await http.GetFromJsonAsync<JsonElement>(
            $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=es", ct);
        if (!geo.TryGetProperty("results", out var rs) || rs.GetArrayLength() == 0) return null;
        var place = rs[0];
        var lat = place.GetProperty("latitude").GetDouble().ToString(CultureInfo.InvariantCulture);
        var lon = place.GetProperty("longitude").GetDouble().ToString(CultureInfo.InvariantCulture);
        var f = await http.GetFromJsonAsync<JsonElement>(
            $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code" +
            "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=auto&forecast_days=1", ct);
        var current = f.GetProperty("current");
        var daily = f.GetProperty("daily");
        double? First(string field) => daily.TryGetProperty(field, out var a) && a.GetArrayLength() > 0 && a[0].ValueKind == JsonValueKind.Number ? a[0].GetDouble() : null;
        var weather = new MyDayWeather(place.GetProperty("name").GetString() ?? city,
            current.GetProperty("temperature_2m").GetDouble(), First("temperature_2m_max"), First("temperature_2m_min"),
            First("precipitation_probability_max") is { } p ? (int)p : null, Describe(current.GetProperty("weather_code").GetInt32()));
        _weatherCache = (city, weather, time.GetUtcNow());
        return weather;
    }

    /// <summary>Códigos WMO de Open-Meteo → texto.</summary>
    internal static string Describe(int code) => code switch
    {
        0 => "Despejado",
        1 or 2 => "Parcialmente nublado",
        3 => "Nublado",
        45 or 48 => "Neblina",
        >= 51 and <= 57 => "Llovizna",
        >= 61 and <= 67 => "Lluvia",
        >= 71 and <= 77 => "Nieve",
        >= 80 and <= 82 => "Chubascos",
        >= 85 and <= 86 => "Nevadas",
        >= 95 => "Tormenta",
        _ => "Variable",
    };

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Es) + s[1..];
}

public static class MyDayApi
{
    public static void MapMyDayApi(this WebApplication app)
    {
        app.MapGet("/api/midia", async (HttpContext ctx, MyDayService day, IOptionsMonitor<AgentOptions> agent, CancellationToken ct) =>
            BrainAuth.IsAuthorized(ctx, agent.CurrentValue.Token) ? Results.Ok(await day.BuildAsync(ct)) : Results.Unauthorized());
    }
}
