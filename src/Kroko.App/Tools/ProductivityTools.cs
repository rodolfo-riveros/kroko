using System.Globalization;
using System.Text.Json;
using System.Windows;
using Kroko.App.Services;
using Kroko.Shared.Tools;

namespace Kroko.App.Tools;

// Las herramientas tocan la interfaz (temporizadores en el panel), así que se ejecutan en el hilo de la UI.

public sealed class TemporizadorTool(TimerService timers) : ILocalTool
{
    public string Name => "temporizador";

    public async Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        if (!args.Raw.TryGetProperty("minutos", out var m) || m.ValueKind != JsonValueKind.Number)
            return ToolOutcome.Fail("Dime cuántos minutos.");
        var minutes = Math.Clamp(m.GetDouble(), 0.1, 600);
        var label = args.OptionalString("etiqueta") ?? "Temporizador";
        await Application.Current.Dispatcher.InvokeAsync(() => timers.StartTimer(TimeSpan.FromMinutes(minutes), label));
        return ToolOutcome.Success(new
        {
            temporizador = label,
            minutos = minutes,
            terminaA = DateTime.Now.AddMinutes(minutes).ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        });
    }
}

public sealed class TemporizadorCancelarTool(TimerService timers) : ILocalTool
{
    public string Name => "temporizador_cancelar";

    public async Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var wasActive = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var active = timers.IsActive;
            timers.Cancel();
            return active;
        });
        return wasActive ? ToolOutcome.Success(new { cancelado = true }) : ToolOutcome.Fail("No había ningún temporizador en marcha.");
    }
}

public sealed class PomodoroTool(TimerService timers) : ILocalTool
{
    public string Name => "pomodoro";

    public async Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var action = args.String("accion").ToLowerInvariant();
        if (action == "detener")
        {
            await Application.Current.Dispatcher.InvokeAsync(timers.Cancel);
            return ToolOutcome.Success(new { pomodoro = "detenido" });
        }

        var work = ToolCatalog.Int(args.Raw, "minutos_trabajo") is > 0 and var w ? w : 25;
        var rest = ToolCatalog.Int(args.Raw, "minutos_descanso") is > 0 and var r ? r : 5;
        await Application.Current.Dispatcher.InvokeAsync(() => timers.StartPomodoro(work, rest));
        return ToolOutcome.Success(new { pomodoro = "iniciado", trabajo = work, descanso = rest });
    }
}

public sealed class ModoConcentracionTool(FocusService focus) : ILocalTool
{
    public string Name => "modo_concentracion";

    public async Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var on = args.Bool("activar");
        var minutes = ToolCatalog.Int(args.Raw, "minutos");
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (on) focus.Activate(minutes > 0 ? minutes : null);
            else focus.Deactivate();
        });
        return ToolOutcome.Success(new
        {
            modoConcentracion = on,
            notificacionesDeWindows = on ? "silenciadas" : "restauradas",
            hasta = on && minutes > 0 ? DateTime.Now.AddMinutes(minutes).ToString("HH:mm", CultureInfo.InvariantCulture) : null,
        });
    }
}
