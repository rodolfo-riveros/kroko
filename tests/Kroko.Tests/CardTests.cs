using System.Text.Json;
using Kroko.Shared.Protocol;

namespace Kroko.Tests;

/// <summary>Tarjetas visuales del panel a partir del resultado de las herramientas.</summary>
public class CardTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void Las_tareas_se_muestran_como_lista_con_las_pendientes_primero()
    {
        var card = Cards.FromToolResult("tareas_listar", Json("""
            {"hoy":"2026-11-07","tareas":[{"id":1,"tarea":"Llamar al contador","fecha":null,"hecha":true},
                                          {"id":2,"tarea":"Enviar la cotización","fecha":"2026-11-07","hecha":false}]}
            """));
        Assert.NotNull(card);
        Assert.Equal("lista", card.Kind);
        Assert.Equal("Enviar la cotización", card.Items[0].Text);
        Assert.False(card.Items[0].Done);
        Assert.Contains("noviembre", card.Items[0].Detail);
        Assert.True(card.Items[1].Done);
    }

    [Fact]
    public void Los_habitos_muestran_la_racha()
    {
        var card = Cards.FromToolResult("habitos_listar", Json("""{"habitos":[{"Id":1,"habito":"Tomar agua","hechoHoy":true,"racha":3}]}"""));
        Assert.Equal("racha de 3 días", Assert.Single(card!.Items).Detail);
    }

    [Fact]
    public void El_estado_del_equipo_tiene_barras()
    {
        var card = Cards.FromToolResult("estado_equipo", Json("""
            {"cpuPct":12,"ramPct":48,"ramLibreGB":8.2,"bateria":{"porcentaje":76,"cargando":true},
             "discos":[{"unidad":"C:","libreGB":120.5,"totalGB":475,"usadoPct":75}],"internet":true}
            """));
        Assert.Equal("equipo", card!.Kind);
        Assert.Equal(0.76, card.Items.Single(i => i.Text == "Batería").Value);
        Assert.Contains(card.Items, i => i.Text == "Disco C:" && i.Detail == "120.5 GB libres");
        Assert.True(card.Items.Single(i => i.Text == "Internet").Done);
    }

    [Fact]
    public void Otras_herramientas_o_datos_raros_no_generan_tarjeta()
    {
        Assert.Null(Cards.FromToolResult("abrir_programa", Json("""{"ok":true}""")));
        Assert.Null(Cards.FromToolResult("tareas_listar", Json("""{"tareas":"no es una lista"}""")));
        Assert.Null(Cards.FromToolResult("tareas_listar", Json("""[1,2]""")));
    }

    [Fact]
    public void La_tarjeta_viaja_por_el_protocolo()
    {
        var json = KrokoProtocol.Serialize(new CardMessage { Title = "Tareas", Items = [new("Comprar tóner", Done: false)] });
        Assert.Contains("\"type\":\"card\"", json);
        var back = Assert.IsType<CardMessage>(KrokoProtocol.TryDeserialize(json));
        Assert.Equal("Comprar tóner", back.Items[0].Text);
    }
}
