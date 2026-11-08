using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Kroko.App.Tools;
using Kroko.Shared.Protocol;
using Kroko.Shared.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kroko.App.Services;

/// <summary>
/// Ejecuta en la PC las herramientas que pide el Brain. Es la segunda barrera de seguridad:
/// aunque el Brain se equivoque, la App vuelve a calcular el riesgo y NUNCA ejecuta una acción
/// peligrosa que Pol no haya aprobado en la tarjeta. También respeta la pausa de emergencia.
/// </summary>
public sealed class ToolExecutor
{
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(100);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<string, ILocalTool> _tools;
    private readonly BrainConnection _brain;
    private readonly AuditLog _audit;
    private readonly KrokoOptions _opt;
    private readonly ILogger<ToolExecutor> _log;
    private readonly ConcurrentDictionary<string, DateTime> _approved = new();
    private CancellationTokenSource _emergency = new();

    public ToolExecutor(IEnumerable<ILocalTool> tools, BrainConnection brain, AuditLog audit,
        IOptions<KrokoOptions> options, ILogger<ToolExecutor> log)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        _brain = brain;
        _audit = audit;
        _opt = options.Value;
        _log = log;
        _brain.MessageReceived += m => { if (m is ToolCallMessage call) _ = HandleAsync(call); };

        var missing = ToolCatalog.All.Select(t => t.Name).Where(n => !_tools.ContainsKey(n)).ToList();
        if (missing.Count > 0) _log.LogWarning("Herramientas del catálogo sin implementación: {Missing}", missing);
    }

    /// <summary>Pausa de emergencia activa: no se ejecuta ninguna herramienta.</summary>
    public bool IsPaused { get; private set; }

    public event Action<bool>? PausedChanged;

    /// <summary>Pol aprobó (o rechazó) una acción en la tarjeta.</summary>
    public void RecordDecision(string callId, string tool, string text, bool approved)
    {
        if (approved && !IsPaused) _approved[callId] = DateTime.Now;
        if (!approved)
            _audit.Record(new AuditEntry(DateTime.Now, tool, nameof(ToolRisk.Dangerous), text, "rechazada", false, "Rechazada en la tarjeta", 0));
    }

    /// <summary>Ctrl+Alt+K: cancela todo lo que esté en marcha y pausa a Kroko.</summary>
    public async Task EmergencyStopAsync()
    {
        _log.LogWarning("INTERRUPTOR DE EMERGENCIA: deteniendo todo");
        IsPaused = true;
        _approved.Clear();
        await _emergency.CancelAsync();
        _emergency.Dispose();
        _emergency = new CancellationTokenSource();
        await _brain.SendAsync(new StopMessage());
        PausedChanged?.Invoke(true);
    }

    public void Resume()
    {
        if (!IsPaused) return;
        IsPaused = false;
        _log.LogInformation("Kroko reanudado");
        PausedChanged?.Invoke(false);
    }

    private async Task HandleAsync(ToolCallMessage call) =>
        await _brain.SendAsync(await ExecuteAsync(call));

    /// <summary>Comprueba permisos, ejecuta y registra. Devuelve el resultado para el Brain.</summary>
    /// <summary>Una herramienta local produjo datos que se ven mejor como tarjeta (estado del equipo).</summary>
    public event Action<CardMessage>? CardReady;

    internal async Task<ToolResultMessage> ExecuteAsync(ToolCallMessage call)
    {
        var watch = Stopwatch.StartNew();
        var spec = ToolCatalog.Find(call.Name);
        var risk = spec?.EffectiveRisk(call.Args) ?? ToolRisk.Dangerous;
        var decision = risk == ToolRisk.Dangerous ? "aprobada" : "automática";
        ToolOutcome outcome;

        if (spec is null || !_tools.TryGetValue(call.Name, out var tool))
            outcome = ToolOutcome.Fail($"No tengo la herramienta {call.Name}.");
        else if (IsPaused)
            outcome = ToolOutcome.Fail("Kroko está en pausa por el interruptor de emergencia. Tu dueño tiene que reanudarlo (Ctrl+Alt+K).");
        else if (_opt.DisabledTools.Contains(call.Name, StringComparer.OrdinalIgnoreCase))
            outcome = ToolOutcome.Fail($"Tu dueño desactivó la herramienta {call.Name}.");
        else if (risk == ToolRisk.Dangerous && !IsApproved(call, ref decision))
        {
            _log.LogWarning("Bloqueada {Tool}: acción peligrosa sin aprobación de tu dueño", call.Name);
            decision = "bloqueada (sin confirmación)";
            outcome = ToolOutcome.Fail("Acción peligrosa sin confirmación de tu dueño: no la ejecuté.");
        }
        else
        {
            outcome = await RunAsync(tool, call);
        }

        watch.Stop();
        _log.LogInformation("Herramienta {Tool} ({Risk}) → {Result} en {Ms} ms",
            call.Name, risk, outcome.Ok ? "ok" : outcome.Error, watch.ElapsedMilliseconds);

        _audit.Record(new AuditEntry(DateTime.Now, call.Name, risk.ToString(), ArgsText(call.Args), decision,
            outcome.Ok, outcome.Ok ? Summarize(outcome.Data) : outcome.Error ?? "", watch.ElapsedMilliseconds));

        var data = outcome.Ok && outcome.Data is not null ? JsonSerializer.SerializeToElement(outcome.Data, Json) : (JsonElement?)null;
        if (data is { } d && Cards.FromToolResult(call.Name, d) is { } card) CardReady?.Invoke(card);

        return new ToolResultMessage
        {
            CallId = call.CallId,
            Ok = outcome.Ok,
            Data = data,
            Error = outcome.Error,
        };
    }

    /// <summary>
    /// Una acción peligrosa solo se ejecuta si Pol la aprobó: en la tarjeta de esta PC (aprobación de un solo uso)
    /// o, si está permitido, con los botones de Telegram.
    /// </summary>
    private bool IsApproved(ToolCallMessage call, ref string decision)
    {
        if (_approved.TryRemove(call.CallId, out _)) return true;
        if (call.ApprovedRemotely is { Length: > 0 } via && _opt.AllowRemoteApprovals)
        {
            decision = $"aprobada en {via}";
            return true;
        }
        return false;
    }

    private async Task<ToolOutcome> RunAsync(ILocalTool tool, ToolCallMessage call)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_emergency.Token);
        cts.CancelAfter(MaxDuration);
        try
        {
            // Fuera del hilo de la interfaz: Kroko sigue animado mientras trabaja
            return await Task.Run(() => tool.RunAsync(new ToolArgs(call.Args), cts.Token), cts.Token);
        }
        catch (ToolException ex)
        {
            return ToolOutcome.Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            return ToolOutcome.Fail(IsPaused ? "Detenido por el interruptor de emergencia." : "La herramienta tardó demasiado y se canceló.");
        }
        catch (UnauthorizedAccessException)
        {
            return ToolOutcome.Fail("Windows no me dio permiso para hacer eso.");
        }
        catch (IOException ex)
        {
            return ToolOutcome.Fail($"Problema con el archivo: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Nunca se muestran excepciones crudas: se registran y se informa con sencillez
            _log.LogError(ex, "Error inesperado en la herramienta {Tool}", call.Name);
            return ToolOutcome.Fail("Algo falló al hacerlo. Quedó anotado en el registro.");
        }
    }

    private static string ArgsText(JsonElement args) =>
        args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText();

    private static string Summarize(object? data)
    {
        if (data is null) return "ok";
        var json = JsonSerializer.Serialize(data, Json);
        // Las capturas no se guardan en el registro
        var i = json.IndexOf("\"imageBase64\"", StringComparison.Ordinal);
        return i >= 0 ? json[..i] + "\"imagen\":\"(no se guarda)\"}" : json;
    }
}
