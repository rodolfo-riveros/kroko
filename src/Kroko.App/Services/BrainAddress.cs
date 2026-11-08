using System.Net;
using System.Net.Http;
using System.Text.Json;
using Kroko.Shared.Protocol;

namespace Kroko.App.Services;

/// <summary>
/// La dirección del cerebro tal como la escribe el dueño en Ajustes → Cerebro o en el asistente: acepta
/// "kroko.midominio.com", "https://kroko.midominio.com/", "wss://…/ws" o "localhost:5080" y la deja en su forma
/// canónica ("https://kroko.midominio.com"). Así Kroko nunca se rompe por cómo se escribió el dominio.
/// </summary>
public static class BrainAddress
{
    public sealed record Result(bool Ok, string Url, string? Error)
    {
        public bool IsLocal => Ok && Uri.TryCreate(Url, UriKind.Absolute, out var u) && u.IsLoopback;
    }

    public static Result Normalize(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0 || text is "https://" or "http://")
            return Fail("Escribe la dirección del cerebro, por ejemplo https://kroko.midominio.com");

        // Sin esquema: https para internet, http para esta PC
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            var host = text.Split('/', ':')[0];
            text = (IsLocalHost(host) ? "http://" : "https://") + text;
        }
        if (text.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) text = "https://" + text[6..];
        else if (text.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) text = "http://" + text[5..];

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0
            || uri.Host.Contains(' '))
            return Fail($"\"{input?.Trim()}\" no parece una dirección. Ejemplo: https://kroko.midominio.com");

        if (!uri.IsLoopback && uri.Scheme != "https")
            return Fail("Un cerebro en internet tiene que usar https (con candado): https://" + uri.Authority);

        // Solo origen: lo demás (/ws, /health, barras al final) lo pone Kroko
        return new Result(true, uri.GetLeftPart(UriPartial.Authority), null);
    }

    private static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.StartsWith("127.", StringComparison.Ordinal) || host == "[::1]";

    private static Result Fail(string error) => new(false, "", error);

    /// <summary>
    /// Prueba una dirección y un token SIN guardarlos: primero si el cerebro responde (/health/llm, abierto) y luego si
    /// el token sirve (/api/perfil, protegido). Devuelve un texto claro para el dueño.
    /// </summary>
    public static async Task<(bool Ok, string Message)> ProbeAsync(string input, string? token, HttpMessageHandler? handler = null,
        CancellationToken ct = default)
    {
        var address = Normalize(input);
        if (!address.Ok) return (false, address.Error!);

        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = new Uri(address.Url + "/");
        http.Timeout = TimeSpan.FromSeconds(10);
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Add(KrokoProtocol.TokenHeader, token.Trim());

        string ai;
        try
        {
            using var health = await http.GetAsync("health/llm", ct);
            if (!health.IsSuccessStatusCode)
                return (false, $"En {address.Url} hay un servidor, pero no es el cerebro de Kroko (respondió {(int)health.StatusCode}).");
            using var doc = JsonDocument.Parse(await health.Content.ReadAsStringAsync(ct));
            var providers = doc.RootElement.TryGetProperty("providers", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).ToList()
                : [];
            ai = providers.Count > 0 ? "IA: " + string.Join(", ", providers) : "todavía SIN clave de IA (ponla en Configurar Kroko)";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, $"No encuentro el cerebro en {address.Url}. Revisa la dirección, que el servidor esté encendido y que tenga HTTPS.");
        }

        try
        {
            using var profile = await http.GetAsync("api/perfil", ct);
            if (profile.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (false, string.IsNullOrWhiteSpace(token)
                    ? "El cerebro responde, pero falta el token (el valor de Agent__Token en el .env del servidor)."
                    : "El cerebro responde, pero el token no es correcto. Cópialo de nuevo desde el .env del servidor.");
            if (!profile.IsSuccessStatusCode)
                return (false, $"El cerebro responde, pero algo falló al entrar ({(int)profile.StatusCode}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, $"El cerebro respondió una vez y luego se cortó. Prueba de nuevo en unos segundos.");
        }

        return (true, $"Conectado a {address.Url} · {ai}");
    }
}
