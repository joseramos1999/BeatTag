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

    // La última barrera antes de escribir: una subida sin pico real conocido lo mide antes, y los pasos
    // salen de él.
    [Fact]
    public void Una_subida_sin_pico_real_no_se_aplica_sin_medirlo_antes()
    {
        var fila = new LoudnessRow { FilePath = @"C:\m\a.mp3", Lufs = -16, PeakDb = -6.0, Target = -11 };
        Assert.Equal(3, LoudnessViewModel.PasosAplicables(fila.Gain, fila.PicoDecision));   // con el de muestra, 3

        var escaner = new EscanerDoble(new LoudnessResult(-16, -6.0, 200, TruePeakDb: -3.4));
        var d = LoudnessViewModel.Decidir(fila, escaner.Medir);

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
        Assert.Equal(0, LoudnessViewModel.Decidir(fila, new EscanerDoble(null).Medir).Pasos);
        Assert.Equal(0, LoudnessViewModel.Decidir(fila, new EscanerDoble(new LoudnessResult(-16, -6, 200)).Medir).Pasos);
    }

    // Bajar, o subir con el pico real ya conocido, no decodifica nada.
    [Fact]
    public void Bajar_o_subir_con_pico_real_conocido_no_vuelve_a_medir()
    {
        var escaner = new EscanerDoble(null);
        var baja = new LoudnessRow { FilePath = @"C:\m\a.mp3", Lufs = -7, PeakDb = 0, Target = -11 };
        var conReal = new LoudnessRow { FilePath = @"C:\m\b.mp3", Lufs = -14, PeakDb = -8, TruePeakDb = -7, Target = -11 };

        Assert.Equal(-3, LoudnessViewModel.Decidir(baja, escaner.Medir).Pasos);
        Assert.Equal(2, LoudnessViewModel.Decidir(conReal, escaner.Medir).Pasos);
        Assert.Empty(escaner.Medidas);
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

            s.Update(cancion, -11, -1.9);
            s.Save();
            var texto = File.ReadAllText(cache);
            Assert.DoesNotContain("null", texto);
            Assert.DoesNotContain("\"T\"", texto);
            Assert.DoesNotContain("\"V\"", texto);
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
