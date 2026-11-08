using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Kroko.App.Native;
using NAudio.CoreAudioApi;

namespace Kroko.App.Tools;

// ======================================================================== programas

public sealed class AbrirProgramaTool(ProgramLocator locator) : ILocalTool
{
    public string Name => "abrir_programa";

    public async Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var nombre = args.String("nombre");
        var match = await locator.FindAsync(nombre, ct);
        if (match is null)
        {
            var sugerencias = await locator.SuggestAsync(nombre, ct);
            return ToolOutcome.Fail($"No encontré un programa llamado \"{nombre}\"." +
                                    (sugerencias.Count > 0 ? $" Parecidos: {string.Join(", ", sugerencias)}." : ""));
        }

        ProgramLocator.Launch(match);
        return ToolOutcome.Success(new { abierto = match.DisplayName });
    }
}

public sealed class CerrarProgramaTool : ILocalTool
{
    // Procesos que Kroko nunca cierra
    private static readonly HashSet<string> Untouchable = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "csrss", "wininit", "winlogon", "services", "lsass", "smss", "svchost", "dwm",
        "explorer", "Kroko.App", "Kroko.Brain", "MsMpEng", "fontdrvhost", "sihost", "ctfmon",
    };

    public string Name => "cerrar_programa";

    public async Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var nombre = args.String("nombre");
        var forzar = args.Bool("forzar");
        var key = ProgramLocator.Normalize(nombre);
        var names = ProgramLocator.ProcessNames.TryGetValue(key, out var known) ? known : [];
        var own = Environment.ProcessId;

        var targets = Process.GetProcesses()
            .Where(p => p.Id != own && !Untouchable.Contains(p.ProcessName))
            .Where(p => names.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)
                        || ProgramLocator.Score(key, ProgramLocator.Normalize(p.ProcessName)) >= 65
                        || (p.MainWindowHandle != 0 && key.Length >= 4 &&
                            ProgramLocator.Normalize(SafeTitle(p)).Contains(key, StringComparison.Ordinal)))
            .ToList();

        if (targets.Count == 0) return ToolOutcome.Fail($"No hay ningún programa abierto que coincida con \"{nombre}\".");

        var programa = targets[0].ProcessName;
        foreach (var p in targets) TryCloseGracefully(p);

        var deadline = DateTime.Now.AddSeconds(4);
        while (DateTime.Now < deadline && targets.Any(IsAlive)) await Task.Delay(200, ct);

        var vivos = targets.Where(IsAlive).ToList();
        if (vivos.Count > 0 && forzar)
        {
            foreach (var p in vivos)
                try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await Task.Delay(300, ct);
            vivos = targets.Where(IsAlive).ToList();
        }

        foreach (var p in targets) p.Dispose();
        return vivos.Count == 0
            ? ToolOutcome.Success(new { cerrado = programa, procesos = targets.Count })
            : ToolOutcome.Fail($"{programa} no se cerró (quizá está preguntando si guardar cambios). Puedo forzarlo si tu dueño quiere.");
    }

    private static void TryCloseGracefully(Process p)
    {
        try
        {
            if (p.MainWindowHandle != 0) p.CloseMainWindow();
        }
        catch (InvalidOperationException) { }
    }

    private static bool IsAlive(Process p)
    {
        try { return !p.HasExited; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static string SafeTitle(Process p)
    {
        try { return p.MainWindowTitle; } catch (InvalidOperationException) { return ""; }
    }
}

public sealed class ProcesosListarTool : ILocalTool
{
    public string Name => "procesos_listar";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var procs = Process.GetProcesses();
        try
        {
            var ventanas = procs
                .Where(p => SafeHandle(p) != 0 && SafeTitle(p).Length > 0)
                .Select(p => new { programa = p.ProcessName, titulo = SafeTitle(p) })
                .OrderBy(x => x.programa)
                .Take(40)
                .ToList();

            var memoria = procs
                .GroupBy(p => p.ProcessName)
                .Select(g => new { programa = g.Key, mb = (int)(g.Sum(SafeMemory) / (1024 * 1024)) })
                .OrderByDescending(x => x.mb)
                .Take(12)
                .ToList();

            return Task.FromResult(ToolOutcome.Success(new { ventanasAbiertas = ventanas, masMemoria = memoria }));
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    private static nint SafeHandle(Process p) { try { return p.MainWindowHandle; } catch (InvalidOperationException) { return 0; } }
    private static string SafeTitle(Process p) { try { return p.MainWindowTitle; } catch (InvalidOperationException) { return ""; } }
    private static long SafeMemory(Process p) { try { return p.WorkingSet64; } catch (InvalidOperationException) { return 0; } }
}

// ======================================================================== abrir cosas

public sealed class AbrirCarpetaTool : ILocalTool
{
    public string Name => "abrir_carpeta";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var ruta = PathResolver.Resolve(args.String("ruta"));
        if (!Directory.Exists(ruta)) return Task.FromResult(ToolOutcome.Fail($"La carpeta {ruta} no existe."));
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ruta}\"") { UseShellExecute = true })?.Dispose();
        return Task.FromResult(ToolOutcome.Success(new { abierta = ruta }));
    }
}

public sealed class AbrirArchivoTool : ILocalTool
{
    private static readonly HashSet<string> Executable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".scr", ".com", ".jar", ".reg",
    };

    public string Name => "abrir_archivo";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var ruta = PathResolver.Resolve(args.String("ruta"));
        if (!File.Exists(ruta)) return Task.FromResult(ToolOutcome.Fail($"El archivo {ruta} no existe."));

        // Ejecutar scripts o instaladores no es "abrir un archivo" (sección 8: solo lista blanca, fase posterior)
        if (Executable.Contains(Path.GetExtension(ruta)))
            return Task.FromResult(ToolOutcome.Fail("Por seguridad no ejecuto programas ni scripts con esta herramienta. Usa abrir_programa."));

        Process.Start(new ProcessStartInfo(ruta) { UseShellExecute = true })?.Dispose();
        return Task.FromResult(ToolOutcome.Success(new { abierto = ruta }));
    }
}

public sealed class AbrirUrlTool : ILocalTool
{
    public string Name => "abrir_url";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var text = args.String("url");
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return Task.FromResult(ToolOutcome.Fail("Solo abro direcciones web http o https."));

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        return Task.FromResult(ToolOutcome.Success(new { abierta = uri.AbsoluteUri }));
    }
}

// ======================================================================== estado del equipo

public sealed class EstadoEquipoTool : ILocalTool
{
    public string Name => "estado_equipo";

    public async Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var cpu = await MeasureCpuAsync(ct);

        var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        NativeMethods.GlobalMemoryStatusEx(ref mem);

        object bateria;
        if (NativeMethods.GetSystemPowerStatus(out var ps) && ps.BatteryFlag != 128 && ps.BatteryLifePercent != 255)
        {
            bateria = new
            {
                porcentaje = (int)ps.BatteryLifePercent,
                cargando = ps.ACLineStatus == 1,
                minutosRestantes = ps.BatteryLifeTime > 0 ? ps.BatteryLifeTime / 60 : (int?)null,
            };
        }
        else bateria = "sin batería (PC de escritorio o enchufada sin batería)";

        var discos = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new
            {
                unidad = d.Name.TrimEnd('\\'),
                libreGB = Math.Round(d.AvailableFreeSpace / 1e9, 1),
                totalGB = Math.Round(d.TotalSize / 1e9, 1),
                usadoPct = (int)Math.Round(100 - 100.0 * d.AvailableFreeSpace / d.TotalSize),
            })
            .ToList();

        var redes = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                        && n.GetIPProperties().GatewayAddresses.Count > 0)
            .Select(n => new { nombre = n.Name, tipo = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "wifi" : "cable/otra" })
            .ToList();

        var encendida = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return ToolOutcome.Success(new
        {
            cpuPct = cpu,
            ramPct = (int)mem.dwMemoryLoad,
            ramLibreGB = Math.Round(mem.ullAvailPhys / 1e9, 1),
            ramTotalGB = Math.Round(mem.ullTotalPhys / 1e9, 1),
            bateria,
            discos,
            internet = NetworkInterface.GetIsNetworkAvailable() && redes.Count > 0,
            redes,
            encendidaDesde = $"{(int)encendida.TotalHours} h {encendida.Minutes} min",
        });
    }

    private static async Task<int> MeasureCpuAsync(CancellationToken ct)
    {
        NativeMethods.GetSystemTimes(out var i1, out var k1, out var u1);
        await Task.Delay(500, ct);
        NativeMethods.GetSystemTimes(out var i2, out var k2, out var u2);
        var idle = i2.Value - i1.Value;
        var total = (k2.Value - k1.Value) + (u2.Value - u1.Value); // kernel incluye idle
        return total == 0 ? 0 : (int)Math.Round(100.0 * (total - idle) / total);
    }
}

// ======================================================================== sonido y pantalla

public sealed class VolumenTool : ILocalTool
{
    public string Name => "volumen";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var nivel = args.Int("nivel", 0, 100);
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = nivel / 100f;
        if (nivel > 0 && device.AudioEndpointVolume.Mute) device.AudioEndpointVolume.Mute = false;
        return Task.FromResult(ToolOutcome.Success(new { volumen = nivel, dispositivo = device.FriendlyName }));
    }
}

public sealed class SilenciarTool : ILocalTool
{
    public string Name => "silenciar";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var silenciar = args.Raw.TryGetProperty("silenciar", out _) ? args.Bool("silenciar") : true;
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        device.AudioEndpointVolume.Mute = silenciar;
        return Task.FromResult(ToolOutcome.Success(new { silenciado = silenciar }));
    }
}

public sealed class BrilloTool : ILocalTool
{
    public string Name => "brillo";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var nivel = (byte)args.Int("nivel", 0, 100);
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            using var results = searcher.Get();
            var count = 0;
            foreach (ManagementObject m in results)
            {
                using (m) m.InvokeMethod("WmiSetBrightness", [1u, nivel]);
                count++;
            }
            return Task.FromResult(count > 0
                ? ToolOutcome.Success(new { brillo = (int)nivel })
                : ToolOutcome.Fail("Esta pantalla no permite cambiar el brillo por software (pasa en monitores de escritorio)."));
        }
        catch (ManagementException)
        {
            return Task.FromResult(ToolOutcome.Fail("Esta pantalla no permite cambiar el brillo por software (pasa en monitores de escritorio)."));
        }
    }
}

// ======================================================================== energía y sesión

public sealed class ApagarTool : ILocalTool
{
    public string Name => "apagar";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct) =>
        Task.FromResult(Shutdown.Schedule("/s", args.Int("minutos", 0, 1440), "apagado"));
}

public sealed class ReiniciarTool : ILocalTool
{
    public string Name => "reiniciar";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct) =>
        Task.FromResult(Shutdown.Schedule("/r", args.Int("minutos", 0, 1440), "reinicio"));
}

public sealed class CancelarApagadoTool : ILocalTool
{
    public string Name => "cancelar_apagado";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var code = Shutdown.Run("/a");
        return Task.FromResult(code == 0
            ? ToolOutcome.Success(new { cancelado = true })
            : ToolOutcome.Fail("No había ningún apagado ni reinicio programado."));
    }
}

public sealed class CerrarSesionTool : ILocalTool
{
    public string Name => "cerrar_sesion";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        // Pequeña espera para que Kroko alcance a responder antes de que se cierre la sesión
        _ = Task.Delay(4000, CancellationToken.None).ContinueWith(_ => Shutdown.Run("/l"), TaskScheduler.Default);
        return Task.FromResult(ToolOutcome.Success(new { sesion = "se cierra en unos segundos" }));
    }
}

public sealed class BloquearPcTool : ILocalTool
{
    public string Name => "bloquear_pc";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        _ = Task.Delay(1500, CancellationToken.None).ContinueWith(_ => NativeMethods.LockWorkStation(), TaskScheduler.Default);
        return Task.FromResult(ToolOutcome.Success(new { bloqueada = true }));
    }
}

public sealed class SuspenderTool : ILocalTool
{
    public string Name => "suspender";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        _ = Task.Delay(2500, CancellationToken.None).ContinueWith(_ => NativeMethods.SetSuspendState(false, false, false), TaskScheduler.Default);
        return Task.FromResult(ToolOutcome.Success(new { suspendida = "en unos segundos" }));
    }
}

internal static class Shutdown
{
    public static ToolOutcome Schedule(string mode, int minutes, string what)
    {
        // Mínimo 10 s para que Kroko pueda responder (y para poder cancelarlo con "cancela el apagado")
        var seconds = Math.Max(10, minutes * 60);
        var code = Run($"{mode} /t {seconds} /c \"Kroko: {what} programado. Di 'cancela el apagado' para evitarlo.\"");
        if (code == 1190) // ya hay uno programado
            return ToolOutcome.Fail($"Ya hay un apagado o reinicio programado. Cancélalo primero si quieres cambiarlo.");
        return code == 0
            ? ToolOutcome.Success(new { programado = what, enMinutos = minutes, hora = DateTime.Now.AddSeconds(seconds).ToString("HH:mm") })
            : ToolOutcome.Fail($"Windows no aceptó el {what} (código {code}).");
    }

    public static int Run(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo("shutdown.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        p.WaitForExit(10_000);
        return p.HasExited ? p.ExitCode : -1;
    }
}

// ======================================================================== captura y portapapeles

public sealed class CapturaPantallaTool : ILocalTool
{
    private const int MaxWidth = 1600;

    public string Name => "captura_pantalla";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT(0, 0), NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (!NativeMethods.TryGetMonitorInfo(monitor, out var mi)) return Task.FromResult(ToolOutcome.Fail("No pude leer la pantalla."));
        var r = mi.rcMonitor;

        var screen = NativeMethods.GetDC(0);
        var mem = NativeMethods.CreateCompatibleDC(screen);
        var bmp = NativeMethods.CreateCompatibleBitmap(screen, r.Width, r.Height);
        var old = NativeMethods.SelectObject(mem, bmp);
        try
        {
            NativeMethods.BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.Left, r.Top, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
            NativeMethods.SelectObject(mem, old);

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(bmp, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (source.PixelWidth > MaxWidth)
            {
                var k = (double)MaxWidth / source.PixelWidth;
                source = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(k, k));
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);

            return Task.FromResult(ToolOutcome.Success(new
            {
                ancho = source.PixelWidth,
                alto = source.PixelHeight,
                mimeType = "image/jpeg",
                imageBase64 = Convert.ToBase64String(ms.ToArray()),
            }));
        }
        finally
        {
            NativeMethods.DeleteObject(bmp);
            NativeMethods.DeleteDC(mem);
            NativeMethods.ReleaseDC(0, screen);
        }
    }
}

public sealed class PortapapelesLeerTool : ILocalTool
{
    public string Name => "portapapeles_leer";

    public async Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var text = await ClipboardAccess.RunAsync(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        if (text is null) return ToolOutcome.Fail("El portapapeles no tiene texto.");
        return ToolOutcome.Success(new { texto = text.Length > 8000 ? text[..8000] + "…(recortado)" : text });
    }
}

public sealed class PortapapelesEscribirTool : ILocalTool
{
    public string Name => "portapapeles_escribir";

    public async Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var texto = args.String("texto");
        await ClipboardAccess.RunAsync(() =>
        {
            Clipboard.SetText(texto);
            return true;
        });
        return ToolOutcome.Success(new { copiado = texto.Length + " caracteres" });
    }
}

/// <summary>El portapapeles solo se usa desde el hilo de la interfaz y a veces está ocupado: se reintenta.</summary>
internal static class ClipboardAccess
{
    public static async Task<T> RunAsync<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await Application.Current.Dispatcher.InvokeAsync(action);
            }
            catch (COMException) when (attempt < 4)
            {
                await Task.Delay(150);
            }
        }
    }
}

// ======================================================================== scripts (lista blanca)

public sealed class EjecutarScriptTool(Microsoft.Extensions.Options.IOptions<Kroko.App.Services.KrokoOptions> options) : ILocalTool
{
    private const int MaxOutput = 4000;

    public string Name => "ejecutar_script";

    public async Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var name = args.String("nombre");
        var list = options.Value.ScriptWhitelist;
        if (!list.TryGetValue(name, out var path))
            return ToolOutcome.Fail(list.Count == 0
                ? "Tu dueño todavía no autorizó ningún script (Ajustes → Herramientas → Lista blanca)."
                : $"\"{name}\" no está en la lista blanca. Autorizados: {string.Join(", ", list.Keys)}.");

        path = Environment.ExpandEnvironmentVariables(path);
        if (!File.Exists(path)) return ToolOutcome.Fail($"El script autorizado ya no existe: {path}");

        var ext = Path.GetExtension(path).ToLowerInvariant();
        var psi = ext switch
        {
            ".ps1" => new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path } },
            ".bat" or ".cmd" => new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", path } },
            ".exe" => new ProcessStartInfo(path),
            _ => null,
        };
        if (psi is null) return ToolOutcome.Fail("Solo ejecuto .ps1, .bat, .cmd o .exe.");

        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.WorkingDirectory = Path.GetDirectoryName(path)!;

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return ToolOutcome.Fail("El script tardó más de 90 s y lo detuve.");
        }

        string Cut(string s) => s.Length <= MaxOutput ? s.Trim() : s[..MaxOutput] + "…";
        return p.ExitCode == 0
            ? ToolOutcome.Success(new { script = name, salida = Cut(await stdout) })
            : ToolOutcome.Fail($"El script terminó con error {p.ExitCode}: {Cut(await stderr)}");
    }
}
