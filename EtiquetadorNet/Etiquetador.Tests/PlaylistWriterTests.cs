using System.Text;
using Etiquetador.Core;

namespace Etiquetador.Tests;

// Listas M3U8 para llevarse una seleccion a rekordbox, Engine DJ o Serato.
public class PlaylistWriterTests
{
    private static PlaylistItem It(string ruta, string art = "", string tit = "", int dur = 0)
        => new(ruta, art, tit, dur);

    [Fact]
    public void Empieza_por_la_cabecera_obligatoria()
        => Assert.StartsWith("#EXTM3U", PlaylistWriter.Build(new[] { It(@"C:\m\a.mp3") }));

    [Fact]
    public void Cada_cancion_lleva_su_duracion_y_rotulo()
    {
        var m3u = PlaylistWriter.Build(new[] { It(@"C:\m\a.mp3", "Bad Bunny", "Tití Me Preguntó", 243) });
        Assert.Contains("#EXTINF:243,Bad Bunny - Tití Me Preguntó", m3u);
        Assert.Contains(@"C:\m\a.mp3", m3u);
    }

    // Sin artista el rotulo es solo el titulo: es lo que esperan los reproductores.
    [Fact]
    public void Sin_artista_va_solo_el_titulo()
        => Assert.Contains("#EXTINF:100,Solo Titulo",
                           PlaylistWriter.Build(new[] { It(@"C:\m\a.mp3", "", "Solo Titulo", 100) }));

    // Sin datos ningunos, el nombre del archivo es mejor que una linea vacia.
    [Fact]
    public void Sin_datos_se_usa_el_nombre_del_archivo()
        => Assert.Contains("#EXTINF:-1,cancion",
                           PlaylistWriter.Build(new[] { It(Rutas.Archivo("cancion.mp3")) }));

    // Guarda: un salto de linea en el rotulo partiria la entrada en dos y rompe la lista entera.
    [Fact]
    public void Un_salto_de_linea_en_el_rotulo_no_rompe_la_lista()
    {
        var m3u = PlaylistWriter.Build(new[] { It(@"C:\m\a.mp3", "Uno\nDos", "Tema", 10) });
        var lineas = m3u.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lineas.Length);            // cabecera + EXTINF + ruta
    }

    [Fact]
    public void Se_omiten_las_entradas_sin_ruta()
        => Assert.DoesNotContain("#EXTINF", PlaylistWriter.Build(new[] { It("") }));

    // Se escribe en UTF-8 SIN BOM: algunos reproductores lo toman como parte de la primera linea.
    [Fact]
    public void Se_escribe_en_utf8_sin_bom()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-m3u-" + Guid.NewGuid().ToString("N"));
        var ruta = Path.Combine(dir, "lista.m3u8");
        try
        {
            var err = PlaylistWriter.Write(ruta, new[] { It(@"C:\m\a.mp3", "Rosalía", "Malamente", 150) });
            Assert.Equal("", err);

            var bytes = File.ReadAllBytes(ruta);
            Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no deberia llevar BOM");
            Assert.Contains("Rosalía", Encoding.UTF8.GetString(bytes));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
