using System.Text.Json;
using Kroko.Brain.Agent;
using Kroko.Shared.Protocol;

namespace Kroko.Tests;

public class ProtocolTests
{
    [Fact]
    public void UserText_serializa_con_type_y_vuelve_igual()
    {
        var msg = new UserTextMessage { SessionId = "s1", Text = "hola" };
        var json = KrokoProtocol.Serialize(msg);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("user.text", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("s1", doc.RootElement.GetProperty("sessionId").GetString());
        Assert.True(doc.RootElement.TryGetProperty("id", out _));

        var back = Assert.IsType<UserTextMessage>(KrokoProtocol.TryDeserialize(json));
        Assert.Equal("hola", back.Text);
        Assert.Equal(msg.Id, back.Id);
    }

    [Theory]
    [InlineData(typeof(ReplyMessage), "reply")]
    [InlineData(typeof(StateMessage), "state")]
    [InlineData(typeof(ExpressionMessage), "expression")]
    [InlineData(typeof(ToolResultMessage), "tool.result")]
    [InlineData(typeof(ConfirmRequestMessage), "confirm.request")]
    [InlineData(typeof(PingMessage), "ping")]
    public void Cada_tipo_usa_su_discriminador(Type type, string expected)
    {
        var msg = (KrokoMessage)Activator.CreateInstance(type)!;
        using var doc = JsonDocument.Parse(KrokoProtocol.Serialize(msg));
        Assert.Equal(expected, doc.RootElement.GetProperty("type").GetString());
        Assert.IsType(type, KrokoProtocol.TryDeserialize(KrokoProtocol.Serialize(msg)));
    }

    [Theory]
    [InlineData("""{"type":"desconocido"}""")]
    [InlineData("no es json")]
    [InlineData("""{"text":"sin tipo"}""")]
    public void Mensajes_invalidos_devuelven_null_sin_excepcion(string json) =>
        Assert.Null(KrokoProtocol.TryDeserialize(json));

    [Theory]
    [InlineData("[[happy]] ¡Hola Pol!", "¡Hola Pol!", "happy")]
    [InlineData("Listo. [[success]]", "Listo.", "success")]
    [InlineData("[[inventada]] Hola", "Hola", null)]
    [InlineData("Sin etiqueta", "Sin etiqueta", null)]
    [InlineData("[[ Wink ]] **Claro** que sí", "Claro que sí", "wink")]
    [InlineData("[[success] Ya anoté la tarea.", "Ya anoté la tarea.", "success")]
    public void La_etiqueta_de_expresion_se_extrae_y_se_limpia(string raw, string text, string? expression)
    {
        var (clean, expr) = ExpressionTag.Extract(raw);
        Assert.Equal(text, clean);
        Assert.Equal(expression, expr);
    }
}

public class ProtocolOrderTests
{
    [Fact]
    public void El_tipo_puede_venir_en_cualquier_posicion()
    {
        var msg = KrokoProtocol.TryDeserialize("""{"sessionId":"s","text":"hola","type":"user.text"}""");
        Assert.Equal("hola", Assert.IsType<UserTextMessage>(msg).Text);
    }
}
