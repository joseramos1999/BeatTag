using Etiquetador.Core;
using Etiquetador.Core.Analysis;

namespace Etiquetador.Tests;

/// <summary>
/// Agrupacion de duplicados por audio. Lo que se comprueba es que encuentra lo que el nombre y los
/// tags no pueden encontrar, y que no junta cosas que solo se parecen en la duracion.
/// </summary>
public class FingerprintDuplicatesTests
{
    private static int[] Huella(int semilla, int n = 120)
    {
        var r = new Random(semilla);
        var f = new int[n];
        for (var i = 0; i < n; i++) f[i] = r.Next(int.MinValue, int.MaxValue);
        return f;
    }

    private static Track Cancion(string archivo, int dur, string artista = "", string titulo = "")
        => new() { FilePath = @"C:\m\" + archivo, Folder = @"C:\m", DurationSeconds = dur, Artist = artista, Title = titulo };

    // El caso que justifica toda la funcion: misma grabacion, nombres y tags completamente
    // distintos. Por titulo no se juntarian jamas.
    [Fact]
    public void Junta_la_misma_grabacion_aunque_los_nombres_no_se_parezcan()
    {
        var f = Huella(1);
        var a = Cancion("pista01.mp3", 200);
        var b = Cancion("Quevedo - Gasolina (Extended).mp3", 202, "Quevedo", "Gasolina");
        var mapa = new Dictionary<string, int[]> { [a.FilePath] = f, [b.FilePath] = (int[])f.Clone() };

        var grupos = FingerprintDuplicates.Find(new[] { a, b }, t => mapa[t.FilePath]);

        Assert.Single(grupos);
        Assert.Equal(2, grupos[0].Tracks.Count);
    }

    // Guarda: durar lo mismo no es sonar igual. Sin esto, la funcion agruparia media biblioteca.
    [Fact]
    public void No_junta_canciones_distintas_que_duran_lo_mismo()
    {
        var a = Cancion("a.mp3", 200);
        var b = Cancion("b.mp3", 200);
        var mapa = new Dictionary<string, int[]> { [a.FilePath] = Huella(1), [b.FilePath] = Huella(2) };

        Assert.Empty(FingerprintDuplicates.Find(new[] { a, b }, t => mapa[t.FilePath]));
    }

    // Si A suena igual que B y B igual que C, los tres van al mismo grupo aunque A y C no lleguen
    // a compararse directamente.
    [Fact]
    public void Encadena_las_copias_en_un_solo_grupo()
    {
        var f = Huella(3);
        var a = Cancion("a.mp3", 200);
        var b = Cancion("b.mp3", 206);
        var c = Cancion("c.mp3", 211);
        var mapa = new Dictionary<string, int[]>
        {
            [a.FilePath] = f, [b.FilePath] = (int[])f.Clone(), [c.FilePath] = (int[])f.Clone(),
        };

        var grupos = FingerprintDuplicates.Find(new[] { a, b, c }, t => mapa[t.FilePath]);

        Assert.Single(grupos);
        Assert.Equal(3, grupos[0].Tracks.Count);
    }

    // Guarda de rendimiento y de sentido: dos copias que se llevan mucho tiempo no se comparan.
    // Si duran muy distinto, o no son la misma version o algo va mal.
    [Fact]
    public void No_compara_lo_que_dura_muy_distinto()
    {
        var f = Huella(4);
        var a = Cancion("corta.mp3", 100);
        var b = Cancion("larga.mp3", 400);
        var mapa = new Dictionary<string, int[]> { [a.FilePath] = f, [b.FilePath] = (int[])f.Clone() };

        Assert.Empty(FingerprintDuplicates.Find(new[] { a, b }, t => mapa[t.FilePath]));
    }

    // Las canciones sin huella calculada se quedan fuera, no rompen nada.
    [Fact]
    public void Las_que_no_tienen_huella_se_ignoran()
    {
        var a = Cancion("a.mp3", 200);
        var b = Cancion("b.mp3", 200);
        var grupos = FingerprintDuplicates.Find(new[] { a, b }, _ => null);
        Assert.Empty(grupos);
    }

    // Para el titulo del grupo se elige la copia que mas datos trae, no la primera que caiga.
    [Fact]
    public void El_grupo_se_titula_con_la_copia_mejor_etiquetada()
    {
        var f = Huella(5);
        var pelada = Cancion("track07.mp3", 200);
        var buena = Cancion("bien.mp3", 201, "Bad Bunny", "Tití Me Preguntó");
        var mapa = new Dictionary<string, int[]> { [pelada.FilePath] = f, [buena.FilePath] = (int[])f.Clone() };

        var grupos = FingerprintDuplicates.Find(new[] { pelada, buena }, t => mapa[t.FilePath]);

        Assert.Single(grupos);
        Assert.Equal("Bad Bunny", grupos[0].Artist);
        Assert.Equal("Tití Me Preguntó", grupos[0].Title);
    }
}
