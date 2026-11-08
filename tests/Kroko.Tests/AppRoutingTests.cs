using Kroko.Brain.Agent;
using Kroko.Shared.Protocol;
using Kroko.Shared.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kroko.Tests;

/// <summary>Kroko elige la app adecuada según lo que esté vinculado, y solo sin nada usa su propia memoria.</summary>
public class AppRoutingTests
{
    [Fact]
    public void Sin_nada_vinculado_todo_va_a_su_memoria()
    {
        var text = AppRouting.Describe(new AppLinks(false, false, false, false), "Pol");
        Assert.Contains("apps vinculadas AHORA: ninguna", text);
        Assert.Contains("tu memoria (tarea_crear)", text);
        Assert.Contains("recordatorio_crear (tu memoria", text);
        Assert.Contains("recordar empezando con \"Nota:\"", text);
        Assert.DoesNotContain("google_tarea_crear", text);
        Assert.DoesNotContain("google_calendario_crear", text);
        Assert.Contains("abrir_ventana asistente", text);
    }

    [Fact]
    public void Con_Google_eventos_al_calendario_y_tareas_a_Google_Tasks()
    {
        var text = AppRouting.Describe(new AppLinks(Google: true, Trello: false, Todoist: false, Notion: false), "Pol");
        Assert.Contains("Google Calendar (google_calendario_crear)", text);
        Assert.Contains("Google Tasks (google_tarea_crear)", text);
        Assert.DoesNotContain("tu memoria (tarea_crear)", text);
        Assert.Contains("sin vincular: Todoist, Trello, Notion", text);
    }

    [Fact]
    public void Todoist_manda_en_tareas_Trello_para_proyectos_y_Notion_para_notas()
    {
        var text = AppRouting.Describe(new AppLinks(Google: true, Trello: true, Todoist: true, Notion: true), "Pol");
        Assert.Contains("Tareas y pendientes sueltos (\"tengo que…\", \"anota que debo…\") → Todoist", text);
        Assert.Contains("proyecto, un tablero, un equipo o un cliente → Trello", text);
        Assert.Contains("SIEMPRE en la base de datos «Kroko · Notas y agenda»", text);
        Assert.Contains("Las tareas no van a Notion", text);
        Assert.DoesNotContain("sin vincular", text);
        // Los avisos siempre los da Kroko
        Assert.Contains("siempre recordatorio_crear", text);
    }

    [Fact]
    public void Con_Notion_y_Google_la_agenda_va_a_los_dos()
    {
        var text = AppRouting.Describe(new AppLinks(Google: true, Trello: false, Todoist: false, Notion: true), "Pol");
        Assert.Contains("los DOS: una entrada en su agenda de Notion", text);
        Assert.Contains("google_calendario_crear; en la descripción pon el enlace de la página de Notion", text);
        Assert.Contains("créala UNA sola vez", text);
        Assert.Contains("Tipo (selección: Nota, Idea, Agenda, Reunión, Lista)", text);
        Assert.Contains("recordar", text); // guarda el enlace de la base para no volver a crearla
    }

    [Fact]
    public void Con_Notion_sin_Google_la_agenda_queda_en_Notion_y_ofrece_aviso()
    {
        var text = AppRouting.Describe(new AppLinks(Google: false, Trello: false, Todoist: false, Notion: true), "Pol");
        Assert.Contains("su agenda de Notion (Tipo Agenda o Reunión, con Fecha). Google Calendar no está vinculado", text);
        Assert.DoesNotContain("google_calendario_crear", text);
        Assert.Contains("tu memoria (tarea_crear)", text); // las tareas no van a Notion
    }

    [Theory]
    [InlineData("notion-create-pages", ToolRisk.Write)]
    [InlineData("notion-create-database", ToolRisk.Write)]
    [InlineData("notion-create-view", ToolRisk.Write)]
    [InlineData("notion-search", ToolRisk.Read)]
    [InlineData("notion-update-page", ToolRisk.Dangerous)]
    [InlineData("notion-move-pages", ToolRisk.Dangerous)]
    [InlineData("notion-duplicate-page", ToolRisk.Dangerous)]
    [InlineData("notion-delete-pages", ToolRisk.Dangerous)]
    [InlineData("notion-create-comment", ToolRisk.Dangerous)]
    public void En_Notion_crear_no_pide_permiso_y_modificar_si(string tool, ToolRisk expected)
    {
        using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(TestPaths.IsolatedMemory())));
        var notion = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Kroko.Brain.Mcp.McpOptions>>().Value.Servers["notion"];
        var risk = Kroko.Brain.Mcp.McpServerOptions.Matches(notion.DangerousTools, tool) ? ToolRisk.Dangerous
            : Kroko.Brain.Mcp.McpServerOptions.Matches(notion.WriteTools, tool) ? ToolRisk.Write
            : Kroko.Brain.Mcp.McpServerOptions.Matches(notion.ReadOnlyTools, tool) ? ToolRisk.Read
            : Enum.Parse<ToolRisk>(notion.DefaultRisk);
        Assert.Equal(expected, risk);
    }

    [Fact]
    public void Solo_Trello_las_tareas_van_a_Trello()
    {
        var text = AppRouting.Describe(new AppLinks(Google: false, Trello: true, Todoist: false, Notion: false), "Pol");
        Assert.Contains("→ Trello (trello_crear_tarjeta", text);
        Assert.DoesNotContain("un proyecto, un tablero", text); // no hay otra app con la que repartir
    }

    [Fact]
    public void El_prompt_real_dice_lo_que_esta_vinculado_ahora()
    {
        using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(TestPaths.IsolatedMemory())));
        var prompt = app.Services.GetRequiredService<SystemPrompt>().Build(DateTimeOffset.Now);
        Assert.Contains("apps vinculadas AHORA: ninguna", prompt);
        Assert.Contains("abrir_ventana", prompt);
    }

    [Fact]
    public void Abrir_la_configuracion_no_pide_permiso()
    {
        var tool = Assert.Single(ToolCatalog.All, t => t.Name == "abrir_ventana");
        Assert.Equal(ToolRisk.Read, tool.Risk);
        Assert.Contains("abre tu configuración", tool.Description);
    }
}
