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
