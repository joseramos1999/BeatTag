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
}
