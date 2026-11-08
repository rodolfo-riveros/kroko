using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using Kroko.App.Audio;
using Kroko.App.Services;
using Vosk;

namespace Kroko.App.Tests;

/// <summary>
/// «Oye Kroko» con el modelo real de Vosk y frases dichas por la voz de Windows.
/// Si el modelo no está descargado en esta PC, las pruebas que lo necesitan no hacen nada.
/// </summary>
public sealed class WakeWordTests : IDisposable
{
    private static readonly string ModelPath = Path.Combine(VoskModelManager.Folder, new WakeWordSettings().ModelName);
    private readonly Model? _model;

    public WakeWordTests()
    {
        if (!File.Exists(Path.Combine(ModelPath, "am", "final.mdl"))) return;
        Vosk.Vosk.SetLogLevel(-1);
        _model = new Model(ModelPath);
    }

    [Theory]
    [InlineData("oye kroko", false)]
    [InlineData("hola kroko", false)]
    [InlineData("oye kroko, abre excel", true)]
    [InlineData("hola kroko, qué tal", true)]
    public void Despierta_con_la_frase(string phrase, bool hasCommand)
    {
        if (_model is null) return;
        var hit = Detect(phrase);
        Assert.NotNull(hit);
        Assert.Equal(hasCommand, hit.HasCommand);
        Assert.True(hit.UtterancePcm.Length > 16000, "Guarda el audio de la frase para mandarlo entero");
    }

    [Theory]
    [InlineData("qué hora es")]
    [InlineData("hola, cómo estás")]
    [InlineData("hola, croquetas")]
    [InlineData("oye, coloca eso ahí")]
    [InlineData("voy a comer coco")]
    [InlineData("el cocodrilo come")]
    [InlineData("hola loco")]
    public void No_despierta_con_frases_parecidas(string phrase)
    {
        if (_model is null) return;
        Assert.Null(Detect(phrase));
    }

    [Fact]
    public void Los_subtitulos_en_vivo_entienden_lo_que_dices()
    {
        if (_model is null) return;
        using var captions = new LiveTranscriber(_model);
        var text = "";
        var pcm = Speak("abre la carpeta de descargas");
        for (var i = 0; i < pcm.Length; i += 1600) text = captions.Feed(pcm[i..Math.Min(pcm.Length, i + 1600)], Math.Min(1600, pcm.Length - i));
        Assert.Contains("descargas", text);
    }

    [Fact]
    public void Las_listas_por_defecto_no_se_duplican_al_leer_la_configuracion()
    {
        var s = new VoiceSettings();
        Assert.Contains("eso es todo", s.EffectiveEndPhrases);
        s.EndPhrases = ["ya fue"];
        Assert.Equal(["ya fue"], s.EffectiveEndPhrases);
        Assert.Equal(["oye cro co", "hola cro co"], new WakeWordSettings().EffectivePhrases);
    }

    [Fact]
    public void Normaliza_puntuacion_y_mayusculas() =>
        Assert.Equal("oye kroko abre excel", WakeWordDetector.Normalize("¡Oye, Kroko! Abre Excel."));

    private WakeDetection? Detect(string phrase)
    {
        var wake = new WakeWordSettings();
        using var detector = new WakeWordDetector(_model!, wake.EffectivePhrases, wake.EffectiveDecoys);
        // medio segundo de silencio antes y después, en bloques de 50 ms como el micrófono
        var pcm = new byte[16000].Concat(Speak(phrase)).Concat(new byte[32000]).ToArray();
        WakeDetection? hit = null;
        for (var i = 0; i < pcm.Length && hit is null; i += 1600)
        {
            var n = Math.Min(1600, pcm.Length - i);
            hit = detector.Feed(pcm[i..(i + n)], n);
        }
        return hit;
    }

    private static byte[] Speak(string text)
    {
        using var synth = new SpeechSynthesizer();
        var voice = synth.GetInstalledVoices().FirstOrDefault(v => v.VoiceInfo.Culture.TwoLetterISOLanguageName == "es");
        if (voice is not null) synth.SelectVoice(voice.VoiceInfo.Name);
        var file = Path.GetTempFileName();
        try
        {
            synth.SetOutputToWaveFile(file, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak(text);
            synth.SetOutputToNull();
            return File.ReadAllBytes(file)[44..];
        }
        finally
        {
            File.Delete(file);
        }
    }

    public void Dispose() => _model?.Dispose();
}
