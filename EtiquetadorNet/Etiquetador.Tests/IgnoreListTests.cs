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

    // Visto en revisión: si el proceso muere mientras se guarda descartadas.json, el archivo queda
    // truncado; el siguiente arranque lo leía como vacío SIN avisar y el siguiente guardado lo
    // pisaba: las 84 descartadas a mano volvían a aparecer en Enriquecer. Ahora se aparta tal cual,
    // con aviso, y lo que se guarde después no lo toca.
    [Fact]
    public void Un_archivo_truncado_se_aparta_en_vez_de_pisarse()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var f = Path.Combine(dir, "descartadas.json");
            var a = new IgnoreList(f);
            for (var i = 1; i <= 84; i++) a.Add($@"E:\Musica\COMERCIAL\Descartada {i:00}.mp3");
            Assert.Equal("", a.Save());

            var completo = File.ReadAllText(f);
            var truncado = completo[..(completo.Length / 2)];   // el corte a media escritura
            File.WriteAllText(f, truncado);

            var b = new IgnoreList(f);
            Assert.Equal(0, b.Count);
            Assert.NotEqual("", b.AvisoCarga);
            var apartado = Assert.Single(Directory.GetFiles(dir, "descartadas.json.ilegible_*"));
            Assert.Equal(truncado, File.ReadAllText(apartado));

            b.Add(@"E:\Musica\COMERCIAL\Nueva.mp3");
            Assert.Equal("", b.Save());
            Assert.Equal(truncado, File.ReadAllText(apartado));   // lo apartado no se toca
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // La escritura es atómica: va a un temporal y se intercambia, y no deja restos.
    [Fact]
    public void Guardar_no_deja_temporales()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var f = Path.Combine(dir, "aplicadas.json");
            var l = new IgnoreList(f);
            l.Add(@"E:\Musica\a.mp3");
            Assert.Equal("", l.Save());
            l.Add(@"E:\Musica\b.mp3");
            Assert.Equal("", l.Save());   // segunda vez: sustituye al existente

            Assert.Equal(new[] { "aplicadas.json" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
            Assert.Equal(2, new IgnoreList(f).Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Si no se puede ni leer (otro programa lo tiene bloqueado), no se guarda encima: el archivo
    // quizá está perfectamente bien. Y en cuanto se puede leer, se junta lo guardado con lo de la
    // sesión (T10): antes no se reintentaba nunca y las decisiones de la sesión se perdían al cerrar.
    [WindowsFact]
    public void Un_archivo_bloqueado_no_se_pisa()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var f = Path.Combine(dir, "aplicadas.json");
            File.WriteAllText(f, """["E:\\Musica\\a.mp3"]""");

            IgnoreList l;
            using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                l = new IgnoreList(f);
                Assert.NotEqual("", l.AvisoCarga);
                l.Add(@"E:\Musica\b.mp3");
                Assert.NotEqual("", l.Save());                   // se niega y lo dice
            }
            Assert.Equal("""["E:\\Musica\\a.mp3"]""", File.ReadAllText(f));

            // Ya no está bloqueado: se guarda lo de antes MÁS lo de esta sesión.
            Assert.Equal("", l.Save());
            var guardado = new IgnoreList(f);
            Assert.True(guardado.Contains(@"E:\Musica\a.mp3"));
            Assert.True(guardado.Contains(@"E:\Musica\b.mp3"));
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
