using Etiquetador.Core.Analysis;
using Xunit;

namespace Etiquetador.Tests;

/// <summary>
/// Ajuste de volumen sin recodificar. Lo crítico es que sea EXACTAMENTE reversible: aplicar +N y
/// luego -N tiene que devolver el archivo tal cual estaba.
/// </summary>
public class Mp3GainTests
{
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
            Mp3Fixture.WriteMinMp3(p, frames: 30);
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
            Mp3Fixture.WriteMinMp3(p, frames: 30);
            var antes = File.ReadAllBytes(p);
            var r = Mp3Gain.Apply(p, 300);
            Assert.False(r.Ok);
            Assert.Equal(antes, File.ReadAllBytes(p));   // intacto
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
