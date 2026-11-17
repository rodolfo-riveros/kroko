using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Kroko.App.Native;
using Kroko.Shared.Tools;

namespace Kroko.App.Tools;

/// <summary>
/// Manda un WhatsApp desde la PC (pedido por Pol, 17/11/2026). Llega aquí solo después de que el dueño dijo «sí» en la
/// tarjeta (riesgo Dangerous; el ejecutor lo vuelve a comprobar). Abre WhatsApp de escritorio con el chat y el mensaje
/// ya escritos y pulsa Enter cuando la ventana de WhatsApp está al frente. Sin WhatsApp de escritorio, abre WhatsApp
/// Web con el mensaje listo y le dice al dueño que solo falta pulsar Enter (no lo da por enviado si no lo envió).
/// </summary>
public sealed partial class WhatsappEnviarTool : ILocalTool
{
    public string Name => "whatsapp_enviar";

    public async Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var number = new string(args.String("numero").Where(char.IsDigit).ToArray());
        var message = args.String("mensaje").Trim();
        if (number.Length is < 8 or > 15)
            return ToolOutcome.Fail("Ese número no parece válido: necesito el número con código de país (Perú: 51 + 9 dígitos).");
        if (message.Length == 0) return ToolOutcome.Fail("Falta el mensaje.");
        var text = Uri.EscapeDataString(message);

        // 1) WhatsApp de escritorio (enlace whatsapp://): chat abierto con el mensaje escrito
        try
        {
            Process.Start(new ProcessStartInfo($"whatsapp://send?phone={number}&text={text}") { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            // 2) Sin WhatsApp de escritorio: WhatsApp Web con el mensaje listo (lo envía el dueño con Enter)
            Process.Start(new ProcessStartInfo($"https://web.whatsapp.com/send?phone={number}&text={text}") { UseShellExecute = true })?.Dispose();
            return ToolOutcome.Success(new
            {
                enviado = false,
                aviso = "No encontré WhatsApp de escritorio: abrí WhatsApp Web con el mensaje escrito. Solo falta pulsar Enter.",
            });
        }

        // Espera a que la ventana de WhatsApp quede al frente y a que cargue el chat, y envía con Enter
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250, ct);
            if (!WhatsAppInFront()) continue;
            await Task.Delay(1500, ct); // el chat y el texto terminan de cargarse
            if (!WhatsAppInFront()) break;
            Keyboard.Type(VirtualKeyShort.ENTER);
            return ToolOutcome.Success(new { enviado = true, a = number });
        }
        return ToolOutcome.Success(new
        {
            enviado = false,
            aviso = "Abrí WhatsApp con el mensaje escrito, pero no quedó al frente para enviarlo. Solo falta pulsar Enter.",
        });
    }

    private static bool WhatsAppInFront()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0 || GetWindowThreadProcessId(hwnd, out var pid) == 0) return false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
