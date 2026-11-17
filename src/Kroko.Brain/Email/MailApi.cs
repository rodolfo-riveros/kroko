using Kroko.Brain.Agent;
using MailKit;
using MailKit.Search;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Email;

/// <summary>
/// La sección «Correo» del panel (17/11/2026, como en Troko Desk): tus correos importantes sin leer, para que Kroko te
/// ayude a responderlos. Solo lee (no marca como leído); responder pasa por Kroko y siempre pide permiso antes de enviar.
/// </summary>
public static class MailApi
{
    public sealed record MailItem(string Id, string De, string Asunto, string Fecha);

    public static void MapMailApi(this WebApplication app)
    {
        app.MapGet("/api/correo/importantes", async (HttpContext ctx, EmailService email, IOptionsMonitor<AgentOptions> agent,
            TimeProvider time, CancellationToken ct) =>
        {
            if (!BrainAuth.IsAuthorized(ctx, agent.CurrentValue.Token)) return Results.Unauthorized();
            if (!email.IsConfigured) return Results.Ok(new { conectado = false, correos = Array.Empty<MailItem>() });
            try
            {
                var items = await email.WithImapAsync(async client =>
                {
                    var inbox = client.Inbox;
                    await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
                    var uids = await inbox.SearchAsync(SearchQuery.GMailRawSearch("is:important is:unread newer_than:3d"), ct);
                    var newest = uids.OrderByDescending(u => u.Id).Take(15).ToList();
                    if (newest.Count == 0) return new List<MailItem>();
                    var summaries = await inbox.FetchAsync(newest, MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId, ct);
                    var tz = time.LocalTimeZone;
                    return summaries.OrderByDescending(s => s.UniqueId.Id).Select(s =>
                    {
                        var from = s.Envelope?.From.Mailboxes.FirstOrDefault();
                        var when = s.Envelope?.Date is { } d ? TimeZoneInfo.ConvertTime(d, tz).ToString("dd/MM HH:mm") : "";
                        return new MailItem(s.UniqueId.Id.ToString(), from?.Name is { Length: > 0 } n ? n : from?.Address ?? "alguien",
                            s.Envelope?.Subject ?? "(sin asunto)", when);
                    }).ToList();
                }, ct);
                return Results.Ok(new { conectado = true, correos = items });
            }
            catch (Exception ex) when (ex is MailKit.Security.AuthenticationException or MailKit.ServiceNotConnectedException
                                           or IOException or System.Net.Sockets.SocketException)
            {
                return Results.Ok(new { conectado = true, correos = Array.Empty<MailItem>(), error = "No pude leer tu correo ahora." });
            }
        });
    }
}
