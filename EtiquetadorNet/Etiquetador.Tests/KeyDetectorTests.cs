using Etiquetador.Core.Analysis;

namespace Etiquetador.Tests;

/// <summary>
/// Deteccion de tonalidad. Se prueba con senales generadas de tonalidad CONOCIDA: si el detector
/// acierta con un acorde puro no esta garantizado que acierte con una cancion, pero si falla ahi
/// es que algo esta mal de raiz.
/// </summary>
public class KeyDetectorTests
{
    private const int Sr = 44100;

    /// <summary>Frecuencia de una nota MIDI (69 = La4 = 440 Hz).</summary>
    private static double Hz(int midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);

    /// <summary>Genera unos segundos con las notas indicadas sonando a la vez.</summary>
    private static float[] Acorde(int[] midis, double segundos = 4.0)
    {
        var n = (int)(Sr * segundos);
        var x = new float[n];
        foreach (var m in midis)
        {
            var f = Hz(m);
            // Un poco de segundo armonico: un tono puro no se parece a nada real.
            for (var i = 0; i < n; i++)
                x[i] += (float)(0.3 * Math.Sin(2 * Math.PI * f * i / Sr)
                              + 0.1 * Math.Sin(2 * Math.PI * 2 * f * i / Sr));
        }
        return x;
    }

    // ---- Cromagrama ----

    [Fact]
    public void Un_la_440_carga_la_energia_en_la()
    {
        var croma = KeyDetector.Chromagram(Acorde(new[] { 69 }), Sr);   // La4
        Assert.NotNull(croma);
        var mayor = Array.IndexOf(croma!, croma!.Max());
        Assert.Equal(9, mayor);      // 9 = La
    }

    [Fact]
    public void Un_do_carga_la_energia_en_do()
    {
        var croma = KeyDetector.Chromagram(Acorde(new[] { 60 }), Sr);   // Do4
        Assert.NotNull(croma);
        Assert.Equal(0, Array.IndexOf(croma!, croma!.Max()));
    }

    [Fact]
    public void El_cromagrama_suma_uno()
    {
        var croma = KeyDetector.Chromagram(Acorde(new[] { 60, 64, 67 }), Sr);
        Assert.NotNull(croma);
        Assert.Equal(1.0, croma!.Sum(), 6);
    }

    // ---- Tonalidad ----

    // Do mayor: Do-Mi-Sol.
    [Fact]
    public void Reconoce_do_mayor()
    {
        var k = KeyDetector.Detect(Acorde(new[] { 60, 64, 67 }), Sr);
        Assert.NotNull(k);
        Assert.Equal(0, k!.PitchClass);
        Assert.False(k.IsMinor);
        Assert.Equal("C", k.Name);
    }

    // La menor: La-Do-Mi. Mismas notas que Do mayor pero otro centro, que es el caso dificil.
    [Fact]
    public void Reconoce_la_menor()
    {
        var k = KeyDetector.Detect(Acorde(new[] { 57, 60, 64 }), Sr);
        Assert.NotNull(k);
        Assert.Equal(9, k!.PitchClass);
        Assert.True(k.IsMinor);
    }

    [Fact]
    public void El_silencio_no_da_tonalidad()
        => Assert.Null(KeyDetector.Detect(new float[Sr * 2], Sr));

    [Fact]
    public void Un_audio_demasiado_corto_no_da_tonalidad()
        => Assert.Null(KeyDetector.Detect(new float[100], Sr));

    // ---- Camelot ----

    // La rueda: el relativo mayor y menor comparten numero, que es lo que la hace util para mezclar.
    [Theory]
    [InlineData(0, false, "C", "8B")]     // Do mayor
    [InlineData(9, true, "Am", "8A")]     // La menor, su relativo
    [InlineData(7, false, "G", "9B")]     // Sol mayor
    [InlineData(4, true, "Em", "9A")]     // Mi menor, su relativo
    [InlineData(6, false, "F#", "2B")]
    [InlineData(3, true, "D#m", "2A")]
    public void La_notacion_camelot_es_la_estandar(int clase, bool menor, string nombre, string camelot)
    {
        var k = new MusicalKey(clase, menor, 1.0);
        Assert.Equal(nombre, k.Name);
        Assert.Equal(camelot, k.Camelot);
    }

    // Guarda de la rueda entera: los 24 codigos tienen que ser distintos, o dos tonalidades
    // acabarian mezclandose como si fueran compatibles.
    [Fact]
    public void Los_veinticuatro_codigos_son_distintos()
    {
        var vistos = new HashSet<string>();
        for (var c = 0; c < 12; c++)
            foreach (var menor in new[] { false, true })
                Assert.True(vistos.Add(new MusicalKey(c, menor, 1).Camelot), $"repetido en {c}/{menor}");
        Assert.Equal(24, vistos.Count);
    }

    // ---- FFT ----

    [Fact]
    public void La_fft_encuentra_la_frecuencia_de_un_tono()
    {
        const int n = 4096;
        var re = new double[n];
        var im = new double[n];
        const double f = 1000.0;
        for (var i = 0; i < n; i++) re[i] = Math.Sin(2 * Math.PI * f * i / Sr);

        var mag = Fft.Magnitudes(re, im);
        var pico = Array.IndexOf(mag, mag.Max());
        var hz = pico * (double)Sr / n;
        Assert.InRange(hz, f - 15, f + 15);
    }

    [Fact]
    public void La_fft_exige_potencia_de_dos()
        => Assert.Throws<ArgumentException>(() => Fft.Forward(new double[3], new double[3]));
}
