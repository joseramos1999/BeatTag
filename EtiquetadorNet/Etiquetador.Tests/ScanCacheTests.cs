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
    public void Prune_quita_los_ausentes()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "s.mp3");
            Mp3Fixture.WriteMinMp3(mp3);
            var cacheFile = Path.Combine(dir, "cache.json");
            var c = new ScanCache(cacheFile);
            c.Read(mp3);
            c.Prune(new HashSet<string>());   // ninguno vivo -> se vacía
            c.Save();
            var reloaded = new ScanCache(cacheFile);
            // Tras prune+save, borrar el mp3 y leer devuelve Track vacío (no había entrada cacheada válida)
            File.Delete(mp3);
            Assert.True(string.IsNullOrEmpty(reloaded.Read(mp3).Title));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
