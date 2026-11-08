using System.Globalization;
using System.Text.Json;

namespace Kroko.Shared.Protocol;

/// <summary>
/// Convierte el resultado de algunas herramientas en una tarjeta visual para el panel.
/// Lo usan los dos lados: el Brain (tareas, hábitos) y la App (estado del equipo).
/// </summary>
public static class Cards
{
    private const int MaxItems = 8;

    /// <summary>La tarjeta para esa herramienta, o null si no tiene (o el resultado no sirve).</summary>
    public static CardMessage? FromToolResult(string tool, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return tool switch
            {
                "tareas_listar" => Tasks(data),
                "habitos_listar" => Habits(data),
                "estado_equipo" => SystemStatus(data),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null; // un resultado con otra forma nunca rompe el panel: simplemente no hay tarjeta
        }
    }

    private static CardMessage? Tasks(JsonElement data)
    {
        if (!data.TryGetProperty("tareas", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var items = list.EnumerateArray()
            .OrderBy(t => Bool(t, "hecha"))
            .Take(MaxItems)
            .Select(t => new CardItem(Str(t, "tarea"), DueText(Str(t, "fecha")), Bool(t, "hecha")))
            .ToList();
        var total = list.GetArrayLength();
        return items.Count == 0 ? null : new CardMessage
        {
            Kind = "lista",
            Title = total > items.Count ? $"Tareas ({items.Count} de {total})" : "Tareas",
            Items = items,
        };
    }

    private static CardMessage? Habits(JsonElement data)
    {
        if (!data.TryGetProperty("habitos", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var items = list.EnumerateArray().Take(MaxItems).Select(h =>
        {
            var streak = h.TryGetProperty("racha", out var r) && r.TryGetInt32(out var n) ? n : 0;
            return new CardItem(Str(h, "habito"), streak > 0 ? $"racha de {streak} día{(streak == 1 ? "" : "s")}" : null, Bool(h, "hechoHoy"));
        }).ToList();
        return items.Count == 0 ? null : new CardMessage { Kind = "lista", Title = "Hábitos de hoy", Items = items };
    }

    private static CardMessage SystemStatus(JsonElement data)
    {
        var items = new List<CardItem>();
        if (data.TryGetProperty("bateria", out var b) && b.ValueKind == JsonValueKind.Object && b.TryGetProperty("porcentaje", out var p))
        {
            var pct = p.GetInt32();
            var detail = $"{pct} %" + (Bool(b, "cargando") ? " · cargando" : "");
            items.Add(new CardItem("Batería", detail, Value: pct / 100.0));
        }
        if (data.TryGetProperty("cpuPct", out var cpu))
            items.Add(new CardItem("Procesador", $"{cpu.GetInt32()} %", Value: cpu.GetInt32() / 100.0));
        if (data.TryGetProperty("ramPct", out var ram))
        {
            var free = data.TryGetProperty("ramLibreGB", out var f) ? $" · {f.GetDouble().ToString("0.#", CultureInfo.InvariantCulture)} GB libres" : "";
            items.Add(new CardItem("Memoria", $"{ram.GetInt32()} %{free}", Value: ram.GetInt32() / 100.0));
        }
        if (data.TryGetProperty("discos", out var disks) && disks.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in disks.EnumerateArray().Take(3))
            {
                var used = d.GetProperty("usadoPct").GetInt32();
                var free = d.GetProperty("libreGB").GetDouble().ToString("0.#", CultureInfo.InvariantCulture);
                items.Add(new CardItem($"Disco {Str(d, "unidad")}", $"{free} GB libres", Value: used / 100.0));
            }
        }
        if (data.TryGetProperty("internet", out var net) && net.ValueKind is JsonValueKind.True or JsonValueKind.False)
            items.Add(new CardItem("Internet", net.GetBoolean() ? "conectado" : "sin conexión", Done: net.GetBoolean()));
        return new CardMessage { Kind = "equipo", Title = "Estado del equipo", Items = items };
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? DueText(string date)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return null;
        return "para el " + d.ToString("d 'de' MMMM", CultureInfo.GetCultureInfo("es-PE"));
    }
}
