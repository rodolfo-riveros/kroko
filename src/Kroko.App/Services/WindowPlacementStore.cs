using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Kroko.App.Services;

/// <summary>
/// Recuerda dónde dejaste a Kroko en cada monitor (%LOCALAPPDATA%\Kroko\placement.json).
/// La posición se guarda como distancia (en píxeles lógicos) desde la esquina inferior derecha
/// del área de trabajo, así sobrevive a cambios de resolución y de escala.
/// </summary>
public sealed class WindowPlacementStore(ILogger<WindowPlacementStore> log)
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", "placement.json");

    public sealed class MonitorPlacement
    {
        public double FromRight { get; set; }
        public double FromBottom { get; set; }
    }

    public sealed class PlacementData
    {
        public string? LastMonitor { get; set; }
        public int LastCenterX { get; set; }
        public int LastCenterY { get; set; }
        public Dictionary<string, MonitorPlacement> Monitors { get; set; } = [];
    }

    public PlacementData Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<PlacementData>(File.ReadAllText(FilePath)) ?? new PlacementData();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "No se pudo leer la posición guardada; se usa la esquina por defecto");
        }
        return new PlacementData();
    }

    public void Save(PlacementData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "No se pudo guardar la posición de Kroko");
        }
    }
}
