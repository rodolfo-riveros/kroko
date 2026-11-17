using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kroko.Brain.Agent;
using Kroko.Brain.Config;
using Kroko.Brain.Email;
using Kroko.Brain.Providers;
using Kroko.Brain.Telegram;
using Kroko.Brain.Web;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Owner;

/// <summary>
/// API del asistente "Configurar Kroko": perfil del dueño, cuentas (guardadas cifradas en el SecretStore),
/// pruebas de conexión y vinculación de Telegram. Misma protección que el WebSocket (token o solo esta PC).
/// Nunca devuelve el valor de una clave: solo si está puesta y de dónde sale.
/// </summary>
public static class SetupApi
{
    public sealed record ProfileDto(string? Nombre, string? ComoLlamarte, string? Ciudad, string? Pais,
        string? ZonaHoraria, string? Acento, string? SobreMi);

    public sealed record TestResult(bool Ok, string Mensaje);

    public static void MapSetupApi(this WebApplication app)
    {
        var group = app.MapGroup("/api").AddEndpointFilter(async (ctx, next) =>
        {
            var token = ctx.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<AgentOptions>>().CurrentValue.Token;
            return BrainAuth.IsAuthorized(ctx.HttpContext, token) ? await next(ctx) : Results.Unauthorized();
        });

        // ------------------------------------------------------------ perfil

        group.MapGet("/perfil", (OwnerProfileStore owner) =>
        {
            var dto = ToDto(owner.Get(), out var complete);
            return Results.Ok(new { perfil = dto, completo = complete });
        });

        group.MapPut("/perfil", (ProfileDto body, OwnerProfileStore owner) =>
        {
            if (string.IsNullOrWhiteSpace(body.Nombre)) return Results.BadRequest(new { error = "Falta tu nombre." });
            try
            {
                var saved = owner.Save(new OwnerProfile
                {
                    Name = body.Nombre ?? "",
                    CallMe = body.ComoLlamarte ?? "",
                    City = body.Ciudad ?? "",
                    Country = body.Pais ?? "",
                    TimeZone = body.ZonaHoraria ?? "",
                    Accent = body.Acento ?? "",
                    About = body.SobreMi ?? "",
                });
                return Results.Ok(new { perfil = ToDto(saved, out var complete), completo = complete });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ------------------------------------------------------------ cuentas

        group.MapGet("/configuracion", (IConfiguration config, IOptionsMonitor<LlmOptions> llm, IOptionsMonitor<WebOptions> web,
            EmailService email, TelegramBot telegram, OwnerProfileStore owner, Apps.TrelloClient trello, Apps.GoogleAccount google) =>
        {
            var sources = KeySources.Describe(config);
            return Results.Ok(new
            {
                perfilCompleto = owner.Get().IsComplete,
                gemini = !string.IsNullOrEmpty(llm.CurrentValue.Gemini.ResolveApiKey()),
                // Claves de Gemini que se turnan: cuántas hay y cuántas pueden usarse ahora (las agotadas descansan un rato)
                geminiClaves = llm.CurrentValue.Gemini.AllApiKeys().Count,
                geminiDisponibles = GeminiKeyRing.Available(llm.CurrentValue.Gemini.AllApiKeys()),
                deepseek = !string.IsNullOrEmpty(llm.CurrentValue.DeepSeek.ResolveApiKey()),
                tavily = web.CurrentValue.TavilyConfigured,
                fuentes = sources,
                correo = new { configurado = email.IsConfigured, direccion = email.Address },
                telegram = TelegramStatus(telegram, config),
                trello = trello.IsConfigured,
                google = new { cliente = google.HasClient, conectado = google.IsConnected, correo = google.Email },
            });
        });

        group.MapPut("/configuracion/secretos", (Dictionary<string, string?> body, SecretStore secrets) =>
        {
            try
            {
                secrets.Update(body);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/configuracion/probar/{que}", async (string que, IServiceProvider sp, CancellationToken ct) =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                return Results.Ok(que.ToLowerInvariant() switch
                {
                    "gemini" => await TestGeminiAsync(sp, cts.Token),
                    "deepseek" => await TestDeepSeekAsync(sp, cts.Token),
                    "tavily" => await TestTavilyAsync(sp, cts.Token),
                    "correo" => await TestEmailAsync(sp, cts.Token),
                    "telegram" => TestTelegram(sp),
                    "trello" => await TestTrelloAsync(sp, cts.Token),
                    _ => new TestResult(false, "No sé probar eso."),
                });
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Ok(new TestResult(false, "No respondió a tiempo. Revisa tu internet."));
            }
        });

        // ------------------------------------------------------------ Telegram

        group.MapPost("/telegram/vincular", (TelegramBot telegram) =>
        {
            if (!telegram.IsRunning || telegram.BotUsername is null)
                return Results.BadRequest(new { error = "Primero pon el token del bot (y espera unos segundos a que conecte)." });
            var (code, expires) = telegram.StartPairing();
            return Results.Ok(new
            {
                codigo = code,
                bot = "@" + telegram.BotUsername,
                enlace = $"https://t.me/{telegram.BotUsername}?start={code}",
                caduca = expires,
            });
        });

        group.MapGet("/telegram/estado", (TelegramBot telegram, IConfiguration config) => Results.Ok(TelegramStatus(telegram, config)));

        group.MapDelete("/telegram/vinculo", (TelegramBot telegram) =>
        {
            telegram.Unlink();
            return Results.NoContent();
        });
    }

    private static ProfileDto ToDto(OwnerProfile p, out bool complete)
    {
        complete = p.IsComplete;
        return new ProfileDto(p.Name, p.CallMe, p.City, p.Country, p.TimeZone, p.Accent, p.About);
    }

    private static object TelegramStatus(TelegramBot telegram, IConfiguration config) => new
    {
        token = !string.IsNullOrWhiteSpace(config["Telegram:BotToken"]),
        conectado = telegram.IsRunning,
        bot = telegram.BotUsername is { } u ? "@" + u : null,
        vinculado = telegram.LinkedChats.Count > 0,
        chats = telegram.LinkedChats.Count,
        esperandoCodigo = telegram.PairingPending,
    };

    // ------------------------------------------------------------ pruebas de conexión

    private static async Task<TestResult> TestGeminiAsync(IServiceProvider sp, CancellationToken ct)
    {
        var opt = sp.GetRequiredService<IOptionsMonitor<LlmOptions>>().CurrentValue.Gemini;
        var keys = opt.AllApiKeys();
        if (keys.Count == 0) return new(false, "Falta la API key de Gemini.");

        // Cada clave hace una pregunta real y diminuta con el modelo que usa Kroko (17/11/2026: antes solo se miraba si
        // Google conocía la clave, y una clave sin saldo o sin cuota «funcionaba» en la prueba pero no podía pensar).
        string[] jobs = ["pensar", "entender tu voz", "hablar", "buscar"];
        var lines = new List<string>();
        var ok = 0;
        for (var i = 0; i < keys.Count; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{opt.BaseUrl.TrimEnd('/')}/v1beta/models/{Uri.EscapeDataString(opt.Model)}:generateContent")
            {
                Content = JsonContent.Create(new
                {
                    contents = new[] { new { role = "user", parts = new[] { new { text = "Responde solo: ok" } } } },
                    generationConfig = new { maxOutputTokens = 16 },
                }),
            };
            req.Headers.Add("x-goog-api-key", keys[i]);
            var who = keys.Count == 1 ? "Tu clave" : $"Clave {i + 1} ({jobs[Math.Min(i, 3)]})";
            try
            {
                using var res = await sp.GetRequiredService<IHttpClientFactory>().CreateClient().SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                var status = (int)res.StatusCode;
                if (res.IsSuccessStatusCode)
                {
                    ok++;
                    lines.Add($"✓ {who}: funciona.");
                    continue;
                }
                GeminiKeyRing.Report(keys[i], status, body);
                lines.Add($"✗ {who}: {KeyProblem(status, body)}");
            }
            catch (HttpRequestException)
            {
                lines.Add($"✗ {who}: el cerebro no logra comunicarse con Google.");
            }
        }
        var head = ok == keys.Count
            ? (keys.Count == 1 ? "Gemini responde: tu clave funciona." : $"Las {keys.Count} claves funcionan, cada una con su trabajo.")
            : $"Funcionan {ok} de {keys.Count} claves.";
        return new(ok > 0, keys.Count == 1 && ok == 1 ? head : head + "\n" + string.Join("\n", lines));
    }

    /// <summary>El motivo, en palabras simples.</summary>
    internal static string KeyProblem(int status, string body) => status switch
    {
        402 => "se acabó su saldo (ai.studio/projects → Facturación).",
        _ when GeminiKeyRing.IsBadKey(status, body) => "Google no la acepta (no es válida, venció o la API está desactivada en su proyecto).",
        429 when body.Contains("limit: 0", StringComparison.OrdinalIgnoreCase) =>
            "su proyecto no tiene cuota para este modelo (en el plan gratis puede no estar disponible).",
        429 => "llegó a su límite de uso por ahora; vuelve sola en un rato.",
        404 => "el modelo no está disponible para esta clave.",
        >= 500 => "Gemini está saturado; prueba en un momento.",
        _ => $"Google respondió {status}.",
    };

    private static async Task<TestResult> TestDeepSeekAsync(IServiceProvider sp, CancellationToken ct)
    {
        var opt = sp.GetRequiredService<IOptionsMonitor<LlmOptions>>().CurrentValue.DeepSeek;
        if (opt.ResolveApiKey() is not { } key) return new(false, "Falta la API key de DeepSeek.");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{opt.BaseUrl.TrimEnd('/')}/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return await SendAsync(sp, req, "DeepSeek responde: la clave funciona.", ct);
    }

    private static async Task<TestResult> TestTavilyAsync(IServiceProvider sp, CancellationToken ct)
    {
        var tavily = sp.GetRequiredService<TavilyClient>();
        if (!tavily.IsConfigured) return new(false, "Falta la API key de Tavily.");
        try
        {
            var r = await tavily.SearchAsync("Kroko prueba de conexión", ct);
            return r is null ? new(false, "Tavily no respondió.") : new(true, "Tavily responde: la clave funciona (gastó 1 crédito).");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return new(false, $"Tavily rechazó la clave ({ex.Message}).");
        }
    }

    private static async Task<TestResult> TestEmailAsync(IServiceProvider sp, CancellationToken ct)
    {
        var email = sp.GetRequiredService<EmailService>();
        if (!email.IsConfigured) return new(false, "Falta tu correo o la contraseña de aplicación.");
        try
        {
            await email.WithImapAsync(async c =>
            {
                await c.Inbox.OpenAsync(MailKit.FolderAccess.ReadOnly, ct);
                return true;
            }, ct);
            return new(true, $"Gmail conectado ({email.Address}).");
        }
        catch (EmailNotConfiguredException ex)
        {
            return new(false, ex.Message);
        }
        catch (Exception ex) when (ex is MailKit.ServiceNotConnectedException or MailKit.ProtocolException or IOException
                                       or System.Net.Sockets.SocketException or MailKit.Net.Imap.ImapCommandException)
        {
            return new(false, $"No pude entrar a Gmail ({ex.Message}).");
        }
    }

    private static async Task<TestResult> TestTrelloAsync(IServiceProvider sp, CancellationToken ct)
    {
        var trello = sp.GetRequiredService<Apps.TrelloClient>();
        if (!trello.IsConfigured) return new(false, "Falta la clave o el token de Trello.");
        try
        {
            var me = await trello.GetAsync("members/me", new() { ["fields"] = "fullName,username" }, ct);
            return new(true, $"Trello conectado ({me.GetProperty("fullName").GetString()}).");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            return new(false, ex.Message);
        }
    }

    private static TestResult TestTelegram(IServiceProvider sp)
    {
        var bot = sp.GetRequiredService<TelegramBot>();
        return bot.IsRunning
            ? new(true, $"Bot conectado: @{bot.BotUsername}.")
            : new(false, "El bot no está conectado: revisa el token (te lo da @BotFather).");
    }

    private static async Task<TestResult> SendAsync(IServiceProvider sp, HttpRequestMessage req, string okText, CancellationToken ct)
    {
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("setup-test");
        try
        {
            using var res = await http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return new(true, okText);
            return new(false, (int)res.StatusCode is 400 or 401 or 403
                ? "La clave no es válida (el servicio la rechazó)."
                : $"El servicio respondió {(int)res.StatusCode}. Prueba otra vez en un rato.");
        }
        catch (HttpRequestException ex)
        {
            return new(false, $"No pude conectar ({ex.Message}).");
        }
    }
}
