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
