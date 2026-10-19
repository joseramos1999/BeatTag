using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

public class AnalysisCacheTests
{
    [Fact]
    public void Guarda_y_sirve_si_no_cambia_y_falla_si_cambian_opciones_o_archivo()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "s.mp3");
            Mp3Fixture.WriteMinMp3(mp3);
            var cacheFile = Path.Combine(dir, "an.json");

            var c1 = new AnalysisCache(cacheFile);
            var r = new ProcessResult { FilePath = mp3, Old = "s.mp3", New = "Artista - Tema.mp3", Title = "Tema", Found = true };
            c1.Set(mp3, "sigA", r);
            c1.Save();

            // Nueva instancia (carga de disco): sirve si coincide firma y archivo
            var c2 = new AnalysisCache(cacheFile);
            Assert.NotNull(c2.Get(mp3, "sigA"));
            Assert.Equal("Artista - Tema.mp3", c2.Get(mp3, "sigA")!.New);

            // Distinta firma de opciones -> miss
            Assert.Null(c2.Get(mp3, "sigB"));

            // Archivo modificado (cambia fecha/tamaño) -> miss
            System.Threading.Thread.Sleep(10);
            File.AppendAllText(mp3, "x");
            Assert.Null(c2.Get(mp3, "sigA"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static ProcessResult Resultado(string mp3)
        => new() { FilePath = mp3, Old = Path.GetFileName(mp3), New = "Artista - Tema.mp3", Found = true };

    // Visto en una biblioteca real: con 42 de las 43 carpetas desmarcadas, analizar las 59 canciones
    // de Descargas\MUSICA borró los resultados del resto (analysis-cache.json pasó de ~6 MB a 57.730
    // bytes) y la siguiente pasada completa tuvo que volver a consultarlo todo.
    [Fact]
    public void Analizar_una_carpeta_conserva_lo_analizado_de_las_demas()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var musica = Directory.CreateDirectory(Path.Combine(dir, "E", "Musica", "COMERCIAL")).FullName;
            var descargas = Directory.CreateDirectory(Path.Combine(dir, "Downloads", "MUSICA")).FullName;
            var deLaBiblioteca = Enumerable.Range(1, 5).Select(i => Path.Combine(musica, $"Tema {i:00}.mp3")).ToList();
            var deDescargas = Enumerable.Range(1, 3).Select(i => Path.Combine(descargas, $"Descarga {i:00}.mp3")).ToList();
            foreach (var p in deLaBiblioteca.Concat(deDescargas)) Mp3Fixture.WriteMinMp3(p);

            var cacheFile = Path.Combine(dir, "analysis-cache.json");
            var c = new AnalysisCache(cacheFile);
            foreach (var p in deLaBiblioteca.Concat(deDescargas)) c.Set(p, "sig", Resultado(p));
            c.Save();

            // Pasada solo por Descargas\MUSICA: es lo único que se ha visto y la única raíz recorrida.
            var vistos = new HashSet<string>(deDescargas, StringComparer.OrdinalIgnoreCase);
            c.Prune(vistos, new[] { descargas });
            Assert.Equal("", c.Save());

            var recargada = new AnalysisCache(cacheFile);
            Assert.Equal(8, recargada.Count);
            Assert.All(deLaBiblioteca, p => Assert.NotNull(recargada.Get(p, "sig")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Dentro de la carpeta analizada sí se olvida lo que ya no está.
    [Fact]
    public void Dentro_de_la_carpeta_analizada_se_olvida_lo_que_ya_no_esta()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var viva = Path.Combine(dir, "viva.mp3");
            var borrada = Path.Combine(dir, "borrada.mp3");
            Mp3Fixture.WriteMinMp3(viva);
            Mp3Fixture.WriteMinMp3(borrada);
            var c = new AnalysisCache(Path.Combine(Path.GetDirectoryName(dir)!, Path.GetFileName(dir) + "-an.json"));
            c.Set(viva, "sig", Resultado(viva));
            c.Set(borrada, "sig", Resultado(borrada));

            c.Prune(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { viva }, new[] { dir });

            Assert.Equal(1, c.Count);
            Assert.NotNull(c.Get(viva, "sig"));
            c.Clear();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Una biblioteca que llega vacía (disco desconectado) no aporta ninguna raíz: no se poda nada.
    // Antes, con «vistos» vacío, se tiraba la caché entera.
    [Fact]
    public void Sin_carpetas_recorridas_no_se_poda_nada()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "Tema 01.mp3");
            Mp3Fixture.WriteMinMp3(mp3);
            var c = new AnalysisCache(Path.Combine(dir, "an.json"));
            c.Set(mp3, "sig", Resultado(mp3));

            c.Prune(new HashSet<string>(), Array.Empty<string>());

            Assert.Equal(1, c.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Un archivo de caché dañado (un corte a media escritura) ya no se vacía en silencio y se pisa:
    // se aparta y se avisa.
    [Fact]
    public void Una_cache_danada_se_aparta_y_se_avisa()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var cacheFile = Path.Combine(dir, "analysis-cache.json");
            const string truncado = """{"E:\\Musica\\Tema 01.mp3":{"M":638""";
            File.WriteAllText(cacheFile, truncado);

            var c = new AnalysisCache(cacheFile);

            Assert.Equal(0, c.Count);
            Assert.Contains("ilegible", c.AvisoCarga);
            var apartado = Assert.Single(Directory.GetFiles(dir, "analysis-cache.json.ilegible_*"));
            Assert.Equal(truncado, File.ReadAllText(apartado));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
