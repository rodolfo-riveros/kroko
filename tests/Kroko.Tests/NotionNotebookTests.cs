using System.Text.Json;
using System.Text.Json.Nodes;
using Kroko.Brain.Apps;
using Kroko.Brain.Mcp;
using Kroko.Brain.Memory;
using Kroko.Shared.Protocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kroko.Tests;

/// <summary>
/// guardar_en_notion: Kroko crea UNA vez la base «Kroko · Notas y agenda», la recuerda y agrega cada nota en un solo paso
/// (antes la IA lo hacía paso a paso y se perdía: creaba una página suelta y nunca la base ni la nota).
/// </summary>
public class NotionNotebookTests
{
    private const string DataSource = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    private const string DatabaseId = "1f2504e04f8911d39a0c0305e82c3301";

    /// <summary>Un Notion simulado que anota cada llamada.</summary>
    private sealed class FakeNotion(bool connected = true, string? existingDatabase = null, bool rejectTags = false) : INotionCalls
    {
        public readonly List<(string Tool, JsonObject Args)> Calls = [];
        public bool IsConnected => connected;

        public Task<string> CallAsync(string tool, JsonObject args, CancellationToken ct)
        {
            Calls.Add((tool, (JsonObject)args.DeepClone()));
            return Task.FromResult(tool switch
            {
                "notion-search" => existingDatabase is null
                    ? """{"results":[]}"""
                    : $$"""{"results":[{"id":"{{existingDatabase}}","title":"Kroko · Notas y agenda","type":"database"}]}""",
                "notion-fetch" => $"""<database url="https://www.notion.so/{DatabaseId}"><data-source url="collection://{DataSource}"></data-source></database>""",
                "notion-create-database" => $"""Created <database url="https://www.notion.so/{DatabaseId}"> <data-source url="collection://{DataSource}">""",
                "notion-create-view" => "ok",
                "notion-create-pages" when rejectTags && args["pages"]![0]!["properties"]!["Etiquetas"] is not null
                    => throw new McpCallException("Invalid value for property Etiquetas (multi_select)"),
                "notion-create-pages" => """{"pages":[{"id":"aa","url":"https://www.notion.so/Comprar-pan-0a2504e04f8911d39a0c0305e82c3301"}]}""",
                _ => throw new McpCallException("herramienta inesperada " + tool),
            });
        }
    }

    private sealed class LimaTime : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("Lima", TimeSpan.FromHours(-5), "Lima", "Lima");
    }

    private static KrokoDatabase Db() => new(
        Options.Create(new MemoryOptions { DatabasePath = TestPaths.IsolatedMemory()["Memory:DatabasePath"] }),
        NullLogger<KrokoDatabase>.Instance);

    private static NotionNotebook Notebook(FakeNotion notion, KrokoDatabase? db = null) =>
        new(notion, db ?? Db(), new LimaTime(), NullLogger<NotionNotebook>.Instance);

    [Fact]
    public async Task La_primera_nota_crea_la_base_con_sus_vistas_y_guarda_la_nota_en_un_solo_paso()
    {
        var notion = new FakeNotion();
        var url = await Notebook(notion).SaveAsync(
            new NotionNote("Reunión con Juan", "- Llevar el contrato", "reunion", "2026-11-13T15:00", ["trabajo"]), default);

        Assert.Equal("https://www.notion.so/Comprar-pan-0a2504e04f8911d39a0c0305e82c3301", url);
        Assert.Equal(["notion-search", "notion-create-database", "notion-create-view", "notion-create-view", "notion-create-pages"],
            notion.Calls.Select(c => c.Tool));

        var database = notion.Calls.Single(c => c.Tool == "notion-create-database").Args;
        Assert.Equal("Kroko · Notas y agenda", database["title"]!.GetValue<string>());
        Assert.Contains("\"Tipo\" SELECT('Nota'", database["schema"]!.GetValue<string>());
        Assert.Contains("\"Fecha\" DATE", database["schema"]!.GetValue<string>());

        var views = notion.Calls.Where(c => c.Tool == "notion-create-view").Select(c => c.Args["configure"]!.GetValue<string>()).ToList();
        Assert.Contains("CALENDAR BY \"Fecha\"", views);
        Assert.Contains("GROUP BY \"Tipo\"", views);

        var page = notion.Calls.Last().Args;
        Assert.Equal(DataSource, page["parent"]!["data_source_id"]!.GetValue<string>());
        var props = page["pages"]![0]!["properties"]!;
        Assert.Equal("Reunión con Juan", props["Nombre"]!.GetValue<string>());
        Assert.Equal("Reunión", props["Tipo"]!.GetValue<string>());
        Assert.Equal("2026-11-13T15:00:00-05:00", props["date:Fecha:start"]!.GetValue<string>()); // hora del dueño
        Assert.Equal(1, props["date:Fecha:is_datetime"]!.GetValue<int>());
        Assert.Equal("trabajo", props["Etiquetas"]![0]!.GetValue<string>());
        Assert.Equal("- Llevar el contrato", page["pages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Despues_recuerda_la_base_y_no_la_vuelve_a_buscar_ni_crear()
    {
        var notion = new FakeNotion();
        var db = Db();
        await Notebook(notion, db).SaveAsync(new NotionNote("Idea 1", null, "Idea", null, []), default);
        notion.Calls.Clear();

        // Aunque Kroko se reinicie (otro cuaderno, misma base de datos de Kroko)
        await Notebook(notion, db).SaveAsync(new NotionNote("Idea 2", null, "Idea", "2026-11-20", []), default);

        var only = Assert.Single(notion.Calls);
        Assert.Equal("notion-create-pages", only.Tool);
        var props = only.Args["pages"]![0]!["properties"]!;
        Assert.Equal("2026-11-20", props["date:Fecha:start"]!.GetValue<string>());
        Assert.Equal(0, props["date:Fecha:is_datetime"]!.GetValue<int>());
    }

    [Fact]
    public async Task Si_la_base_ya_existia_en_Notion_la_usa_en_vez_de_duplicarla()
    {
        var notion = new FakeNotion(existingDatabase: DatabaseId);
        await Notebook(notion).SaveAsync(new NotionNote("Comprar pan", null, "Lista", null, []), default);

        Assert.DoesNotContain(notion.Calls, c => c.Tool == "notion-create-database");
        Assert.Equal(["notion-search", "notion-fetch", "notion-create-pages"], notion.Calls.Select(c => c.Tool));
    }

    [Fact]
    public async Task Si_Notion_no_acepta_las_etiquetas_guarda_la_nota_igual()
    {
        var notion = new FakeNotion(rejectTags: true);
        var url = await Notebook(notion).SaveAsync(new NotionNote("Nota", "texto", "Nota", null, ["x"]), default);

        Assert.NotNull(url);
        var pages = notion.Calls.Where(c => c.Tool == "notion-create-pages").ToList();
        Assert.Equal(2, pages.Count);
        Assert.Null(pages[1].Args["pages"]![0]!["properties"]!["Etiquetas"]);
    }

    [Fact]
    public async Task La_herramienta_explica_si_Notion_no_esta_conectado()
    {
        var tool = new GuardarEnNotionTool(Notebook(new FakeNotion(connected: false)));
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { titulo = "Algo", tipo = "Nota" }), default);
        Assert.Contains("\"ok\":false", result.Json);
        Assert.Contains("Conectar Notion", result.Json);
        Assert.Equal(ToolRisk.Write, tool.Definition.Risk);
    }

    [Theory]
    [InlineData("reunion", "Reunión")]
    [InlineData("REUNIÓN", "Reunión")]
    [InlineData("idea", "Idea")]
    [InlineData("cualquier cosa", "Nota")]
    [InlineData(null, "Nota")]
    public void Normaliza_el_tipo(string? typed, string expected) => Assert.Equal(expected, NotionNotebook.NormalizeType(typed));

    [Fact]
    public void Las_etiquetas_de_expresion_nunca_se_muestran()
    {
        Assert.Equal("He anotado tu sugerencia.", Reactions.StripTags("[[happy]] He anotado tu sugerencia."));
        Assert.Equal("Listo", Reactions.StripTags("[[ success ]] Listo"));
        Assert.Equal("Hola", Reactions.StripTags("[[happy] Hola"));
        Assert.Equal("Texto [normal]", Reactions.StripTags("Texto [normal]"));
    }

    [Fact]
    public void La_voz_en_tiempo_real_no_recibe_la_orden_de_poner_etiquetas()
    {
        using var app = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(TestPaths.IsolatedMemory())));
        var prompt = (Kroko.Brain.Agent.SystemPrompt)app.Services.GetService(typeof(Kroko.Brain.Agent.SystemPrompt))!;
        Assert.Contains("[[happy]]", prompt.Build(DateTimeOffset.Now));
        Assert.DoesNotContain("[[happy]]", prompt.Build(DateTimeOffset.Now, expressionTags: false));
    }
}

public class KrokoNameTests
{
    [Theory]
    [InlineData("oye croco, abre Excel", "oye Kroko, abre Excel")]
    [InlineData("Hola cro co", "Hola Kroko")]
    [InlineData("Kroco, ¿qué hora es?", "Kroko, ¿qué hora es?")]
    [InlineData("gracias CROKO", "gracias Kroko")]
    [InlineData("Croco: listo", "Kroko: listo")]
    [InlineData("hola kroko", "hola Kroko")]
    [InlineData("compré croquetas y algo crocante", "compré croquetas y algo crocante")] // no toca otras palabras
    [InlineData("un cocodrilo", "un cocodrilo")]
    public void El_nombre_siempre_es_Kroko(string heard, string expected) =>
        Assert.Equal(expected, Kroko.Shared.KrokoName.Fix(heard));

    [Fact]
    public void Las_respuestas_salen_con_Kroko_y_sin_etiqueta()
    {
        var (text, expression) = Kroko.Brain.Agent.ExpressionTag.Extract("[[happy]] Soy Croco, tu asistente.");
        Assert.Equal("Soy Kroko, tu asistente.", text);
        Assert.Equal("happy", expression);
    }
}
