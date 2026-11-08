using System.Text.Json.Nodes;
using System.Windows;
using Kroko.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kroko.App.Tools;

/// <summary>
/// «Cállate, mándamelo por chat» / «Háblame otra vez»: cambia entre responder con voz o solo por chat.
/// Se aplica al instante y se guarda en settings.json (igual que el interruptor de Ajustes → Voz).
/// </summary>
public sealed class ModoRespuestaTool(IOptions<KrokoOptions> options, IServiceProvider services) : ILocalTool
{
    public string Name => "modo_respuesta";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var voz = args.Bool("voz");
        Apply(options.Value.Voice, voz);

        // Si estaba en una conversación de voz en tiempo real, se termina: a partir de ahora solo escribe
        if (!voz)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var live = services.GetService<LiveConversation>();
                if (live is { IsActive: true }) live.Stop("Listo, me callo. Desde ahora te respondo por chat.");
            });
        }

        return Task.FromResult(ToolOutcome.Success(new
        {
            modo = voz ? "voz" : "solo_chat",
            nota = voz
                ? "Desde ahora respondes hablando y escribiendo."
                : "Desde ahora solo escribes, sin audio. Para volver a hablar te dirán «háblame».",
        }));
    }

    /// <summary>Cambia el modo en memoria y lo guarda (también lo usa la ventana de Ajustes).</summary>
    public static void Apply(VoiceSettings voice, bool repliesWithVoice)
    {
        voice.RepliesWithVoice = repliesWithVoice;
        var k = UserSettings.LoadKrokoSection();
        var v = k["Voice"] as JsonObject ?? new JsonObject();
        v["RepliesWithVoice"] = repliesWithVoice;
        k["Voice"] = v.DeepClone();
        UserSettings.SaveKrokoSection(k);
    }
}
