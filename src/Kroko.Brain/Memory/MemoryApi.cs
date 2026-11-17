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

        // La conversación para la sección «Chat» del panel (17/11/2026, como en Troko Desk): lo que dijiste, lo que
        // respondió y lo que hizo (con la misma palabra del check: «Anotado», «Agendado»…). Sin resultados internos.
        group.MapGet("/conversacion/{sessionId}", (string sessionId, Kroko.Brain.Agent.ConversationStore conversations) =>
        {
            var items = new List<object>();
            foreach (var m in conversations.Get(sessionId).Snapshot())
            {
                if (m.Role == Kroko.Brain.Providers.ChatRole.User && !m.Text.StartsWith("[Automatización", StringComparison.Ordinal))
                    items.Add(new { quien = "tu", texto = m.Text });
                else if (m.Role == Kroko.Brain.Providers.ChatRole.Assistant)
                {
                    foreach (var call in m.ToolCalls)
                        if (Kroko.Brain.Agent.Badges.For(call.Name) is { } done) items.Add(new { quien = "accion", texto = done });
                    var said = Kroko.Shared.Protocol.Reactions.StripTags(m.Text);
                    if (said.Length > 0) items.Add(new { quien = "kroko", texto = said });
                }
            }
            return Results.Ok(new { mensajes = items.TakeLast(80) });
        });

        group.MapDelete("/conversacion", (Kroko.Brain.Agent.ConversationStore conversations) =>
        {
            conversations.ClearAll();
            return Results.NoContent();
        });

        group.MapPost("/mejoras/{id:long}/hecha", (long id, SuggestionStore suggestions) =>
            suggestions.SetStatus(id, "hecha") ? Results.NoContent() : Results.NotFound());
    }
}
