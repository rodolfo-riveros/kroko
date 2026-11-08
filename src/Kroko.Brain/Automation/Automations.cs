using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Kroko.Brain.Agent;
using Kroko.Brain.Memory;
using Kroko.Brain.Productivity;
using Kroko.Brain.Providers;
using Kroko.Brain.Telegram;
using Kroko.Brain.Tts;
using Kroko.Shared.Protocol;
using Kroko.Shared.Tools;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Automation;

/// <summary>
/// Una tarea programada con IA: a la hora indicada, Kroko hace lo que dicen las instrucciones con sus
/// herramientas (buscar noticias, leer tareas, notas, correo…) y le manda el resultado a su dueño.
/// </summary>
public sealed record AutomationItem(long Id, string Name, string Instructions, IReadOnlyList<DayOfWeek> Days, TimeOnly Time,
    string Deliver, bool Enabled, DateOnly? LastRun)
{
    public static readonly string[] DeliverOptions = ["telegram", "app", "ambos"];

    public string DaysText => Days.Count == 7 ? "todos los días"
        : Days.Count == 5 && !Days.Contains(DayOfWeek.Saturday) && !Days.Contains(DayOfWeek.Sunday) ? "de lunes a viernes"
        : string.Join(", ", Days.Select(DayName));

    public static string DayName(DayOfWeek d) => CultureInfo.GetCultureInfo("es-PE").DateTimeFormat.GetDayName(d);

    /// <summary>¿Le toca ahora? Solo dentro de las 3 horas siguientes a su hora (si la PC estaba apagada, no se dispara de madrugada).</summary>
    public bool IsDue(DateTimeOffset now)
    {
        if (!Enabled || !Days.Contains(now.DayOfWeek)) return false;
        var today = DateOnly.FromDateTime(now.DateTime);
        if (LastRun == today) return false;
        var late = now.TimeOfDay - Time.ToTimeSpan();
        return late >= TimeSpan.Zero && late <= TimeSpan.FromHours(3);
    }

    /// <summary>"lunes", "laborables", "todos", "fin de semana"… → días.</summary>
    public static IReadOnlyList<DayOfWeek> ParseDays(IEnumerable<string> words)
    {
        var set = new SortedSet<DayOfWeek>();
        foreach (var raw in words)
        {
            var w = raw.Trim().ToLowerInvariant().Replace("é", "e").Replace("á", "a");
            switch (w)
            {
                case "todos" or "todos los dias" or "diario" or "cada dia":
                    foreach (var d in Enum.GetValues<DayOfWeek>()) set.Add(d);
                    break;
                case "laborables" or "lunes a viernes" or "entre semana":
                    for (var d = DayOfWeek.Monday; d <= DayOfWeek.Friday; d++) set.Add(d);
                    break;
                case "fin de semana" or "fines de semana":
                    set.Add(DayOfWeek.Saturday);
                    set.Add(DayOfWeek.Sunday);
                    break;
                case "lunes": set.Add(DayOfWeek.Monday); break;
                case "martes": set.Add(DayOfWeek.Tuesday); break;
                case "miercoles": set.Add(DayOfWeek.Wednesday); break;
                case "jueves": set.Add(DayOfWeek.Thursday); break;
                case "viernes": set.Add(DayOfWeek.Friday); break;
                case "sabado": set.Add(DayOfWeek.Saturday); break;
                case "domingo": set.Add(DayOfWeek.Sunday); break;
                default: throw new ArgumentException($"No entiendo el día \"{raw}\".");
            }
        }
        return set.ToList();
    }
}

public sealed class AutomationStore(KrokoDatabase db)
{
    public IReadOnlyList<AutomationItem> All()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, instructions, days, time, deliver, enabled, last_run FROM automations ORDER BY time, id";
        using var r = cmd.ExecuteReader();
        var list = new List<AutomationItem>();
        while (r.Read())
        {
            list.Add(new AutomationItem(r.GetInt64(0), r.GetString(1), r.GetString(2),
                r.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(d => (DayOfWeek)int.Parse(d, CultureInfo.InvariantCulture)).ToList(),
                TimeOnly.ParseExact(r.GetString(4), "HH:mm", CultureInfo.InvariantCulture), r.GetString(5), r.GetInt64(6) == 1,
                r.IsDBNull(7) ? null : DateOnly.ParseExact(r.GetString(7), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }
        return list;
    }

    public AutomationItem? Get(long id) => All().FirstOrDefault(a => a.Id == id);

    public long Add(string name, string instructions, IReadOnlyList<DayOfWeek> days, TimeOnly time, string deliver)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO automations(name, instructions, days, time, deliver, enabled, created)
            VALUES($n, $i, $d, $t, $v, 1, $c); SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$i", instructions);
        cmd.Parameters.AddWithValue("$d", string.Join(',', days.Select(d => ((int)d).ToString(CultureInfo.InvariantCulture))));
        cmd.Parameters.AddWithValue("$t", time.ToString("HH:mm", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$v", deliver);
        cmd.Parameters.AddWithValue("$c", KrokoDatabase.Now());
        return (long)cmd.ExecuteScalar()!;
    }

    public bool Delete(long id) => Exec("DELETE FROM automations WHERE id = $id", id);

    public bool SetEnabled(long id, bool enabled) =>
        Exec($"UPDATE automations SET enabled = {(enabled ? 1 : 0)} WHERE id = $id", id);

    public void MarkRun(long id, DateOnly day)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE automations SET last_run = $d WHERE id = $id";
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private bool Exec(string sql, long id)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }
}

/// <summary>
/// Ejecuta una automatización: el agente recibe las instrucciones como si se las dijera su dueño (en una
/// conversación aparte que se borra al terminar) y el resultado se envía por Telegram y/o a la App.
/// Las acciones peligrosas piden permiso con botones en Telegram (o con la tarjeta en la PC).
/// </summary>
public sealed class AutomationRunner(
    IServiceProvider services,
    AutomationStore store,
    AppConnections apps,
    TelegramBot telegram,
    IOptionsMonitor<ProductivityOptions> productivity,
    TimeProvider time,
    ILogger<AutomationRunner> log)
{
    private readonly ConcurrentDictionary<long, bool> _running = new();

    public async Task<string> RunAsync(AutomationItem a, CancellationToken ct)
    {
        if (!_running.TryAdd(a.Id, true)) return "Ya se está ejecutando.";
        var session = $"automatizacion-{a.Id}-{Guid.NewGuid():N}";
        try
        {
            log.LogInformation("Automatización «{Name}» en marcha", a.Name);
            var channel = new AutomationChannel(session, a.Name, apps, telegram);
            var prompt = $"""
                [Automatización programada «{a.Name}» ({a.DaysText} a las {a.Time:HH\:mm}). Tu dueño no está mirando la pantalla:
                no le hagas preguntas; haz lo pedido con tus herramientas y responde con el mensaje final listo para enviárselo
                (texto plano, claro, puede llevar líneas que empiecen con "• ").]
                {a.Instructions}
                """;
            using (var scope = services.CreateScope())
            {
                var agent = scope.ServiceProvider.GetRequiredService<AgentLoop>();
                await agent.HandleUserTextAsync(prompt, channel, ct);
            }

            var text = channel.Reply ?? "No pude completar la automatización esta vez.";
            await DeliverAsync(a, ExpressionTag.Extract(text).Text, channel.Cards, ct);
            return text;
        }
        finally
        {
            store.MarkRun(a.Id, DateOnly.FromDateTime(time.GetLocalNow().DateTime));
            _running.TryRemove(a.Id, out _);
            using var scope = services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ConversationStore>().Forget(session);
        }
    }

    private async Task DeliverAsync(AutomationItem a, string text, IReadOnlyList<CardMessage> cards, CancellationToken ct)
    {
        var wantTelegram = a.Deliver is "telegram" or "ambos";
        var wantApp = a.Deliver is "app" or "ambos";
        // Si el canal elegido no está disponible, se usa el otro: lo importante es que le llegue
        if (wantTelegram && !telegram.IsAvailable && apps.Any) wantApp = true;
        if (wantApp && !apps.Any && telegram.IsAvailable) wantTelegram = true;

        var delivered = false;
        if (wantTelegram && telegram.IsAvailable)
            delivered |= await telegram.SendToOwnerAsync($"⏰ {a.Name}\n\n{text}", ct);

        if (wantApp && apps.Any)
        {
            string? audio = null;
            if (productivity.CurrentValue.SpeakNotifications && text.Length <= 450)
            {
                using var scope = services.CreateScope();
                var wav = await scope.ServiceProvider.GetRequiredService<ITtsProvider>().SynthesizeAsync(text, ct);
                audio = wav is null ? null : Convert.ToBase64String(wav);
            }
            delivered |= await apps.BroadcastAsync(new NotifyMessage
            {
                Kind = "automatizacion",
                Title = a.Name,
                Text = text,
                Expression = "idea",
                AudioBase64 = audio,
            }, ct);
            foreach (var card in cards) await apps.BroadcastAsync(card, ct);
        }

        if (!delivered) log.LogWarning("Automatización «{Name}»: no había a quién entregarla (App cerrada y sin Telegram)", a.Name);
    }
}

/// <summary>Canal del agente para una automatización: guarda la respuesta, pide permisos por Telegram o la App y usa las "manos" de la App.</summary>
internal sealed class AutomationChannel(string sessionId, string name, AppConnections apps, TelegramBot telegram) : IAgentChannel
{
    private readonly ConcurrentDictionary<string, bool> _approvedRemotely = new();
    private readonly List<CardMessage> _cards = [];

    public string SessionId => sessionId;
    public string? Reply { get; private set; }
    public IReadOnlyList<CardMessage> Cards => _cards;

    public Task SendAsync(KrokoMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case ReplyMessage r: Reply = r.Text; break;
            case CardMessage c: _cards.Add(c); break;
        }
        return Task.CompletedTask;
    }

    public async Task<TResponse?> RequestAsync<TResponse>(KrokoMessage request, string callId, TimeSpan timeout, CancellationToken ct)
        where TResponse : KrokoMessage
    {
        switch (request)
        {
            case ConfirmRequestMessage confirm:
                if (telegram.IsRunning && telegram.LinkedChats.Count > 0)
                {
                    var answer = await telegram.AskOwnerAsync($"Automatización «{name}»: {confirm.Text}", ct);
                    if (answer is null) return null;
                    if (answer.Value) _approvedRemotely[callId] = true;
                    return new ConfirmResponseMessage { CallId = callId, Approved = answer.Value } as TResponse;
                }
                // Sin Telegram: la tarjeta sale en la PC (si está encendida)
                if (apps.Primary is { } app)
                    return await app.RequestAsync<TResponse>(confirm with { SessionId = app.SessionId }, callId, timeout, ct);
                return null;

            case ToolCallMessage call:
                if (apps.Primary is not { } pc)
                {
                    return new ToolResultMessage
                    {
                        CallId = callId,
                        Ok = false,
                        Error = "La PC está apagada o Kroko no está abierto en ella: eso no se pudo hacer.",
                    } as TResponse;
                }
                var forwarded = _approvedRemotely.TryRemove(callId, out _) ? call with { ApprovedRemotely = "Telegram" } : call;
                return await pc.RequestAsync<TResponse>(forwarded with { SessionId = pc.SessionId }, callId, timeout, ct);

            default:
                return null;
        }
    }
}

/// <summary>Revisa cada 30 s si alguna automatización tiene que correr.</summary>
public sealed class AutomationScheduler(AutomationStore store, AutomationRunner runner, TimeProvider time, ILogger<AutomationScheduler> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                var now = time.GetLocalNow();
                foreach (var a in store.All().Where(a => a.IsDue(now)))
                    _ = Task.Run(() => runner.RunAsync(a, stoppingToken), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Error revisando las automatizaciones");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

// ---------------------------------------------------------------- herramientas

public sealed class AutomatizacionCrearTool(AutomationStore store) : SyncBrainTool
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("automatizacion_crear",
        "Programa una tarea automática que repites con IA: a la hora indicada harás lo que digan las instrucciones con tus " +
        "herramientas (buscar noticias, leer tareas de Todoist/Trello/Google, notas de Notion, correos…) y le mandarás el resultado a tu dueño. " +
        "Ej.: \"cada mañana a las 7 resumen de noticias de Perú por Telegram\". Escribe las instrucciones completas y claras.",
        """
        {"type":"object","properties":{
          "nombre":{"type":"string","description":"Nombre corto, p. ej. Noticias de la mañana"},
          "instrucciones":{"type":"string","description":"Qué hacer, en detalle, como si te lo pidiera tu dueño"},
          "dias":{"type":"array","items":{"type":"string"},"description":"lunes…domingo, o 'todos', 'laborables', 'fin de semana'"},
          "hora":{"type":"string","description":"HH:mm (hora de tu dueño)"},
          "enviar_por":{"type":"string","enum":["telegram","app","ambos"]}
        },"required":["nombre","instrucciones","dias","hora"]}
        """);

    public override ToolExecution Execute(JsonElement args)
    {
        var name = ToolCatalog.Str(args, "nombre").Trim();
        var instructions = ToolCatalog.Str(args, "instrucciones").Trim();
        if (name.Length == 0 || instructions.Length < 5) return ToolConfirmation.Fail("Falta el nombre o las instrucciones.");
        if (!TimeOnly.TryParseExact(ToolCatalog.Str(args, "hora").Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            return ToolConfirmation.Fail("La hora debe ser HH:mm, por ejemplo 08:00.");
        IReadOnlyList<DayOfWeek> days;
        try
        {
            days = AutomationItem.ParseDays(BrainToolResult.List(args, "dias"));
        }
        catch (ArgumentException ex)
        {
            return ToolConfirmation.Fail(ex.Message);
        }
        if (days.Count == 0) return ToolConfirmation.Fail("Dime qué días.");
        var deliver = ToolCatalog.Str(args, "enviar_por") is { Length: > 0 } d && AutomationItem.DeliverOptions.Contains(d) ? d : "ambos";

        var id = store.Add(name, instructions, days, at, deliver);
        var item = store.Get(id)!;
        return BrainToolResult.Ok(new { id, nombre = name, cuando = $"{item.DaysText} a las {at:HH\\:mm}", enviar_por = deliver });
    }
}

public sealed class AutomatizacionesListarTool(AutomationStore store) : SyncBrainTool
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("automatizaciones_listar",
        "Lista las automatizaciones programadas (con su id, cuándo corren y si están activas).",
        """{"type":"object","properties":{}}""", ToolRisk.Read);

    public override ToolExecution Execute(JsonElement args) => BrainToolResult.Ok(new
    {
        automatizaciones = store.All().Select(a => new
        {
            a.Id,
            nombre = a.Name,
            cuando = $"{a.DaysText} a las {a.Time:HH\\:mm}",
            enviar_por = a.Deliver,
            activa = a.Enabled,
            instrucciones = a.Instructions,
        }),
    });
}

public sealed class AutomatizacionCambiarTool(AutomationStore store) : SyncBrainTool
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("automatizacion_cambiar",
        "Pausa, reactiva o borra una automatización por su id.",
        """{"type":"object","properties":{"id":{"type":"integer"},"accion":{"type":"string","enum":["pausar","activar","borrar"]}},"required":["id","accion"]}""");

    public override ToolExecution Execute(JsonElement args)
    {
        var id = ToolCatalog.Int(args, "id");
        var ok = ToolCatalog.Str(args, "accion") switch
        {
            "pausar" => store.SetEnabled(id, false),
            "activar" => store.SetEnabled(id, true),
            "borrar" => store.Delete(id),
            _ => false,
        };
        return ok ? BrainToolResult.Ok(new { id, hecho = ToolCatalog.Str(args, "accion") })
                  : ToolConfirmation.Fail($"No encontré la automatización #{id}.");
    }
}

public sealed class AutomatizacionProbarTool(AutomationStore store, AutomationRunner runner) : IBrainTool
{
    public ToolDefinition Definition { get; } = BrainToolResult.Define("automatizacion_probar",
        "Ejecuta ahora una automatización (por su id) para probarla; el resultado le llega a tu dueño por donde esté configurada.",
        """{"type":"object","properties":{"id":{"type":"integer"}},"required":["id"]}""");

    public async Task<ToolExecution> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (store.Get(ToolCatalog.Int(args, "id")) is not { } a) return ToolConfirmation.Fail("No encontré esa automatización.");
        // Se lanza aparte: la automatización usa el agente y no debe bloquear esta conversación
        _ = Task.Run(() => runner.RunAsync(a with { LastRun = null }, CancellationToken.None), CancellationToken.None);
        return BrainToolResult.Ok(new { nombre = a.Name, mensaje = "En marcha; el resultado llegará en un momento." });
    }
}
