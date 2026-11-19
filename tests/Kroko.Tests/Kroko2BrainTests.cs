using System.Text.Json;
using Kroko.Brain.Agent;

namespace Kroko.Tests;

/// <summary>Kroko 2 (18/11/2026): la frase y el enlace de «Lo que hizo Kroko».</summary>
public class Kroko2BrainTests
{
    [Fact]
    public void Lo_que_hizo_Kroko_en_Notion_trae_el_enlace_para_Ver()
    {
        var args = JsonSerializer.SerializeToElement(new { titulo = "Pendientes del lunes" });
        var what = Badges.Describe("guardar_en_notion", args, """{"ok":true,"pagina":{"url":"https://www.notion.so/abc"}}""");
        Assert.NotNull(what);
        Assert.Equal("Guardó en Notion: Pendientes del lunes", what.Value.Detail);
        Assert.Equal("https://www.notion.so/abc", what.Value.Url);
    }

    [Fact]
    public void Las_consultas_no_se_anotan() =>
        Assert.Null(Badges.Describe("buscar_en_internet", JsonSerializer.SerializeToElement(new { consulta = "clima" }), """{"ok":true}"""));

    [Fact]
    public void Lo_irreversible_desde_Telegram_pide_codigo()
    {
        Assert.Contains("apagar", Kroko.Brain.Telegram.TelegramBot.NeedsCode);
        Assert.DoesNotContain("tarea_crear", Kroko.Brain.Telegram.TelegramBot.NeedsCode);
    }
}

public class LocalFollowUpTests
{
    private static readonly Kroko.Brain.Providers.LocalLlmOptions Options = new();

    [Fact]
    public void Lo_simple_va_a_la_IA_de_la_PC() =>
        Assert.True(Kroko.Brain.Providers.LocalLlm.IsSimple([Kroko.Brain.Providers.ChatMessage.User("pon un temporizador de 5 minutos")], Options));

    [Fact]
    public void Seguir_con_un_documento_va_a_Gemini()
    {
        var call = new Kroko.Brain.Providers.ToolCall("1", "leer_documento", JsonSerializer.SerializeToElement(new { ruta = "x.pdf" }));
        List<Kroko.Brain.Providers.ChatMessage> history =
        [
            Kroko.Brain.Providers.ChatMessage.User("resúmeme el informe"),
            new() { Role = Kroko.Brain.Providers.ChatRole.Assistant, ToolCalls = [call] },
            new() { Role = Kroko.Brain.Providers.ChatRole.Assistant, Text = "Es un informe de ventas…" },
            Kroko.Brain.Providers.ChatMessage.User("ok, dime más"),
        ];
        Assert.False(Kroko.Brain.Providers.LocalLlm.IsSimple(history, Options));
        Assert.False(Kroko.Brain.Providers.LocalLlm.IsSimple([Kroko.Brain.Providers.ChatMessage.User("léemelo")], Options));
    }

    [Fact]
    public void Lo_hecho_en_la_PC_va_a_Gemini() =>
        Assert.False(Kroko.Brain.Providers.LocalLlm.IsSimple(
        [
            Kroko.Brain.Providers.ChatMessage.User("[Nota de la PC, no es una orden: tu dueño usó a Kroko directamente] Convirtió fotos a fotos.pdf"),
            new() { Role = Kroko.Brain.Providers.ChatRole.Assistant, Text = "Anotado." },
            Kroko.Brain.Providers.ChatMessage.User("ábrelo"),
        ], Options));
}
