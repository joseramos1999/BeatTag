using Etiquetador.App.ViewModels;
using Etiquetador.Core.Analysis;
using Xunit;

namespace Etiquetador.Tests;

/// <summary>
/// Referencia del modo automático de Volumen: el nivel más alto que alcanza sin distorsión el 95 % de
/// la biblioteca, sin pasar de la mediana ni quedar más de 3 dB por debajo (ver ReferenciaVolumen).
/// </summary>
public class ReferenciaVolumenTests
{
    private static (double, double, bool) Mp3(double lufs, double pico) => (lufs, pico, true);

    private static List<(double, double, bool)> Repetir(int n, (double, double, bool) fila)
        => Enumerable.Repeat(fila, n).ToList();

    [Fact]
    public void Sin_medidas_la_referencia_es_la_de_siempre()
        => Assert.Equal((-14.0, -14.0), ReferenciaVolumen.Automatica(new List<(double, double, bool)>()));

    // Todas pueden subir hasta la mediana: no hay motivo para bajar a nadie.
    [Fact]
    public void Si_todas_alcanzan_la_mediana_la_referencia_es_la_mediana()
    {
        var filas = Enumerable.Range(0, 100).Select(i => Mp3(-14 + i * 0.05, -8)).ToList();
        var (referencia, mediana) = ReferenciaVolumen.Automatica(filas);
        Assert.Equal(mediana, referencia);
        Assert.Equal(-11.5, mediana);
    }

    // Un 4 % de grabaciones bajas y sin margen no mueve la referencia: el percentil 5 cae entre las normales.
    [Fact]
    public void Unas_pocas_bajas_sin_margen_no_mueven_la_referencia()
    {
        var filas = Repetir(4, Mp3(-16, -0.1)).Concat(Repetir(96, Mp3(-11, -8))).ToList();
        var (referencia, mediana) = ReferenciaVolumen.Automatica(filas);
        Assert.Equal(-11.0, mediana);
        Assert.Equal(-11.0, referencia);
    }

    // Un 10 % a −13 sin margen: la referencia baja hasta que esas quedan a medio paso (−13 + 0,75).
    [Fact]
    public void Si_mas_del_5_por_ciento_no_alcanza_la_referencia_baja_hasta_ellas()
    {
        var filas = Repetir(10, Mp3(-13, -0.1)).Concat(Repetir(90, Mp3(-11, -0.2))).ToList();
        var (referencia, mediana) = ReferenciaVolumen.Automatica(filas);
        Assert.Equal(-11.0, mediana);
        Assert.Equal(-12.3, referencia);   // −12,25 redondeado a una décima
    }

    // Nunca más de 3 dB por debajo de la mediana: más allá, el nivel general lo decide el DJ.
    [Fact]
    public void Nunca_baja_mas_de_3_dB_de_la_mediana()
    {
        var filas = Repetir(20, Mp3(-17, -0.1)).Concat(Repetir(80, Mp3(-9, -0.1))).ToList();
        var (referencia, mediana) = ReferenciaVolumen.Automatica(filas);
        Assert.Equal(-9.0, mediana);
        Assert.Equal(-12.0, referencia);
    }

    // Lo que no es MP3 no se ajusta: su alcance es su propio nivel, tenga el margen que tenga.
    [Fact]
    public void Un_no_mp3_alcanza_solo_su_propio_nivel()
    {
        Assert.Equal(-16.0, ReferenciaVolumen.Alcance(-16, -20, ajustable: false));
        Assert.Equal(-16.0 + 3 * 1.5, ReferenciaVolumen.Alcance(-16, -5.2, ajustable: true));   // caben 4,7 dB: 3 pasos
        Assert.Equal(-16.0, ReferenciaVolumen.Alcance(-16, -1.9, ajustable: true));             // no cabe ni uno

        var filas = Repetir(10, (-13.0, -20.0, false)).Concat(Repetir(90, Mp3(-11, -8))).ToList();
        Assert.Equal(-12.3, ReferenciaVolumen.Automatica(filas).Referencia);
    }

    // ---------- En la pestaña: sin las excluidas, y explicada ----------

    // Las acapellas o intros que el DJ excluye porque las quiere bajas no pueden arrastrar la
    // referencia de todas las demás.
    [Fact]
    public void Las_excluidas_no_cuentan_para_la_referencia()
    {
        var acapellas = Enumerable.Range(0, 20)
            .Select(i => new LoudnessRow { FilePath = $@"C:\m\Acapellas\{i}.mp3", Lufs = -20, PeakDb = -0.1, Target = -11, Exclusion = ExclusionVolumen.Carpeta })
            .ToList();
        var resto = Enumerable.Range(0, 80)
            .Select(i => new LoudnessRow { FilePath = $@"C:\m\{i}.mp3", Lufs = -11, PeakDb = -0.2, Target = -11 })
            .ToList();
        var filas = acapellas.Concat(resto).ToList();

        Assert.Equal((-11.0, -11.0), LoudnessViewModel.ReferenciaAutomatica(filas));

        // Sin excluirlas, bajarían la referencia al tope de 3 dB.
        foreach (var a in acapellas) a.Exclusion = ExclusionVolumen.Ninguna;
        Assert.Equal((-14.0, -11.0), LoudnessViewModel.ReferenciaAutomatica(filas));

        // Si todo está excluido, se calcula con todo: la referencia solo se muestra.
        foreach (var f in filas) f.Exclusion = ExclusionVolumen.Cancion;
        Assert.Equal((-14.0, -11.0), LoudnessViewModel.ReferenciaAutomatica(filas));
    }

    [Fact]
    public void La_explicacion_da_los_dos_valores_cuando_la_referencia_queda_bajo_el_nivel_medio()
    {
        // Los números, con la cultura de quien ejecuta la prueba (en la aplicación, «-12,1»).
        string N(double v) => v.ToString("0.0");
        var baja = LoudnessViewModel.TextoReferenciaAutomatica(-12.1, -11.5);
        Assert.StartsWith($"Referencia: {N(-12.1)} LUFS, el nivel más alto que alcanzan sin distorsión el 95 % de tus grabaciones", baja);
        Assert.Contains($"(nivel medio: {N(-11.5)} LUFS)", baja);
        Assert.EndsWith("se igualan reduciendo las demás.", baja);

        Assert.Equal($"Referencia: {N(-11.5)} LUFS, el nivel medio de tus grabaciones.",
                     LoudnessViewModel.TextoReferenciaAutomatica(-11.5, -11.5));
        Assert.Contains("al analizarlas", LoudnessViewModel.TextoReferenciaAutomatica(null, null));
        Assert.Equal("Nivel común de la biblioteca (recomendado)", LoudnessViewModel.ModoAutomatico);
    }

    // ---------- Estabilidad: un segundo «Ajustar» no vuelve a tocar nada ----------

    private sealed class Cancion
    {
        public double L, P;
        public bool Mp3 = true;
    }

    private static List<Cancion> Biblioteca(Random rnd, int n)
        => Enumerable.Range(0, n).Select(_ => new Cancion
        {
            L = -11 + Normal(rnd),          // LUFS ~ N(−11, 1)
            P = -6 * rnd.NextDouble(),     // picos uniformes en [−6, 0]
        }).ToList();

    private static double Normal(Random rnd)
        => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());

    private static double Referencia(IEnumerable<Cancion> c)
        => ReferenciaVolumen.Automatica(c.Select(x => (x.L, x.P, x.Mp3)).ToList(), LoudnessViewModel.MargenSeguridad).Referencia;

    private static LoudnessRow Fila(Cancion c, double referencia)
        => new() { FilePath = c.Mp3 ? @"C:\m\a.mp3" : @"C:\m\a.wav", Lufs = c.L, PeakDb = c.P, Target = referencia };

    /// <summary>Lo que hace «Ajustar volumen» con todo marcado: aplica los pasos a las candidatas.</summary>
    private static HashSet<Cancion> Ajustar(List<Cancion> biblioteca, double referencia)
    {
        var tocadas = new HashSet<Cancion>();
        foreach (var c in biblioteca)
        {
            var f = Fila(c, referencia);
            if (!f.Candidata) continue;
            var pasos = LoudnessViewModel.PasosAplicables(f.Gain, f.PicoDecision);
            c.L += pasos * Mp3Gain.DbPerStep;
            c.P += pasos * Mp3Gain.DbPerStep;
            tocadas.Add(c);
        }
        return tocadas;
    }

    [Fact]
    public void Un_segundo_ajuste_no_vuelve_a_tocar_nada_ni_tras_anadir_canciones()
    {
        var rnd = new Random(20261029);
        var biblioteca = Biblioteca(rnd, 5000);

        var t1 = Referencia(biblioteca);
        var primera = Ajustar(biblioteca, t1);
        Assert.True(primera.Count > 500, $"la prueba no ajusta casi nada ({primera.Count})");

        // Ajustar no cambia el alcance de nadie (nivel y pico se mueven juntos): la referencia se queda.
        var t2 = Referencia(biblioteca);
        Assert.Equal(t1, t2);
        Assert.Equal(0, biblioteca.Count(c => Fila(c, t2).Candidata));

        // Un 20 % de canciones nuevas mueve algo la referencia, pero ninguna de las ya ajustadas
        // vuelve a necesitar ajuste: quedaron a 0,75 dB o menos, y el umbral es 1.
        var con = biblioteca.Concat(Biblioteca(rnd, 1000)).ToList();
        var t3 = Referencia(con);
        Assert.True(Math.Abs(t3 - t1) <= 0.25, $"la referencia se movió {t3 - t1:0.00} dB");
        Assert.DoesNotContain(primera, c => Fila(c, t3).Candidata);
    }
}
