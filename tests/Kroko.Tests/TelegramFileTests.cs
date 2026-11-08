using System.Text.Json;
using Kroko.Brain.Agent;
using Kroko.Brain.Providers;
using Kroko.Brain.Telegram;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kroko.Tests;

/// <summary>"Mándame el PDF por Telegram": el archivo sale de la PC, va a Telegram y la IA solo ve que se envió.</summary>
public class TelegramFileTests
{
    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46, 1, 2, 3];

    /// <summary>Hace de App: contesta la herramienta con el archivo, como EnviarArchivoTool.</summary>
    private sealed class PcChannel : IAgentChannel
    {
        public string SessionId => "pc";
        public Task SendAsync(KrokoMessage message, CancellationToken ct) => Task.CompletedTask;

        public Task<TResponse?> RequestAsync<TResponse>(KrokoMessage request, string callId, TimeSpan timeout, CancellationToken ct)
            where TResponse : KrokoMessage
        {
            var data = JsonSerializer.SerializeToElement(new
            {
                nombre = "contrato.pdf",
                ruta = @"C:\Users\Pol\Documents\contrato.pdf",
                bytes = Pdf.Length,
                mimeType = "application/pdf",
                mensaje = "Aquí tienes el contrato",
                archivoBase64 = Convert.ToBase64String(Pdf),
            });
            return Task.FromResult(new ToolResultMessage { CallId = callId, Ok = true, Data = data } as TResponse);
        }
    }

    private sealed class FakeTelegram(string? problem = null) : ITelegramDelivery
    {
        public FileAttachment? Sent;

        public Task<string?> SendFileAsync(IAgentChannel channel, FileAttachment file, CancellationToken ct)
        {
            if (problem is null) Sent = file;
            return Task.FromResult(problem);
        }

        public Task SendImageIfTelegramAsync(IAgentChannel channel, byte[] image, string? caption, CancellationToken ct) => Task.CompletedTask;
    }

    private static AppToolGateway Gateway(ITelegramDelivery? telegram)
    {
        var services = new ServiceCollection();
        if (telegram is not null) services.AddSingleton(telegram);
        return new AppToolGateway(new JarvisToolTests.StaticMonitor<AgentOptions>(new AgentOptions()), services.BuildServiceProvider(),
            NullLogger<AppToolGateway>.Instance);
    }

    private static ToolCall Call() =>
        new("c1", "enviar_archivo", JsonSerializer.SerializeToElement(new { ruta = "documentos\\contrato.pdf" }));

    [Fact]
    public async Task El_archivo_llega_a_Telegram_y_la_IA_no_recibe_su_contenido()
    {
        var telegram = new FakeTelegram();
        var result = await Gateway(telegram).ExecuteAsync(Call(), new PcChannel(), default);

        Assert.NotNull(telegram.Sent);
        Assert.Equal("contrato.pdf", telegram.Sent!.Name);
        Assert.Equal(Pdf, telegram.Sent.Data);
        Assert.Equal("Aquí tienes el contrato", telegram.Sent.Caption);
        Assert.Contains("\"ok\":true", result.Json);
        Assert.Contains("enviado", result.Json);
        Assert.DoesNotContain("archivoBase64", result.Json);
        Assert.DoesNotContain(Convert.ToBase64String(Pdf), result.Json);
    }

    [Fact]
    public async Task Si_Telegram_no_esta_vinculado_lo_dice_en_vez_de_inventar()
    {
        var result = await Gateway(new FakeTelegram("Todavía no vinculaste tu Telegram con Kroko")).ExecuteAsync(Call(), new PcChannel(), default);
        Assert.Contains("\"ok\":false", result.Json);
        Assert.Contains("no vinculaste tu Telegram", result.Json);

        var noBot = await Gateway(null).ExecuteAsync(Call(), new PcChannel(), default);
        Assert.Contains("\"ok\":false", noBot.Json);
    }

    [Fact]
    public void Las_tarjetas_se_ven_bien_en_el_chat()
    {
        var tasks = TelegramFormat.Card(new CardMessage
        {
            Kind = "lista",
            Title = "Tus pendientes",
            Items = [new("Comprar pan", Done: false), new("Pagar luz", "vence hoy", Done: true)],
        });
        Assert.Equal("📋 Tus pendientes\n⬜ Comprar pan\n✅ Pagar luz — vence hoy", tasks.Replace("\r", ""));

        var pc = TelegramFormat.Card(new CardMessage { Kind = "equipo", Title = "Tu PC", Items = [new("Batería", Value: 0.8)] });
        Assert.Contains("💻 Tu PC", pc);
        Assert.Contains("▰▰▰▰▰▰▰▰▱▱ 80%", pc);
    }
}
