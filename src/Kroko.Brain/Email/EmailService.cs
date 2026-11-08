using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Kroko.Brain.Email;

/// <summary>
/// Sección "Email". La contraseña de aplicación NO va aquí: se guarda con
/// dotnet user-secrets set "Email:AppPassword" "xxxx xxxx xxxx xxxx".
/// </summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    public string? Address { get; set; }
    public string? AppPassword { get; set; }
    /// <summary>Nombre que aparece como remitente (vacío = el nombre del perfil del dueño).</summary>
    public string? DisplayName { get; set; }
    public string ImapHost { get; set; } = "imap.gmail.com";
    public int ImapPort { get; set; } = 993;
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 465;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Address) && !string.IsNullOrWhiteSpace(AppPassword);
}

/// <summary>Identificador de un correo para la IA: "carpeta#uid".</summary>
public readonly record struct MailId(string Folder, UniqueId Uid)
{
    public override string ToString() => $"{Folder}#{Uid.Id}";

    public static bool TryParse(string text, out MailId id)
    {
        id = default;
        var i = text.LastIndexOf('#');
        if (i <= 0 || !uint.TryParse(text[(i + 1)..], out var uid) || uid == 0) return false;
        id = new MailId(text[..i], new UniqueId(uid));
        return true;
    }
}

/// <summary>
/// Acceso a Gmail por IMAP (leer, buscar, carpetas, mover) y SMTP (enviar) con contraseña de aplicación.
/// Abre una conexión por operación (más lento pero robusto: nunca queda una conexión colgada).
/// </summary>
public sealed class EmailService(IOptionsMonitor<EmailOptions> options, Owner.OwnerProfileStore owner, TimeProvider time,
    ILogger<EmailService> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsConfigured => options.CurrentValue.IsConfigured;

    public string Address => options.CurrentValue.Address ?? "";

    /// <summary>Ejecuta algo con una conexión IMAP autenticada.</summary>
    public async Task<T> WithImapAsync<T>(Func<ImapClient, Task<T>> action, CancellationToken ct)
    {
        var opt = options.CurrentValue;
        if (!opt.IsConfigured) throw new EmailNotConfiguredException();

        await _gate.WaitAsync(ct);
        using var client = new ImapClient { Timeout = 30_000 };
        try
        {
            await client.ConnectAsync(opt.ImapHost, opt.ImapPort, SecureSocketOptions.SslOnConnect, ct);
            await client.AuthenticateAsync(opt.Address!, opt.AppPassword!.Replace(" ", ""), ct);
            return await action(client);
        }
        catch (AuthenticationException ex)
        {
            log.LogWarning("Gmail rechazó el acceso: {Message}", ex.Message);
            throw new EmailNotConfiguredException("Gmail rechazó la contraseña de aplicación. Hay que generarla de nuevo.");
        }
        finally
        {
            if (client.IsConnected)
            {
                try { await client.DisconnectAsync(true, CancellationToken.None); } catch (Exception) { }
            }
            _gate.Release();
        }
    }

    public async Task SendAsync(MimeMessage message, CancellationToken ct)
    {
        var opt = options.CurrentValue;
        if (!opt.IsConfigured) throw new EmailNotConfiguredException();

        using var smtp = new SmtpClient { Timeout = 30_000 };
        try
        {
            await smtp.ConnectAsync(opt.SmtpHost, opt.SmtpPort, SecureSocketOptions.SslOnConnect, ct);
            await smtp.AuthenticateAsync(opt.Address!, opt.AppPassword!.Replace(" ", ""), ct);
            await smtp.SendAsync(message, ct);
        }
        catch (AuthenticationException)
        {
            throw new EmailNotConfiguredException("Gmail rechazó la contraseña de aplicación. Hay que generarla de nuevo.");
        }
        finally
        {
            if (smtp.IsConnected) await smtp.DisconnectAsync(true, CancellationToken.None);
        }
    }

    /// <summary>Fecha de un correo en la hora del dueño.</summary>
    public string LocalDate(DateTimeOffset date) => TimeZoneInfo.ConvertTime(date, time.LocalTimeZone).ToString("yyyy-MM-dd HH:mm");

    public MailboxAddress Me()
    {
        var opt = options.CurrentValue;
        var name = !string.IsNullOrWhiteSpace(opt.DisplayName) ? opt.DisplayName : owner.Get().Name;
        return new MailboxAddress(name ?? "", opt.Address ?? throw new EmailNotConfiguredException());
    }

    /// <summary>Busca una carpeta por nombre (acepta "inbox", "enviados", "papelera"… o el nombre exacto).</summary>
    public static async Task<IMailFolder> ResolveFolderAsync(ImapClient client, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return client.Inbox;
        var key = name.Trim().ToLowerInvariant();

        if (key is "inbox" or "bandeja de entrada" or "entrada" or "recibidos") return client.Inbox;

        // Carpetas especiales: Gmail les pone nombres distintos según el idioma ("[Gmail]/Enviados"…),
        // por eso se piden por su función y no por su nombre.
        SpecialFolder? special = key switch
        {
            "enviados" or "sent" => SpecialFolder.Sent,
            "papelera" or "trash" => SpecialFolder.Trash,
            "spam" or "junk" or "no deseado" => SpecialFolder.Junk,
            "borradores" or "drafts" => SpecialFolder.Drafts,
            "destacados" or "starred" or "flagged" => SpecialFolder.Flagged,
            "todos" or "all" or "archivo" or "archivados" => SpecialFolder.All,
            "importantes" or "important" => SpecialFolder.Important,
            _ => null,
        };
        if (special is { } s && (client.Capabilities & ImapCapabilities.SpecialUse) != 0 && client.GetFolder(s) is { } f)
            return f;

        // Etiquetas y carpetas por nombre
        var all = await client.GetFoldersAsync(client.PersonalNamespaces[0], cancellationToken: ct);
        return all.FirstOrDefault(f => f.FullName.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? throw new EmailToolException($"No encontré la carpeta o etiqueta \"{name}\".");
    }

    public static SearchQuery BuildQuery(string? text, bool onlyUnread)
    {
        SearchQuery q = string.IsNullOrWhiteSpace(text) ? SearchQuery.All : SearchQuery.GMailRawSearch(text);
        return onlyUnread ? q.And(SearchQuery.NotSeen) : q;
    }
}

public sealed class EmailNotConfiguredException(string? message = null)
    : Exception(message ?? "El correo todavía no está conectado: falta la contraseña de aplicación de Gmail.");

public sealed class EmailToolException(string message) : Exception(message);
