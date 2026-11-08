using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kroko.Brain.Productivity;
using Kroko.Shared.Protocol;
using Kroko.Shared.Tools;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Kroko.Tests;

/// <summary>Voz segura, Mi día, vectores y mensajes de la voz en tiempo real.</summary>
public class AssistantFeatureTests
{
    [Theory]
    [InlineData("sí, hazlo", true)]
    [InlineData("si dale", true)]
    [InlineData("claro adelante", true)]
    [InlineData("no", false)]
    [InlineData("cancela", false)]
    [InlineData("mejor no", false)]
    [InlineData("eh", null)]
    public void Entiende_si_y_no_dichos_en_voz_alta(string heard, bool? expected) =>
        Assert.Equal(expected, VoiceConfirm.Interpret(heard));

    [Fact]
    public void Solo_las_acciones_medianas_se_aprueban_por_voz()
    {
        Assert.True(VoiceConfirm.IsAllowed("google_calendario_crear"));
        Assert.True(VoiceConfirm.IsAllowed("mover_archivos"));
        foreach (var grave in new[] { "enviar_a_papelera", "apagar", "correo_enviar", "ejecutar_script", "confirmar_accion", "clic", "notion_create-pages", null })
            Assert.False(VoiceConfirm.IsAllowed(grave));
    }

    [Fact]
    public void El_clima_se_describe_en_espanol()
    {
        Assert.Equal("Despejado", MyDayService.Describe(0));
        Assert.Equal("Lluvia", MyDayService.Describe(63));
        Assert.Equal("Tormenta", MyDayService.Describe(95));
    }

    [Fact]
    public void Los_mensajes_de_voz_en_tiempo_real_viajan_por_el_protocolo()
    {
        var back = KrokoProtocol.TryDeserialize(KrokoProtocol.Serialize(new LiveEventMessage { Kind = "you", Text = "hola" }));
        Assert.Equal("hola", Assert.IsType<LiveEventMessage>(back).Text);
        Assert.IsType<LiveStartMessage>(KrokoProtocol.TryDeserialize(KrokoProtocol.Serialize(new LiveStartMessage())));
        Assert.Equal("AAA=", Assert.IsType<LiveAudioMessage>(KrokoProtocol.TryDeserialize(KrokoProtocol.Serialize(new LiveAudioMessage { Data = "AAA=" }))).Data);
    }

    [Fact]
    public async Task Mi_dia_y_los_vectores_piden_token_y_validan()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?>(TestPaths.IsolatedMemory()) { ["Agent:Token"] = "secreto" })));

        var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/midia")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/vectores", new { textos = new[] { "x" } })).StatusCode);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(KrokoProtocol.TokenHeader, "secreto");
        var day = await client.GetFromJsonAsync<JsonElement>("/api/midia");
        Assert.Equal(JsonValueKind.Array, day.GetProperty("tareas").ValueKind);
        Assert.Contains(day.GetProperty("avisos").EnumerateArray(), a => a.GetString()!.Contains("Google"));

        var tooMany = await client.PostAsJsonAsync("/api/vectores", new { textos = Enumerable.Repeat("x", 101) });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public void Las_herramientas_nuevas_estan_en_el_catalogo()
    {
        foreach (var name in new[] { "musica", "mostrar_mi_dia", "buscar_en_documentos", "leer_documento" })
            Assert.Contains(ToolCatalog.All, t => t.Name == name);
    }
}
