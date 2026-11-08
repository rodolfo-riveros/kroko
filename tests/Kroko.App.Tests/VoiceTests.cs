using Kroko.App.Audio;
using Kroko.App.Native;
using Kroko.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kroko.App.Tests;

public class VoiceTests
{
    [Theory]
    [InlineData("Ctrl+Space", NativeMethods.MOD_CONTROL, 0x20u)]
    [InlineData("Ctrl+Alt+J", NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x4Au)]
    [InlineData("Shift+F2", 0x0004u, 0x71u)]
    public void Lee_el_atajo_de_voz(string text, uint mods, uint vk)
    {
        Assert.True(VoiceController.TryParseHotkey(text, out var m, out var v));
        Assert.Equal(mods, m);
        Assert.Equal(vk, v);
    }

    [Fact]
    public void Atajo_invalido() => Assert.False(VoiceController.TryParseHotkey("Ctrl+Patata", out _, out _));

    [Fact]
    public void Cabecera_wav_correcta()
    {
        var wav = Wav.FromPcm16(new byte[3200], 16000);
        Assert.Equal(44 + 3200, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
    }

    [Fact]
    public void La_voz_de_Windows_genera_audio_sin_reproducirlo()
    {
        var wav = new LocalTts(NullLogger<LocalTts>.Instance).Synthesize("Hola Pol");
        Assert.NotNull(wav);
        Assert.True(wav!.Length > 10_000);
    }
}

public class ReplyModeTests
{
    [Theory]
    [InlineData(true, true, true, true)]    // con voz: lo hablado responde hablando
    [InlineData(true, true, false, true)]   // con voz: lo escrito también (por defecto)
    [InlineData(true, false, false, false)] // "hablar lo escrito" apagado
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, false)]  // solo chat («cállate»): nunca suena
    [InlineData(false, true, false, false)]
    public void El_modo_decide_si_la_respuesta_suena(bool repliesWithVoice, bool speakTyped, bool spoken, bool expected)
    {
        var v = new VoiceSettings { RepliesWithVoice = repliesWithVoice, SpeakTypedReplies = speakTyped };
        Assert.Equal(expected, v.WantsVoice(spoken));
    }

    [Fact]
    public void Por_defecto_responde_con_voz_tambien_a_lo_escrito()
    {
        var v = new VoiceSettings();
        Assert.True(v.WantsVoice(spoken: false));
        Assert.True(v.WantsVoice(spoken: true));
    }
}

public class OpenWindowTests
{
    [Fact]
    public void Las_secciones_de_Ajustes_coinciden_con_las_del_catalogo()
    {
        var tool = Assert.Single(Kroko.Shared.Tools.ToolCatalog.All, t => t.Name == "abrir_ventana");
        foreach (var s in Kroko.App.ViewModels.SettingsViewModel.Sections)
            Assert.Contains($"\"{s}\"", tool.Parameters);
        Assert.Equal(6, Kroko.App.ViewModels.SettingsViewModel.Sections.Length);
    }
}

public class AutoApproveTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 18, 0, 0);

    [Theory]
    [InlineData("apagar", true)]
    [InlineData("reiniciar", true)]
    [InlineData("cerrar_sesion", true)]
    [InlineData("ordenar_carpeta", true)]
    [InlineData("mover_archivos", true)]
    [InlineData("copiar_archivos", true)]
    [InlineData("cerrar_programa", false)]   // Pol eligió que siga preguntando
    [InlineData("enviar_a_papelera", false)] // Pol eligió que siga preguntando
    [InlineData("correo_enviar", false)]
    [InlineData("ejecutar_script", false)]
    public void Solo_las_acciones_elegidas_se_hacen_sin_tarjeta(string tool, bool expected)
    {
        var o = new KrokoOptions();
        Assert.Equal(expected, o.CanAutoApprove(tool, Now.AddSeconds(-20), Now));
    }

    [Fact]
    public void Sin_una_orden_reciente_del_dueno_sale_la_tarjeta()
    {
        var o = new KrokoOptions();
        Assert.False(o.CanAutoApprove("apagar", null, Now));                  // automatización, sin orden suya
        Assert.False(o.CanAutoApprove("apagar", Now.AddMinutes(-10), Now));   // la orden fue hace rato
        Assert.True(o.CanAutoApprove("apagar", Now.AddMinutes(-2), Now));
    }

    [Fact]
    public void La_lista_se_puede_cambiar_en_los_ajustes()
    {
        var o = new KrokoOptions { AutoApproveTools = ["cerrar_programa"] };
        Assert.True(o.CanAutoApprove("cerrar_programa", Now, Now));
        Assert.False(o.CanAutoApprove("apagar", Now, Now));
    }
}
