using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kroko.Shared.Protocol;

namespace Kroko.Brain.Agent;

/// <summary>
/// Quién puede hablar con el Brain: con token configurado, solo quien mande X-Kroko-Token correcto;
/// sin token, solo programas de esta misma computadora.
/// </summary>
public static class BrainAuth
{
    public static bool IsAuthorized(HttpContext context, string? expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken))
        {
            var ip = context.Connection.RemoteIpAddress;
            return ip is not null && IPAddress.IsLoopback(ip);
        }

        var given = context.Request.Headers[KrokoProtocol.TokenHeader].ToString();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expectedToken));
    }
}
