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
