using System.Globalization;
using System.Text.Json;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core.Analysis;
using NAudio.Wave;

namespace Etiquetador.Tests;

/// <summary>
/// P4 (tanda 4, equipo audio_medido): calidad real por espectro. Un «320 kbps» hecho de un 128
/// conserva el corte del 128 hacia 16 kHz; la página Calidad lo mide y lo enseña como SOSPECHA,
/// con la frecuencia medida. Aquí se prueba la medida con señales de corte conocido, la valoración
/// frente a lo declarado, los textos y la caché que sigue al archivo.
/// </summary>
public class CalidadRealEspectroTests
{
    // ---------- Señales de prueba ----------

    /// <summary>
    /// Tonos cada 50 Hz desde 150 Hz hasta <paramref name="hasta"/>, con fase al azar (semilla fija):
    /// un espectro plano que acaba de golpe donde se quiere, como el de un codificador.
    /// </summary>
    private static float[] Tonos(int sr, int canales, double segundos, double hasta, double amplitud = 0.004, bool contrafase = false)
    {
        var tramas = (int)(sr * segundos);
        var x = new double[tramas];
        var rnd = new Random(7);
        for (var f = 150.0; f <= hasta; f += 50)
        {
            // Fasor que gira: mucho más barato que un seno por muestra.
            var fase = rnd.NextDouble() * 2 * Math.PI;
            double re = Math.Cos(fase), im = Math.Sin(fase);
            double wr = Math.Cos(2 * Math.PI * f / sr), wi = Math.Sin(2 * Math.PI * f / sr);
            for (var n = 0; n < tramas; n++)
            {
                x[n] += amplitud * re;
                var nr = re * wr - im * wi;
                im = re * wi + im * wr;
                re = nr;
            }
        }
        var r = new float[tramas * canales];
        for (var n = 0; n < tramas; n++)
            for (var c = 0; c < canales; c++)
                r[n * canales + c] = (float)(contrafase && c == 1 ? -x[n] : x[n]);
        return r;
    }

    private static MedidaEspectro MedirSenal(float[] s, int sr, int canales)
    {
        var e = new EspectroMedio(sr, canales);
        e.Add(s, s.Length);
        return CorteEspectral.Medir(e);
    }

    // ---------- La medida ----------

    // El caso del hallazgo: el audio acaba hacia 16 kHz, como el de un MP3 de 128.
    [Fact]
    public void Un_audio_que_acaba_en_16_kHz_da_un_corte_hacia_16_kHz()
    {
        var m = MedirSenal(Tonos(44100, 2, 3, 16000), 44100, 2);

        Assert.Equal(EstadoCorte.Corte, m.Estado);
        Assert.InRange(m.CorteHz, 15700, 16300);
    }

    // La escala de los bins depende de la frecuencia de muestreo: a 48 kHz el corte sale en el mismo sitio.
    [Fact]
    public void A_48_kHz_el_corte_sale_en_la_misma_frecuencia()
    {
        var m = MedirSenal(Tonos(48000, 2, 3, 16000), 48000, 2);

        Assert.Equal(EstadoCorte.Corte, m.Estado);
        Assert.InRange(m.CorteHz, 15700, 16300);
    }

    // Un audio que llega hasta arriba no tiene corte, y el techo es lo que permite el muestreo.
    [Fact]
    public void Un_audio_que_llega_hasta_arriba_no_tiene_corte()
    {
        var m = MedirSenal(Tonos(44100, 2, 3, 21900), 44100, 2);

        Assert.Equal(EstadoCorte.SinCorte, m.Estado);
        Assert.InRange(m.TechoHz, 21500, 22050);
        Assert.Equal(m.TechoHz, m.AnchoReal);
    }

    // Un corte de 320 (hacia 20 kHz) también se mide, y no es el de 16.
    [Fact]
    public void Un_corte_hacia_20_kHz_se_mide_donde_esta()
    {
        var m = MedirSenal(Tonos(44100, 2, 3, 19900), 44100, 2);

        Assert.Equal(EstadoCorte.Corte, m.Estado);
        Assert.InRange(m.CorteHz, 19600, 20200);
    }

    // Lo que suena en contrafase entre los dos canales se anularía al mezclar a mono: cada canal se
    // analiza por separado, y unos agudos así no pueden parecer un corte.
    [Fact]
    public void Los_agudos_en_contrafase_no_se_anulan()
    {
        var m = MedirSenal(Tonos(44100, 2, 3, 21900, contrafase: true), 44100, 2);

        Assert.Equal(EstadoCorte.SinCorte, m.Estado);
    }

    [Fact]
    public void El_silencio_y_lo_cortisimo_no_dan_medida()
    {
        Assert.Equal(EstadoCorte.SinDatos, MedirSenal(new float[44100 * 2 * 3], 44100, 2).Estado);
        Assert.Equal(EstadoCorte.SinDatos, MedirSenal(Tonos(44100, 2, 0.2, 16000), 44100, 2).Estado);
        Assert.Null(new EspectroMedio(44100, 2).Potencia());
    }

    // Las bandas de 100 Hz se colocan en hercios, sea cual sea el muestreo: un tono de 16.050 Hz cae
    // en la banda 160 (16.000-16.100 Hz) a 44,1 y a 48 kHz.
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Un_tono_cae_en_su_banda_de_100_Hz(int sr)
    {
        var tramas = sr;
        var s = new float[tramas];
        for (var n = 0; n < tramas; n++) s[n] = (float)(0.5 * Math.Sin(2 * Math.PI * 16050 * n / sr));
        var e = new EspectroMedio(sr, 1);
        e.Add(s, s.Length);

        var bandas = CorteEspectral.Bandas(e.Potencia()!, sr);

        Assert.Equal(160, Array.IndexOf(bandas, bandas.Max()));
        Assert.Equal((int)(Math.Min(sr / 2.0, CorteEspectral.TechoAnalisis) / CorteEspectral.AnchoBanda), bandas.Length);
    }

    // ---------- La medida, sobre espectros ya resumidos ----------

    /// <summary>Bandas de 100 Hz hasta 22 kHz: <paramref name="nivel"/> hasta <paramref name="corte"/> y −100 dB encima.</summary>
    private static double[] Escalon(double corte, double nivel = -10, double encima = -100, int n = 220)
    {
        var b = new double[n];
        for (var j = 0; j < n; j++) b[j] = j * CorteEspectral.AnchoBanda < corte ? nivel : encima;
        return b;
    }

    // Un descenso natural de los agudos (un máster suave) baja poco a poco y no es un muro.
    [Fact]
    public void Un_descenso_gradual_no_es_un_corte()
    {
        var b = new double[220];
        for (var j = 0; j < b.Length; j++) b[j] = -10 - Math.Max(0, j - 50) * 0.35;   // −60 dB a 22 kHz, sin escalón

        Assert.Equal(EstadoCorte.SinCorte, CorteEspectral.MedirBandas(b).Estado);
    }

    // Un «320» hecho de un 128 tiene dos muros: el del 128 y, más arriba, el del 320 al recodificar,
    // con un resto de ruido entre medias. El que dice de dónde viene el audio es el primero.
    [Fact]
    public void Con_dos_muros_cuenta_el_mas_bajo()
    {
        var b = Escalon(16000);
        for (var j = 160; j < 205; j++) b[j] = -55;   // ruido del segundo codificador hasta 20,5 kHz

        var m = CorteEspectral.MedirBandas(b);

        Assert.Equal(EstadoCorte.Corte, m.Estado);
        Assert.InRange(m.CorteHz, 15800, 16200);
    }

    // Un pitido aislado por encima del corte (un efecto de 19 kHz) no es contenido.
    [Fact]
    public void Un_pico_aislado_por_encima_no_tapa_el_corte()
    {
        var b = Escalon(16000);
        b[190] = -10;

        Assert.Equal(EstadoCorte.Corte, CorteEspectral.MedirBandas(b).Estado);
    }

    // Agudos muy débiles: el codificador los quita a cualquier tasa, así que un corte bajo no dice
    // nada del origen. Uno alto sí: al menos el audio llegó hasta ahí.
    [Fact]
    public void Con_agudos_debiles_un_corte_bajo_no_es_concluyente()
    {
        // Los agudos bajan poco a poco (sin escalón) hasta −50 dB en el corte, 40 dB por debajo del medio.
        Assert.Equal(EstadoCorte.NoConcluyente, CorteEspectral.MedirBandas(Rampa(8000, 16000)).Estado);

        var alto = CorteEspectral.MedirBandas(Rampa(12000, 20000));
        Assert.Equal(EstadoCorte.Corte, alto.Estado);
        Assert.InRange(alto.CorteHz, 19700, 20200);
    }

    /// <summary>−10 dB hasta <paramref name="desde"/>, bajada lineal hasta −50 dB en <paramref name="corte"/> y −100 dB encima.</summary>
    private static double[] Rampa(double desde, double corte)
    {
        var b = new double[220];
        for (var j = 0; j < b.Length; j++)
        {
            var f = j * CorteEspectral.AnchoBanda;
            b[j] = f < desde ? -10 : f < corte ? -10 - 40 * (f - desde) / (corte - desde) : -100;
        }
        return b;
    }

    [Fact]
    public void Sin_bandas_suficientes_o_en_silencio_no_hay_medida()
    {
        Assert.Equal(EstadoCorte.SinDatos, CorteEspectral.MedirBandas(Escalon(16000, n: 80)).Estado);
        Assert.Equal(EstadoCorte.SinDatos, CorteEspectral.MedirBandas(Escalon(16000, nivel: -100)).Estado);
    }

    // Tres tramos repartidos (al 25, 50 y 75 %) y no uno: un puente filtrado de 30 s parecería un corte.
    [Fact]
    public void Se_analizan_tres_tramos_repartidos_o_la_cancion_entera_si_es_corta()
    {
        var t = CorteEspectral.Tramos(300);
        Assert.Equal(new[] { 70.0, 145.0, 220.0 }, t.Select(x => x.Inicio));
        Assert.All(t, x => Assert.Equal(CorteEspectral.SegundosTramo, x.Segundos));

        Assert.Equal(new[] { (0.0, 30.0) }, CorteEspectral.Tramos(30));
        Assert.Single(CorteEspectral.Tramos(0));
    }

    // ---------- Lo que se dice: siempre una sospecha, con la frecuencia a la vista ----------

    private static MedidaEspectro Corte(double hz) => new(EstadoCorte.Corte, hz, 21800);
    private static readonly MedidaEspectro SinCorte = new(EstadoCorte.SinCorte, 0, 21800);

    [Theory]
    // Sin pérdida: con el corte de un 128 es sospechoso; con el de un 320, dudoso.
    [InlineData(true, 0, 16000, ValoracionCalidad.Sospechosa)]
    [InlineData(true, 0, 19900, ValoracionCalidad.Dudosa)]
    [InlineData(true, 0, 21000, ValoracionCalidad.Coherente)]
    // 320 declarados: el corte de un 128 es sospechoso; el de un 192, dudoso (el codificador de
    // Windows corta ahí a veces); el de un 320, coherente.
    [InlineData(false, 320, 16000, ValoracionCalidad.Sospechosa)]
    [InlineData(false, 320, 18600, ValoracionCalidad.Dudosa)]
    [InlineData(false, 320, 19900, ValoracionCalidad.Coherente)]
    [InlineData(false, 256, 16000, ValoracionCalidad.Sospechosa)]
    [InlineData(false, 256, 18600, ValoracionCalidad.Coherente)]
    // 192: un corte de 128 solo es dudoso; un 128 que corta en 16 kHz es lo esperado.
    [InlineData(false, 192, 16000, ValoracionCalidad.Dudosa)]
    [InlineData(false, 192, 17100, ValoracionCalidad.Dudosa)]
    [InlineData(false, 192, 18600, ValoracionCalidad.Coherente)]
    [InlineData(false, 128, 16000, ValoracionCalidad.Coherente)]
    // 160: un LAME de 160 legítimo acaba hacia 17,1 kHz con este método y es coherente; con el corte
    // de un 128 (hasta 16,8 kHz) sí es dudoso.
    [InlineData(false, 160, 17100, ValoracionCalidad.Coherente)]
    [InlineData(false, 160, 16600, ValoracionCalidad.Dudosa)]
    public void La_valoracion_compara_el_corte_con_lo_declarado(bool sinPerdida, int kbps, double corte, ValoracionCalidad esperada)
        => Assert.Equal(esperada, CorteEspectral.Valorar(Corte(corte), sinPerdida, kbps));

    [Fact]
    public void Sin_corte_es_coherente_y_sin_medida_no_se_valora()
    {
        Assert.Equal(ValoracionCalidad.Coherente, CorteEspectral.Valorar(SinCorte, sinPerdida: true, 0));
        Assert.Equal(ValoracionCalidad.Coherente, CorteEspectral.Valorar(SinCorte, sinPerdida: false, 320));
        Assert.Equal(ValoracionCalidad.SinValorar, CorteEspectral.Valorar(new(EstadoCorte.NoConcluyente, 0, 21800), false, 320));
        Assert.Equal(ValoracionCalidad.SinValorar, CorteEspectral.Valorar(default, false, 320));
    }

    // A 32 kHz el techo de lo medido es 15,8 kHz, y a 22,05 kHz, 10,8: «sin corte» no dice nada del
    // origen. Antes la tabla decía a la vez «Sin corte» y «≈ 128 kbps o menos · Sospechoso».
    [Fact]
    public void Sin_corte_con_un_muestreo_bajo_no_se_valora_y_lo_explica()
    {
        var a32 = new MedidaEspectro(EstadoCorte.SinCorte, 0, 15800);
        var a22 = new MedidaEspectro(EstadoCorte.SinCorte, 0, 10800);

        Assert.Equal(ValoracionCalidad.SinValorar, CorteEspectral.Valorar(a32, sinPerdida: false, 320));
        Assert.Equal(ValoracionCalidad.SinValorar, CorteEspectral.Valorar(a22, sinPerdida: true, 0));
        Assert.Equal("Sin corte", CorteEspectral.TextoCorte(a32));
        Assert.Equal("Limitado por el muestreo", CorteEspectral.TextoCalidadReal(a32, sinPerdida: false));

        var fila = new QualRow { FilePath = @"C:\m\a.wav", Bitrate = 1411, SinPerdida = true };
        fila.PonerMedida(a22);
        Assert.Equal("Limitado por el muestreo", fila.CalidadReal);
        Assert.Equal(ValoracionCalidad.SinValorar, fila.Valoracion);

        // A 44,1 o 48 kHz, sin corte sigue siendo coherente.
        Assert.Equal(ValoracionCalidad.Coherente, CorteEspectral.Valorar(SinCorte, sinPerdida: false, 320));
    }

    // La frecuencia, con coma decimal, aunque el sistema esté en inglés.
    [Fact]
    public void El_corte_se_escribe_en_kHz_con_coma_decimal()
    {
        var antes = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("16,1 kHz", CorteEspectral.TextoCorte(Corte(16100)));
        }
        finally { CultureInfo.CurrentCulture = antes; }
        Assert.Equal("Sin corte", CorteEspectral.TextoCorte(SinCorte));
        Assert.Equal("No concluyente", CorteEspectral.TextoCorte(new(EstadoCorte.NoConcluyente, 0, 21800)));
    }

    [Theory]
    [InlineData(16000, false, "≈ 128 kbps o menos")]
    [InlineData(16800, false, "≈ 128 kbps o menos")]
    [InlineData(17100, false, "≈ 160 kbps")]   // un MP3 de 160 legítimo (antes, «≈ 128 kbps o menos»)
    [InlineData(18600, false, "≈ 192 kbps")]
    [InlineData(19200, false, "≈ 256 kbps")]
    [InlineData(19900, false, "≈ 320 kbps")]
    [InlineData(19900, true, "≈ 320 kbps")]
    [InlineData(21000, true, "Sin pérdida")]
    public void La_calidad_estimada_nombra_la_tasa_que_corta_ahi(double corte, bool sinPerdida, string esperado)
        => Assert.Equal(esperado, CorteEspectral.TextoCalidadReal(Corte(corte), sinPerdida));

    [Fact]
    public void Sin_corte_la_estimacion_depende_del_formato()
    {
        Assert.Equal("Sin pérdida", CorteEspectral.TextoCalidadReal(SinCorte, sinPerdida: true));
        Assert.Equal("≈ 320 kbps o más", CorteEspectral.TextoCalidadReal(SinCorte, sinPerdida: false));
        Assert.Equal("", CorteEspectral.TextoCalidadReal(new(EstadoCorte.NoConcluyente, 0, 21800), false));
    }

    // ---------- La fila de la tabla ----------

    [Fact]
    public void La_fila_ensena_el_corte_y_la_sospecha_y_se_puede_vaciar()
    {
        var fila = new QualRow { FilePath = @"C:\m\a.mp3", Bitrate = 320 };

        fila.PonerMedida(Corte(16000));
        Assert.Equal("16,0 kHz", fila.CorteMedido);
        Assert.Equal("≈ 128 kbps o menos · Sospechoso", fila.CalidadReal);
        Assert.Equal(ValoracionCalidad.Sospechosa, fila.Valoracion);
        Assert.Equal(16000, fila.CorteOrden);

        fila.PonerMedida(null);
        Assert.Equal("", fila.CorteMedido);
        Assert.Equal("", fila.CalidadReal);
        Assert.Equal(ValoracionCalidad.SinValorar, fila.Valoracion);
        Assert.True(fila.CorteOrden < 0);
    }

    // Ordenar por «Corte medido»: sin corte va por encima de cualquier corte; lo no concluyente, abajo.
    [Fact]
    public void Al_ordenar_por_corte_sin_corte_va_arriba_y_no_concluyente_abajo()
    {
        QualRow Con(MedidaEspectro m) { var f = new QualRow { Bitrate = 320 }; f.PonerMedida(m); return f; }

        var sin = Con(SinCorte);
        var alto = Con(Corte(21700));
        var nc = Con(new(EstadoCorte.NoConcluyente, 0, 21800));
        var nada = new QualRow();

        Assert.True(sin.CorteOrden > alto.CorteOrden);
        Assert.True(alto.CorteOrden > nc.CorteOrden);
        Assert.True(nc.CorteOrden > nada.CorteOrden);
    }

    [Fact]
    public void El_resumen_concuerda_en_numero()
    {
        Assert.Equal("Calidad real: 1 archivo analizado · 1 sospechoso · 1 dudoso · 1 sin analizar o ilegible.",
            QualityViewModel.TextoResumenEspectro(1, 1, 1, 1, 0));
        Assert.Equal("Calidad real: 12 archivos analizados · 0 sospechosos · 3 dudosos.",
            QualityViewModel.TextoResumenEspectro(12, 0, 3, 0, 0));
    }

    // Fuera de Windows solo se decodifican MP3, WAV y AIFF: el resto se dice aparte, no como ilegible.
    [Fact]
    public void Lo_que_este_sistema_no_decodifica_se_dice_aparte()
    {
        Assert.EndsWith("En este sistema solo se analizan archivos MP3, WAV y AIFF: 1 archivo de otro formato queda sin analizar.",
            QualityViewModel.TextoResumenEspectro(5, 0, 0, 0, 1));
        Assert.EndsWith("4 archivos de otros formatos quedan sin analizar.",
            QualityViewModel.TextoResumenEspectro(5, 0, 0, 2, 4));
        Assert.Contains("2 sin analizar o ilegibles", QualityViewModel.TextoResumenEspectro(5, 0, 0, 2, 4));
    }

    [Theory]
    [InlineData("a.mp3", false, true)]
    [InlineData("a.WAV", false, true)]
    [InlineData("a.aif", false, true)]
    [InlineData("a.aiff", false, true)]
    [InlineData("a.flac", false, false)]
    [InlineData("a.m4a", false, false)]
    [InlineData("a.flac", true, true)]
    [InlineData("a.m4a", true, true)]
    public void Fuera_de_Windows_solo_se_analizan_mp3_wav_y_aiff(string ruta, bool windows, bool esperado)
        => Assert.Equal(esperado, EspectroScanner.Analizable(ruta, windows));

    // ---------- La caché: por fecha y tamaño, y sigue al archivo ----------

    private static void EscribirWav(string ruta, double hasta, double segundos = 3, int sr = 44100)
    {
        var s = Tonos(sr, 2, segundos, hasta);
        using var w = new WaveFileWriter(ruta, WaveFormat.CreateIeeeFloatWaveFormat(sr, 2));
        w.WriteSamples(s, 0, s.Length);
    }

    [Fact]
    public void Mide_un_archivo_lo_guarda_y_no_lo_vuelve_a_leer_si_no_cambia()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var wav = Path.Combine(dir, "falso.wav");
            EscribirWav(wav, 16000);
            var cache = Path.Combine(dir, EspectroScanner.NombreCache);

            var s = new EspectroScanner(cache);
            var m = s.Measure(wav);

            Assert.NotNull(m);
            Assert.Equal(EstadoCorte.Corte, m!.Value.Estado);
            Assert.InRange(m.Value.CorteHz, 15700, 16300);
            Assert.Equal(m, s.Get(wav));
            s.Save();

            // Otra sesión: la medida está guardada y vale mientras el archivo no cambie.
            var otra = new EspectroScanner(cache);
            Assert.Equal(m, otra.Get(wav));

            EscribirWav(wav, 21900, segundos: 4);
            Assert.Null(otra.Get(wav));
            Assert.Equal(EstadoCorte.SinCorte, otra.Measure(wav)!.Value.Estado);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // El silencio no da medida y no se guarda: se volverá a intentar, no se queda «sin datos» para siempre.
    [Fact]
    public void Lo_que_no_da_medida_no_se_guarda()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var wav = Path.Combine(dir, "silencio.wav");
            using (var w = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)))
                w.WriteSamples(new float[44100 * 2 * 2], 0, 44100 * 2 * 2);
            var s = new EspectroScanner(Path.Combine(dir, EspectroScanner.NombreCache));

            Assert.Equal(EstadoCorte.SinDatos, s.Measure(wav)!.Value.Estado);
            Assert.Equal(0, s.Count);
            Assert.Null(s.Measure(Path.Combine(dir, "no-existe.wav")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Mide_varios_en_paralelo_con_avance_y_lo_deja_guardado()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var rutas = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var r = Path.Combine(dir, $"t{i}.wav");
                EscribirWav(r, i == 0 ? 16000 : 21900, segundos: 2);
                rutas.Add(r);
            }
            var cache = Path.Combine(dir, EspectroScanner.NombreCache);
            var avances = new List<int>();
            var s = new EspectroScanner(cache);

            await s.MeasureManyAsync(rutas, force: false, new SyncProgress(p => { lock (avances) avances.Add(p.done); }));

            Assert.Contains(3, avances);
            var otra = new EspectroScanner(cache);
            Assert.Equal(EstadoCorte.Corte, otra.Get(rutas[0])!.Value.Estado);
            Assert.Equal(EstadoCorte.SinCorte, otra.Get(rutas[1])!.Value.Estado);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Cancelar_deja_guardado_lo_hecho()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var wav = Path.Combine(dir, "a.wav");
            EscribirWav(wav, 16000, segundos: 2);
            var cache = Path.Combine(dir, EspectroScanner.NombreCache);
            var s = new EspectroScanner(cache);
            s.Measure(wav);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => s.MeasureManyAsync(new[] { wav, wav + "2" }, false, null, cts.Token));

            Assert.NotNull(new EspectroScanner(cache).Get(wav));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Renombrar, mover o escribir etiquetas no cambia el audio: la medida acompaña al archivo en vez
    // de caducar por la fecha o perderse con la ruta, como en las demás cachés de audio.
    [Fact]
    public void La_medida_sigue_al_archivo_renombrado_y_retocado()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var a = Path.Combine(dir, "a.wav");
            EscribirWav(a, 16000, segundos: 2);
            var s = new EspectroScanner(Path.Combine(dir, EspectroScanner.NombreCache));
            var medida = s.Measure(a);

            // Se renombra y se le escriben etiquetas (aquí, unos bytes al final: cambian fecha y tamaño).
            var antes = SelloArchivo.De(a);
            var b = Path.Combine(dir, "b.wav");
            File.Move(a, b);
            File.AppendAllText(b, "etiquetas");
            File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(1));

            s.Seguir(a, b, antes);

            Assert.Null(s.Get(a));
            Assert.Equal(medida, s.Get(b));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Solo se pone al día lo que valía justo antes de escribir: una medida ya caducada no revive.
    [Fact]
    public void Una_medida_caducada_no_revive_al_retocar()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var a = Path.Combine(dir, "a.wav");
            EscribirWav(a, 16000, segundos: 2);
            var s = new EspectroScanner(Path.Combine(dir, EspectroScanner.NombreCache));
            s.Measure(a);

            // El archivo cambió por fuera (otro programa) y después lo retoca la aplicación.
            EscribirWav(a, 21900, segundos: 3);
            var antes = SelloArchivo.De(a);
            File.AppendAllText(a, "etiquetas");

            Assert.False(s.Retocar(a, antes.M, antes.S));
            Assert.Null(s.Get(a));
            Assert.False(s.Mover(a, a));
            Assert.False(s.Mover(Path.Combine(dir, "otro.wav"), Path.Combine(dir, "x.wav")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Un espectro.json dañado se aparta en vez de leerse como vacío y pisarse en el siguiente guardado.
    [Fact]
    public void Una_cache_danada_se_aparta_y_no_se_pisa()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var wav = Path.Combine(dir, "a.wav");
            EscribirWav(wav, 16000, segundos: 2);
            var cache = Path.Combine(dir, EspectroScanner.NombreCache);
            var entero = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [wav] = new { M = 1L, S = 2L, E = 2, C = 16000, T = 21800, V = EspectroScanner.VersionMetodo },
            });
            File.WriteAllText(cache, entero[..(entero.Length / 2)]);

            var s = new EspectroScanner(cache);
            Assert.Equal(0, s.Count);
            s.Measure(wav);
            s.Save();

            var apartado = Assert.Single(Directory.GetFiles(dir, EspectroScanner.NombreCache + ".ilegible_*"));
            Assert.Equal(entero[..(entero.Length / 2)], File.ReadAllText(apartado));
            Assert.NotNull(new EspectroScanner(cache).Get(wav));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Si cambia la forma de medir, las medidas antiguas dejan de valer y se vuelven a hacer.
    [Fact]
    public void Una_medida_de_otra_version_del_metodo_no_vale()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var wav = Path.Combine(dir, "a.wav");
            EscribirWav(wav, 16000, segundos: 2);
            var sello = SelloArchivo.De(wav);
            var cache = Path.Combine(dir, EspectroScanner.NombreCache);
            File.WriteAllText(cache, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [wav] = new { M = sello.M, S = sello.S, E = 2, C = 16000, T = 21800, V = EspectroScanner.VersionMetodo + 1 },
            }));

            Assert.Null(new EspectroScanner(cache).Get(wav));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>Progress&lt;T&gt; publica en el contexto de sincronización; en las pruebas conviene recibirlo en el acto.</summary>
    private sealed class SyncProgress(Action<(int done, int total, string file)> accion) : IProgress<(int done, int total, string file)>
    {
        public void Report((int done, int total, string file) value) => accion(value);
    }
}
