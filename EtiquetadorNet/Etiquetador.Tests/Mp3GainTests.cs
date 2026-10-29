using Etiquetador.Core.Analysis;
using Xunit;
using static Etiquetador.Tests.Mp3GainSintetico;

namespace Etiquetador.Tests;

/// <summary>
/// Ajuste de volumen sin recodificar. Lo crítico es que sea reversible: aplicar +N y luego -N tiene
/// que devolver el nivel exacto (y, salvo gránulos de silencio inaudibles recortados, el archivo tal
/// cual estaba).
/// </summary>
public class Mp3GainTests
{
    /// <summary>Escribe <paramref name="bytes"/> en un MP3 temporal, ejecuta la prueba y lo borra.</summary>
    private static void ConArchivo(byte[] bytes, Action<string> prueba)
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "x.mp3");
            File.WriteAllBytes(p, bytes);
            prueba(p);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- V4: gránulos sin audio y gránulos inaudibles ----------

    // El caso real: el 42 % de los MP3 tiene gránulos vacíos (part2_3_length = 0) con global_gain = 0.
    // Bajar un paso los sacaba de rango y, pasado el 1 %, se rechazaba el cambio entero («el cambio es
    // demasiado grande») por gránulos que no suenan. Ahora no se tocan, y la ida y vuelta es idéntica.
    [Fact]
    public void Los_granulos_vacios_con_ganancia_0_no_impiden_bajar_ni_se_tocan()
    {
        // 5 % de tramas vacías con global_gain 0; el resto, con audio a 150.
        var original = Tramas(100, k => k % 20 == 0 ? 0 : 150, vacia: k => k % 20 == 0);

        ConArchivo(original, p =>
        {
            var bajar = Mp3Gain.Apply(p, -2);
            Assert.True(bajar.Ok, bajar.Error);
            Assert.Equal(0, bajar.Clamped);

            var b = File.ReadAllBytes(p);
            for (var k = 0; k < 100; k++)
            {
                var esperado = k % 20 == 0 ? 0 : 148;
                Assert.All(Ganancias(b, k * LargoTrama), g => Assert.Equal(esperado, g));
                Assert.All(BitsDeAudioDe(b, k * LargoTrama), n => Assert.Equal(k % 20 == 0 ? 0 : 1000, n));
            }

            var subir = Mp3Gain.Apply(p, +2);
            Assert.True(subir.Ok, subir.Error);
            Assert.Equal(original, File.ReadAllBytes(p));   // la ida y vuelta deja el archivo idéntico
        });
    }

    [Fact]
    public void Los_granulos_vacios_al_tope_no_impiden_subir_ni_se_tocan()
    {
        var original = Tramas(100, k => k % 10 == 0 ? 254 : 150, vacia: k => k % 10 == 0);   // 10 % vacías a 254
        ConArchivo(original, p =>
        {
            var r = Mp3Gain.Apply(p, +2);
            Assert.True(r.Ok, r.Error);
            var b = File.ReadAllBytes(p);
            for (var k = 0; k < 100; k++)
                Assert.All(Ganancias(b, k * LargoTrama), g => Assert.Equal(k % 10 == 0 ? 254 : 152, g));
        });
    }

    // Con audio pero por debajo de −200 dBFS (global_gain < 4): se recortan a 0 si hace falta, pero no
    // cuentan para rechazar el cambio, aunque sean muchos más del 1 %.
    [Fact]
    public void Los_granulos_inaudibles_se_recortan_sin_rechazar_el_cambio()
    {
        var original = Tramas(100, k => k % 20 == 0 ? 2 : 150);   // 5 % con audio y global_gain 2
        ConArchivo(original, p =>
        {
            var r = Mp3Gain.Apply(p, -3);
            Assert.True(r.Ok, r.Error);
            var b = File.ReadAllBytes(p);
            for (var k = 0; k < 100; k++)
                Assert.All(Ganancias(b, k * LargoTrama), g => Assert.Equal(k % 20 == 0 ? 0 : 147, g));
        });
    }

    // Lo que sí suena y se saldría de rango sigue frenando el cambio: más del 1 % de gránulos con
    // audio al tope no admite subir.
    [Fact]
    public void Los_granulos_con_audio_al_tope_siguen_rechazando_la_subida()
    {
        var original = Tramas(100, k => k % 20 == 0 ? 254 : 150);   // 5 % con audio a 254
        ConArchivo(original, p =>
        {
            var r = Mp3Gain.Apply(p, +2);
            Assert.False(r.Ok);
            Assert.Contains("demasiado grande", r.Error);
            Assert.Equal(original, File.ReadAllBytes(p));
        });
    }

    // Un archivo que es todo silencio digital se acepta y no cambia: no hay nada que suene.
    [Fact]
    public void Un_mp3_de_silencio_digital_se_acepta_sin_cambios()
    {
        var original = Tramas(30, _ => 0, vacia: _ => true);
        ConArchivo(original, p =>
        {
            var r = Mp3Gain.Apply(p, -2);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(original, File.ReadAllBytes(p));
        });
    }
    [Theory]
    [InlineData(1.5, 1)]
    [InlineData(-1.5, -1)]
    [InlineData(3.0, 2)]
    [InlineData(0.7, 0)]     // menos de medio paso: no merece tocarlo
    [InlineData(0.8, 1)]
    [InlineData(-2.4, -2)]
    public void Los_pasos_se_calculan_por_1_5_db(double db, int esperado)
        => Assert.Equal(esperado, Mp3Gain.StepsFor(db));

    [Fact]
    public void Cada_paso_vale_1_5_db()
        => Assert.Equal(3.0, new Mp3GainResult(true, 2, 0, "").Db, 3);

    [Fact]
    public void Reconoce_las_tramas_de_un_mp3()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "x.mp3");
            Mp3Fixture.WriteMinMp3(p, frames: 30);
            var r = Mp3Gain.Analyze(p);
            Assert.True(r.Ok, r.Error);
            Assert.True(r.Frames > 0, "debería encontrar tramas");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Aplicar_y_revertir_deja_el_archivo_identico()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "x.mp3");
            File.WriteAllBytes(p, Tramas(30, _ => 150));   // tramas con audio: las vacías no se tocan
            var original = File.ReadAllBytes(p);

            var subir = Mp3Gain.Apply(p, +3);
            Assert.True(subir.Ok, subir.Error);
            Assert.NotEqual(original, File.ReadAllBytes(p));   // de verdad cambió algo

            var bajar = Mp3Gain.Apply(p, -3);
            Assert.True(bajar.Ok, bajar.Error);
            Assert.Equal(original, File.ReadAllBytes(p));      // y volvió exactamente a su sitio
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void El_tamaño_del_archivo_no_cambia()
    {
        // No se recodifica: se toca un campo dentro de cada trama, así que pesa lo mismo.
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "x.mp3");
            Mp3Fixture.WriteMinMp3(p, frames: 30);
            var antes = new FileInfo(p).Length;
            Mp3Gain.Apply(p, +2);
            Assert.Equal(antes, new FileInfo(p).Length);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Cero_pasos_no_toca_nada()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "x.mp3");
            Mp3Fixture.WriteMinMp3(p, frames: 10);
            var antes = File.ReadAllBytes(p);
            var r = Mp3Gain.Apply(p, 0);
            Assert.True(r.Ok);
            Assert.Equal(antes, File.ReadAllBytes(p));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Un_cambio_desmesurado_no_se_aplica()
    {
        // Pedir +300 pasos (450 dB) sacaría de rango a todas las tramas: mejor no tocar el archivo.
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "x.mp3");
            File.WriteAllBytes(p, Tramas(30, _ => 150));
            var antes = File.ReadAllBytes(p);
            var r = Mp3Gain.Apply(p, 300);
            Assert.False(r.Ok);
            Assert.Equal(antes, File.ReadAllBytes(p));   // intacto
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
