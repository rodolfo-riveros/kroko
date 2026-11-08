using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kroko.Brain.Agent;
using Kroko.Brain.Config;
using Kroko.Brain.Providers;
using Kroko.Shared.Protocol;
using Kroko.Shared.Tools;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Apps;

/// <summary>
/// Sección "Google": cliente OAuth de escritorio que el dueño crea en Google Cloud (Tasks, Calendar y Drive).
/// ClientId/ClientSecret se ponen en "Configurar Kroko" (cifrados); el permiso se guarda como Google:RefreshToken.
/// </summary>
public sealed class GoogleOptions
{
    public const string Section = "Google";

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? RefreshToken { get; set; }

    /// <summary>Permisos que se piden: tareas y calendario (leer/escribir), Drive solo lectura.</summary>
    public List<string>? Scopes { get; set; }

    public IReadOnlyList<string> EffectiveScopes => Scopes is { Count: > 0 } ? Scopes : DefaultScopes;

    private static readonly string[] DefaultScopes =
    [
        "openid", "email",
        "https://www.googleapis.com/auth/tasks",
        "https://www.googleapis.com/auth/calendar.events",
        "https://www.googleapis.com/auth/drive.readonly",
        "https://www.googleapis.com/auth/contacts.readonly",
    ];

    public bool HasClient => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
    public bool IsConnected => HasClient && !string.IsNullOrWhiteSpace(RefreshToken);
}

public sealed class GoogleNotConnectedException(string? message = null)
    : Exception(message ?? "La cuenta de Google todavía no está conectada (Configurar Kroko → Tus apps → Google).");

/// <summary>
/// Inicio de sesión con Google (OAuth 2.0 con PKCE) y llamadas a sus APIs con el token de acceso renovado solo.
/// </summary>
public sealed class GoogleAccount(HttpClient http, IOptionsMonitor<GoogleOptions> options, SecretStore secrets, TimeProvider time,
    ILogger<GoogleAccount> log)
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private readonly ConcurrentDictionary<string, (string Verifier, string Redirect, DateTimeOffset Expires)> _pending = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private (string Token, DateTimeOffset Expires)? _access;

    public bool HasClient => options.CurrentValue.HasClient;
    public bool IsConnected => options.CurrentValue.IsConnected;
    public string? Email { get; private set; }

    /// <summary>Dirección de Google para dar permiso. redirect = la vuelta al cerebro (/api/google/oauth/callback).</summary>
    public string StartAuthorization(string redirect)
    {
        var o = options.CurrentValue;
        if (!o.HasClient) throw new GoogleNotConnectedException("Falta el cliente OAuth de Google (ID y secreto) en Configurar Kroko.");
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        foreach (var old in _pending.Where(p => p.Value.Expires < time.GetUtcNow()).Select(p => p.Key)) _pending.TryRemove(old, out _);
        _pending[state] = (verifier, redirect, time.GetUtcNow().AddMinutes(10));

        var q = new Dictionary<string, string>
        {
            ["client_id"] = o.ClientId!,
            ["redirect_uri"] = redirect,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', o.EffectiveScopes),
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["access_type"] = "offline",
            ["prompt"] = "consent", // así Google siempre devuelve un refresh token
        };
        return AuthEndpoint + "?" + string.Join('&', q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>Google vuelve con el código: se cambia por el refresh token y se guarda cifrado.</summary>
    public async Task<bool> CompleteAuthorizationAsync(string? state, string? code, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(code) || !_pending.TryRemove(state, out var p) || p.Expires < time.GetUtcNow())
            return false;
        var o = options.CurrentValue;
        using var res = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = o.ClientId!,
            ["client_secret"] = o.ClientSecret!,
            ["code"] = code,
            ["code_verifier"] = p.Verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = p.Redirect,
        }), ct);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!res.IsSuccessStatusCode || !json.TryGetProperty("refresh_token", out var refresh))
        {
            log.LogWarning("Google no entregó el permiso: {Status}", (int)res.StatusCode);
            return false;
        }
        secrets.Update(new Dictionary<string, string?> { ["Google:RefreshToken"] = refresh.GetString() }, @internal: true);
        if (json.TryGetProperty("access_token", out var at))
            _access = (at.GetString()!, time.GetUtcNow().AddSeconds(json.TryGetProperty("expires_in", out var e) ? e.GetInt32() - 60 : 3000));
        await LoadEmailAsync(ct);
        log.LogInformation("Cuenta de Google conectada");
        return true;
    }

    public void Disconnect()
    {
        _access = null;
        Email = null;
        secrets.Update(new Dictionary<string, string?> { ["Google:RefreshToken"] = "" }, @internal: true);
    }

    public async Task LoadEmailAsync(CancellationToken ct)
    {
        try
        {
            var me = await GetAsync("https://www.googleapis.com/oauth2/v3/userinfo", ct);
            Email = me.TryGetProperty("email", out var e) ? e.GetString() : null;
        }
        catch (Exception ex) when (ex is GoogleNotConnectedException or HttpRequestException or InvalidOperationException) { }
    }

    public Task<JsonElement> GetAsync(string url, CancellationToken ct) => SendAsync(HttpMethod.Get, url, null, ct);

    public async Task<JsonElement> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var res = await RawAsync(method, url, body, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NoContent) return default;
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var res = await RawAsync(HttpMethod.Get, url, null, ct);
        return await res.Content.ReadAsStringAsync(ct);
    }

    private async Task<HttpResponseMessage> RawAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        var token = await AccessTokenAsync(ct);
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body);
        var res = await http.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return res;
        var text = await res.Content.ReadAsStringAsync(ct);
        res.Dispose();
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized) _access = null;
        throw new InvalidOperationException(res.StatusCode switch
        {
            System.Net.HttpStatusCode.Forbidden when text.Contains("has not been used", StringComparison.Ordinal) || text.Contains("disabled", StringComparison.Ordinal)
                => "Esa API de Google no está activada en tu proyecto de Google Cloud (actívala en la Biblioteca de APIs).",
            System.Net.HttpStatusCode.NotFound => "Google no encontró eso (¿el id es correcto?).",
            _ => $"Google respondió {(int)res.StatusCode}.",
        });
    }

    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        if (_access is { } a && a.Expires > time.GetUtcNow()) return a.Token;
        var o = options.CurrentValue;
        if (!o.IsConnected) throw new GoogleNotConnectedException();
        await _refresh.WaitAsync(ct);
        try
        {
            if (_access is { } again && again.Expires > time.GetUtcNow()) return again.Token;
            using var res = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = o.ClientId!,
                ["client_secret"] = o.ClientSecret!,
                ["refresh_token"] = o.RefreshToken!,
                ["grant_type"] = "refresh_token",
            }), ct);
            var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (!res.IsSuccessStatusCode)
                throw new GoogleNotConnectedException("Google retiró el permiso de Kroko. Vuelve a conectar tu cuenta de Google.");
            var token = json.GetProperty("access_token").GetString()!;
            _access = (token, time.GetUtcNow().AddSeconds(json.GetProperty("expires_in").GetInt32() - 60));
            return token;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>API de la cuenta de Google para "Configurar Kroko".</summary>
public static class GoogleApi
{
    public static void MapGoogleApi(this WebApplication app)
    {
        // Vuelta del permiso: la abre el navegador del dueño (sin token); la protege el "state" de un solo uso
        app.MapGet("/api/google/oauth/callback", async (HttpContext ctx, GoogleAccount google, CancellationToken ct) =>
        {
            var q = ctx.Request.Query;
            var ok = await google.CompleteAuthorizationAsync(q["state"], q["code"], ct);
            var body = ok
                ? $"<h2>¡Listo! Kroko ya puede usar tu cuenta de Google{(google.Email is { } e ? $" ({System.Net.WebUtility.HtmlEncode(e)})" : "")}.</h2><p>Puedes cerrar esta pestaña.</p>"
                : "<h2>No se pudo conectar</h2><p>El permiso caducó o fue rechazado. Vuelve a pulsar Conectar en Kroko.</p>";
            return Results.Content($"<!doctype html><meta charset=utf-8><body style='font-family:Segoe UI;background:#0f0f0f;color:#f5f1e9;padding:40px'>{body}</body>", "text/html; charset=utf-8");
        });

        var group = app.MapGroup("/api/google").AddEndpointFilter(async (ctx, next) =>
        {
            var token = ctx.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<AgentOptions>>().CurrentValue.Token;
            return BrainAuth.IsAuthorized(ctx.HttpContext, token) ? await next(ctx) : Results.Unauthorized();
        });

        group.MapGet("/estado", async (GoogleAccount google, CancellationToken ct) =>
        {
            if (google.IsConnected && google.Email is null) await google.LoadEmailAsync(ct);
            return Results.Ok(new { cliente = google.HasClient, conectado = google.IsConnected, correo = google.Email });
        });

        group.MapPost("/conectar", (HttpContext ctx, GoogleAccount google, IOptionsMonitor<Mcp.McpOptions> mcp) =>
        {
            try
            {
                // En la PC: vuelve a 127.0.0.1 (lo que Google permite a los clientes de escritorio).
                // En un servidor: a su dirección pública (necesita un cliente OAuth de tipo "Aplicación web").
                var publicBase = mcp.CurrentValue.PublicBaseUrl;
                var redirect = !string.IsNullOrWhiteSpace(publicBase)
                    ? publicBase.TrimEnd('/') + "/api/google/oauth/callback"
                    : $"http://127.0.0.1:{ctx.Request.Host.Port ?? 5080}/api/google/oauth/callback";
                return Results.Ok(new { url = google.StartAuthorization(redirect) });
            }
            catch (GoogleNotConnectedException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/desconectar", (GoogleAccount google) =>
        {
            google.Disconnect();
            return Results.NoContent();
        });
    }
}

// ---------------------------------------------------------------- herramientas

public abstract class GoogleTool(GoogleAccount google) : IBrainTool
{
    protected GoogleAccount Google => google;

    public abstract ToolDefinition Definition { get; }

    protected abstract Task<object> RunAsync(JsonElement args, CancellationToken ct);

    public virtual string Describe(JsonElement args) => $"{Definition.Name}: {BrainToolResult.Summarize(args)}";

    public async Task<ToolExecution> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            return BrainToolResult.Ok(await RunAsync(args, ct));
        }
        catch (Exception ex) when (ex is GoogleNotConnectedException or ArgumentException or InvalidOperationException or HttpRequestException)
        {
            return ToolConfirmation.Fail(ex.Message);
        }
    }

    protected static string Esc(string s) => Uri.EscapeDataString(s);
}

file static class GoogleUrls
{
    public const string Tasks = "https://tasks.googleapis.com/tasks/v1";
    public const string Calendar = "https://www.googleapis.com/calendar/v3";
    public const string Drive = "https://www.googleapis.com/drive/v3";
}

public sealed class GoogleTareasListarTool(GoogleAccount google) : GoogleTool(google)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("google_tareas_listar",
        "Tareas pendientes de Google Tasks de tu dueño (todas sus listas, o una).",
        """{"type":"object","properties":{"lista":{"type":"string","description":"Nombre de la lista (opcional)"}}}""", ToolRisk.Read);

    protected override async Task<object> RunAsync(JsonElement args, CancellationToken ct)
    {
        var lists = await Google.GetAsync($"{GoogleUrls.Tasks}/users/@me/lists?maxResults=100", ct);
        var wanted = ToolCatalog.Str(args, "lista");
        var result = new List<object>();
        foreach (var l in lists.TryGetProperty("items", out var items) ? items.EnumerateArray() : Enumerable.Empty<JsonElement>())
        {
            var title = l.GetProperty("title").GetString() ?? "";
            if (wanted.Length > 0 && !title.Contains(wanted, StringComparison.OrdinalIgnoreCase)) continue;
            var tasks = await Google.GetAsync($"{GoogleUrls.Tasks}/lists/{Esc(l.GetProperty("id").GetString()!)}/tasks?showCompleted=false&maxResults=100", ct);
            result.Add(new
            {
                lista = title,
                listaId = l.GetProperty("id").GetString(),
                tareas = (tasks.TryGetProperty("items", out var ts) ? ts.EnumerateArray() : Enumerable.Empty<JsonElement>()).Select(t => new
                {
                    id = t.GetProperty("id").GetString(),
                    tarea = t.TryGetProperty("title", out var tt) ? tt.GetString() : "",
                    notas = t.TryGetProperty("notes", out var n) ? n.GetString() : null,
                    vence = t.TryGetProperty("due", out var d) ? d.GetString()?[..10] : null,
                }).ToList(),
            });
        }
        return new { listas = result };
    }
}

public sealed class GoogleTareaCrearTool(GoogleAccount google) : GoogleTool(google)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("google_tarea_crear",
        "Crea una tarea en Google Tasks (en la lista principal si no se indica otra).",
        """{"type":"object","properties":{"titulo":{"type":"string"},"notas":{"type":"string"},"vence":{"type":"string","description":"yyyy-MM-dd"},"lista":{"type":"string"}},"required":["titulo"]}""");

    protected override async Task<object> RunAsync(JsonElement args, CancellationToken ct)
    {
        var listId = await GoogleTasks.FindListIdAsync(Google, ToolCatalog.Str(args, "lista"), ct);
        var due = ToolCatalog.Str(args, "vence");
        if (due.Length > 0 && !DateOnly.TryParseExact(due, "yyyy-MM-dd", out _)) throw new ArgumentException("La fecha debe ser yyyy-MM-dd.");
        var created = await Google.SendAsync(HttpMethod.Post, $"{GoogleUrls.Tasks}/lists/{Esc(listId)}/tasks", new Dictionary<string, string?>
        {
            ["title"] = ToolCatalog.Str(args, "titulo"),
            ["notes"] = ToolCatalog.Str(args, "notas") is { Length: > 0 } n ? n : null,
            ["due"] = due.Length > 0 ? due + "T00:00:00.000Z" : null,
        }.Where(kv => kv.Value is not null).ToDictionary(), ct);
        return new { id = created.GetProperty("id").GetString(), tarea = created.GetProperty("title").GetString() };
    }
}

public sealed class GoogleTareaCompletarTool(GoogleAccount google) : GoogleTool(google)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("google_tarea_completar",
        "Marca como hecha una tarea de Google Tasks (id y listaId de google_tareas_listar).",
        """{"type":"object","properties":{"id":{"type":"string"},"listaId":{"type":"string"}},"required":["id","listaId"]}""");

    protected override async Task<object> RunAsync(JsonElement args, CancellationToken ct)
    {
        var t = await Google.SendAsync(HttpMethod.Patch,
            $"{GoogleUrls.Tasks}/lists/{Esc(ToolCatalog.Str(args, "listaId"))}/tasks/{Esc(ToolCatalog.Str(args, "id"))}",
            new { status = "completed" }, ct);
        return new { hecha = t.GetProperty("title").GetString() };
    }
}

public sealed class GoogleTareaBorrarTool(GoogleAccount google) : GoogleTool(google)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("google_tarea_borrar",
        "Borra una tarea de Google Tasks.",
        """{"type":"object","properties":{"id":{"type":"string"},"listaId":{"type":"string"},"titulo":{"type":"string","description":"Para la confirmación"}},"required":["id","listaId"]}""",
        ToolRisk.Dangerous);

    public override string Describe(JsonElement args) => $"Borrar de Google Tasks la tarea «{ToolCatalog.Str(args, "titulo")}»";

    protected override async Task<object> RunAsync(JsonElement args, CancellationToken ct)
    {
        await Google.SendAsync(HttpMethod.Delete,
            $"{GoogleUrls.Tasks}/lists/{Esc(ToolCatalog.Str(args, "listaId"))}/tasks/{Esc(ToolCatalog.Str(args, "id"))}", null, ct);
        return new { borrada = true };
    }
}

file static class GoogleTasks
{
    public static async Task<string> FindListIdAsync(GoogleAccount google, string name, CancellationToken ct)
    {
        if (name.Length == 0) return "@default";
        var lists = await google.GetAsync($"{GoogleUrls.Tasks}/users/@me/lists?maxResults=100", ct);
        foreach (var l in lists.TryGetProperty("items", out var items) ? items.EnumerateArray() : Enumerable.Empty<JsonElement>())
            if ((l.GetProperty("title").GetString() ?? "").Contains(name, StringComparison.OrdinalIgnoreCase))
                return l.GetProperty("id").GetString()!;
        throw new ArgumentException($"No encontré la lista \"{name}\" en Google Tasks.");
    }
}

public sealed class GoogleCalendarioEventosTool(GoogleAccount google, TimeProvider time) : GoogleTool(google)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("google_calendario_eventos",
        "Eventos del Google Calendar principal de tu dueño desde una fecha (hoy por defecto) durante N días.",
        """{"type":"object","properties":{"desde":{"type":"string","description":"yyyy-MM-dd (opcional)"},"dias":{"type":"integer","minimum":1,"maximum":31}}}""",
        ToolRisk.Read);

    protected override async Task<object> RunAsync(JsonElement args, CancellationToken ct)
    {
        var now = time.GetLocalNow();
        var start = DateOnly.TryParseExact(ToolCatalog.Str(args, "desde"), "yyyy-MM-dd", out var d) ? d : DateOnly.FromDateTime(now.DateTime);
        var days = Math.Clamp(ToolCatalog.Int(args, "dias") is > 0 and var n ? n : 1, 1, 31);
        var from = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), now.Offset);
        var to = from.AddDays(days);
        var json = await Google.GetAsync($"{GoogleUrls.Calendar}/calendars/primary/events?singleEvents=true&orderBy=startTime&maxResults=100" +
            $"&timeMin={Esc(from.ToString("o"))}&timeMax={Esc(to.ToString("o"))}", ct);
        return new
        {
            eventos = (json.TryGetProperty("items", out var items) ? items.EnumerateArray() : Enumerable.Empty<JsonElement>()).Select(e => new
            {
                id = e.GetProperty("id").GetString(),
                titulo = e.TryGetProperty("summary", out var s) ? s.GetString() : "(sin título)",
                inicio = When(e, "start"),
                fin = When(e, "end"),
                lugar = e.TryGetProperty("location", out var l) ? l.GetString() : null,
            }),
        };
    }

    private static string? When(JsonElement e, string field) =>
        e.TryGetProperty(field, out var w)
            ? w.TryGetProperty("dateTime", out var dt) ? dt.GetString() : w.TryGetProperty("date", out var day) ? day.GetString() + " (todo el día)" : null
            : null;
}

public sealed class GoogleCalendarioCrearTool(GoogleAccount google, TimeProvider time) : GoogleTool(google)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("google_calendario_crear",
        "Crea un evento en el Google Calendar principal (sin invitar a nadie). Fechas en hora de tu dueño.",
        """
        {"type":"object","properties":{"titulo":{"type":"string"},"inicio":{"type":"string","description":"yyyy-MM-ddTHH:mm"},
          "fin":{"type":"string","description":"yyyy-MM-ddTHH:mm (opcional: 1 h después)"},"descripcion":{"type":"string"},"lugar":{"type":"string"}},
         "required":["titulo","inicio"]}
        """, ToolRisk.Dangerous);

    public override string Describe(JsonElement args) =>
        $"Crear en tu Google Calendar «{ToolCatalog.Str(args, "titulo")}» el {ToolCatalog.Str(args, "inicio").Replace('T', ' ')}";

    protected override async Task<object> RunAsync(JsonElement args, CancellationToken ct)
    {
        if (!DateTime.TryParse(ToolCatalog.Str(args, "inicio"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var start))
            throw new ArgumentException("La fecha de inicio debe ser yyyy-MM-ddTHH:mm.");
        var end = DateTime.TryParse(ToolCatalog.Str(args, "fin"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var e) ? e : start.AddHours(1);
        var tz = time.LocalTimeZone;
        string Iso(DateTime dt) => new DateTimeOffset(dt, tz.GetUtcOffset(dt)).ToString("o");
        var ev = await Google.SendAsync(HttpMethod.Post, $"{GoogleUrls.Calendar}/calendars/primary/events", new Dictionary<string, object?>
        {
            ["summary"] = ToolCatalog.Str(args, "titulo"),
            ["description"] = ToolCatalog.Str(args, "descripcion") is { Length: > 0 } dsc ? dsc : null,
            ["location"] = ToolCatalog.Str(args, "lugar") is { Length: > 0 } loc ? loc : null,
            ["start"] = new { dateTime = Iso(start) },
            ["end"] = new { dateTime = Iso(end) },
        }.Where(kv => kv.Value is not null).ToDictionary(), ct);
        return new { creado = ev.GetProperty("summary").GetString(), enlace = ev.TryGetProperty("htmlLink", out var l) ? l.GetString() : null };
    }
}

public sealed class GoogleDriveBuscarTool(GoogleAccount google) : GoogleTool(google)
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("google_drive_buscar",
        "Busca archivos en el Google Drive de tu dueño por nombre o contenido.",
        """{"type":"object","properties":{"consulta":{"type":"string"}},"required":["consulta"]}""", ToolRisk.Read);

    protected override async Task<object> RunAsync(JsonElement args, CancellationToken ct)
    {
        var text = ToolCatalog.Str(args, "consulta").Replace("\\", "").Replace("'", "\\'");
        var q = $"(name contains '{text}' or fullText contains '{text}') and trashed = false";
        var json = await Google.GetAsync($"{GoogleUrls.Drive}/files?pageSize=15&orderBy=modifiedTime desc" +
            $"&fields=files(id,name,mimeType,modifiedTime,webViewLink)&q={Esc(q)}", ct);
        return new
        {
            archivos = (json.TryGetProperty("files", out var f) ? f.EnumerateArray() : Enumerable.Empty<JsonElement>()).Select(x => new
            {
                id = x.GetProperty("id").GetString(),
                nombre = x.GetProperty("name").GetString(),
                tipo = x.GetProperty("mimeType").GetString(),
                modificado = x.TryGetProperty("modifiedTime", out var m) ? m.GetString() : null,
                enlace = x.TryGetProperty("webViewLink", out var l) ? l.GetString() : null,
            }),
        };
    }
}

public sealed class GoogleDriveLeerTool(GoogleAccount google) : GoogleTool(google)
{
    private const int MaxChars = 20_000;

    public override ToolDefinition Definition { get; } = BrainToolResult.Define("google_drive_leer",
        "Lee el texto de un archivo de Drive (Documentos, Hojas de cálculo como CSV, Presentaciones o archivos de texto) por su id.",
        """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""", ToolRisk.Read);

    protected override async Task<object> RunAsync(JsonElement args, CancellationToken ct)
    {
        var id = Esc(ToolCatalog.Str(args, "id"));
        var meta = await Google.GetAsync($"{GoogleUrls.Drive}/files/{id}?fields=name,mimeType", ct);
        var mime = meta.GetProperty("mimeType").GetString() ?? "";
        var url = mime switch
        {
            "application/vnd.google-apps.document" or "application/vnd.google-apps.presentation" => $"{GoogleUrls.Drive}/files/{id}/export?mimeType=text/plain",
            "application/vnd.google-apps.spreadsheet" => $"{GoogleUrls.Drive}/files/{id}/export?mimeType=text/csv",
            _ when mime.StartsWith("text/", StringComparison.Ordinal) || mime is "application/json" => $"{GoogleUrls.Drive}/files/{id}?alt=media",
            _ => throw new ArgumentException($"No sé leer archivos de tipo {mime} (solo documentos, hojas, presentaciones y texto)."),
        };
        var text = await Google.GetTextAsync(url, ct);
        return new { nombre = meta.GetProperty("name").GetString(), texto = text.Length > MaxChars ? text[..MaxChars] + "…(recortado)" : text };
    }
}

public sealed class GoogleContactosBuscarTool(GoogleAccount google) : GoogleTool(google)
{
    private int _warmedUp;

    public override ToolDefinition Definition { get; } = BrainToolResult.Define("google_contactos_buscar",
        "Busca en los Contactos de Google de tu dueño por nombre, correo o teléfono (para escribirle a alguien, " +
        "saber su número, etc.). Úsalo antes de enviar un correo a alguien nombrado sin su dirección.",
        """{"type":"object","properties":{"nombre":{"type":"string"}},"required":["nombre"]}""", ToolRisk.Read);

    protected override async Task<object> RunAsync(JsonElement args, CancellationToken ct)
    {
        const string api = "https://people.googleapis.com/v1/people:searchContacts";
        const string mask = "names,emailAddresses,phoneNumbers,organizations";
        // Google pide una búsqueda vacía antes de la primera para preparar su caché
        if (Interlocked.Exchange(ref _warmedUp, 1) == 0)
            await Google.GetAsync($"{api}?query=&readMask={mask}", ct);
        var json = await Google.GetAsync($"{api}?query={Esc(ToolCatalog.Str(args, "nombre"))}&readMask={mask}&pageSize=10", ct);
        static IEnumerable<string?> Values(JsonElement p, string field, string key) =>
            p.TryGetProperty(field, out var arr) ? arr.EnumerateArray().Select(x => x.TryGetProperty(key, out var v) ? v.GetString() : null) : [];
        return new
        {
            contactos = (json.TryGetProperty("results", out var r) ? r.EnumerateArray() : Enumerable.Empty<JsonElement>()).Select(x =>
            {
                var p = x.GetProperty("person");
                return new
                {
                    nombre = Values(p, "names", "displayName").FirstOrDefault(),
                    correos = Values(p, "emailAddresses", "value").Where(v => v is not null).ToList(),
                    telefonos = Values(p, "phoneNumbers", "value").Where(v => v is not null).ToList(),
                    empresa = Values(p, "organizations", "name").FirstOrDefault(),
                };
            }),
        };
    }
}
