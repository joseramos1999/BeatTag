using Etiquetador.Core;

namespace Etiquetador.Tests;

public class ScanCacheTests
{
    [Fact]
    public void Sirve_de_cache_si_no_cambia_y_relee_si_cambia()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "s.mp3");
            Mp3Fixture.WriteMinMp3(mp3);
            Mp3Fixture.SetTags(mp3, "Titulo A", "Artista", "Album");
            var cacheFile = Path.Combine(dir, "cache.json");

            var c1 = new ScanCache(cacheFile);
            Assert.Equal("Titulo A", c1.Read(mp3).Title);   // miss -> lee
            c1.Save();

            // Nueva instancia carga la caché; sin cambios -> sirve el valor cacheado
            var c2 = new ScanCache(cacheFile);
            Assert.Equal("Titulo A", c2.Read(mp3).Title);

            // Cambia el archivo (nuevo tag) -> se re-lee.
            //
            // La fecha se adelanta a mano a propósito: «Titulo A» y «Titulo B» ocupan lo mismo, y en
            // una máquina rápida las dos escrituras pueden caer en la misma marca de tiempo del
            // sistema de archivos. Entonces la caché da el archivo por intacto y la prueba falla sin
            // que haya nada roto: pasó en la CI de Windows. Lo que se quiere comprobar es que un
            // archivo MODIFICADO se relee, y eso es justo lo que queda escrito así.
            Mp3Fixture.SetTags(mp3, "Titulo B", "Artista", "Album");
            File.SetLastWriteTimeUtc(mp3, File.GetLastWriteTimeUtc(mp3).AddSeconds(2));
            Assert.Equal("Titulo B", c2.Read(mp3).Title);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Prune_quita_los_que_faltan_dentro_de_las_carpetas_escaneadas()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "borrada.mp3");
            Mp3Fixture.WriteMinMp3(mp3);
            var cacheFile = Path.Combine(Path.GetDirectoryName(dir)!, Path.GetFileName(dir) + "-cache.json");
            var c = new ScanCache(cacheFile);
            c.Read(mp3);
            c.Prune(new HashSet<string>(), new[] { dir });   // se escaneó su carpeta y ya no estaba
            c.Save();

            Assert.DoesNotContain("borrada.mp3", File.ReadAllText(cacheFile));
            File.Delete(cacheFile);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Visto en una biblioteca real: la biblioteca se quedó un rato con otra carpeta, el escaneo
    // borró las 15.000 entradas de la de siempre y volver a ella costó 331 s en vez de ~4 s.
    [Fact]
    public void Prune_conserva_lo_de_carpetas_que_no_se_han_escaneado()
    {
        var dir = Mp3Fixture.NewTempDir();
        var otra = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "de-la-biblioteca.mp3");
            Mp3Fixture.WriteMinMp3(mp3);
            var cacheFile = Path.Combine(Path.GetDirectoryName(dir)!, Path.GetFileName(dir) + "-cache.json");
            var c = new ScanCache(cacheFile);
            c.Read(mp3);
            c.Prune(new HashSet<string>(), new[] { otra });   // solo se escaneó OTRA carpeta
            c.Save();

            Assert.Contains("de-la-biblioteca.mp3", File.ReadAllText(cacheFile));
            File.Delete(cacheFile);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
            try { Directory.Delete(otra, true); } catch { }
        }
    }
}
