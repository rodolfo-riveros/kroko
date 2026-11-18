using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using QRCoder;

namespace Kroko.App.Services.LanShare;

/// <summary>
/// Enviar un archivo al celular sin app ni cuentas (17/11/2026, especificación «soltar archivos»): Kroko abre un
/// servidor diminuto solo en la red privada (Wi-Fi o cable de casa), muestra un QR con un enlace de UN solo uso que vence
/// a los 5 minutos, y se apaga solo al terminar o vencer. Varios archivos se entregan en un .zip.
/// </summary>
public sealed class PhoneShare(ILogger<PhoneShare> log) : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly Lock _gate = new();
    private Offer? _current;
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;

    public sealed record Offer(string Token, string FilePath, string FileName, bool TempZip, DateTime ExpiresAt, string Link,
        BitmapSource Qr)
    {
        public bool Used { get; set; }
    }

    /// <summary>Prepara el envío y devuelve el enlace con su QR. Reemplaza cualquier envío anterior.</summary>
    public async Task<Offer> OfferAsync(IReadOnlyList<string> files)
    {
        Cancel();
        var ip = PrivateAddress() ?? throw new InvalidOperationException(
            "No encuentro tu red Wi-Fi o de casa. Conecta la PC y el celular a la misma red.");

        // Varios archivos: un .zip temporal
        string path, name;
        var zip = false;
        if (files.Count == 1)
        {
            path = files[0];
            name = Path.GetFileName(path);
        }
        else
        {
            name = $"Kroko {DateTime.Now:yyyy-MM-dd HH.mm}.zip";
            path = Path.Combine(Path.GetTempPath(), "kroko-" + Guid.NewGuid().ToString("N") + ".zip");
            await Task.Run(() =>
            {
                using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
                foreach (var f in files) archive.CreateEntryFromFile(f, Path.GetFileName(f), CompressionLevel.Fastest);
            });
            zip = true;
        }

        _listener = new TcpListener(ip, 0); // un puerto libre, solo en la red privada
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var link = $"http://{ip}:{port}/d/{token}";
        var offer = new Offer(token, path, name, zip, DateTime.UtcNow + Lifetime, link, MakeQr(link));
        lock (_gate) _current = offer;
        _stop = new CancellationTokenSource(Lifetime);
        _stop.Token.Register(Cancel);
        _ = ServeAsync(_listener, _stop.Token);
        log.LogInformation("Envío al celular listo en {Ip}:{Port} (vence en 5 min)", ip, port);
        return offer;
    }

    /// <summary>Apaga el servidor y borra el .zip temporal (si lo hubo).</summary>
    public void Cancel()
    {
        Offer? old;
        lock (_gate)
        {
            old = _current;
            _current = null;
        }
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
        if (old is { TempZip: true }) TryDelete(old.FilePath);
    }

    /// <summary>Solo para las pruebas: deja preparado un envío sin abrir el servidor.</summary>
    internal void UseForTest(Offer offer)
    {
        lock (_gate) _current = offer;
    }

    /// <summary>¿Este enlace sirve ahora? (para las pruebas y para el servidor).</summary>
    internal Offer? Take(string token, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_current is not { } o || o.Used || nowUtc > o.ExpiresAt || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(o.Token), Encoding.ASCII.GetBytes(token)))
                return null;
            o.Used = true; // un solo uso
            return o;
        }
    }

    private async Task ServeAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 10_000;
                var stream = client.GetStream();
                var line = await ReadRequestLineAsync(stream, ct);
                var parts = line.Split(' ');
                var path = parts.Length >= 2 ? parts[1] : "";
                var token = path.StartsWith("/d/", StringComparison.Ordinal) ? path[3..] : "";
                if (parts[0] != "GET" || Take(token, DateTime.UtcNow) is not { } offer)
                {
                    await WriteTextAsync(stream, "410 Gone", "Este enlace ya se usó o venció. Pídele a Kroko uno nuevo.", ct);
                    return;
                }
                var size = new FileInfo(offer.FilePath).Length;
                var header = "HTTP/1.1 200 OK\r\n" +
                             $"Content-Type: {(offer.TempZip ? "application/zip" : Tools.FileSendGuard.MimeOf(offer.FileName))}\r\n" +
                             $"Content-Length: {size}\r\n" +
                             $"Content-Disposition: attachment; filename=\"{Ascii(offer.FileName)}\"; filename*=UTF-8''{Uri.EscapeDataString(offer.FileName)}\r\n" +
                             "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
                await using (var file = File.OpenRead(offer.FilePath)) await file.CopyToAsync(stream, ct);
                log.LogInformation("El celular descargó {Name}", offer.FileName);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                // Un solo uso: tras la descarga (o el intento) el servidor se apaga
                lock (_gate)
                    if (_current is { Used: true }) _ = Task.Run(Cancel);
            }
        }
    }

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer, ct);
        var text = Encoding.ASCII.GetString(buffer, 0, read);
        var end = text.IndexOf("\r\n", StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }

    private static async Task WriteTextAsync(NetworkStream stream, string status, string message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width'><p style='font:18px sans-serif;padding:24px'>{message}</p>");
        var header = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await stream.WriteAsync(body, ct);
    }

    /// <summary>La IP de la red de casa (10.x, 172.16–31.x o 192.168.x) de una conexión activa con puerta de enlace.</summary>
    internal static IPAddress? PrivateAddress() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address))
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a));

    internal static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    internal static BitmapSource MakeQr(string text)
    {
        using var data = new QRCodeGenerator().CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8, [0, 0, 0], [255, 255, 255]);
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(png);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string Ascii(string s) => new([.. s.Select(c => c is >= ' ' and < (char)127 and not '"' ? c : '_')]);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => Cancel();
}
