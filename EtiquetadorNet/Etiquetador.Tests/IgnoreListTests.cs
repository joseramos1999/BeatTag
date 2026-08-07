using Etiquetador.Core.Pipeline;
using Xunit;

namespace Etiquetador.Tests;

// Canciones descartadas a mano ("Quitar de la lista"): persisten entre sesiones.
public class IgnoreListTests
{
    [Fact]
    public void Descartar_persiste_al_recargar()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var f = Path.Combine(dir, "descartadas.json");
            var a = new IgnoreList(f);
            a.Add(@"C:\musica\tema.mp3");
            a.Save();

            var b = new IgnoreList(f);   // nueva sesión
            Assert.True(b.Contains(@"C:\musica\tema.mp3"));
            Assert.Equal(1, b.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void No_distingue_mayusculas_en_la_ruta()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var l = new IgnoreList(Path.Combine(dir, "d.json"));
            l.Add(@"C:\Musica\Tema.mp3");
            Assert.True(l.Contains(@"c:\musica\tema.mp3"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Duplicados_no_suman()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var l = new IgnoreList(Path.Combine(dir, "d.json"));
            l.Add(@"C:\a.mp3");
            l.Add(@"C:\a.mp3");
            Assert.Equal(1, l.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Prune_olvida_las_que_ya_no_existen()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var l = new IgnoreList(Path.Combine(dir, "d.json"));
            l.Add(@"C:\viva.mp3");
            l.Add(@"C:\borrada.mp3");
            l.Prune(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\viva.mp3" });
            Assert.True(l.Contains(@"C:\viva.mp3"));
            Assert.False(l.Contains(@"C:\borrada.mp3"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Clear_vacia_y_borra_el_archivo()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var f = Path.Combine(dir, "d.json");
            var l = new IgnoreList(f);
            l.Add(@"C:\a.mp3");
            l.Save();
            Assert.True(File.Exists(f));

            l.Clear();
            Assert.Equal(0, l.Count);
            Assert.False(File.Exists(f));
            Assert.Equal(0, new IgnoreList(f).Count);   // y no reaparece al recargar
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
