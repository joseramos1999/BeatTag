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

            // Cambia el archivo (nuevo tag -> cambia mtime/tamaño) -> se re-lee
            Mp3Fixture.SetTags(mp3, "Titulo B", "Artista", "Album");
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
