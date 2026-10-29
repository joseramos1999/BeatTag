using System.Text.Json;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core.Analysis;

namespace Etiquetador.Tests;

/// <summary>
/// Pestaña Volumen.
///
/// A4: al subir con tope por pico, el paso se redondeaba al más cercano y podía pasarse del tope
/// hasta 0,75 dB, más que los 0,5 dB de margen: el pico acababa por encima de 0 dBFS.
///
/// A11: sonoridad.json se escribía directamente encima. Un corte a media escritura lo dejaba
/// truncado, el arranque lo leía como vacío sin decir nada y el siguiente guardado lo pisaba: había
/// que volver a medir la biblioteca entera.
/// </summary>
public class VolumenTopeYCacheTests
{
    // ---------- A4: pasos con tope por pico ----------

    // El caso del hallazgo: −16 LUFS con objetivo −11 (+5 dB) y pico de −2,8 dBFS. Caben 2,3 dB;
    // antes salían 2 pasos (+3,0 dB) y el pico quedaba en +0,2 dBFS.
    [Fact]
    public void Con_el_pico_a_menos_2_8_y_5_dB_deseados_se_aplica_un_paso()
    {
        var pasos = LoudnessViewModel.PasosAplicables(ganancia: 5.0, picoDb: -2.8);

        Assert.Equal(1, pasos);
        Assert.True(-2.8 + pasos * Mp3Gain.DbPerStep <= -LoudnessViewModel.MargenSeguridad);
    }

    // También cuando lo deseado CABE: +2,3 dB con pico de −2,9 dejan 2,4 dB de hueco, pero el paso
    // más cercano (+3,0 dB) no cabe.
    [Fact]
    public void El_tope_se_respeta_aunque_la_ganancia_deseada_quepa()
        => Assert.Equal(1, LoudnessViewModel.PasosAplicables(ganancia: 2.3, picoDb: -2.9));

    // Al subir, nunca se pasa del margen de seguridad, sea cual sea la combinación.
    [Fact]
    public void Ninguna_subida_deja_el_pico_por_encima_del_margen()
    {
        for (var g = 0.1; g <= 12; g += 0.05)
            for (var pico = -15.0; pico <= 0.5; pico += 0.05)
            {
                var pasos = LoudnessViewModel.PasosAplicables(g, pico);
                Assert.InRange(pasos, 0, int.MaxValue);
                if (pasos > 0)
                    Assert.True(pico + pasos * Mp3Gain.DbPerStep <= -LoudnessViewModel.MargenSeguridad + 1e-9,
                        $"ganancia {g:0.00}, pico {pico:0.00}: {pasos} pasos lo dejan en {pico + pasos * Mp3Gain.DbPerStep:0.00} dBFS");
            }
    }

    // Con margen de sobra no cambia nada: sigue redondeando al paso más cercano.
    [Theory]
    [InlineData(5.0, -12.0, 3)]    // 5 / 1,5 = 3,33 -> 3
    [InlineData(2.3, -10.0, 2)]    // 1,53 -> 2: el pico queda en −7
    [InlineData(4.4, -9.0, 3)]     // 2,93 -> 3
    public void Con_margen_de_sobra_se_redondea_al_mas_cercano(double ganancia, double pico, int esperados)
        => Assert.Equal(esperados, LoudnessViewModel.PasosAplicables(ganancia, pico));

    // Bajar siempre cabe y no depende del pico.
    [Theory]
    [InlineData(-4.4, -0.1, -3)]
    [InlineData(-2.3, 0.0, -2)]
    public void Bajar_redondea_al_mas_cercano(double ganancia, double pico, int esperados)
        => Assert.Equal(esperados, LoudnessViewModel.PasosAplicables(ganancia, pico));

    // Sin hueco ninguno, no se sube.
    [Fact]
    public void Sin_margen_no_se_sube_nada()
        => Assert.Equal(0, LoudnessViewModel.PasosAplicables(ganancia: 6, picoDb: -0.3));

    // El aviso previo y el resumen final cuentan las mismas subidas parciales.
    [Fact]
    public void Una_subida_recortada_por_el_pico_cuenta_como_parcial()
    {
        var fila = new LoudnessRow { FilePath = @"C:\m\a.mp3", Lufs = -16, PeakDb = -2.8, Target = -11 };
        Assert.True(LoudnessViewModel.AumentoParcial(fila));

        var holgada = new LoudnessRow { FilePath = @"C:\m\b.mp3", Lufs = -16, PeakDb = -12, Target = -11 };
        Assert.False(LoudnessViewModel.AumentoParcial(holgada));

        var bajada = new LoudnessRow { FilePath = @"C:\m\c.mp3", Lufs = -6, PeakDb = 0, Target = -11 };
        Assert.False(LoudnessViewModel.AumentoParcial(bajada));
    }

    // ---------- V3: las subidas se limitan con el pico REAL ----------

    // El pico de muestra se queda corto (hasta 2 dB medidos en una biblioteca real). Con −3,0 de
    // muestra y −1,4 de pico real, subir un paso dejaría la onda en +0,1 dBTP.
    [Fact]
    public void Con_el_pico_real_a_menos_1_4_no_se_sube_aunque_el_de_muestra_lo_permitiera()
    {
        Assert.Equal(1, LoudnessViewModel.PasosAplicables(5.0, -3.0));
        var fila = new LoudnessRow { FilePath = @"C:\m\a.mp3", Lufs = -16, PeakDb = -3.0, TruePeakDb = -1.4, Target = -11 };
        Assert.Equal(0, LoudnessViewModel.PasosAplicables(fila.Gain, fila.PicoDecision));
    }

    [Fact]
    public void Ninguna_subida_deja_el_pico_real_por_encima_de_menos_0_5_dBTP()
    {
        for (var g = 0.1; g <= 12; g += 0.1)
            for (var muestra = -15.0; muestra <= 0; muestra += 0.1)
                foreach (var exceso in new[] { 0.0, 0.23, 1.0, 2.0 })
                {
                    var fila = new LoudnessRow { FilePath = @"C:\m\a.mp3", Lufs = -11 - g, PeakDb = muestra, TruePeakDb = muestra + exceso, Target = -11 };
                    var pasos = LoudnessViewModel.PasosAplicables(fila.Gain, fila.PicoDecision);
                    if (pasos > 0)
                        Assert.True(muestra + exceso + pasos * Mp3Gain.DbPerStep <= -LoudnessViewModel.MargenSeguridad + 1e-9,
                            $"ganancia {g:0.0}, pico real {muestra + exceso:0.00}: {pasos} pasos");
                }
    }

    /// <summary>Un doble del escáner: cuenta las veces que se le pide medir y devuelve lo que se le diga.</summary>
    private sealed class EscanerDoble(LoudnessResult? resultado)
    {
        public List<string> Medidas { get; } = new();
        public LoudnessResult? Medir(string ruta) { Medidas.Add(ruta); return resultado; }
    }

    /// <summary>La medida guardada sigue valiendo y es la de la fila: el archivo no ha cambiado desde el análisis.</summary>
    private static Func<string, LoudnessResult?> ComoLaFila(LoudnessRow f)
        => _ => new LoudnessResult(f.Lufs, f.PeakDb, 0, f.TruePeakDb, f.GananciaAplicada);

    // La última barrera antes de escribir: una subida sin pico real conocido lo mide antes, y los pasos
    // salen de él.
    [Fact]
    public void Una_subida_sin_pico_real_no_se_aplica_sin_medirlo_antes()
    {
        var fila = new LoudnessRow { FilePath = @"C:\m\a.mp3", Lufs = -16, PeakDb = -6.0, Target = -11 };
        Assert.Equal(3, LoudnessViewModel.PasosAplicables(fila.Gain, fila.PicoDecision));   // con el de muestra, 3

        var escaner = new EscanerDoble(new LoudnessResult(-16, -6.0, 200, TruePeakDb: -3.4));
        var d = LoudnessViewModel.Decidir(fila, ComoLaFila(fila), escaner.Medir);

        Assert.Equal(new[] { fila.FilePath }, escaner.Medidas);
        Assert.Equal(1, d.Pasos);                 // caben 2,9 dB: un paso
        Assert.Equal(-3.4, d.PicoReal);
        Assert.Equal(-6.0, d.PicoMuestra);
        Assert.True(d.Medida);
        Assert.True(LoudnessViewModel.EsParcial(fila.Gain, d.Pasos));
    }

    [Fact]
    public void Si_el_pico_real_no_se_puede_medir_no_se_sube()
    {
        var fila = new LoudnessRow { FilePath = @"C:\m\a.mp3", Lufs = -16, PeakDb = -6.0, Target = -11 };
        var sinMedida = LoudnessViewModel.Decidir(fila, ComoLaFila(fila), new EscanerDoble(null).Medir);
        Assert.Equal(0, sinMedida.Pasos);
        Assert.True(sinMedida.NoSePudoMedir);   // no es falta de margen: no se pudo leer
        Assert.Equal(0, LoudnessViewModel.Decidir(fila, ComoLaFila(fila), new EscanerDoble(new LoudnessResult(-16, -6, 200)).Medir).Pasos);
    }

    // Bajar, o subir con el pico real ya conocido, no decodifica nada.
    [Fact]
    public void Bajar_o_subir_con_pico_real_conocido_no_vuelve_a_medir()
    {
        var escaner = new EscanerDoble(null);
        var baja = new LoudnessRow { FilePath = @"C:\m\a.mp3", Lufs = -7, PeakDb = 0, Target = -11 };
        var conReal = new LoudnessRow { FilePath = @"C:\m\b.mp3", Lufs = -14, PeakDb = -8, TruePeakDb = -7, Target = -11 };

        Assert.Equal(-3, LoudnessViewModel.Decidir(baja, ComoLaFila(baja), escaner.Medir).Pasos);
        Assert.Equal(2, LoudnessViewModel.Decidir(conReal, ComoLaFila(conReal), escaner.Medir).Pasos);
        Assert.Empty(escaner.Medidas);
    }

    // ---------- Revisión final: la barrera decide con la medida vigente del archivo, no con la fila ----------

    // El caso de la revisión: un MP3 a −10,0 LUFS y −0,3 dBTP se bajó 2 pasos (la fila quedó en −13,0 y
    // −3,3), «Deshacer última» lo devolvió a −10,0 y se eligió «Nivel de club» sin volver a analizar. Con
    // la fila se subía 1 paso y el archivo acababa a +1,2 dBTP. Con la medida de ahora está a 1 dB de −11.
    [Fact]
    public void Si_el_archivo_cambio_desde_el_analisis_se_decide_con_su_medida_de_ahora()
    {
        var fila = new LoudnessRow { FilePath = @"C:\m\a.mp3", Lufs = -13.0, PeakDb = -3.5, TruePeakDb = -3.3, GananciaAplicada = -3, Target = -11 };
        Assert.Equal(1, LoudnessViewModel.PasosAplicables(fila.Gain, fila.PicoDecision));   // con la fila, +1 paso

        // La guardada ya no vale (el deshacer cambió la fecha): se mide.
        var escaner = new EscanerDoble(new LoudnessResult(-10.0, -0.5, 200, TruePeakDb: -0.3));
        var d = LoudnessViewModel.Decidir(fila, _ => null, escaner.Medir);
        Assert.Equal(new[] { fila.FilePath }, escaner.Medidas);
        Assert.Equal(0, d.Pasos);
        Assert.Equal(-10.0, d.Lufs);
        Assert.False(d.NoSePudoMedir);

        // La guardada vale, pero es otra que la de la fila (el deshacer la puso al día): manda la guardada.
        var otra = new EscanerDoble(null);
        var conGuardada = LoudnessViewModel.Decidir(fila, _ => new LoudnessResult(-10.0, -0.5, 0, -0.3, 0), otra.Medir);
        Assert.Equal(0, conGuardada.Pasos);
        Assert.Empty(otra.Medidas);

        // Si ha bajado de nivel, se ajusta con lo que tiene ahora y con el pico real de ahora.
        var bajo = LoudnessViewModel.Decidir(fila, _ => new LoudnessResult(-16.0, -6.0, 0, -4.4, 0), otra.Medir);
        Assert.Equal(2, bajo.Pasos);   // caben 3,9 dB: dos pasos de los tres deseados
        Assert.Equal(-16.0, bajo.Lufs);

        // Si no se puede medir (movido, renombrado), no se toca.
        var perdido = LoudnessViewModel.Decidir(fila, _ => null, new EscanerDoble(null).Medir);
        Assert.Equal(0, perdido.Pasos);
        Assert.True(perdido.NoSePudoMedir);
    }

    // Solo se miden de nuevo las que se aumentarían y no tienen pico real: ni las que bajan, ni las que
    // están en nivel, ni las excluidas, ni las que no son MP3.
    [Fact]
    public void Solo_falta_el_pico_real_de_las_que_se_aumentarian()
    {
        LoudnessRow F(string n, double lufs, double pico, double? real = null) =>
            new() { FilePath = @"C:\m\" + n, Lufs = lufs, PeakDb = pico, TruePeakDb = real, Target = -11 };
        var sube = F("sube.mp3", -14, -6);
        var excluida = F("excluida.mp3", -14, -6);
        excluida.Exclusion = ExclusionVolumen.Carpeta;
        var filas = new[]
        {
            sube, excluida,
            F("ya.mp3", -14, -6, -5.5),        // ya tiene pico real
            F("baja.mp3", -8, -1),             // baja
            F("bien.mp3", -11.5, -6),          // en nivel
            F("sinmargen.mp3", -14, -1.0),     // no sube ni con el de muestra
            F("flac.flac", -14, -6),           // no se ajusta
        };
        Assert.Equal(new[] { sube }, LoudnessViewModel.SinPicoReal(filas));
    }

    // Tras ajustar se guarda la medida sin volver a decodificar. Si el pico estaba recortado por el
    // decodificador (en el tope), su valor de verdad se desconoce: se guarda en el tope y sin pico real,
    // de modo que no se vuelva a subir sin medirlo.
    [Fact]
    public void Tras_ajustar_un_pico_recortado_se_guarda_en_el_tope_y_sin_pico_real()
    {
        Assert.Equal((-12.5, 0.0, (double?)null), LoudnessScanner.TrasAjustar(-11, -0.005, 0.8, -1.5));
        Assert.Equal((-12.5, 0.0, (double?)null), LoudnessScanner.TrasAjustar(-11, 0.0, null, -1.5));

        var (l, p, t) = LoudnessScanner.TrasAjustar(-14, -3.0, -2.6, 1.5);
        Assert.Equal(-12.5, l, 9);
        Assert.Equal(-1.5, p, 9);
        Assert.Equal(-1.1, t!.Value, 9);
        Assert.Null(LoudnessScanner.TrasAjustar(-14, -3.0, null, 1.5).PicoReal);
    }

    // ---------- sonoridad.json: campos nuevos opcionales (T, V) ----------

    // Un sonoridad.json de una versión anterior (sin T ni V) se sigue leyendo, y al guardar no se
    // escriben nulos.
    [Fact]
    public void Un_sonoridad_json_antiguo_se_lee_y_no_se_llena_de_nulos()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var cancion = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(cancion);
            var fi = new FileInfo(cancion);
            var cache = Path.Combine(dir, "sonoridad.json");
            File.WriteAllText(cache, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [cancion] = new { M = fi.LastWriteTimeUtc.Ticks, S = fi.Length, Lufs = -9.5, Peak = -0.4 },
            }));

            var s = new LoudnessScanner(cache);
            var r = s.Get(cancion);
            Assert.NotNull(r);
            Assert.Equal(-9.5, r!.Value.Lufs, 6);
            Assert.Null(r.Value.TruePeakDb);
            Assert.Null(s.VersionDe(cancion));
            Assert.Equal(0, r.Value.GananciaAplicada);   // sin G: nada aplicado

            s.Update(cancion, -11, -1.9);
            s.Save();
            var texto = File.ReadAllText(cache);
            Assert.DoesNotContain("null", texto);
            Assert.DoesNotContain("\"T\"", texto);
            Assert.DoesNotContain("\"V\"", texto);
            Assert.DoesNotContain("\"G\"", texto);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Una medida nueva lleva pico real y versión, y los dos sobreviven a guardar y volver a leer.
    [Fact]
    public void Una_medida_nueva_guarda_el_pico_real_y_la_version()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            // fs/4 a 45°: las muestras se quedan en −9 dBFS y la onda llega a −6 dBTP.
            var wav = Path.Combine(dir, "tono.wav");
            const int fs = 48000;
            var x = new float[fs * 3 * 2];
            for (int i = 0; i < fs * 3; i++)
            {
                var entrada = Math.Min(1.0, i / 4800.0);   // 100 ms de entrada: sin el transitorio de un arranque brusco
                x[2 * i] = x[2 * i + 1] = (float)(entrada * 0.5 * Math.Sin(2 * Math.PI * i / 4.0 + Math.PI / 4));
            }
            using (var w = new NAudio.Wave.WaveFileWriter(wav, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(fs, 2)))
                w.WriteSamples(x, 0, x.Length);
            var cache = Path.Combine(dir, "sonoridad.json");

            var s = new LoudnessScanner(cache);
            var m = s.Measure(wav);
            Assert.NotNull(m);
            Assert.InRange(m!.Value.PeakDb, -9.1, -8.9);
            Assert.InRange(m.Value.TruePeakDb!.Value, -6.3, -5.8);
            Assert.Equal(LoudnessScanner.VersionActual, s.VersionDe(wav));
            s.Save();

            var otra = new LoudnessScanner(cache);
            var r = otra.Get(wav);
            Assert.Equal(m.Value.TruePeakDb!.Value, r!.Value.TruePeakDb!.Value, 9);
            Assert.Equal(m.Value.Lufs, r.Value.Lufs, 9);
            Assert.Equal(LoudnessScanner.VersionActual, otra.VersionDe(wav));

            // Ajustar no cambia la versión: la sonoridad sigue medida igual.
            otra.Ajustado(wav, r.Value.Lufs, r.Value.PeakDb, r.Value.TruePeakDb, -1.5);
            Assert.Equal(LoudnessScanner.VersionActual, otra.VersionDe(wav));
            Assert.Equal(m.Value.TruePeakDb!.Value - 1.5, otra.Get(wav)!.Value.TruePeakDb!.Value, 9);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- G: la ganancia que BeatTag ha aplicado, para calcular con el nivel original ----------

    // Se suma en cada ajuste, sobrevive a guardar y leer, Update no la toca y, al volver a cero, se
    // guarda como 0 (ajustada con los cambios compensados), no como «nunca ajustada».
    [Fact]
    public void La_ganancia_aplicada_se_suma_al_ajustar_y_se_guarda()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var cancion = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(cancion);
            var cache = Path.Combine(dir, "sonoridad.json");
            var s = new LoudnessScanner(cache);

            s.Update(cancion, -9, -0.2, -0.1);
            Assert.Equal(0, s.Get(cancion)!.Value.GananciaAplicada);

            s.Ajustado(cancion, -9, -0.2, -0.1, -3.0);
            Assert.Equal(-3.0, s.Get(cancion)!.Value.GananciaAplicada, 9);
            Assert.Equal(-12.0, s.Get(cancion)!.Value.Lufs, 9);

            s.Ajustado(cancion, -12, -3.2, -3.1, 1.5);
            s.Update(cancion, -10.5, -1.7, -1.6);
            Assert.Equal(-1.5, s.Get(cancion)!.Value.GananciaAplicada, 9);
            s.Save();
            Assert.Contains("\"G\":-1.5", File.ReadAllText(cache));

            var otra = new LoudnessScanner(cache);
            Assert.Equal(-1.5, otra.Get(cancion)!.Value.GananciaAplicada, 9);
            otra.Ajustado(cancion, -10.5, -1.7, -1.6, 1.5);
            Assert.Equal(0, otra.Get(cancion)!.Value.GananciaAplicada);
            otra.Save();
            Assert.Contains("\"G\":0", File.ReadAllText(cache));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Sumar_ganancia_distingue_nunca_ajustada_de_compensada()
    {
        Assert.Null(LoudnessScanner.SumarGanancia(null, 0));
        Assert.Equal(-1.5, LoudnessScanner.SumarGanancia(null, -1.5));
        Assert.Equal(0.0, LoudnessScanner.SumarGanancia(-1.5, 1.5));
        Assert.Equal(-1.5, LoudnessScanner.SumarGanancia(0.0, -1.5));
        Assert.Equal(0.0, LoudnessScanner.SumarGanancia(0.0, 0));
    }

    // El caso de la revisión: el modo automático baja 1 paso, «Nivel de club» lo sube 1 paso (la ganancia
    // vuelve a 0) y «Deshacer última» deshace solo la subida. Antes el 0 se guardaba como «nunca
    // ajustada», y al volver a medir el archivo, 1,5 dB por debajo de su original, contaba como original.
    [Fact]
    public void Deshacer_solo_una_de_las_pasadas_no_pierde_la_ganancia_aplicada()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var wav = Path.Combine(dir, "tono.wav");
            var paso = Math.Pow(2, -0.25);
            EscribirTono(wav, 0.3);
            var s = new LoudnessScanner(Path.Combine(dir, "sonoridad.json"));
            var m = s.Measure(wav, force: true)!.Value;

            // A: el modo automático lo baja un paso (como la pestaña: se cambia el archivo y se anota).
            EscribirTono(wav, 0.3 * paso);
            s.Ajustado(wav, m.Lufs, m.PeakDb, m.TruePeakDb, -1.5);
            var a = s.Get(wav)!.Value;
            // B: «Nivel de club» lo sube un paso: vuelve a su nivel original, con la ganancia compensada.
            EscribirTono(wav, 0.3);
            s.Ajustado(wav, a.Lufs, a.PeakDb, a.TruePeakDb, 1.5);
            Assert.Equal(0, s.Get(wav)!.Value.GananciaAplicada);

            // Se deshace solo B, por un camino que no avisa a la caché (aquí se reescribe el audio a mano).
            EscribirTono(wav, 0.3 * paso);
            var tras = s.Measure(wav, force: true)!.Value;
            Assert.Equal(-1.5, tras.GananciaAplicada, 9);
            Assert.InRange(tras.Lufs - tras.GananciaAplicada - m.Lufs, -0.02, 0.02);   // el nivel original sigue siendo el de antes
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // El deshacer de BeatTag conoce los pasos que devuelve: la medida se desplaza con ellos y la ganancia
    // aplicada se pone al día, sin volver a medir. Si la medida ya no valía justo antes, no se toca.
    [Fact]
    public void Reajustar_desplaza_la_medida_que_valia_antes_del_deshacer()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var cancion = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(cancion);
            var s = new LoudnessScanner(Path.Combine(dir, "sonoridad.json"));
            s.Update(cancion, -9, -0.6, -0.4);
            s.Ajustado(cancion, -9, -0.6, -0.4, -3.0);
            var antes = SelloArchivo.De(cancion);

            File.AppendAllText(cancion, " ");   // el deshacer reescribe el archivo
            Assert.Null(s.Get(cancion));
            Assert.True(s.Reajustar(cancion, antes.M, antes.S, 1.5, 1.5));

            var r = s.Get(cancion)!.Value;
            Assert.Equal(-10.5, r.Lufs, 9);
            Assert.Equal(-2.1, r.PeakDb, 9);
            Assert.Equal(-1.9, r.TruePeakDb!.Value, 9);
            Assert.Equal(-1.5, r.GananciaAplicada, 9);

            // Con un sello que no es el de la medida guardada, no se inventa nada.
            File.AppendAllText(cancion, " ");
            Assert.False(s.Reajustar(cancion, antes.M, antes.S, 1.5, 1.5));
            Assert.Null(s.Get(cancion));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // El caso de la revisión: BeatTag 1.10 ajustaba sin anotar la ganancia (Update dejaba la medida
    // desplazada y sin G). Deshacer uno de esos ajustes sumaba sus pasos a una ganancia que nunca los
    // había contado: el archivo, ya en su nivel original (−10,76), quedaba con +3 dB «aplicados» y un
    // nivel original calculado de −13,76, guardado además en volumen-aplicado.json. Ahora el deshacer
    // solo descuenta los pasos de los ajustes anotados.
    [Fact]
    public void Deshacer_un_ajuste_de_la_version_anterior_no_inventa_ganancia_aplicada()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var cancion = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(cancion);
            var cache = Path.Combine(dir, "sonoridad.json");
            var aplicada = Path.Combine(dir, "volumen-aplicado.json");
            var s = new LoudnessScanner(cache, null, aplicada);
            s.Update(cancion, -13.76, -3.4, -3.2);   // lo que dejaba 1.10 tras bajarla 2 pasos
            var antes = SelloArchivo.De(cancion);

            File.AppendAllText(cancion, " ");   // el deshacer reescribe el archivo
            Assert.True(s.Reajustar(cancion, antes.M, antes.S, 3.0, dbAnotados: 0));

            var r = s.Get(cancion)!.Value;
            Assert.Equal(-10.76, r.Lufs, 9);              // la medida sí se desplaza
            Assert.Equal(0, r.GananciaAplicada);          // sin ganancia inventada: el original es −10,76
            s.Save();
            Assert.False(File.Exists(aplicada) && File.ReadAllText(aplicada).Contains("a.mp3"));
            Assert.DoesNotContain("\"G\"", File.ReadAllText(cache));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // El caso mixto: un ajuste de 1.10 (2 pasos, sin anotar) y otro posterior (1 paso, anotado). Al
    // deshacer el nuevo la ganancia vuelve a 0; al deshacer después el antiguo, sigue en 0 (antes pasaba
    // a +3) y el nivel original calculado es el de verdad.
    [Fact]
    public void Deshacer_un_ajuste_nuevo_y_otro_antiguo_deja_el_nivel_original_de_verdad()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var cancion = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(cancion);
            var s = new LoudnessScanner(Path.Combine(dir, "sonoridad.json"));
            s.Update(cancion, -13.76, -3.4, -3.2);                 // 1.10: −3 dB sin anotar
            s.Ajustado(cancion, -13.76, -3.4, -3.2, -1.5);         // ahora: −1,5 dB anotados
            Assert.Equal(-1.5, s.Get(cancion)!.Value.GananciaAplicada, 9);

            var antes = SelloArchivo.De(cancion);
            File.AppendAllText(cancion, " ");
            Assert.True(s.Reajustar(cancion, antes.M, antes.S, 1.5, 1.5));   // se deshace el nuevo
            Assert.Equal(0, s.Get(cancion)!.Value.GananciaAplicada);

            antes = SelloArchivo.De(cancion);
            File.AppendAllText(cancion, " ");
            Assert.True(s.Reajustar(cancion, antes.M, antes.S, 3.0, 0));     // y luego el de 1.10
            var r = s.Get(cancion)!.Value;
            Assert.Equal(-10.76, r.Lufs, 9);
            Assert.Equal(0, r.GananciaAplicada);
            Assert.Equal(-10.76, r.Lufs - r.GananciaAplicada, 9);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // El caso de la revisión: en el equipo viejo se bajó un paso un WAV medido a −10,46 (el espejo queda
    // con G −1,5); en el equipo nuevo se analizó antes de restaurar la copia, así que sonoridad.json
    // tiene la ruta a −11,96 y sin G. Antes mandaba sonoridad.json: la ganancia se perdía y el primer
    // guardado dejaba volumen-aplicado.json en «{}». Ahora se vuelve a medir y, al ser el mismo audio,
    // la ganancia se conserva.
    [Fact]
    public void La_ganancia_del_espejo_no_se_pierde_si_sonoridad_json_tiene_la_ruta_sin_ella()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var wav = Path.Combine(dir, "tono.wav");
            var aplicada = Path.Combine(dir, "volumen-aplicado.json");
            EscribirTono(wav, 0.3);
            var viejo = new LoudnessScanner(Path.Combine(dir, "sonoridad-viejo.json"), null, aplicada);
            var m = viejo.Measure(wav, force: true)!.Value;
            EscribirTono(wav, 0.3 * Math.Pow(2, -0.25));   // se baja un paso…
            viejo.Ajustado(wav, m.Lufs, m.PeakDb, m.TruePeakDb, -1.5);   // …y se anota
            viejo.Save();
            Assert.Contains("\"G\":-1.5", File.ReadAllText(aplicada));

            // El equipo nuevo midió el archivo ya bajado, sin saber nada del ajuste.
            var cache = Path.Combine(dir, "sonoridad.json");
            var sinG = new LoudnessScanner(cache);
            Assert.Equal(0, sinG.Measure(wav, force: true)!.Value.GananciaAplicada);
            sinG.Save();
            Assert.DoesNotContain("\"G\"", File.ReadAllText(cache));

            // Se restaura la copia (solo trae volumen-aplicado.json) y se arranca.
            var nuevo = new LoudnessScanner(cache, null, aplicada);
            nuevo.Save();   // el primer guardado, que antes lo dejaba en «{}»
            Assert.Contains("\"G\":-1.5", File.ReadAllText(aplicada));
            Assert.Null(nuevo.Get(wav));   // hay que volver a medirla…
            Assert.Equal(-1.5, nuevo.Measure(wav)!.Value.GananciaAplicada, 9);   // …y la ganancia sigue
            nuevo.Save();
            Assert.Contains("\"G\":-1.5", File.ReadAllText(aplicada));
            Assert.Contains("\"G\":-1.5", File.ReadAllText(cache));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // BeatTag nunca escribe una entrada null, pero una edición a mano o una sincronización sí pueden.
    // En volumen-aplicado.json impedía arrancar (NullReferenceException en el constructor), y en
    // sonoridad.json hacía fallar todos los guardados. Ahora se salta.
    [Fact]
    public void Una_entrada_null_no_impide_arrancar_ni_guardar()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var cancion = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(cancion);
            var fi = new FileInfo(cancion);
            var cache = Path.Combine(dir, "sonoridad.json");
            var aplicada = Path.Combine(dir, "volumen-aplicado.json");
            var nula = Path.Combine(dir, "nula.mp3");
            File.WriteAllText(cache, JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [nula] = null,
                [cancion] = new { M = fi.LastWriteTimeUtc.Ticks, S = fi.Length, Lufs = -9.5, Peak = -0.4 },
            }));
            File.WriteAllText(aplicada, JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [nula] = null,
                [Path.Combine(dir, "b.mp3")] = new { G = -1.5, Lufs = -12.0 },
            }));

            var s = new LoudnessScanner(cache, null, aplicada);
            Assert.Equal(-9.5, s.Get(cancion)!.Value.Lufs, 9);
            s.Update(cancion, -11, -1.9);
            s.Save();

            var texto = File.ReadAllText(cache);
            Assert.Contains("-11", texto);
            Assert.DoesNotContain("nula.mp3", texto);
            Assert.Contains("b.mp3", File.ReadAllText(aplicada));
            s.Clear();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- La ganancia aplicada no es una caché: sobrevive a «Limpiar» y viaja en la copia ----------

    // sonoridad.json no entra en la copia de seguridad. Sin volumen-aplicado.json, al restaurar en otro
    // equipo (o tras perder la carpeta de datos) la referencia se calculaba con el nivel ya ajustado.
    [Fact]
    public void La_ganancia_aplicada_se_recupera_sin_sonoridad_json()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var wav = Path.Combine(dir, "tono.wav");
            var otro = Path.Combine(dir, "otro.wav");
            var cache = Path.Combine(dir, "sonoridad.json");
            var aplicada = Path.Combine(dir, "volumen-aplicado.json");
            EscribirTono(wav, 0.3);
            EscribirTono(otro, 0.2);
            var s = new LoudnessScanner(cache, null, aplicada);
            var m = s.Measure(wav, force: true)!.Value;
            s.Measure(otro, force: true);
            EscribirTono(wav, 0.3 * Math.Pow(2, -0.25));   // se baja un paso…
            s.Ajustado(wav, m.Lufs, m.PeakDb, m.TruePeakDb, -1.5);   // …y se anota
            s.Save();

            var texto = File.ReadAllText(aplicada);
            Assert.Contains("\"G\":-1.5", texto);
            Assert.DoesNotContain("otro.wav", texto);   // solo lo ajustado

            File.Delete(cache);
            var restaurada = new LoudnessScanner(cache, null, aplicada);
            Assert.Null(restaurada.Get(wav));            // la medida hay que repetirla…
            Assert.Equal(-1.5, restaurada.Measure(wav)!.Value.GananciaAplicada, 9);   // …y la ganancia sigue
            Assert.Equal(0, restaurada.Measure(otro)!.Value.GananciaAplicada);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // «Limpiar carpeta de datos» borra las mediciones también en memoria (antes seguían sirviéndose y el
    // siguiente guardado volvía a escribirlas), y conserva la ganancia aplicada.
    [Fact]
    public void Clear_borra_las_mediciones_y_conserva_la_ganancia_aplicada()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var wav = Path.Combine(dir, "tono.wav");
            var otro = Path.Combine(dir, "otro.wav");
            var cache = Path.Combine(dir, "sonoridad.json");
            EscribirTono(wav, 0.3);
            EscribirTono(otro, 0.2);
            var s = new LoudnessScanner(cache, null, Path.Combine(dir, "volumen-aplicado.json"));
            var m = s.Measure(wav, force: true)!.Value;
            s.Measure(otro, force: true);
            EscribirTono(wav, 0.3 * Math.Pow(2, -0.25));   // se baja un paso…
            s.Ajustado(wav, m.Lufs, m.PeakDb, m.TruePeakDb, -1.5);   // …y se anota
            s.Save();
            Assert.NotNull(s.Get(wav));
            Assert.NotNull(s.Get(otro));

            s.Clear();
            Assert.Null(s.Get(otro));
            Assert.Null(s.Get(wav));
            s.Save();
            Assert.DoesNotContain("otro.wav", File.ReadAllText(cache));

            Assert.Equal(-1.5, s.Measure(wav)!.Value.GananciaAplicada, 9);
            Assert.Equal(-1.5, new LoudnessScanner(cache, null, Path.Combine(dir, "volumen-aplicado.json")).Measure(wav)!.Value.GananciaAplicada, 9);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- Audio estéreo a 22,05 kHz: el búfer de medida, en tramas completas ----------

    // El búfer tenía SampleRate·Channels/4 muestras: 11.025 a 22,05 kHz en estéreo. Un WAV así no se
    // podía medir («Must read complete blocks»), y en un MP3 se perdía una muestra por búfer.
    [Fact]
    public void Un_wav_estereo_a_22050_Hz_se_mide()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            const int fs = 22050;
            var wav = Path.Combine(dir, "22k.wav");
            var x = new float[fs * 3 * 2];
            for (int i = 0; i < fs * 3; i++)
                x[2 * i] = x[2 * i + 1] = (float)(0.3 * Math.Sin(2 * Math.PI * 997 * i / fs));
            using (var w = new NAudio.Wave.WaveFileWriter(wav, new NAudio.Wave.WaveFormat(fs, 16, 2)))
                w.WriteSamples(x, 0, x.Length);

            var m = new LoudnessScanner(Path.Combine(dir, "sonoridad.json")).Measure(wav);

            Assert.NotNull(m);
            Assert.InRange(m!.Value.PeakDb, -10.6, -10.3);   // 0,3 de amplitud: −10,5 dBFS
            Assert.NotNull(m.Value.TruePeakDb);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Al volver a medir: el mismo audio conserva la ganancia; un número exacto de pasos (el deshacer de
    // BeatTag, o MP3Gain) se suma; otro audio la pierde.
    [Theory]
    [InlineData(-3.0, -12.0, -12.0, -3.0)]      // mismo audio
    [InlineData(-3.0, -12.0, -11.94, -3.0)]     // a menos de 0,1 dB: el mismo
    [InlineData(-3.0, -12.0, -10.495, -1.5)]    // un paso más alto: se deshizo uno
    [InlineData(-3.0, -12.0, -8.99, 0.0)]       // dos pasos: se deshizo todo (ajustada, compensada)
    [InlineData(-3.0, -12.0, -11.4, null)]      // 0,6 dB: es otro audio
    [InlineData(1.5, -10.0, -11.505, 0.0)]      // una subida deshecha
    [InlineData(0.0, -11.0, -12.505, -1.5)]     // compensada y después un paso menos: la bajada vuelve a contar
    public void Al_volver_a_medir_la_ganancia_sigue_al_audio(double aplicada, double guardado, double medido, double? esperada)
    {
        var g = LoudnessScanner.GananciaTrasMedir(aplicada, LoudnessScanner.VersionActual, guardado, medido, canales: 2);
        if (esperada is { } e) Assert.Equal(e, g!.Value, 9);
        else Assert.Null(g);
    }

    [Fact]
    public void Sin_ganancia_aplicada_o_con_un_mono_antiguo_no_se_inventa_nada()
    {
        Assert.Null(LoudnessScanner.GananciaTrasMedir(null, LoudnessScanner.VersionActual, -12, -12, 2));
        Assert.Null(LoudnessScanner.GananciaTrasMedir(-3, LoudnessScanner.VersionActual, double.NaN, -12, 2));
        // Un mono medido antes del mono doble sale 3,01 dB más alto al volver a medirlo: no son dos pasos.
        Assert.Equal(-3.0, LoudnessScanner.GananciaTrasMedir(-3, null, -15, -15 + 10 * Math.Log10(2), canales: 1)!.Value, 9);
        Assert.Equal(-3.0, LoudnessScanner.GananciaTrasMedir(-3, null, -12, -12, canales: 2)!.Value, 9);
    }

    // De punta a punta con un archivo de verdad: medir, ajustar, cambiar el audio y volver a medir.
    [Fact]
    public void Medir_de_nuevo_conserva_la_ganancia_si_el_audio_es_el_mismo()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var wav = Path.Combine(dir, "tono.wav");
            var cache = Path.Combine(dir, "sonoridad.json");
            var paso = Math.Pow(2, -0.25);   // un paso de global_gain en amplitud
            EscribirTono(wav, 0.3);
            var s = new LoudnessScanner(cache);
            var m = s.Measure(wav, force: true)!.Value;
            Assert.Equal(0, m.GananciaAplicada);

            // BeatTag lo baja un paso (aquí se reescribe el audio a mano) y se vuelve a medir: sigue ajustado.
            s.Ajustado(wav, m.Lufs, m.PeakDb, m.TruePeakDb, -1.5);
            EscribirTono(wav, 0.3 * paso);
            var tras = s.Measure(wav, force: true)!.Value;
            Assert.Equal(-1.5, tras.GananciaAplicada, 9);
            Assert.Equal(-1.5, s.Get(wav)!.Value.GananciaAplicada, 9);

            // Se deshace (vuelve al nivel de antes): nada aplicado.
            EscribirTono(wav, 0.3);
            Assert.Equal(0, s.Measure(wav, force: true)!.Value.GananciaAplicada);

            // Otro audio en la misma ruta: su nivel original es el que se acaba de medir.
            var de = s.Measure(wav, force: true)!.Value;
            s.Ajustado(wav, de.Lufs, de.PeakDb, de.TruePeakDb, -1.5);
            EscribirTono(wav, 0.2);   // 3,5 dB por debajo: ni el mismo nivel ni un número exacto de pasos
            Assert.Equal(0, s.Measure(wav, force: true)!.Value.GananciaAplicada);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void EscribirTono(string ruta, double amplitud)
    {
        const int fs = 48000;
        var x = new float[fs * 3 * 2];
        for (int i = 0; i < fs * 3; i++)
            x[2 * i] = x[2 * i + 1] = (float)(amplitud * Math.Sin(2 * Math.PI * 997 * i / fs));
        using var w = new NAudio.Wave.WaveFileWriter(ruta, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(fs, 2));
        w.WriteSamples(x, 0, x.Length);
    }

    // Una grabación que BeatTag bajó con el pico recortado se guarda con el pico en el tope y sin pico
    // real. Antes de decir que no admite aumento se mide; una recortada de origen no, porque su pico
    // real no puede ser menor que el de muestra.
    [Fact]
    public void Una_bajada_con_el_pico_recortado_se_mide_antes_de_decir_que_no_admite_aumento()
    {
        var bajada = new LoudnessRow { FilePath = @"C:\m\a.mp3", Lufs = -14, PeakDb = 0, GananciaAplicada = -3, Target = -11 };
        var original = new LoudnessRow { FilePath = @"C:\m\b.mp3", Lufs = -14, PeakDb = 0, Target = -11 };
        Assert.Equal(new[] { bajada }, LoudnessViewModel.SinPicoReal(new[] { bajada, original }));
        Assert.StartsWith("Falta medir su pico real", bajada.Aviso);
        Assert.Equal("Sin margen suficiente: aumentarla produciría distorsión", original.Aviso);

        // Medido (−2,6 dBTP), admite recuperar un paso de los dos.
        var medida = new LoudnessRow { FilePath = @"C:\m\a.mp3", Lufs = -14, PeakDb = -3.0, TruePeakDb = -2.6, GananciaAplicada = -3, Target = -11 };
        Assert.Equal(1, LoudnessViewModel.PasosAplicables(medida.Gain, medida.PicoDecision));
        Assert.Empty(LoudnessViewModel.SinPicoReal(new[] { medida }));

        // En nivel no hace falta medir nada (en el modo automático quedan a 0,75 dB o menos).
        var enNivel = new LoudnessRow { FilePath = @"C:\m\c.mp3", Lufs = -11.5, PeakDb = 0, GananciaAplicada = -3, Target = -11 };
        Assert.Empty(LoudnessViewModel.SinPicoReal(new[] { enNivel }));
    }

    // ---------- V5: los mono medidos antes del mono doble se vuelven a medir, y solo ellos ----------

    [Fact]
    public void Solo_los_mono_medidos_antes_del_mono_doble_se_vuelven_a_medir()
    {
        Assert.True(LoudnessScanner.MonoSinDoble(null, 1));
        Assert.False(LoudnessScanner.MonoSinDoble(LoudnessScanner.VersionActual, 1));
        Assert.False(LoudnessScanner.MonoSinDoble(null, 2));

        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mono = Path.Combine(dir, "mono.mp3");
            var estereo = Path.Combine(dir, "estereo.mp3");
            Mp3Fixture.WriteMinMp3(mono);
            Mp3Fixture.WriteMinMp3(estereo);
            var s = new LoudnessScanner(Path.Combine(dir, "sonoridad.json"));
            s.Update(mono, -15, -3);       // como una medida antigua: sin versión
            s.Update(estereo, -12, -3);

            var rehacer = s.MonoPorVolverAMedir(new[] { (mono, 1), (estereo, 2), (Path.Combine(dir, "nueva.mp3"), 1) });

            Assert.Equal(new[] { mono }, rehacer);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- A11: escritura atómica de sonoridad.json ----------

    [Fact]
    public void Las_mediciones_se_guardan_sin_temporales_y_se_releen()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var cancion = Path.Combine(dir, "Quevedo - Columbia.mp3");
            Mp3Fixture.WriteMinMp3(cancion);
            var cache = Path.Combine(dir, "sonoridad.json");

            var s = new LoudnessScanner(cache);
            s.Update(cancion, -9.4, -0.3);
            s.Save();

            Assert.True(File.Exists(cache));
            Assert.False(File.Exists(cache + ".tmp"));
            var r = new LoudnessScanner(cache).Get(cancion);
            Assert.NotNull(r);
            Assert.Equal(-9.4, r!.Value.Lufs, 3);
            Assert.Equal(-0.3, r.Value.PeakDb, 3);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Si el guardado falla a mitad, el archivo anterior queda intacto y lo nuevo sigue pendiente.
    // Aquí falla porque el temporal no se puede crear (hay una carpeta con su nombre).
    [Fact]
    public void Un_guardado_que_falla_no_toca_las_mediciones_anteriores()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var a = Path.Combine(dir, "a.mp3");
            var b = Path.Combine(dir, "b.mp3");
            Mp3Fixture.WriteMinMp3(a);
            Mp3Fixture.WriteMinMp3(b);
            var cache = Path.Combine(dir, "sonoridad.json");

            var s = new LoudnessScanner(cache);
            s.Update(a, -10, -1);
            s.Save();
            var antes = File.ReadAllText(cache);

            Directory.CreateDirectory(cache + ".tmp");
            s.Update(b, -12, -2);
            s.Save();

            Assert.Equal(antes, File.ReadAllText(cache));
            Assert.NotNull(new LoudnessScanner(cache).Get(a));

            // Cuando se puede volver a escribir, lo pendiente se guarda.
            Directory.Delete(cache + ".tmp");
            s.Save();
            Assert.NotNull(new LoudnessScanner(cache).Get(b));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Un sonoridad.json truncado (corte de luz con la versión anterior) no se trata como vacío en
    // silencio: se aparta con otro nombre, de modo que el siguiente guardado no destruye lo que
    // pudiera rescatarse de él.
    [Fact]
    public void Un_archivo_truncado_se_aparta_y_no_se_pisa()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var cancion = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(cancion);
            var cache = Path.Combine(dir, "sonoridad.json");
            var entero = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [cancion] = new { M = 1L, S = 2L, Lufs = -9.0, Peak = -0.5 },
                [cancion + "2"] = new { M = 1L, S = 2L, Lufs = -8.0, Peak = -0.2 },
            });
            File.WriteAllText(cache, entero[..(entero.Length / 2)]);

            var s = new LoudnessScanner(cache);
            s.Update(cancion, -11, -1);
            s.Save();

            var apartado = Assert.Single(Directory.GetFiles(dir, "sonoridad.json.ilegible_*"));
            Assert.Equal(entero[..(entero.Length / 2)], File.ReadAllText(apartado));
            Assert.NotNull(new LoudnessScanner(cache).Get(cancion));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
