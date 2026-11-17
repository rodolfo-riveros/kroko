using Kroko.App.Native;

namespace Kroko.App.Services;

/// <summary>
/// Dónde está el cuerpo de Kroko en pantalla (píxeles físicos). La burbuja lo actualiza
/// y el panel de respuesta lo usa para colocarse encima.
/// </summary>
public sealed class BubbleGeometry
{
    public NativeMethods.RECT Body { get; private set; }
    public NativeMethods.RECT WorkArea { get; private set; }

    /// <summary>Escala del monitor (1.0 = 100 %).</summary>
    public double MonitorScale { get; private set; } = 1;

    /// <summary>Unidades de cara → píxeles físicos.</summary>
    public double UnitPx { get; private set; } = 1;

    public bool IsKnown { get; private set; }

    /// <summary>Kroko vive en la isla de arriba (17/11/2026): los paneles se abren debajo y centrados.</summary>
    public bool IsIsland { get; private set; }

    public event Action? Changed;

    public void Update(NativeMethods.RECT body, NativeMethods.RECT workArea, double monitorScale, double unitPx, bool island = false)
    {
        IsIsland = island;
        Body = body;
        WorkArea = workArea;
        MonitorScale = monitorScale;
        UnitPx = unitPx;
        IsKnown = true;
        Changed?.Invoke();
    }
}
