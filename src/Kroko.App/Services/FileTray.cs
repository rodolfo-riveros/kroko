using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Kroko.App.Services;

/// <summary>Un archivo estacionado en la bandeja.</summary>
public sealed partial class TrayFile(string path, DateTime added, FileTray owner)
{
    public string Path { get; } = path;
    public DateTime Added { get; } = added;
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>PDF, DOCX, PNG… (la etiqueta de color del icono).</summary>
    public string Kind
    {
        get
        {
            var e = System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
            return e.Length is > 0 and <= 4 ? e : "ARCH";
        }
    }

    /// <summary>Color de la etiqueta: rojo PDF, azul Word, lima imágenes, ámbar audio, gris lo demás (diseño Kroko 2).</summary>
    public string KindColor => Kind switch
    {
        "PDF" => "#FF5C6C",
        "DOC" or "DOCX" or "TXT" or "MD" or "RTF" => "#5AB4FF",
        "PNG" or "JPG" or "JPEG" or "GIF" or "BMP" or "WEBP" or "TIF" or "TIFF" => "#B6F23A",
        "MP3" or "WAV" or "M4A" or "OGG" or "FLAC" => "#FFB547",
        "XLS" or "XLSX" or "CSV" => "#3DDC97",
        "PPT" or "PPTX" => "#FF8A4C",
        _ => "#A1A1AA",
    };

    [RelayCommand] private void Open() => ActivityFeed.OpenShell(Path);
    [RelayCommand] private void ShowInFolder() => ActivityFeed.OpenShell("explorer.exe", $"/select,\"{Path}\"");
    [RelayCommand] private void Remove() => owner.Remove(this);
}

/// <summary>
/// La bandeja (diseño Kroko 2, 18/11/2026): archivos «estacionados» en la isla mientras cambias de ventana; se sacan
/// arrastrándolos a cualquier app. Guarda solo la RUTA (nunca copia ni mueve el original) y se vacía sola a las 24 h
/// sin borrar nada. Se recuerda entre reinicios en %LOCALAPPDATA%\Kroko\bandeja.json.
/// </summary>
public sealed class FileTray
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private readonly string _file;
    private readonly ILogger<FileTray> _log;

    public FileTray(ILogger<FileTray> log) : this(log, Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", "bandeja.json")) { }

    internal FileTray(ILogger<FileTray> log, string file)
    {
        _log = log;
        _file = file;
        Load();
    }

    public ObservableCollection<TrayFile> Items { get; } = [];
    public event Action? Changed;

    public string CountText => Items.Count == 1 ? "1 archivo" : $"{Items.Count} archivos";

    /// <summary>Estaciona archivos (los repetidos no se duplican). Devuelve cuántos entraron.</summary>
    public int Add(IEnumerable<string> paths)
    {
        Prune();
        var added = 0;
        foreach (var p in paths)
        {
            if (!File.Exists(p) && !Directory.Exists(p)) continue;
            var full = Path.GetFullPath(p);
            if (Items.Any(i => i.Path.Equals(full, StringComparison.OrdinalIgnoreCase))) continue;
            Items.Insert(0, new TrayFile(full, DateTime.Now, this));
            added++;
        }
        if (added > 0) Save();
        return added;
    }

    public void Remove(TrayFile file)
    {
        if (Items.Remove(file)) Save();
    }

    public void Clear()
    {
        Items.Clear();
        Save();
    }

    /// <summary>Quita lo vencido (24 h) y lo que ya no existe. Nunca toca los archivos.</summary>
    public void Prune(DateTime? now = null)
    {
        var limit = (now ?? DateTime.Now) - Lifetime;
        var gone = Items.Where(i => i.Added < limit || (!File.Exists(i.Path) && !Directory.Exists(i.Path))).ToList();
        foreach (var g in gone) Items.Remove(g);
        if (gone.Count > 0) Save();
    }

    private sealed record Saved(string Path, DateTime Added);

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            foreach (var s in JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(_file)) ?? [])
                Items.Add(new TrayFile(s.Path, s.Added, this));
            Prune();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning("No pude leer la bandeja: {Error}", ex.Message);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(Items.Select(i => new Saved(i.Path, i.Added)).ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("No pude guardar la bandeja: {Error}", ex.Message);
        }
        Changed?.Invoke();
    }

    /// <summary>Para arrastrar un archivo de la bandeja a cualquier app (Explorador, WhatsApp, Gmail…).</summary>
    public static void DragOut(DependencyObject source, TrayFile file) =>
        DragDrop.DoDragDrop(source, new DataObject(DataFormats.FileDrop, new[] { file.Path }), DragDropEffects.Copy);
}
