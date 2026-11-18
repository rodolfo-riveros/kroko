using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kroko.App.Services;

/// <summary>Algo que copiaste: un enlace, un texto o una imagen.</summary>
public sealed partial class ClipItem : ObservableObject
{
    public required string Kind { get; init; } // enlace, texto, imagen
    public string Text { get; init; } = "";
    public BitmapSource? Image { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;

    public string Preview => Kind == "imagen" ? "Captura de pantalla" : Text.Length > 90 ? Text[..87].ReplaceLineEndings(" ") + "…" : Text.ReplaceLineEndings(" ");

    public string Ago
    {
        get
        {
            var m = (int)(DateTime.Now - Time).TotalMinutes;
            return m < 1 ? "ahora" : m < 60 ? $"hace {m} min" : $"hace {m / 60} h";
        }
    }

    public bool IsLink => Kind == "enlace";
    public bool IsImage => Kind == "imagen";
    public bool IsText => Kind == "texto";
}

/// <summary>
/// El portapapeles (diseño Kroko 2, 18/11/2026): Windows avisa cada vez que copias algo (AddClipboardFormatListener)
/// y Kroko sugiere qué hacer — «Copiaste un enlace: Enviar al celular · Resumir». El historial vive SOLO en memoria
/// (últimos 20), nunca se guarda ni sale de la PC salvo que pulses un botón. Nunca guarda lo que parece contraseña.
/// </summary>
public sealed class ClipboardWatcher(IOptions<KrokoOptions> options, ILogger<ClipboardWatcher> log)
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private DateTime _ignoreUntil;

    public ObservableCollection<ClipItem> History { get; } = [];

    /// <summary>Copiaste algo nuevo (la isla lo sugiere unos segundos).</summary>
    public event Action<ClipItem>? Copied;

    public bool Enabled => options.Value.ClipboardSuggestions;

    public void Attach(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !AddClipboardFormatListener(hwnd)) return;
        HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
        log.LogInformation("Portapapeles: escuchando cambios");
    }

    /// <summary>Kroko mismo va a copiar algo (Pegar desde el historial): no lo sugiere de vuelta.</summary>
    public void IgnoreNext() => _ignoreUntil = DateTime.Now.AddMilliseconds(800);

    private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && Enabled && DateTime.Now > _ignoreUntil)
            Application.Current?.Dispatcher.BeginInvoke(Read);
        return 0;
    }

    private void Read()
    {
        try
        {
            ClipItem? item = null;
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
            {
                img.Freeze();
                item = new ClipItem { Kind = "imagen", Image = img };
            }
            else if (Clipboard.ContainsText() && Clipboard.GetText().Trim() is { Length: > 0 } text && !LooksSecret(text))
            {
                item = Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && !text.Contains(' ')
                    ? new ClipItem { Kind = "enlace", Text = text }
                    : new ClipItem { Kind = "texto", Text = text };
            }
            if (item is null) return;
            if (History.FirstOrDefault() is { } last && last.Kind == item.Kind && last.Text == item.Text && item.Kind != "imagen") return;
            History.Insert(0, item);
            while (History.Count > 20) History.RemoveAt(History.Count - 1);
            Copied?.Invoke(item);
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException)
        {
            log.LogDebug("No pude leer el portapapeles: {Error}", ex.Message);
        }
    }

    /// <summary>Contraseñas, claves y tarjetas no entran al historial (seguridad, sección 8).</summary>
    internal static bool LooksSecret(string text)
    {
        var t = text.Trim();
        if (t.Length is >= 8 and <= 64 && !t.Contains(' ') && t.Any(char.IsDigit) && t.Any(char.IsLetter) && t.Any(c => !char.IsLetterOrDigit(c))
            && !t.Contains('.') && !t.Contains('/')) return true; // parece una contraseña
        var digits = new string([.. t.Where(char.IsDigit)]);
        if (digits.Length is >= 13 and <= 19 && t.Length - digits.Length <= 4) return true; // parece una tarjeta
        return t.StartsWith("sk-", StringComparison.Ordinal) || t.StartsWith("AIza", StringComparison.Ordinal) || t.StartsWith("ghp_", StringComparison.Ordinal);
    }

    /// <summary>Guarda una imagen copiada en un PNG temporal (para «Extraer texto» con ver_imagen).</summary>
    public static string SaveImage(BitmapSource image)
    {
        var path = Path.Combine(Path.GetTempPath(), $"kroko-portapapeles-{DateTime.Now:HHmmss}.png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(nint hwnd);
}
