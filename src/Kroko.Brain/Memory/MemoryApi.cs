using Kroko.Brain.Agent;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Memory;

/// <summary>
/// API para la ventana "Memoria de Kroko" de la App: ver y borrar recuerdos, rutinas y mejoras.
/// Misma protección que el WebSocket (token o solo esta computadora).
/// </summary>
public static class MemoryApi
{
    public static void MapMemoryApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/memoria").AddEndpointFilter(async (ctx, next) =>
        {
            var token = ctx.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<AgentOptions>>().CurrentValue.Token;
            return BrainAuth.IsAuthorized(ctx.HttpContext, token) ? await next(ctx) : Results.Unauthorized();
        });

        group.MapGet("/", (MemoryStore memory, RoutineStore routines, SuggestionStore suggestions) => Results.Ok(new
        {
            recuerdos = memory.All(),
            rutinas = routines.All(),
            mejoras = suggestions.All(),
            buzon = suggestions.FilePath,
        }));

        group.MapDelete("/recuerdos/{id:long}", (long id, MemoryStore memory) =>
            memory.Forget(id) ? Results.NoContent() : Results.NotFound());

        group.MapDelete("/rutinas/{id:long}", (long id, RoutineStore routines) =>
            routines.Delete(id) ? Results.NoContent() : Results.NotFound());

        group.MapDelete("/mejoras/{id:long}", (long id, SuggestionStore suggestions) =>
            suggestions.Delete(id) ? Results.NoContent() : Results.NotFound());

        group.MapDelete("/conversacion", (Kroko.Brain.Agent.ConversationStore conversations) =>
        {
            conversations.ClearAll();
            return Results.NoContent();
        });

        group.MapPost("/mejoras/{id:long}/hecha", (long id, SuggestionStore suggestions) =>
            suggestions.SetStatus(id, "hecha") ? Results.NoContent() : Results.NotFound());
    }
}
