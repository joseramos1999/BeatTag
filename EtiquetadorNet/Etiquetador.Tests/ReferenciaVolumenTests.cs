using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core.Analysis;
using Xunit;

namespace Etiquetador.Tests;

/// <summary>
/// Referencia del modo automático de Volumen: el nivel más alto que alcanza sin distorsión el 95 % de
/// la biblioteca, sin pasar de la mediana ni quedar más de 3 dB por debajo, calculado con el nivel
/// original de cada grabación (ver ReferenciaVolumen).
/// </summary>
public class ReferenciaVolumenTests
{
    private static FilaReferencia Mp3(double lufs, double pico, double aplicada = 0) => new(lufs, pico, true, aplicada);
    private static FilaReferencia NoMp3(double lufs, double pico = -6) => new(lufs, pico, false);

    private static List<FilaReferencia> Repetir(int n, FilaReferencia fila) => Enumerable.Repeat(fila, n).ToList();

    [Fact]
    public void Sin_medidas_la_referencia_es_la_de_siempre()
        => Assert.Equal(new ResultadoReferencia(-14.0, -14.0, MotivoReferencia.SinMedidas),
                        ReferenciaVolumen.Automatica(new List<FilaReferencia>()));

    // Todas pueden subir hasta la mediana: no hay motivo para bajar a nadie.
    [Fact]
    public void Si_todas_alcanzan_la_mediana_la_referencia_es_la_mediana()
    {
        var filas = Enumerable.Range(0, 100).Select(i => Mp3(-14 + i * 0.05, -8)).ToList();
        var r = ReferenciaVolumen.Automatica(filas);
        Assert.Equal(r.Mediana, r.Referencia);
        Assert.Equal(-11.5, r.Mediana);
        Assert.Equal(MotivoReferencia.Mediana, r.Motivo);
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
        var r = ReferenciaVolumen.Automatica(filas);
        Assert.Equal(-11.0, r.Mediana);
        Assert.Equal(-12.3, r.Referencia);   // −12,25 redondeado a una décima
        Assert.Equal(MotivoReferencia.Alcance, r.Motivo);
    }

    // Nunca más de 3 dB por debajo de la mediana: más allá, el nivel general lo decide el DJ.
    [Fact]
    public void Nunca_baja_mas_de_3_dB_de_la_mediana()
    {
        var filas = Repetir(20, Mp3(-17, -0.1)).Concat(Repetir(80, Mp3(-9, -0.1))).ToList();
        var r = ReferenciaVolumen.Automatica(filas);
        Assert.Equal(-9.0, r.Mediana);
        Assert.Equal(-12.0, r.Referencia);
        Assert.Equal(MotivoReferencia.Tope, r.Motivo);
    }

    // Lo que no es MP3 no se ajusta: su alcance es su propio nivel, tenga el margen que tenga.
    [Fact]
    public void Un_no_mp3_alcanza_solo_su_propio_nivel()
    {
        Assert.Equal(-16.0, ReferenciaVolumen.Alcance(-16, -20, ajustable: false));
        Assert.Equal(-16.0 + 3 * 1.5, ReferenciaVolumen.Alcance(-16, -5.2, ajustable: true));   // caben 4,7 dB: 3 pasos
        Assert.Equal(-16.0, ReferenciaVolumen.Alcance(-16, -1.9, ajustable: true));             // no cabe ni uno

        var filas = Repetir(10, NoMp3(-13, -20)).Concat(Repetir(90, Mp3(-11, -8))).ToList();
        Assert.Equal(-12.3, ReferenciaVolumen.Automatica(filas).Referencia);
    }

    // ---------- Salvaguardas: carpetas pequeñas y bibliotecas sin MP3 ----------

    // Con menos de 20 grabaciones el 5 % no llega a una: el «percentil 5» sería la más baja, y una sola
    // acapella o intro bajaría la referencia de toda la carpeta.
    [Fact]
    public void En_una_carpeta_pequena_una_sola_grabacion_baja_no_mueve_la_referencia()
    {
        Assert.Equal(20, ReferenciaVolumen.MinimoGrabaciones);

        var pequena = Repetir(1, Mp3(-20, -0.1)).Concat(Repetir(9, Mp3(-11, -0.2))).ToList();
        Assert.Equal(new ResultadoReferencia(-11.0, -11.0, MotivoReferencia.PocasGrabaciones), ReferenciaVolumen.Automatica(pequena));

        // Con 20 ya hay percentil: una sola sigue sin mover nada, dos (el 10 %) sí.
        var una = Repetir(1, Mp3(-13, -0.1)).Concat(Repetir(19, Mp3(-11, -0.2))).ToList();
        Assert.Equal(new ResultadoReferencia(-11.0, -11.0, MotivoReferencia.Mediana), ReferenciaVolumen.Automatica(una));
        var dos = Repetir(2, Mp3(-13, -0.1)).Concat(Repetir(18, Mp3(-11, -0.2))).ToList();
        Assert.Equal(new ResultadoReferencia(-12.3, -11.0, MotivoReferencia.Alcance), ReferenciaVolumen.Automatica(dos));
    }

    // En una biblioteca de AIFF o FLAC no se puede bajar nada para igualar: bajar la referencia solo
    // marcaría como «Alto» lo que está en su nivel.
    [Fact]
    public void Si_la_mayoria_no_son_mp3_la_referencia_es_la_mediana()
    {
        var rnd = new Random(7);
        var aiff = Enumerable.Range(0, 3000).Select(_ => NoMp3(-10 + 1.5 * Normal(rnd))).ToList();
        var r = ReferenciaVolumen.Automatica(aiff);
        Assert.Equal(MotivoReferencia.PocasAjustables, r.Motivo);
        Assert.Equal(r.Mediana, r.Referencia);

        // Con la mitad o más en MP3 sí se baja: hay algo que bajar.
        var mitad = Repetir(10, NoMp3(-13)).Concat(Repetir(40, NoMp3(-11))).Concat(Repetir(50, Mp3(-11, -0.2))).ToList();
        Assert.Equal(new ResultadoReferencia(-12.3, -11.0, MotivoReferencia.Alcance), ReferenciaVolumen.Automatica(mitad));
        var menos = Repetir(10, NoMp3(-13)).Concat(Repetir(41, NoMp3(-11))).Concat(Repetir(49, Mp3(-11, -0.2))).ToList();
        Assert.Equal(new ResultadoReferencia(-11.0, -11.0, MotivoReferencia.PocasAjustables), ReferenciaVolumen.Automatica(menos));
    }

    // ---------- Con el nivel original ----------

    // Una grabación que BeatTag ya ha bajado cuenta con el nivel y el pico que tenía antes: la misma
    // biblioteca da la misma referencia, ajustada o no. Un pico recortado que se bajó se guarda en el
    // tope (0 dBFS); con la ganancia restada queda por encima de 0 y no admite subir, como antes.
    [Fact]
    public void Una_grabacion_ajustada_cuenta_con_su_nivel_original()
    {
        var antes = Repetir(20, Mp3(-17, -0.1)).Concat(Repetir(80, Mp3(-9, -0.1))).ToList();
        var despues = Repetir(20, Mp3(-17, -0.1)).Concat(Repetir(80, Mp3(-12, 0.0, aplicada: -3))).ToList();

        Assert.Equal(ReferenciaVolumen.Automatica(antes), ReferenciaVolumen.Automatica(despues));
        Assert.Equal(-9.0, ReferenciaVolumen.Automatica(despues).Mediana);
        // Con el nivel actual, la mediana sería −12 y el tope bajaría a −15.
        var sinGanancia = despues.Select(f => f with { Aplicada = 0 }).ToList();
        Assert.Equal(-15.0, ReferenciaVolumen.Automatica(sinGanancia).Referencia);

        // Una subida también se descuenta: nivel y pico vuelven a los de antes.
        Assert.Equal(-13.0, ReferenciaVolumen.Alcance(-14.5 + 1.5 - 1.5, -2.6 + 1.5 - 1.5, true));
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

        Assert.Equal(new ResultadoReferencia(-11.0, -11.0, MotivoReferencia.Mediana), LoudnessViewModel.ReferenciaAutomatica(filas));

        // Sin excluirlas, bajarían la referencia al tope de 3 dB.
        foreach (var a in acapellas) a.Exclusion = ExclusionVolumen.Ninguna;
        Assert.Equal(new ResultadoReferencia(-14.0, -11.0, MotivoReferencia.Tope), LoudnessViewModel.ReferenciaAutomatica(filas));

        // Si todo está excluido, se calcula con todo: la referencia solo se muestra.
        foreach (var f in filas) f.Exclusion = ExclusionVolumen.Cancion;
        Assert.Equal(-14.0, LoudnessViewModel.ReferenciaAutomatica(filas).Referencia);
    }

    // La tabla pasa la ganancia aplicada de cada fila: lo ya ajustado cuenta con su nivel original.
    [Fact]
    public void En_la_tabla_lo_ajustado_cuenta_con_su_nivel_original()
    {
        LoudnessRow F(int i, double lufs, double pico, double aplicada) =>
            new() { FilePath = $@"C:\m\{i}.mp3", Lufs = lufs, PeakDb = pico, GananciaAplicada = aplicada, Target = -11 };
        var bajas = Enumerable.Range(0, 20).Select(i => F(i, -17, -0.1, 0));
        var ajustadas = Enumerable.Range(20, 80).Select(i => F(i, -12, 0, -3));
        Assert.Equal(new ResultadoReferencia(-12.0, -9.0, MotivoReferencia.Tope),
                     LoudnessViewModel.ReferenciaAutomatica(bajas.Concat(ajustadas).ToList()));
    }

    [Fact]
    public void La_explicacion_dice_por_que_la_referencia_esta_donde_esta()
    {
        // Los números, con la cultura de quien ejecuta la prueba (en la aplicación, «-12,1»).
        string N(double v) => v.ToString("0.0");
        string T(double r, double m, MotivoReferencia motivo, bool ajustadas = false)
            => LoudnessViewModel.TextoReferenciaAutomatica(new ResultadoReferencia(r, m, motivo), ajustadas);

        var baja = T(-12.1, -11.5, MotivoReferencia.Alcance);
        Assert.StartsWith($"Referencia: {N(-12.1)} LUFS, el nivel más alto que alcanzan sin distorsión el 95 % de tus grabaciones", baja);
        Assert.Contains($"(nivel medio: {N(-11.5)} LUFS)", baja);
        Assert.EndsWith("se igualan reduciendo las demás.", baja);
        Assert.Contains($"(nivel medio antes de ajustarlas: {N(-11.5)} LUFS)", T(-12.1, -11.5, MotivoReferencia.Alcance, ajustadas: true));

        // Con el límite de 3 dB no es el nivel que alcanza el 95 %: se dice que se ha aplicado el límite.
        var tope = T(-12.0, -9.0, MotivoReferencia.Tope);
        Assert.StartsWith($"Referencia: {N(-12.0)} LUFS, 3 dB por debajo del nivel medio de tus grabaciones ({N(-9.0)} LUFS)", tope);
        Assert.Contains("lo máximo que se reduce de forma automática", tope);
        Assert.Contains("Más del 5 % de tus grabaciones no alcanza ese nivel", tope);
        Assert.DoesNotContain("alcanzan sin distorsión el 95 %", tope);
        Assert.Contains("del nivel medio que tenían tus grabaciones antes de ajustarlas", T(-12.0, -9.0, MotivoReferencia.Tope, ajustadas: true));

        Assert.Equal($"Referencia: {N(-11.5)} LUFS, el nivel medio de tus grabaciones.", T(-11.5, -11.5, MotivoReferencia.Mediana));
        Assert.Equal($"Referencia: {N(-11.5)} LUFS, el nivel medio que tenían tus grabaciones antes de ajustarlas.",
                     T(-11.5, -11.5, MotivoReferencia.Mediana, ajustadas: true));
        Assert.Contains("Con menos de 20 grabaciones", T(-11.5, -11.5, MotivoReferencia.PocasGrabaciones));
        Assert.Contains("La mayoría no son MP3", T(-11.5, -11.5, MotivoReferencia.PocasAjustables));

        Assert.Contains("al analizarlas", LoudnessViewModel.TextoReferenciaAutomatica(null));
        Assert.Equal("Nivel común de la biblioteca (recomendado)", LoudnessViewModel.ModoAutomatico);
    }

    // ---------- Estabilidad: repetir «Analizar → Ajustar» no vuelve a tocar nada ----------

    private static double Normal(Random rnd)
        => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());

    /// <summary>Una grabación: lo que tiene de verdad el archivo y lo que guarda sonoridad.json de ella.</summary>
    private sealed class Cancion
    {
        public bool Mp3 = true;
        public double Muestra;                  // pico de muestra del audio, sin el recorte del decodificador (puede pasar de 0)
        public double Exceso;                   // lo que el pico real queda por encima del de muestra
        public double L, P;                     // lo guardado: nivel y pico de muestra
        public double? T;                       // lo guardado: pico real, si se midió
        public double G;                        // lo guardado: ganancia aplicada por BeatTag
        public double Bajada;                   // cuánto se ha bajado en total (para comprobar el tope)
        public int Veces;                       // cuántas veces se ha tocado

        /// <summary>Lo que daría medir el archivo ahora: el decodificador recorta el pico de muestra en 0.</summary>
        public LoudnessResult Medir() => new(L, Math.Min(0, Muestra), 0, Muestra + Exceso, G);
    }

    private static LoudnessRow Fila(Cancion c, double referencia, bool conGanancia)
        => new()
        {
            FilePath = c.Mp3 ? @"C:\m\a.mp3" : @"C:\m\a.wav",
            Lufs = c.L, PeakDb = c.P, TruePeakDb = c.T, GananciaAplicada = conGanancia ? c.G : 0, Target = referencia,
        };

    private static double Referencia(List<Cancion> biblioteca, bool conGanancia)
        => LoudnessViewModel.ReferenciaAutomatica(biblioteca.Select(c => Fila(c, -14, conGanancia)).ToList()).Referencia;

    /// <summary>
    /// Una pasada de «Analizar → Ajustar volumen» con todo marcado, como la pestaña: la referencia con
    /// ReferenciaAutomatica; el pico real de lo que se aumentaría, medido y guardado al terminar de
    /// analizar (dos rondas, como ComprobarPicoRealAsync); las candidatas de la tabla; la última
    /// comprobación antes de subir (Decidir) y lo que se guarda después (TrasAjustar, más la ganancia).
    /// Devuelve la referencia con la que se ajustó y cuántas tocó.
    /// </summary>
    private static (double Referencia, int Tocadas) Pasada(List<Cancion> biblioteca, bool conGanancia)
    {
        var referencia = Referencia(biblioteca, conGanancia);
        for (var ronda = 0; ronda < 2; ronda++)
        {
            var filas = biblioteca.Select(c => (c, f: Fila(c, referencia, conGanancia))).ToList();
            var faltan = LoudnessViewModel.SinPicoReal(filas.Select(x => x.f)).ToHashSet();
            if (faltan.Count == 0) break;
            foreach (var (c, f) in filas)
                if (faltan.Contains(f)) { var m = c.Medir(); c.P = m.PeakDb; c.T = m.TruePeakDb; }
            referencia = Referencia(biblioteca, conGanancia);
        }

        var tocadas = 0;
        foreach (var c in biblioteca)
        {
            var f = Fila(c, referencia, conGanancia);
            if (!f.Candidata) continue;
            var d = LoudnessViewModel.Decidir(f, _ => new LoudnessResult(c.L, c.P, 0, c.T, c.G), _ => c.Medir());
            if (d.Medida) { c.P = d.PicoMuestra; c.T = d.PicoReal; }
            if (d.Pasos == 0) continue;
            var db = d.Pasos * Mp3Gain.DbPerStep;
            (c.L, c.P, c.T) = LoudnessScanner.TrasAjustar(c.L, d.PicoMuestra, d.PicoReal, db);
            c.Muestra += db;
            c.G += db;
            if (db < 0) c.Bajada -= db;
            c.Veces++;
            tocadas++;
        }
        return (referencia, tocadas);
    }

    /// <summary>
    /// Biblioteca bimodal: un grupo alto y otro bajo. Picos de muestra entre −picoMinimo y 0, uno de
    /// cada cinco recortado por el decodificador, y la mitad de las medidas sin pico real (antiguas).
    /// </summary>
    private static List<Cancion> Bimodal(Random rnd, int n, double fraccionBaja, double nivelAlto, double nivelBajo,
                                         double sigma, double picoMinimo, double fraccionNoMp3 = 0)
        => Enumerable.Range(0, n).Select(_ =>
        {
            var baja = rnd.NextDouble() < fraccionBaja;
            var muestra = rnd.NextDouble() < 0.2 ? 0.5 * rnd.NextDouble() : -picoMinimo * rnd.NextDouble();
            var c = new Cancion
            {
                Mp3 = rnd.NextDouble() >= fraccionNoMp3,
                L = (baja ? nivelBajo : nivelAlto) + sigma * Normal(rnd),
                Muestra = muestra,
                Exceso = 0.6 * rnd.NextDouble(),
            };
            var m = c.Medir();
            c.P = m.PeakDb;
            c.T = rnd.NextDouble() < 0.5 ? m.TruePeakDb : null;
            return c;
        }).ToList();

    // Los dos casos del revisor: con el nivel actual, la referencia bajaba en cada pasada (de −12,2 a
    // −15,3 y −16,8) y el grupo alto acababa 7,5 dB más bajo. Con el nivel original, la referencia es la
    // misma en cada pasada, la segunda no toca nada y nadie baja más de lo que pide el tope de 3 dB.
    [Theory]
    [InlineData(0.20, -9.0, -17.0, 0.6, 1.0)]   // 80 % a −9 y 20 % antiguas a −17, todas sin margen: el tope actúa
    [InlineData(0.30, -9.0, -13.0, 0.5, 2.0)]   // 70 % a −9 y 30 % a −13 con poco margen
    public void Repetir_el_ajuste_no_vuelve_a_tocar_nada_y_el_tope_se_cumple(double fraccionBaja, double alto, double bajo, double sigma, double picoMinimo)
    {
        var biblioteca = Bimodal(new Random(1), 2000, fraccionBaja, alto, bajo, sigma, picoMinimo);
        var medianaOriginal = Math.Round(biblioteca.Select(c => c.L).OrderBy(x => x).ElementAt(biblioteca.Count / 2), 1);

        var (r1, primera) = Pasada(biblioteca, conGanancia: true);
        Assert.True(primera > 500, $"la prueba no ajusta casi nada ({primera})");
        Assert.True(r1 < medianaOriginal, "la prueba debe bajar la referencia");

        for (var pasada = 2; pasada <= 4; pasada++)
        {
            var (r, tocadas) = Pasada(biblioteca, conGanancia: true);
            Assert.Equal(r1, r);
            Assert.Equal(0, tocadas);
        }
        Assert.All(biblioteca, c => Assert.True(c.Veces <= 1));
        // Nadie queda más de 3 dB (más medio paso de redondeo) por debajo de la mediana original.
        Assert.All(biblioteca.Where(c => c.Bajada > 0), c => Assert.True(c.L >= medianaOriginal - ReferenciaVolumen.DescensoMaximo - 0.75 - 1e-9,
            $"bajada a {c.L:0.00} con mediana original {medianaOriginal:0.0}"));
    }

    // La prueba anterior detecta el fallo: sin la ganancia aplicada, el mismo caso vuelve a bajar.
    [Fact]
    public void Sin_la_ganancia_aplicada_la_referencia_bajaba_en_cada_pasada()
    {
        var biblioteca = Bimodal(new Random(1), 2000, 0.20, -9.0, -17.0, 0.6, 1.0);
        var (r1, _) = Pasada(biblioteca, conGanancia: false);
        var (r2, segunda) = Pasada(biblioteca, conGanancia: false);
        Assert.True(r2 < r1 - 1, $"{r1} → {r2}");
        Assert.True(segunda > 500, $"segunda pasada: {segunda}");
    }

    // Propiedad, sobre muchas bibliotecas distintas (bimodales, con el tope activo o no, con algo que
    // no es MP3): la referencia no se mueve y la segunda pasada no toca nada.
    [Fact]
    public void En_cualquier_biblioteca_la_segunda_pasada_no_toca_nada()
    {
        var rnd = new Random(20261029);
        var conTope = 0;
        for (var caso = 0; caso < 150; caso++)
        {
            var biblioteca = Bimodal(rnd, 300 + rnd.Next(700),
                fraccionBaja: 0.4 * rnd.NextDouble(),
                nivelAlto: -7 - 4 * rnd.NextDouble(),
                nivelBajo: -12 - 6 * rnd.NextDouble(),
                sigma: 0.3 + 1.5 * rnd.NextDouble(),
                picoMinimo: 0.5 + 5 * rnd.NextDouble(),
                fraccionNoMp3: rnd.NextDouble() < 0.3 ? 0.2 * rnd.NextDouble() : 0);
            var filas = biblioteca.Select(c => Fila(c, -14, true)).ToList();
            if (LoudnessViewModel.ReferenciaAutomatica(filas).Motivo == MotivoReferencia.Tope) conTope++;

            var (r1, _) = Pasada(biblioteca, conGanancia: true);
            var (r2, segunda) = Pasada(biblioteca, conGanancia: true);
            var (r3, tercera) = Pasada(biblioteca, conGanancia: true);
            Assert.True(r1 == r2 && r2 == r3, $"caso {caso}: {r1} → {r2} → {r3}");
            Assert.True(segunda == 0 && tercera == 0, $"caso {caso}: la segunda pasada tocó {segunda} y la tercera {tercera}");
        }
        Assert.True(conTope >= 20, $"pocos casos con el tope activo ({conTope})");
    }

    // Añadir canciones nuevas mueve algo la referencia, pero ninguna de las ya ajustadas vuelve a
    // necesitar ajuste: quedaron a 0,75 dB o menos, y el umbral es 1.
    [Fact]
    public void Tras_anadir_canciones_las_ya_ajustadas_no_vuelven_a_tocarse()
    {
        var rnd = new Random(20261029);
        Cancion Nueva()
        {
            var muestra = -6 * rnd.NextDouble();
            return new Cancion { L = -11 + Normal(rnd), Muestra = muestra, Exceso = 0.3, P = muestra, T = muestra + 0.3 };
        }
        var biblioteca = Enumerable.Range(0, 5000).Select(_ => Nueva()).ToList();

        var (t1, primera) = Pasada(biblioteca, conGanancia: true);
        Assert.True(primera > 500, $"la prueba no ajusta casi nada ({primera})");
        var ajustadas = biblioteca.Where(c => c.Veces == 1).ToList();

        var nuevas = Enumerable.Range(0, 1000).Select(_ => Nueva()).ToList();
        var con = biblioteca.Concat(nuevas).ToList();
        var t3 = LoudnessViewModel.ReferenciaAutomatica(con.Select(c => Fila(c, -14, true)).ToList()).Referencia;
        Assert.True(Math.Abs(t3 - t1) <= 0.25, $"la referencia se movió {t3 - t1:0.00} dB");
        Assert.DoesNotContain(ajustadas, c => Fila(c, t3, true).Candidata);
    }
}
