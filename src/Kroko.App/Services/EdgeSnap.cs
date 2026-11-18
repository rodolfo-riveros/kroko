using Kroko.App.Native;

namespace Kroko.App.Services;

/// <summary>Imán a los bordes de Kroko libre (17/11/2026): cerca de un borde (12 px), se pega a él.</summary>
public static class EdgeSnap
{
    public const double DistanceDip = 12;

    /// <summary>Cuánto mover el cuerpo para pegarlo al borde más cercano (0, 0 si no está cerca de ninguno).</summary>
    public static (int Dx, int Dy) Offset(NativeMethods.RECT body, NativeMethods.RECT work, int distance)
    {
        int dx = 0, dy = 0;
        if (Math.Abs(body.Left - work.Left) <= distance) dx = work.Left - body.Left;
        else if (Math.Abs(work.Right - body.Right) <= distance) dx = work.Right - body.Right;
        if (Math.Abs(body.Top - work.Top) <= distance) dy = work.Top - body.Top;
        else if (Math.Abs(work.Bottom - body.Bottom) <= distance) dy = work.Bottom - body.Bottom;
        return (dx, dy);
    }
}
