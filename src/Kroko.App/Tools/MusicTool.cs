using System.Diagnostics;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Kroko.App.Tools;

/// <summary>
/// Música: teclas multimedia de Windows (sirven para Spotify, YouTube en el navegador, Windows Media…)
/// y "poner" abre la búsqueda en Spotify o YouTube Music. Nadie puede pulsar "reproducir" por ti sin la
/// API de pago de Spotify, así que Kroko lo dice con sinceridad.
/// </summary>
public sealed class MusicaTool : ILocalTool
{
    public string Name => "musica";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var action = args.String("accion");
        switch (action)
        {
            case "pausar_reanudar": Keyboard.Type(VirtualKeyShort.MEDIA_PLAY_PAUSE); break;
            case "siguiente": Keyboard.Type(VirtualKeyShort.MEDIA_NEXT_TRACK); break;
            case "anterior": Keyboard.Type(VirtualKeyShort.MEDIA_PREV_TRACK); break;
            case "detener": Keyboard.Type(VirtualKeyShort.MEDIA_STOP); break;
            case "poner": return Task.FromResult(Play(args));
            default: throw new ToolException("Acción de música no válida.");
        }
        return Task.FromResult(ToolOutcome.Success(new { hecho = action }));
    }

    private static ToolOutcome Play(ToolArgs args)
    {
        var query = args.String("consulta").Trim();
        if (query.Length == 0) throw new ToolException("Dime qué quieres escuchar.");
        var service = args.String("servicio");

        if (service != "youtube_music" && TryOpen("spotify:search:" + Uri.EscapeDataString(query)))
            return ToolOutcome.Success(new { abierto = "Spotify", busqueda = query, nota = "Se abrió la búsqueda en Spotify; elige o pulsa reproducir." });

        TryOpen("https://music.youtube.com/search?q=" + Uri.EscapeDataString(query));
        return ToolOutcome.Success(new { abierto = "YouTube Music", busqueda = query, nota = "Se abrió la búsqueda en YouTube Music; pulsa reproducir." });
    }

    private static bool TryOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // no hay programa para ese enlace (Spotify no instalado)
        }
    }
}
