using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Options;

namespace Kroko.App.Services;

public sealed record MemoryDto(long Id, string Category, string Content, DateTimeOffset Updated)
{
    public string UpdatedText => Updated.ToLocalTime().ToString("dd/MM/yyyy");
}

public sealed record RoutineDto(long Id, string Name, List<string> Triggers, List<string> Steps, DateTimeOffset Updated)
{
    public string TriggersText => string.Join(" · ", Triggers);
    public string StepsText => string.Join("\n", Steps.Select((s, i) => $"{i + 1}. {s}"));
}

public sealed record SuggestionDto(long Id, string Kind, string Title, string Detail, int Times, string Status, DateTimeOffset Updated)
{
    public bool IsDone => Status == "hecha";
    public string Meta => $"{(Kind == "fallo" ? "Fallo" : "Idea")} · {Times} vez/veces · {Updated.ToLocalTime():dd/MM}" + (IsDone ? " · hecha" : "");
}

public sealed record MemorySnapshot(List<MemoryDto> Recuerdos, List<RoutineDto> Rutinas, List<SuggestionDto> Mejoras, string Buzon);

/// <summary>Habla con la API de memoria del Brain (/api/memoria).</summary>
public sealed class BrainApiClient(IOptions<KrokoOptions> options) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = Create(options.Value, TimeSpan.FromSeconds(10));

    /// <summary>Para las pruebas de conexión (Gmail, Tavily…), que pueden tardar.</summary>
    private readonly HttpClient _longHttp = Create(options.Value, TimeSpan.FromSeconds(40));

    private static HttpClient Create(KrokoOptions opt, TimeSpan timeout)
    {
        var http = new HttpClient { BaseAddress = new Uri(opt.BrainUrl.TrimEnd('/') + "/"), Timeout = timeout };
        if (!string.IsNullOrEmpty(opt.BrainToken)) http.DefaultRequestHeaders.Add(KrokoProtocol.TokenHeader, opt.BrainToken);
        return http;
    }

    public async Task<MemorySnapshot> GetMemoryAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<MemorySnapshot>("api/memoria", Json, ct)
        ?? new MemorySnapshot([], [], [], "");

    public async Task DeleteAsync(string kind, long id)
    {
        using var res = await _http.DeleteAsync($"api/memoria/{kind}/{id}");
        res.EnsureSuccessStatusCode();
    }

    public async Task ClearConversationAsync()
    {
        using var res = await _http.DeleteAsync("api/memoria/conversacion");
        res.EnsureSuccessStatusCode();
    }

    public async Task MarkDoneAsync(long suggestionId)
    {
        using var res = await _http.PostAsync($"api/memoria/mejoras/{suggestionId}/hecha", null);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>Estado del cerebro y de dónde saca sus claves (sin mostrarlas).</summary>
    public async Task<string> GetLlmStatusAsync(CancellationToken ct = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>("health/llm", Json, ct);
        var providers = json.GetProperty("providers").EnumerateArray().Select(p => p.GetString()).ToList();
        var sources = json.TryGetProperty("fuentes", out var f)
            ? string.Join(", ", f.EnumerateObject().Select(p => $"{p.Name}: {p.Value.GetString()}"))
            : "";
        return providers.Count == 0
            ? $"Conectado, pero SIN clave de IA ({sources})"
            : $"Conectado · IA: {string.Join(", ", providers)} ({sources})";
    }

    // ------------------------------------------------------------ panel: Chat y Correo (17/11/2026)

    public async Task<ChatHistoryDto?> GetConversationAsync(string sessionId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<ChatHistoryDto>($"api/memoria/conversacion/{Uri.EscapeDataString(sessionId)}", Json, ct);

    public async Task<MailListDto?> GetImportantMailAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<MailListDto>("api/correo/importantes", Json, ct);

    // ------------------------------------------------------------ panel «Kroko»: tu día, hábitos y tareas

    public async Task<TodayDto?> GetTodayAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<TodayDto>("api/hoy", Json, ct);

    public async Task<HabitItemDto?> AddHabitAsync(string name, int goal, string? unit, string? style)
    {
        using var res = await _http.PostAsJsonAsync("api/habitos", new { nombre = name, meta = goal, unidad = unit, estilo = style }, Json);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<HabitItemDto>(Json);
    }

    /// <summary>Suma (o resta) a lo hecho hoy. Devuelve el hábito y si recién se cumplió la meta (para celebrar).</summary>
    public async Task<(HabitItemDto? Habit, bool GoalReached)> AddToHabitAsync(long id, int amount = 1)
    {
        using var res = await _http.PostAsJsonAsync($"api/habitos/{id}/sumar", new { cantidad = amount }, Json);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        return (json.GetProperty("habito").Deserialize<HabitItemDto>(Json), json.GetProperty("metaCumplida").GetBoolean());
    }

    public async Task ResetHabitAsync(long id) => (await _http.PostAsync($"api/habitos/{id}/reiniciar", null)).Dispose();

    public async Task DeleteHabitAsync(long id) => (await _http.DeleteAsync($"api/habitos/{id}")).Dispose();

    public async Task AddTaskAsync(string text, DateOnly? due)
    {
        using var res = await _http.PostAsJsonAsync("api/tareas", new { texto = text, fecha = due?.ToString("yyyy-MM-dd") }, Json);
        res.EnsureSuccessStatusCode();
    }

    public async Task CompleteTaskAsync(long id, bool done) =>
        (await _http.PostAsJsonAsync($"api/tareas/{id}/completar", new { hecha = done }, Json)).Dispose();

    public async Task DeleteTaskAsync(long id) => (await _http.DeleteAsync($"api/tareas/{id}")).Dispose();

    public async Task DeleteReminderAsync(long id) => (await _http.DeleteAsync($"api/recordatorios/{id}")).Dispose();

    /// <summary>«+10 min» de la isla: el mismo aviso otra vez dentro de N minutos.</summary>
    public async Task SnoozeReminderAsync(string text, int minutes)
    {
        using var res = await _http.PostAsJsonAsync("api/recordatorios/posponer", new { texto = text, minutos = minutes }, Json);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>Transcribe un audio (soltado sobre Kroko) con Gemini. Devuelve el texto o lanza con el motivo.</summary>
    public async Task<string> TranscribeAsync(byte[] audio, string mime, CancellationToken ct)
    {
        using var transcriber = Create(options.Value, TimeSpan.FromMinutes(3));
        using var res = await transcriber.PostAsJsonAsync("api/transcribir", new { audioBase64 = Convert.ToBase64String(audio), mime }, Json, ct);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(body.TryGetProperty("error", out var e) ? e.GetString() : "No pude transcribirlo.");
        return body.TryGetProperty("texto", out var t) ? t.GetString() ?? "" : "";
    }

    public async Task<NoticesDto?> GetNoticesAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<NoticesDto>("api/avisos", Json, ct);

    public async Task SetNoticesAsync(NoticesDto notices)
    {
        using var res = await _http.PutAsJsonAsync("api/avisos", notices, Json);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>Gasto de IA de hoy y del mes y el tope diario (/api/uso).</summary>
    public async Task<UsageDto?> GetUsageAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<UsageDto>("api/uso", Json, ct);

    public async Task SetDailyLimitAsync(long tokens, CancellationToken ct = default)
    {
        using var res = await _http.PutAsJsonAsync("api/uso/limite", new { tokens }, Json, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task<List<ConnectorDto>> GetConnectorsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<ConnectorDto>>("api/conectores", Json, ct) ?? [];

    public async Task ConnectAsync(string name)
    {
        using var res = await _http.PostAsync($"api/conectores/{Uri.EscapeDataString(name)}/conectar", null);
        res.EnsureSuccessStatusCode();
    }

    public async Task DisconnectAsync(string name)
    {
        using var res = await _http.PostAsync($"api/conectores/{Uri.EscapeDataString(name)}/desconectar", null);
        res.EnsureSuccessStatusCode();
    }


    // ---------------------------------------------------------------- asistente "Configurar Kroko"

    public async Task<ProfileResponse> GetProfileAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<ProfileResponse>("api/perfil", Json, ct) ?? new ProfileResponse(new ProfileDto(), false);

    /// <summary>Guarda el perfil. Devuelve el error legible o null si salió bien.</summary>
    public async Task<string?> SaveProfileAsync(ProfileDto profile)
    {
        using var res = await _http.PutAsJsonAsync("api/perfil", profile, Json);
        return res.IsSuccessStatusCode ? null : await ErrorAsync(res);
    }

    public async Task<SetupStatus> GetSetupAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<SetupStatus>("api/configuracion", Json, ct) ?? new SetupStatus();

    /// <summary>Guarda claves (cifradas en el Brain). Valor vacío = borrar; las claves ausentes no se tocan.</summary>
    public async Task<string?> SaveSecretsAsync(IReadOnlyDictionary<string, string> values)
    {
        using var res = await _http.PutAsJsonAsync("api/configuracion/secretos", values, Json);
        return res.IsSuccessStatusCode ? null : await ErrorAsync(res);
    }

    public async Task<TestResultDto> TestAsync(string what)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var res = await _longHttp.PostAsync($"api/configuracion/probar/{what}", null, cts.Token);
        return res.IsSuccessStatusCode
            ? await res.Content.ReadFromJsonAsync<TestResultDto>(Json, cts.Token) ?? new TestResultDto(false, "Sin respuesta.")
            : new TestResultDto(false, await ErrorAsync(res));
    }

    public async Task<(PairingDto? Pairing, string? Error)> StartTelegramPairingAsync()
    {
        using var res = await _http.PostAsync("api/telegram/vincular", null);
        return res.IsSuccessStatusCode
            ? (await res.Content.ReadFromJsonAsync<PairingDto>(Json), null)
            : (null, await ErrorAsync(res));
    }

    public async Task<TelegramStatusDto> GetTelegramStatusAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<TelegramStatusDto>("api/telegram/estado", Json, ct) ?? new TelegramStatusDto();

    /// <summary>Vectores para buscar por significado (los calcula el cerebro con Gemini).</summary>
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, string kind, IReadOnlyList<string>? titles, CancellationToken ct)
    {
        using var res = await _longHttp.PostAsJsonAsync("api/vectores", new { textos = texts, tipo = kind, titulos = titles }, Json, ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(res));
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return json.GetProperty("vectores").EnumerateArray().Select(v => v.EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToArray();
    }

    public async Task<MyDayDto?> GetMyDayAsync(CancellationToken ct = default) =>
        await _longHttp.GetFromJsonAsync<MyDayDto>("api/midia", Json, ct);

    public async Task<GoogleStatusDto> GetGoogleStatusAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<GoogleStatusDto>("api/google/estado", Json, ct) ?? new GoogleStatusDto();

    /// <summary>Pide al cerebro la dirección de Google para dar permiso (o el error legible).</summary>
    public async Task<(string? Url, string? Error)> StartGoogleConnectAsync()
    {
        using var res = await _http.PostAsync("api/google/conectar", null);
        if (!res.IsSuccessStatusCode) return (null, await ErrorAsync(res));
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        return (json.GetProperty("url").GetString(), null);
    }

    public async Task DisconnectGoogleAsync()
    {
        using var res = await _http.PostAsync("api/google/desconectar", null);
        res.EnsureSuccessStatusCode();
    }

    public async Task UnlinkTelegramAsync()
    {
        using var res = await _http.DeleteAsync("api/telegram/vinculo");
        res.EnsureSuccessStatusCode();
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage res)
    {
        try
        {
            var json = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var e) && e.GetString() is { } msg) return msg;
        }
        catch (JsonException) { }
        return res.StatusCode == System.Net.HttpStatusCode.Unauthorized
            ? "El cerebro no aceptó la conexión (revisa el token en Ajustes → Cerebro)."
            : $"El cerebro respondió {(int)res.StatusCode}.";
    }
    public void Dispose()
    {
        _http.Dispose();
        _longHttp.Dispose();
    }
}

public sealed record ConnectorDto(string Nombre, string Titulo, string Estado, int Herramientas, string? Detalle, bool PuedeConectar, string? AuthUrl = null)
{
    public bool IsConnected => Estado == "Conectado";

    public string StateText => Estado switch
    {
        "Conectado" => $"Conectado · {Herramientas} herramientas",
        "Conectando" => "Conectando…",
        "NecesitaPermiso" => "Necesita tu permiso",
        "FaltaConfiguracion" => "Falta configurarlo",
        "Desactivado" => "Desactivado",
        _ => "Con problemas",
    };
}

public sealed record ProfileDto
{
    public string Nombre { get; init; } = "";
    public string ComoLlamarte { get; init; } = "";
    public string Ciudad { get; init; } = "";
    public string Pais { get; init; } = "";
    public string ZonaHoraria { get; init; } = "";
    public string Acento { get; init; } = "";
    public string SobreMi { get; init; } = "";
}

public sealed record ProfileResponse(ProfileDto Perfil, bool Completo);

public sealed record MailStatusDto(bool Configurado = false, string Direccion = "");

public sealed record TelegramStatusDto(bool Token = false, bool Conectado = false, string? Bot = null, bool Vinculado = false,
    int Chats = 0, bool EsperandoCodigo = false);

public sealed record SetupStatus
{
    public bool PerfilCompleto { get; init; }
    public bool Gemini { get; init; }
    public int GeminiClaves { get; init; }
    public int GeminiDisponibles { get; init; }
    public bool Deepseek { get; init; }
    public bool Tavily { get; init; }
    public Dictionary<string, string> Fuentes { get; init; } = [];
    public MailStatusDto Correo { get; init; } = new();
    public TelegramStatusDto Telegram { get; init; } = new();
    public bool Trello { get; init; }
    public GoogleStatusDto Google { get; init; } = new();
}

public sealed record TestResultDto(bool Ok, string Mensaje);

public sealed record PairingDto(string Codigo, string Bot, string Enlace, DateTimeOffset Caduca);

public sealed record GoogleStatusDto(bool Cliente = false, bool Conectado = false, string? Correo = null);

public sealed record MyDayWeatherDto(string Ciudad, double? Temperatura, double? Maxima, double? Minima, int? Lluvia, string Descripcion)
{
    public string TempText => Temperatura is { } t ? $"{Math.Round(t)}°" : "";
    public string RangeText => Maxima is { } max && Minima is { } min ? $"Máx {Math.Round(max)}° · Mín {Math.Round(min)}°" : "";
    public string RainText => Lluvia is { } r ? $"{r} % de lluvia" : "";
}

public sealed record MyDayEventDto(string Hora, string Titulo, string? Lugar);

public sealed record MyDayTaskDto(string Texto, string Fuente, string? Vence, bool Vencida)
{
    public string Meta => Vence is null ? Fuente : $"{Fuente} · {Vence}";
}

public sealed record MyDayReminderDto(string Hora, string Texto);

public sealed record MyDayHabitDto(string Nombre, bool Hecho, int Racha)
{
    public string Meta => Racha > 0 ? $"racha {Racha}" : "";
}

public sealed record MyDayDto(string Fecha, string Saludo, MyDayWeatherDto? Clima, List<MyDayEventDto> Agenda, List<MyDayTaskDto> Tareas,
    List<MyDayReminderDto> Recordatorios, List<MyDayHabitDto> Habitos, List<string> Avisos);

public sealed record UsageTotalsDto(long Total, long Entrada, long Salida, long EnCache, int Llamadas, Dictionary<string, long> PorTipo);

public sealed record UsageDto(UsageTotalsDto Hoy, UsageTotalsDto Mes, long LimiteDiario, bool ModoAhorro);

public sealed record TodaySummaryDto(int HabitosHechos, int HabitosTotal, int TareasPendientes, string? ProximoAviso, string? ProximoAvisoTexto);

public sealed record HabitItemDto(long Id, string Nombre, int Llevas, int Meta, string Unidad, string Estilo, bool Hecho, int Racha, bool[] Semana, string Avisos);

public sealed record TaskItemDto(long Id, string Texto, string? Fecha, bool Hecha);

public sealed record ReminderItemDto(long Id, string Texto, string Cuando, string Repetir);

public sealed record TodayDto(string Fecha, TodaySummaryDto Resumen, List<HabitItemDto> Habitos, List<TaskItemDto> Tareas, List<ReminderItemDto> Recordatorios);

/// <summary>Avisos automáticos que Kroko da sin que se los pidas (14/11/2026: por defecto, reuniones y correos importantes).</summary>
public sealed record NoticesDto(bool Reuniones, bool Correos, bool TareasVencidas, bool ResumenDelDia, bool? Asistente = null);

public sealed record ChatLineDto(string Quien, string Texto);
public sealed record ChatHistoryDto(List<ChatLineDto> Mensajes);
public sealed record MailItemDto(string Id, string De, string Asunto, string Fecha);
public sealed record MailListDto(bool Conectado, List<MailItemDto> Correos, string? Error = null);
