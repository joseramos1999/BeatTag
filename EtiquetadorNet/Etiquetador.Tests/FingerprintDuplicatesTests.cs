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

    // ---------- Copias DESPLAZADAS (hallazgo A1) ----------
    //
    // Las pruebas de arriba usan clones exactos, con desfase 0, y por eso no veían el fallo: el
    // índice muestreaba por POSICIÓN (k = 0, 8, 16…), así que si una copia trae unos pocos
    // elementos más de silencio al principio, los valores que indexa cada una caen en posiciones
    // distintas y no comparten ninguno. La pareja nunca llegaba a compararse aunque Similarity la
    // diera por igual. Visto en una biblioteca real: de 13 parejas con el mismo nombre que eran la
    // misma grabación, el índice por posición perdía 3, con desfases +1, −20 y +27.

    /// <summary>
    /// Imita otra codificación del mismo audio: en una fracción de los elementos cambian unos
    /// pocos bits, como pasa de verdad entre dos MP3 del mismo máster.
    /// </summary>
    private static int[] Recodificar(int[] f, int semilla, double fraccionTocada, int bitsPorElemento)
    {
        var r = new Random(semilla);
        var g = (int[])f.Clone();
        for (var i = 0; i < g.Length; i++)
        {
            if (r.NextDouble() >= fraccionTocada) continue;
            for (var k = 0; k < bitsPorElemento; k++) g[i] ^= 1 << r.Next(32);
        }
        return g;
    }

    /// <summary>
    /// Desfase positivo: la copia B empieza <paramref name="d"/> elementos más tarde (a la A le
    /// sobra silencio al principio). Negativo: a la B le sobran |d| elementos delante.
    /// </summary>
    private static int[] Desplazar(int[] f, int d, int semilla)
    {
        if (d >= 0) return f[d..];
        var delante = Huella(semilla, -d);
        return delante.Concat(f).ToArray();
    }

    // Unos 2 minutos de huella, lo que da fpcalc por defecto (~0,124 s por elemento).
    private const int ElementosHuellaReal = 960;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Junta_copias_exactas_desplazadas_unos_elementos(int desfase)
    {
        var f = Huella(100 + desfase, ElementosHuellaReal);
        var a = Cancion("Quevedo - Columbia (Extended).mp3", 186);
        var b = Cancion("Quevedo - Columbia (Extended) [pool].mp3", 186);
        var mapa = new Dictionary<string, int[]> { [a.FilePath] = f, [b.FilePath] = Desplazar(f, desfase, 7) };

        // Por la comparación fina son la misma grabación sin discusión...
        Assert.True(AudioFingerprint.Similarity(mapa[a.FilePath], mapa[b.FilePath]) >= AudioFingerprint.UmbralIgual);

        // ...y por tanto el índice tiene que dejarlas llegar a esa comparación.
        var grupos = FingerprintDuplicates.Find(new[] { a, b }, t => mapa[t.FilePath]);
        Assert.Single(grupos);
        Assert.Equal(2, grupos[0].Tracks.Count);
    }

    // Los tres casos que se perdían en la biblioteca real, con sus desfases y su parecido medido
    // (0,986, 0,913 y 0,905). El ruido de recodificación se ajusta para quedar en esas cifras: con
    // él ya no coinciden todos los valores exactos, solo una parte, que es lo realista.
    [Theory]
    [InlineData(1, 0.45, 1, 0.986)]    // 0,12 s más de silencio en una de las dos copias
    [InlineData(-20, 0.70, 4, 0.913)]  // ~2,5 s más de intro en la otra
    [InlineData(27, 0.60, 5, 0.905)]   // ~3,3 s recortados al principio
    public void Junta_los_desfases_medidos_en_una_biblioteca_real(int desfase, double fraccion, int bits, double parecidoReal)
    {
        var f = Huella(200 + Math.Abs(desfase), ElementosHuellaReal);
        var b0 = Recodificar(f, 300 + Math.Abs(desfase), fraccion, bits);
        var a = Cancion("Bad Bunny - DtMF.mp3", 237);
        var b = Cancion("Bad Bunny - DtMF (Clean).mp3", 240);
        var mapa = new Dictionary<string, int[]> { [a.FilePath] = f, [b.FilePath] = Desplazar(b0, desfase, 9) };

        // El parecido sintético queda a menos de 0,02 del medido: el caso es el mismo.
        var parecido = AudioFingerprint.Similarity(mapa[a.FilePath], mapa[b.FilePath]);
        Assert.InRange(parecido, parecidoReal - 0.02, parecidoReal + 0.02);

        var grupos = FingerprintDuplicates.Find(new[] { a, b }, t => mapa[t.FilePath]);
        Assert.Single(grupos);
        Assert.Equal(2, grupos[0].Tracks.Count);
    }

    // Guarda: muestrear por contenido no puede abrir la puerta a juntar canciones distintas.
    //
    // Visto en revisión: la versión anterior de esta prueba usaba 40 huellas sin ningún valor en
    // común, que tampoco se juntaban con el índice viejo ni con ningún muestreo, así que no probaba
    // nada. Aquí un mashup contiene un tramo del original (300 de 960 valores): comparten valores
    // muestreados de sobra para ser candidatas, y lo que impide juntarlas es el umbral de
    // Similarity, como debe ser.
    [Fact]
    public void Muestrear_por_contenido_no_junta_canciones_distintas()
    {
        var original = Huella(7, ElementosHuellaReal);
        var mashup = Huella(8, ElementosHuellaReal);
        Array.Copy(original, 0, mashup, 0, 300);

        var compartidos = original.Take(300).Where(FingerprintDuplicates.SeIndexa).Distinct().Count();
        Assert.True(compartidos >= FingerprintDuplicates.MinimoValoresComunes, $"solo {compartidos} valores muestreados en común");
        Assert.InRange(AudioFingerprint.Similarity(original, mashup), 0.5, AudioFingerprint.UmbralIgual - 0.05);

        var a = Cancion("Original.mp3", 200);
        var b = Cancion("Mashup.mp3", 202);
        var mapa = new Dictionary<string, int[]> { [a.FilePath] = original, [b.FilePath] = mashup };
        Assert.Empty(FingerprintDuplicates.Find(new[] { a, b }, t => mapa[t.FilePath]));

        // Con el umbral rebajado sí se juntarían: es el umbral, y no el muestreo, lo que las separa.
        Assert.Single(FingerprintDuplicates.Find(new[] { a, b }, t => mapa[t.FilePath], umbral: 0.5));
    }
}
