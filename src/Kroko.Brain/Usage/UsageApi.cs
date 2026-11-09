using Kroko.Brain.Agent;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Usage;

/// <summary>/api/uso: cuánto gastó Kroko en IA hoy y este mes, y el tope diario (Ajustes → Cerebro).</summary>
public static class UsageApi
{
    public sealed record LimitDto(long Tokens);

    public static void MapUsageApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/uso").AddEndpointFilter(async (ctx, next) =>
        {
            var token = ctx.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<AgentOptions>>().CurrentValue.Token;
            return BrainAuth.IsAuthorized(ctx.HttpContext, token) ? await next(ctx) : Results.Unauthorized();
        });

        group.MapGet("", (UsageTracker usage) => Results.Ok(new
        {
            hoy = Describe(usage.Today),
            mes = Describe(usage.ThisMonth),
            limiteDiario = usage.DailyLimit,
            modoAhorro = usage.IsOverDailyLimit,
        }));

        group.MapPut("/limite", (LimitDto body, UsageTracker usage) =>
        {
            usage.DailyLimit = body.Tokens;
            return Results.Ok(new { limiteDiario = usage.DailyLimit, modoAhorro = usage.IsOverDailyLimit });
        });
    }

    private static object Describe(UsageTotals t) => new
    {
        total = t.Total,
        entrada = t.Input,
        salida = t.Output,
        enCache = t.Cached,
        llamadas = t.Calls,
        porTipo = t.ByKind,
    };
}
