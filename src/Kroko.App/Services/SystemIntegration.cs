using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using Kroko.App.Face;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using Velopack;
using Velopack.Sources;

namespace Kroko.App.Services;

/// <summary>Ajustes que Pol cambia desde la ventana de Ajustes (%LOCALAPPDATA%\Kroko\settings.json).</summary>
public static class UserSettings
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", "settings.json");

    /// <summary>Lee la sección "Kroko" guardada (o un objeto vacío).</summary>
    public static JsonObject LoadKrokoSection()
    {
        try
        {
            if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject root && root["Kroko"] is JsonObject k)
                return k;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return [];
    }

    public static void SaveKrokoSection(JsonObject kroko)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var root = new JsonObject { ["Kroko"] = kroko };
        File.WriteAllText(FilePath, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }
}

/// <summary>"Iniciar con Windows" (HKCU\…\Run, sin permisos de administrador).</summary>
public static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Kroko";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(Name, throwOnMissingValue: false);
    }
}

/// <summary>Reinicia Kroko (para aplicar ajustes que se leen al arrancar).</summary>
public static class AppRestart
{
    public static void Now()
    {
        if (Environment.ProcessPath is { } exe)
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
        Application.Current.Shutdown();
    }
}

/// <summary>
/// Icono en la bandeja (sección 11): Hablar · Mostrar/Ocultar · Pausar · Ajustes · Salir.
/// El icono es la propia cara de Kroko dibujada con el motor de expresiones.
/// </summary>
public sealed class TrayService(ExpressionLibrary library, Audio.WakeWordService wake, WindowService windows, ILogger<TrayService> log) : IDisposable
{
    private TaskbarIcon? _icon;

    public void Start(Action talk, Action toggleVisible, Action togglePause, Action openSettings)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Hablar (Ctrl+Espacio)", talk));
        menu.Items.Add(Item("Mi día", windows.ShowMyDay));
        menu.Items.Add(Item("Mostrar / ocultar", toggleVisible));
        menu.Items.Add(Item("Pausar / reanudar (Ctrl+Alt+K)", togglePause));
        var listen = new MenuItem { Header = "Escuchar «Oye Kroko»", IsCheckable = true, IsChecked = !wake.UserPaused };
        listen.Click += (_, _) => wake.UserPaused = !listen.IsChecked;
        menu.Items.Add(listen);
        menu.Items.Add(Item("Ajustes", openSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Salir", () => Application.Current.Shutdown()));

        _icon = new TaskbarIcon
        {
            ToolTipText = "Kroko",
            Icon = TrayIconImage.ToIcon(RenderIcon()),
            ContextMenu = menu,
            MenuActivation = H.NotifyIcon.Core.PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => toggleVisible();
        wake.ListeningChanged += listening => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_icon is not null) _icon.ToolTipText = listening ? "Kroko · escuchando «Oye Kroko»" : "Kroko";
        });
        _icon.ForceCreate(false);
        log.LogInformation("Icono de bandeja listo");
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>La cara de Kroko en reposo, 64×64.</summary>
    private BitmapSource RenderIcon()
    {
        var anim = new FaceAnimator(library, () => 1.0) { EnableBlink = false, FollowCursor = false, EnableBreath = false };
        anim.SetBase("idle");
        var frame = anim.Tick(null);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Encuadre centrado en el cuerpo (no en todo el lienzo)
            var scale = 64 / ((FaceRenderer.Radius + 14) * 2);
            FaceRenderer.Render(dc, frame, scale,
                32 - FaceRenderer.CenterX * scale, 34 - FaceRenderer.CenterY * scale);
        }
        var bmp = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    public void Dispose() => _icon?.Dispose();
}

/// <summary>
/// Autoactualización con Velopack desde GitHub Releases (sección 13). Solo en la App instalada.
/// Revisa al arrancar y cada 4 horas; si hay versión nueva la descarga en segundo plano y la aplica
/// reiniciándose cuando Kroko está dormido (no interrumpe nada). Si se cierra antes, se aplica al cerrar.
/// </summary>
public sealed class UpdateService(IOptions<KrokoOptions> options, CompanionBehavior behavior, ILogger<UpdateService> log)
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(4);
    private UpdateManager? _manager;
    private UpdateInfo? _ready;

    /// <summary>true = es la App instalada (no la versión de desarrollo).</summary>
    public static bool IsInstalled
    {
        get
        {
            try
            {
                return new UpdateManager(new SimpleWebSource("https://localhost")).IsInstalled;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public string? CurrentVersion => _manager?.CurrentVersion?.ToString();

    public async Task RunAsync(CancellationToken ct)
    {
        var url = options.Value.UpdatesUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        IUpdateSource source = url.Contains("github.com", StringComparison.OrdinalIgnoreCase)
            ? new GithubSource(url, null, false)
            : new SimpleWebSource(url);
        _manager = new UpdateManager(source);
        if (!_manager.IsInstalled) return; // en desarrollo no hay nada que actualizar

        var nextCheck = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            if (_ready is null && DateTime.Now >= nextCheck)
            {
                nextCheck = DateTime.Now + CheckEvery;
                await CheckAsync();
            }
            // Ya descargada: se aplica cuando Kroko está dormido (nadie lo está usando)
            if (_ready is not null && behavior.IsSleeping)
            {
                log.LogInformation("Aplicando la versión {Version} y reiniciando", _ready.TargetFullRelease.Version);
                _manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease);
                return;
            }
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CheckAsync()
    {
        try
        {
            var info = await _manager!.CheckForUpdatesAsync();
            if (info is null) return;
            log.LogInformation("Nueva versión {Version}: descargando", info.TargetFullRelease.Version);
            await _manager.DownloadUpdatesAsync(info);
            _ready = info;
            // Por si Kroko se cierra antes de dormirse: se aplica al salir, sin preguntar
            _manager.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            log.LogWarning("No se pudo buscar actualizaciones: {Message}", ex.Message);
        }
    }
}

/// <summary>
/// Convierte una imagen dibujada en WPF en un icono de Windows. H.NotifyIcon solo convierte imágenes cargadas desde
/// un archivo (con URI), así que se le da directamente un icono: un .ico con la imagen PNG dentro (Windows Vista+).
/// </summary>
public static class TrayIconImage
{
    public static System.Drawing.Icon ToIcon(BitmapSource image)
    {
        using var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(png);
        var bytes = png.ToArray();

        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((short)0);  // reservado
            w.Write((short)1);  // tipo: icono
            w.Write((short)1);  // una imagen
            w.Write((byte)(image.PixelWidth >= 256 ? 0 : image.PixelWidth));
            w.Write((byte)(image.PixelHeight >= 256 ? 0 : image.PixelHeight));
            w.Write((byte)0);   // sin paleta
            w.Write((byte)0);
            w.Write((short)1);  // planos
            w.Write((short)32); // bits por píxel
            w.Write(bytes.Length);
            w.Write(6 + 16);    // la imagen empieza tras la cabecera y la entrada
            w.Write(bytes);
        }
        ico.Position = 0;
        return new System.Drawing.Icon(ico);
    }
}
