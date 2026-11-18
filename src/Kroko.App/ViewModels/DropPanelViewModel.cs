using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kroko.App.Services;
using Kroko.App.Tools;
using Kroko.App.Tools.FileActions;

namespace Kroko.App.ViewModels;

/// <summary>
/// El panel que sale al soltar archivos sobre Kroko (17/11/2026, especificación «soltar archivos»): Convertir,
/// Analizar, Ordenar y Enviar. Soltar y pulsar una acción ya es la confirmación del dueño; todo queda en la auditoría
/// (nombre, acción y destino, nunca el contenido) y nada se borra.
/// </summary>
public sealed partial class DropPanelViewModel(PanelViewModel chat, AuditLog audit, Services.LanShare.PhoneShare phone) : ObservableObject
{
    public List<string> Files { get; private set; } = [];

    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Detail { get; set; } = "";
    /// <summary>inicio, convertir, analizar, ordenar, enviar o celular.</summary>
    [ObservableProperty] public partial string Mode { get; set; } = "inicio";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool Busy { get; set; }
    [ObservableProperty] public partial string Question { get; set; } = "";
    [ObservableProperty] public partial bool ByDate { get; set; }
    [ObservableProperty] public partial string OrganizePreview { get; set; } = "";
    [ObservableProperty] public partial bool CanUndo { get; set; }
    [ObservableProperty] public partial System.Windows.Media.ImageSource? PhoneQr { get; set; }
    [ObservableProperty] public partial string PhoneLink { get; set; } = "";

    public bool AllImages => Files.Count > 0 && Files.All(FileConvert.IsImage);
    public bool AllAudio => Files.Count > 0 && Files.All(FileConvert.IsAudio);
    public bool HasFfmpeg => FileConvert.FindFfmpeg() is not null;
    public bool CanConvert => AllImages || AllAudio;

    /// <summary>Se cerró o terminó: la ventana se va.</summary>
    public event Action? CloseRequested;

    public void Load(IEnumerable<string> paths)
    {
        Files = [.. paths.Where(File.Exists)];
        Mode = "inicio";
        Status = Question = OrganizePreview = PhoneLink = "";
        PhoneQr = null;
        CanUndo = false;
        if (Files.Count == 1)
        {
            var f = new FileInfo(Files[0]);
            Title = f.Name;
            Detail = $"{Kind(f.Extension)} · {Size(f.Length)}";
        }
        else
        {
            Title = $"{Files.Count} archivos";
            Detail = Size(Files.Sum(p => new FileInfo(p).Length));
        }
        OnPropertyChanged(nameof(AllImages));
        OnPropertyChanged(nameof(AllAudio));
        OnPropertyChanged(nameof(CanConvert));
        OnPropertyChanged(nameof(HasFfmpeg));
    }

    [RelayCommand] private void Show(string mode)
    {
        Status = "";
        Mode = mode;
        if (mode == "ordenar") OnByDateChanged(ByDate);
    }

    [RelayCommand] private void Back() => Show("inicio");

    [RelayCommand] private void Close() => CloseRequested?.Invoke();

    // ---------------------------------------------------------------- Convertir

    [RelayCommand]
    private async Task ConvertImagesAsync(string format) => await Run("convertir", format, async () =>
    {
        var made = await Task.Run(() => Files.Select(f => FileConvert.ImageTo(f, format)).ToList());
        return made.Count == 1 ? $"Listo: {Path.GetFileName(made[0])}" : $"Listo: {made.Count} imágenes en {format.ToUpperInvariant()}";
    });

    [RelayCommand]
    private async Task ImagesToPdfAsync() => await Run("convertir", "pdf", async () =>
        $"Listo: {Path.GetFileName(await Task.Run(() => FileConvert.ImagesToPdf(Files)))}");

    [RelayCommand]
    private async Task ConvertAudioAsync(string format) => await Run("convertir", format, async () =>
    {
        if (!HasFfmpeg) return "Para convertir audio necesito ffmpeg. Descárgalo de ffmpeg.org e instálalo; yo no descargo nada solo.";
        var made = new List<string>();
        foreach (var f in Files) made.Add(await FileConvert.AudioToAsync(f, format, CancellationToken.None));
        return made.Count == 1 ? $"Listo: {Path.GetFileName(made[0])}" : $"Listo: {made.Count} audios en {format.ToUpperInvariant()}";
    });

    [RelayCommand]
    private static void OpenFfmpegPage() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://ffmpeg.org/download.html") { UseShellExecute = true })?.Dispose();

    // ---------------------------------------------------------------- Analizar (el contenido va a Gemini; se avisa en el mosaico)

    [RelayCommand] private void Summarize() => Analyze("Resúmeme");
    [RelayCommand] private void KeyPoints() => Analyze("Dame los puntos clave de");

    [RelayCommand]
    private void Ask()
    {
        if (Question.Trim().Length == 0) return;
        Analyze($"Sobre {(Files.Count == 1 ? "este archivo" : "estos archivos")}, respóndeme: «{Question.Trim()}». Archivo");
    }

    private void Analyze(string what)
    {
        try
        {
            foreach (var f in Files) FileSendGuard.EnsureSendable(f);
        }
        catch (ToolException ex)
        {
            Status = ex.Message;
            return;
        }
        var list = string.Join(", ", Files.Select(f => $"«{f}»"));
        var how = AllImages ? "míralas con ver_imagen" : "léelos con leer_documento";
        Record("analizar", "Gemini");
        chat.InputText = $"{what} {(Files.Count == 1 ? "el archivo" : "los archivos")} {list} ({how}).";
        if (chat.SendCommand.CanExecute(null)) chat.SendCommand.Execute(null);
        CloseRequested?.Invoke(); // la respuesta sale en el panel de Kroko
    }

    // ---------------------------------------------------------------- Ordenar (vista previa, mover y deshacer)

    private List<FileOrganize.Move> _plan = [];

    partial void OnByDateChanged(bool value)
    {
        if (Files.Count == 0) return;
        _plan = FileOrganize.Plan(Files, value);
        var folders = _plan.Select(m => Path.GetRelativePath(Path.GetDirectoryName(m.From)!, Path.GetDirectoryName(m.To)!)).Distinct().ToList();
        OrganizePreview = folders.Count == 1 ? $"→ {folders[0]}" : "→ " + string.Join(", ", folders.Take(4)) + (folders.Count > 4 ? "…" : "");
    }

    [RelayCommand]
    private async Task MoveAsync() => await Run("ordenar", OrganizePreview, async () =>
    {
        var n = await Task.Run(() => FileOrganize.Apply(_plan));
        CanUndo = true;
        return n == 1 ? "Movido. ¿No te gustó? Pulsa Deshacer." : $"Moví {n} archivos. ¿No te gustó? Pulsa Deshacer.";
    });

    [RelayCommand]
    private async Task UndoAsync() => await Run("deshacer", "", async () =>
    {
        var r = await Task.Run(OrderUndo.UndoLast);
        CanUndo = false;
        return r is null ? "No había nada que deshacer." : $"Listo: devolví {r.Devueltos} archivo(s) a su sitio.";
    });

    // ---------------------------------------------------------------- Enviar

    [RelayCommand]
    private void SendTelegram()
    {
        Record("enviar", "Telegram");
        chat.InputText = $"Mándame por Telegram {(Files.Count == 1 ? "el archivo" : "los archivos")} {string.Join(", ", Files.Select(f => $"«{f}»"))}.";
        if (chat.SendCommand.CanExecute(null)) chat.SendCommand.Execute(null);
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private async Task SendPhoneAsync() => await Run("enviar", "celular", async () =>
    {
        foreach (var f in Files) FileSendGuard.EnsureSendable(f);
        var offer = await phone.OfferAsync(Files);
        PhoneQr = offer.Qr;
        PhoneLink = offer.Link;
        Mode = "celular";
        return "Escanea el código con la cámara de tu celular (misma red Wi-Fi). El enlace sirve una vez y vence en 5 minutos.";
    });

    // ---------------------------------------------------------------- utilidades

    private async Task Run(string action, string target, Func<Task<string>> work)
    {
        if (Busy) return;
        Busy = true;
        Status = "Un momento…";
        var ok = true;
        try
        {
            Status = await work();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                                       or NotSupportedException or ToolException or System.Runtime.InteropServices.COMException)
        {
            ok = false;
            Status = "No pude: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
        Record(action, target, ok, Status);
    }

    private void Record(string action, string target, bool ok = true, string result = "") => audit.Record(new AuditEntry(
        DateTime.Now, "soltar_" + action, "Write", $"{string.Join(", ", Files.Select(Path.GetFileName))} → {target}",
        "soltado por el dueño", ok, result, 0));

    private static string Kind(string ext) => ext.ToLowerInvariant() switch
    {
        ".pdf" => "PDF",
        ".docx" or ".doc" => "Word",
        ".xlsx" or ".xls" or ".csv" => "Hoja de cálculo",
        ".pptx" or ".ppt" => "Presentación",
        ".txt" or ".md" => "Texto",
        ".zip" or ".rar" or ".7z" => "Comprimido",
        var e when FileConvert.ImageExtensions.Contains(e) => "Imagen",
        var e when FileConvert.AudioExtensions.Contains(e) => "Audio",
        ".mp4" or ".mkv" or ".mov" or ".avi" => "Video",
        "" => "Archivo",
        var e => e.TrimStart('.').ToUpperInvariant(),
    };

    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}
