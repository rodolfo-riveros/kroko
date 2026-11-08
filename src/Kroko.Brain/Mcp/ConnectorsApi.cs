using Kroko.Brain.Agent;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Mcp;

/// <summary>API para la ventana "Conectores" de la App (misma protección que el WebSocket).</summary>
public static class ConnectorsApi
{
    public static void MapConnectorsApi(this WebApplication app)
    {
        // Vuelta del permiso OAuth en modo servidor: la abre el navegador de Pol, así que no lleva token;
        // la protección es el "state" aleatorio, que solo el cerebro conoce.
        app.MapGet("/api/conectores/oauth/callback", (HttpContext ctx, McpHub hub) =>
        {
            var q = ctx.Request.Query;
            var ok = hub.CompleteOAuth(q["state"], q["code"], q["iss"]);
            var body = ok && !string.IsNullOrEmpty(q["code"])
                ? "<h2>¡Listo! Kroko ya está conectado.</h2><p>Puedes cerrar esta pestaña.</p>"
                : "<h2>No se pudo conectar</h2><p>El permiso caducó o fue rechazado. Vuelve a pulsar Conectar.</p>";
            return Results.Content($"<!doctype html><meta charset=utf-8><body style='font-family:Segoe UI;background:#0f0f0f;color:#f5f1e9;padding:40px'>{body}</body>", "text/html; charset=utf-8");
        });

        var group = app.MapGroup("/api/conectores").AddEndpointFilter(async (ctx, next) =>
        {
            var token = ctx.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<AgentOptions>>().CurrentValue.Token;
            return BrainAuth.IsAuthorized(ctx.HttpContext, token) ? await next(ctx) : Results.Unauthorized();
        });

        group.MapGet("/", (McpHub hub) => Results.Ok(hub.Status().Select(s => new
        {
            nombre = s.Name,
            titulo = s.Title,
            estado = s.State.ToString(),
            herramientas = s.Tools,
            detalle = s.Detail,
            puedeConectar = s.CanConnect,
            authUrl = s.AuthUrl,
        })));

        group.MapPost("/{name}/conectar", (string name, McpHub hub, IHostApplicationLifetime life) =>
        {
            try
            {
                hub.ConnectInteractive(name, life.ApplicationStopping);
                return Results.Accepted();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        group.MapPost("/{name}/desconectar", async (string name, McpHub hub) =>
        {
            await hub.DisconnectAsync(name);
            return Results.NoContent();
        });
    }
}
