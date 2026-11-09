using System.Text.Json;
using Kroko.Brain.Memory;

namespace Kroko.Brain.Usage;

/// <summary>Tokens que informó el proveedor en una llamada (entrada, salida y, de la entrada, cuántos vinieron de caché).</summary>
public sealed record LlmUsage(long Input, long Output, long Cached = 0)
{
    public long Total => Input + Output;
}

/// <summary>Uso acumulado de un día o un mes, por tipo (chat, tiempo_real, resumen, voz, transcripcion…).</summary>
public sealed class UsageTotals
{
    public long Input { get; set; }
    public long Output { get; set; }
    public long Cached { get; set; }
    public int Calls { get; set; }
    public Dictionary<string, long> ByKind { get; set; } = [];

    public long Total => Input + Output;

    internal void Add(string kind, LlmUsage u)
    {
        Input += u.Input;
        Output += u.Output;
        Cached += u.Cached;
        Calls++;
        ByKind[kind] = ByKind.GetValueOrDefault(kind) + u.Total;
    }
}

/// <summary>
/// Medidor de gasto de IA (decidido por Pol, 08/11/2026, tras gastar sus 26 soles de Gemini): cuenta los tokens de cada
/// llamada por día y por mes (en la tabla kv) y aplica el tope diario: al pasarlo, Kroko entra en "modo ahorro"
/// (sin voz en tiempo real ni voz de Gemini; el chat sigue). Los días son los del dueño (OwnerTimeProvider).
/// </summary>
public sealed class UsageTracker(KrokoDatabase db, TimeProvider time, ILogger<UsageTracker> log)
{
    private const string LimitKey = "uso.limite_diario";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly AsyncLocal<string?> CurrentKind = new();
    private readonly Lock _lock = new();
    private string? _noticeDay;

    /// <summary>Marca el tipo de las llamadas que se hagan dentro (p. ej. "resumen"). Por defecto, "chat".</summary>
    public static IDisposable Scope(string kind)
    {
        var previous = CurrentKind.Value;
        CurrentKind.Value = kind;
        return new Restore(() => CurrentKind.Value = previous);
    }

    public void Record(LlmUsage? usage, string? kind = null)
    {
        if (usage is null || usage.Total <= 0) return;
        kind ??= CurrentKind.Value ?? "chat";
        var now = time.GetLocalNow();
        lock (_lock)
        {
            Update(DayKey(now), kind, usage);
            Update(MonthKey(now), kind, usage);
        }
        if (IsOverDailyLimit && _noticeDay != now.ToString("yyyy-MM-dd"))
            log.LogWarning("Se pasó el tope diario de IA ({Limit} tokens): modo ahorro", DailyLimit);
    }

    public UsageTotals Today => Read(DayKey(time.GetLocalNow()));
    public UsageTotals ThisMonth => Read(MonthKey(time.GetLocalNow()));

    /// <summary>Tope diario en tokens (0 = sin tope).</summary>
    public long DailyLimit
    {
        get => long.TryParse(db.GetValue(LimitKey), out var v) && v > 0 ? v : 0;
        set => db.SetValue(LimitKey, Math.Max(0, value).ToString());
    }

    public bool IsOverDailyLimit => DailyLimit > 0 && Today.Total >= DailyLimit;

    /// <summary>true la primera vez del día que se pasa el tope (para avisarle al dueño una sola vez).</summary>
    public bool TakeLimitNotice()
    {
        if (!IsOverDailyLimit) return false;
        var day = time.GetLocalNow().ToString("yyyy-MM-dd");
        lock (_lock)
        {
            if (_noticeDay == day) return false;
            _noticeDay = day;
            return true;
        }
    }

    private void Update(string key, string kind, LlmUsage usage)
    {
        var totals = Read(key);
        totals.Add(kind, usage);
        db.SetValue(key, JsonSerializer.Serialize(totals, Json));
    }

    private UsageTotals Read(string key)
    {
        try
        {
            return db.GetValue(key) is { Length: > 0 } json ? JsonSerializer.Deserialize<UsageTotals>(json, Json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static string DayKey(DateTimeOffset now) => $"uso.dia.{now:yyyy-MM-dd}";
    private static string MonthKey(DateTimeOffset now) => $"uso.mes.{now:yyyy-MM}";

    /// <summary>Lee usageMetadata de Gemini (generateContent y Live) si viene en la respuesta.</summary>
    public static LlmUsage? FromGemini(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("usageMetadata", out var u) || u.ValueKind != JsonValueKind.Object)
            return null;
        long N(string name) => u.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
        // Live usa responseTokenCount; generateContent, candidatesTokenCount. Los "pensamientos" también se cobran.
        var output = N("candidatesTokenCount") + N("responseTokenCount") + N("thoughtsTokenCount");
        var input = N("promptTokenCount") + N("toolUsePromptTokenCount");
        return input + output == 0 ? null : new LlmUsage(input, output, N("cachedContentTokenCount"));
    }

    /// <summary>Lee "usage" de una API compatible con OpenAI (DeepSeek).</summary>
    public static LlmUsage? FromOpenAi(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
            return null;
        long N(string name) => u.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
        return new LlmUsage(N("prompt_tokens"), N("completion_tokens"), N("prompt_cache_hit_tokens"));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
