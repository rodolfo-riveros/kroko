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
