using System.Diagnostics;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Kroko.App.Tools;

/// <summary>
/// Música: teclas multimedia de Windows (sirven para Spotify, YouTube en el navegador, Windows Media…)
/// y "poner" abre la búsqueda en Spotify o YouTube Music. Nadie puede pulsar "reproducir" por ti sin la
/// API de pago de Spotify, así que Kroko lo dice con sinceridad.
/// </summary>
public sealed class MusicaTool(Services.MediaSessionService media) : ILocalTool
{
    public string Name => "musica";

    public async Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var action = args.String("accion");
        // Primero el control multimedia de Windows (17/11/2026); si no hay sesión, las teclas multimedia de siempre
        switch (action)
        {
            case "pausar_reanudar": if (!await media.PlayPauseAsync()) MusicKeys.PlayPause(); break;
            case "siguiente": if (!await media.NextAsync()) MusicKeys.Next(); break;
            case "anterior": if (!await media.PreviousAsync()) MusicKeys.Previous(); break;
            case "detener": if (!await media.StopAsync()) Keyboard.Type(VirtualKeyShort.MEDIA_STOP); break;
            case "que_suena":
                await media.PollAsync();
                var n = media.Now;
                return n.HasMedia
                    ? ToolOutcome.Success(new { titulo = n.Title, artista = n.Artist, app = n.App, sonando = n.Playing })
                    : ToolOutcome.Success(new { titulo = "", nota = "No suena nada (o la app no lo comparte con Windows)." });
            case "poner": return Play(args);
            default: throw new ToolException("Acción de música no válida.");
        }
        return ToolOutcome.Success(new { hecho = action });
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

/// <summary>Teclas multimedia de Windows (respaldo cuando la app no comparte su sesión con el sistema).</summary>
public static class MusicKeys
{
    public static void PlayPause() => Keyboard.Type(VirtualKeyShort.MEDIA_PLAY_PAUSE);
    public static void Next() => Keyboard.Type(VirtualKeyShort.MEDIA_NEXT_TRACK);
    public static void Previous() => Keyboard.Type(VirtualKeyShort.MEDIA_PREV_TRACK);
}
