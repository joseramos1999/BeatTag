using System.Text;
using Etiquetador.Core.Analysis;
using static Etiquetador.Tests.Mp3GainSintetico;

namespace Etiquetador.Tests;

/// <summary>
/// Hallazgos A9 y A10: que ajustar el volumen no deje basura junto a la canción cuando no se puede
/// escribir, y que nunca escriba en bytes que no son el global_gain de una trama de verdad.
/// </summary>
public class Mp3GainRobustezTests
{
    private static void EnTemporal(Action<string, string> prueba)
    {
        var dir = Mp3Fixture.NewTempDir();
        try { prueba(dir, Path.Combine(dir, "Quevedo - Columbia (Extended).mp3")); }
        finally
        {
            foreach (var f in Directory.GetFiles(dir)) try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---------- A9: el temporal ----------

    // El caso real: la canción está cargada en Rekordbox, Serato o VirtualDJ, que la tienen abierta
    // sin permitir que otro la sustituya. Antes el ajuste fallaba (bien) pero dejaba al lado
    // «….mp3.beattag.tmp», una copia entera que nadie ve porque «.tmp» no es audio.
    [WindowsFact]
    public void Si_la_cancion_esta_abierta_en_otro_programa_no_queda_el_temporal()
    {
        EnTemporal((dir, p) =>
        {
            File.WriteAllBytes(p, Tramas(30, _ => 150));
            var original = File.ReadAllBytes(p);

            using (new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var r = Mp3Gain.Apply(p, +2);
                Assert.False(r.Ok);
            }

            Assert.Empty(Directory.GetFiles(dir, "*.beattag.tmp"));
            Assert.Equal(original, File.ReadAllBytes(p));   // la canción, intacta
        });
    }

    [WindowsFact]
    public void Si_la_cancion_es_de_solo_lectura_no_queda_el_temporal()
    {
        EnTemporal((dir, p) =>
        {
            File.WriteAllBytes(p, Tramas(30, _ => 150));
            var original = File.ReadAllBytes(p);
            File.SetAttributes(p, FileAttributes.ReadOnly);

            var r = Mp3Gain.Apply(p, +2);

            Assert.False(r.Ok);
            Assert.Empty(Directory.GetFiles(dir, "*.beattag.tmp"));
            Assert.Equal(original, File.ReadAllBytes(p));
        });
    }

    [Fact]
    public void Tras_un_ajuste_correcto_no_queda_ningun_temporal()
    {
        EnTemporal((dir, p) =>
        {
            File.WriteAllBytes(p, Tramas(30, _ => 150));
            Assert.True(Mp3Gain.Apply(p, -2).Ok);
            Assert.Equal(new[] { p }, Directory.GetFiles(dir));
        });
    }

    // La sustitución crea un archivo nuevo, pero la canción conserva su fecha de creación: es la
    // que se ve al ordenar la carpeta por fecha.
    [WindowsFact]
    public void La_cancion_conserva_su_fecha_de_creacion()
    {
        EnTemporal((dir, p) =>
        {
            File.WriteAllBytes(p, Tramas(30, _ => 150));
            var creada = new DateTime(2019, 5, 4, 22, 0, 0, DateTimeKind.Utc);
            File.SetCreationTimeUtc(p, creada);

            Assert.True(Mp3Gain.Apply(p, -2).Ok);

            Assert.Equal(creada, File.GetCreationTimeUtc(p));
        });
    }

    // ---------- A10: resincronizar sin escribir en una cabecera falsa ----------

    /// <summary>
    /// 200 bytes que imitan un segundo ID3v2 con la carátula dentro, como el que algunas
    /// herramientas anteponen sin quitar el anterior. En el byte 50 hay un 0xFF 0xFB 0x90 0x00,
    /// que es exactamente una cabecera MPEG1 capa III válida; el resto no tiene ningún 0xFF.
    /// </summary>
    private static byte[] CaratulaConCabeceraFalsa()
    {
        var c = new byte[200];
        for (var k = 0; k < c.Length; k++) c[k] = (byte)((k * 7 + 3) % 251);
        Encoding.ASCII.GetBytes("ID3").CopyTo(c, 0);
        c[50] = 0xFF; c[51] = 0xFB; c[52] = 0x90; c[53] = 0x00;
        return c;
    }

    [Fact]
    public void Una_cabecera_falsa_dentro_de_una_caratula_no_se_toca()
    {
        EnTemporal((dir, p) =>
        {
            var antes = Tramas(10, _ => 150);
            var caratula = CaratulaConCabeceraFalsa();
            var despues = Tramas(10, _ => 150);
            File.WriteAllBytes(p, [.. antes, .. caratula, .. despues]);

            var r = Mp3Gain.Apply(p, +2);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(20, r.Frames);   // las 20 de verdad; la falsa no cuenta

            var b = File.ReadAllBytes(p);
            // La carátula sale como entró: ni un byte sumado.
            Assert.Equal(caratula, b[antes.Length..(antes.Length + caratula.Length)]);

            // Y todas las tramas de verdad se ajustan, también la que va justo detrás de la carátula
            // (antes la cabecera falsa «se comía» sus primeros bytes y esa trama se quedaba sin tocar).
            for (var k = 0; k < 10; k++)
            {
                Assert.Equal(new[] { 152, 152, 152, 152 }, Ganancias(b, k * LargoTrama));
                Assert.Equal(new[] { 152, 152, 152, 152 }, Ganancias(b, antes.Length + caratula.Length + k * LargoTrama));
            }
        });
    }

    // La etiqueta APE también puede llevar datos binarios (una carátula, por ejemplo). Antes el
    // recorrido seguía byte a byte por ella al acabar el audio y escribía en lo que pareciera una
    // trama. Ahora se para donde empieza la etiqueta.
    [Fact]
    public void Los_datos_binarios_de_la_etiqueta_APE_no_se_toman_por_audio()
    {
        EnTemporal((dir, p) =>
        {
            var audio = Tramas(30, _ => 150);
            var imagen = new byte[600];
            for (var k = 0; k < imagen.Length; k++) imagen[k] = (byte)((k * 13 + 1) % 251);
            Encoding.ASCII.GetBytes("cover.jpg\0").CopyTo(imagen, 0);
            imagen[10] = 0xFF; imagen[11] = 0xFB; imagen[12] = 0x90; imagen[13] = 0x00;
            // Flags 2: elemento binario. Sin datos de ganancia, la etiqueta no tiene por qué cambiar.
            byte[] cola = [.. Ape(true, ("Cover Art (Front)", imagen, 2u), Texto("Title", "Columbia")), .. Id3v1("Columbia")];
            File.WriteAllBytes(p, [.. audio, .. cola]);

            var r = Mp3Gain.Apply(p, +2);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(30, r.Frames);

            var b = File.ReadAllBytes(p);
            Assert.Equal(cola, b[audio.Length..]);
            Assert.Equal(new[] { 152, 152, 152, 152 }, Ganancias(b, 29 * LargoTrama));
        });
    }

    // Lo de siempre tiene que seguir igual: un archivo limpio, de principio a fin, sin saltos.
    [Fact]
    public void Un_mp3_sin_saltos_se_recorre_entero()
    {
        EnTemporal((dir, p) =>
        {
            File.WriteAllBytes(p, Tramas(40, _ => 150));
            var r = Mp3Gain.Analyze(p);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(40, r.Frames);
        });
    }
}
