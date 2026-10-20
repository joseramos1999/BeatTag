using Etiquetador.Core;
using Etiquetador.Core.Analysis;

namespace Etiquetador.Tests;

/// <summary>
/// Hallazgo A14: «Mismo tema, otra edición». El índice de huellas vota el DESFASE de cada pareja y
/// compara solo ahí, así que una Extended con 30 s más de intro se agrupa con su Original, y se
/// distingue de las copias: no es un duplicado para un DJ.
///
/// Las huellas son sintéticas: enteros al azar (dos canciones distintas no comparten casi ningún
/// valor, como pasa con las de verdad), desplazadas para imitar una intro más larga y, donde hace
/// falta, con bits cambiados para imitar otra codificación del mismo máster.
/// </summary>
public class EdicionesPorAudioTests
{
    // ---------- Ayudantes ----------

    /// <summary>Unos 2 minutos de huella, lo que da fpcalc con el análisis de 120 s.</summary>
    private const int Elementos = 960;

    private static int[] Huella(int semilla, int n = Elementos)
    {
        var r = new Random(semilla);
        var f = new int[n];
        for (var i = 0; i < n; i++) f[i] = r.Next(int.MinValue, int.MaxValue);
        return f;
    }

    /// <summary>Otra codificación del mismo audio: en una fracción de los elementos cambian unos pocos bits.</summary>
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

    private static int ElementosDe(double segundos) => (int)Math.Round(segundos / AudioFingerprint.SegundosPorElemento);

    /// <summary>
    /// La huella de una edición con <paramref name="introSeg"/> segundos más de intro: audio nuevo
    /// delante y luego el tema, cortado a lo que analiza fpcalc (los primeros 120 s).
    /// </summary>
    private static int[] ConIntro(int[] tema, double introSeg, int semilla)
        => Huella(semilla, ElementosDe(introSeg)).Concat(tema).Take(Elementos).ToArray();

    private static Track Cancion(string archivo, int segundos, int kbps = 320)
        => new() { FilePath = @"C:\m\" + archivo, Folder = @"C:\m", DurationSeconds = segundos, Bitrate = kbps };

    private static ResultadoPorAudio Agrupar(Dictionary<Track, int[]> huellas, bool incluirEdiciones = true)
        => FingerprintDuplicates.Agrupar(huellas.Keys, t => huellas[t], incluirEdiciones);

    // ---------- El caso que da sentido a la función ----------

    // Original y Extended con 30 s más de intro: hoy quedaban fuera por los dos límites (±10 s de
    // desfase y ±12 s de duración). Ahora van al mismo grupo, en ediciones distintas, y la etiqueta
    // dice cuánto tarda en entrar el tema.
    [Fact]
    public void La_extended_con_intro_mas_larga_es_otra_edicion_del_mismo_grupo()
    {
        var tema = Huella(1);
        var original = Cancion("Bad Bunny - Titi Me Pregunto.mp3", 243);
        var extended = Cancion("Bad Bunny - Titi Me Pregunto (Extended).mp3", 275);
        var res = Agrupar(new() { [original] = tema, [extended] = Recodificar(ConIntro(tema, 30, 2), 3, 0.4, 1) });

        var grupo = Assert.Single(res.Grupos);
        Assert.Equal(2, grupo.Tracks.Count);
        var ao = res.Alineacion[original.FilePath];
        var ae = res.Alineacion[extended.FilePath];
        Assert.NotEqual(ao.Edicion, ae.Edicion);
        Assert.InRange(ae.InicioSeg - ao.InicioSeg, 29.8, 30.2);
        Assert.Equal(1, res.GruposConVariasEdiciones);

        var etiquetas = EdicionesPorAudio.Etiquetas(grupo.Tracks, res.Alineacion);
        Assert.Equal(EdicionesPorAudio.Referencia, etiquetas[original.FilePath]);
        Assert.Equal("Otra edición: empieza 30 s después", etiquetas[extended.FilePath]);
    }

    // Lo que sigue llamándose «duplicado» no cambia: sin ediciones, la Extended no es una copia del
    // Original y no aparece. Es lo que garantiza que «Mismo audio» nunca la proponga para la papelera.
    [Fact]
    public void Sin_ediciones_una_extended_no_es_un_duplicado()
    {
        var tema = Huella(4);
        var original = Cancion("Quevedo - Columbia.mp3", 186);
        var extended = Cancion("Quevedo - Columbia (Extended).mp3", 216);
        var huellas = new Dictionary<Track, int[]> { [original] = tema, [extended] = ConIntro(tema, 30, 5) };

        Assert.Empty(FingerprintDuplicates.Find(huellas.Keys, t => huellas[t]));
        Assert.Empty(Agrupar(huellas, incluirEdiciones: false).Grupos);
    }

    // Intros de DJ habituales: de unos pocos compases a 32 compases a ritmo de reggaeton (~80 s).
    // Con huellas de 120 s, todas dejan al menos 30 s en común con el original.
    [Theory]
    [InlineData(12)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(85)]
    public void Junta_intros_de_hasta_casi_90_segundos(double introSeg)
    {
        var tema = Huella(10 + (int)introSeg);
        var original = Cancion("Original.mp3", 200);
        var intro = Cancion("Original (Hype Intro).mp3", 200 + (int)introSeg);
        var res = Agrupar(new() { [original] = tema, [intro] = Recodificar(ConIntro(tema, introSeg, 11), 12, 0.5, 2) });

        var grupo = Assert.Single(res.Grupos);
        var despues = res.Alineacion[intro.FilePath].InicioSeg - res.Alineacion[original.FilePath].InicioSeg;
        Assert.InRange(despues, introSeg - 0.2, introSeg + 0.2);
        Assert.Equal($"Otra edición: empieza {(int)Math.Round(introSeg)} s después",
                     EdicionesPorAudio.Etiquetas(grupo.Tracks, res.Alineacion)[intro.FilePath]);
    }

    // El límite, dicho en la Ayuda: si lo que comparten dentro de los 120 s analizados no llega a
    // 30 s, no se puede afirmar que sea el mismo tema (puede ser un sample o una transición).
    [Fact]
    public void Una_intro_que_deja_menos_de_30_segundos_en_comun_no_se_agrupa()
    {
        var tema = Huella(20);
        var original = Cancion("Original.mp3", 200);
        var larga = Cancion("Original (Long Intro).mp3", 300);
        var comun = (Elementos - ElementosDe(100)) * AudioFingerprint.SegundosPorElemento;
        Assert.True(comun < FingerprintDuplicates.MinimoSolapeEdicionSeg, $"comparten {comun:F1} s");

        Assert.Empty(Agrupar(new() { [original] = tema, [larga] = ConIntro(tema, 100, 21) }).Grupos);
    }

    // Un Radio Edit empieza igual y corta antes: mismo inicio, otra duración. Tampoco es una copia.
    [Fact]
    public void Mismo_inicio_y_otra_duracion_es_otra_edicion()
    {
        var tema = Huella(30);
        var radio = Cancion("Tema (Radio Edit).mp3", 170);
        var extended = Cancion("Tema (Extended Mix).mp3", 245);
        var huellas = new Dictionary<Track, int[]> { [radio] = tema, [extended] = Recodificar(tema, 31, 0.3, 1) };
        var res = Agrupar(huellas);

        var grupo = Assert.Single(res.Grupos);
        Assert.Empty(FingerprintDuplicates.Find(huellas.Keys, t => huellas[t]));   // no es una copia
        var etiquetas = EdicionesPorAudio.Etiquetas(grupo.Tracks, res.Alineacion);
        Assert.Equal(EdicionesPorAudio.Referencia, etiquetas[radio.FilePath]);
        Assert.Equal("Otra edición: mismo inicio, 1:15 más larga", etiquetas[extended.FilePath]);
    }

    // Copias y ediciones en el mismo grupo: las dos copias del Original comparten edición (una
    // sobra), la Extended va aparte. Sin ediciones, el grupo son solo las dos copias.
    [Fact]
    public void Copias_y_ediciones_conviven_en_el_grupo_cada_una_en_su_edicion()
    {
        var tema = Huella(40);
        var a1 = Cancion("Gasolina.mp3", 193);
        var a2 = Cancion("Gasolina [pool].mp3", 193, 192);
        var ext = Cancion("Gasolina (Extended).mp3", 226);
        var huellas = new Dictionary<Track, int[]>
        {
            [a1] = tema,
            [a2] = Recodificar(tema[1..], 41, 0.45, 1),     // 0,12 s menos de silencio delante
            [ext] = Recodificar(ConIntro(tema, 32, 42), 43, 0.45, 1),
        };

        var res = Agrupar(huellas);
        var grupo = Assert.Single(res.Grupos);
        Assert.Equal(3, grupo.Tracks.Count);
        Assert.Equal(res.Alineacion[a1.FilePath].Edicion, res.Alineacion[a2.FilePath].Edicion);
        Assert.NotEqual(res.Alineacion[a1.FilePath].Edicion, res.Alineacion[ext.FilePath].Edicion);
        Assert.Equal(2, EdicionesPorAudio.Contar(grupo.Tracks, res.Alineacion));

        var copias = Assert.Single(FingerprintDuplicates.Find(huellas.Keys, t => huellas[t]));
        Assert.Equal(new[] { a1.FilePath, a2.FilePath }.Order(), copias.Tracks.Select(t => t.FilePath).Order());
    }

    // Guarda: compartir un tramo corto (un sample de 20 s metido en otra canción) no convierte una
    // canción en otra edición: el parecido se mide en todo lo que comparten, no solo en el tramo.
    [Fact]
    public void Un_sample_corto_dentro_de_otra_cancion_no_la_hace_otra_edicion()
    {
        var tema = Huella(50);
        var otra = Huella(51);
        Array.Copy(tema, 100, otra, 400, ElementosDe(20));
        var a = Cancion("Tema.mp3", 200);
        var b = Cancion("Otra cancion con sample.mp3", 210);

        Assert.Empty(Agrupar(new() { [a] = tema, [b] = otra }).Grupos);
    }

    // Visto en una biblioteca real (dos ediciones en vivo de «Muevelo»): el desfase más votado no
    // es siempre el bueno. Aquí el desfase 0 recibe más votos (valores sueltos que coinciden) pero no
    // casa; el bueno, −66, casa con parecido alto. Con solo el primer pico la pareja se perdía.
    [Fact]
    public void Si_el_desfase_mas_votado_no_casa_se_prueba_el_siguiente()
    {
        var largo = Huella(60, Elementos + 66);
        var a = largo[..Elementos];
        var b = Recodificar(largo[66..], 61, 0.5, 1);
        // Valores sueltos de A en su misma posición: votan el desfase 0 sin que el audio case ahí.
        var plantados = 0;
        for (var i = 0; i < Elementos; i++)
            if (FingerprintDuplicates.SeIndexa(a[i])) { b[i] = a[i]; plantados++; }

        int Votos(int d)
        {
            var n = 0;
            for (var x = d - 1; x <= d + 1; x++)
                for (var i = Math.Max(0, -x); i < Math.Min(a.Length, b.Length - x); i++)
                    if (a[i] == b[i + x] && FingerprintDuplicates.SeIndexa(a[i])) n++;
            return n;
        }
        Assert.True(Votos(0) > Votos(-66), $"el montaje no reproduce el caso: {Votos(0)} votos en 0, {Votos(-66)} en −66");
        Assert.True(AudioFingerprint.SimilitudEnDesfase(a, b, 0).Parecido < AudioFingerprint.UmbralIgual);
        Assert.True(AudioFingerprint.SimilitudEnDesfase(a, b, -66).Parecido >= AudioFingerprint.UmbralIgual);

        var ta = Cancion("Muevelo (Live Mashup).mp3", 207);
        var tb = Cancion("Muevelo (Live Mashup) [otro pool].mp3", 215);
        var grupo = Assert.Single(FingerprintDuplicates.Find(new[] { ta, tb }, t => t == ta ? a : b));
        Assert.Equal(2, grupo.Tracks.Count);
    }

    // Una biblioteca entera de mentira: 1.500 canciones distintas, más 15 ediciones con intros de
    // 15 a 85 s y 15 copias desplazadas. Salen exactamente esos 30 grupos, nada más.
    [Fact]
    public void En_una_biblioteca_grande_encuentra_las_ediciones_y_las_copias_sin_inventar_grupos()
    {
        var huellas = new Dictionary<Track, int[]>();
        for (var i = 0; i < 1500; i++) huellas[Cancion($"tema{i:0000}.mp3", 150 + i % 120)] = Huella(1000 + i);

        var esperadas = new List<(string A, string B, bool Edicion)>();
        for (var i = 0; i < 15; i++)
        {
            var original = huellas.Keys.ElementAt(i * 7);
            var ext = Cancion($"tema{i * 7:0000} (Extended).mp3", original.DurationSeconds + 15 + i * 5);
            huellas[ext] = Recodificar(ConIntro(huellas[original], 15 + i * 5, 5000 + i), 6000 + i, 0.5, 1);
            esperadas.Add((original.FilePath, ext.FilePath, true));

            var otra = huellas.Keys.ElementAt(500 + i * 11);
            var copia = Cancion($"tema{500 + i * 11:0000} [copia].mp3", otra.DurationSeconds + 1);
            huellas[copia] = Recodificar(huellas[otra][(1 + i)..], 7000 + i, 0.5, 1);
            esperadas.Add((otra.FilePath, copia.FilePath, false));
        }

        var res = Agrupar(huellas);
        Assert.Equal(30, res.Grupos.Count);
        foreach (var (a, b, edicion) in esperadas)
        {
            var grupo = Assert.Single(res.Grupos, g => g.Tracks.Any(t => t.FilePath == a));
            Assert.Equal(new[] { a, b }.Order(), grupo.Tracks.Select(t => t.FilePath).Order());
            Assert.Equal(edicion, res.Alineacion[a].Edicion != res.Alineacion[b].Edicion);
        }
        // Primero los grupos con copias que sobran, que es a lo que se viene a Duplicados.
        Assert.All(res.Grupos.Take(15), g => Assert.Equal(1, EdicionesPorAudio.Contar(g.Tracks, res.Alineacion)));

        // Y sin ediciones, solo las 15 copias.
        Assert.Equal(15, FingerprintDuplicates.Find(huellas.Keys, t => huellas[t]).Count);
    }

    // ---------- Una intro de DJ hecha con OTRO tema ----------

    private static Track Etiquetada(string archivo, int segundos, string artista, string titulo)
        => new() { FilePath = @"C:\m\" + archivo, Folder = @"C:\m", DurationSeconds = segundos, Artist = artista, Title = titulo };

    // Visto en una biblioteca real: «Plan B - Choca (Hype Intro)» arranca con 54 s de «Tocate Sola»
    // para entrar desde ella. El audio coincide de verdad, pero no es otra edición de «Tocate Sola».
    [Fact]
    public void Una_hype_intro_hecha_con_otro_tema_no_es_otra_edicion_suya()
    {
        var tocate = Huella(80);
        var original = Etiquetada("Lucho RK, La Pantera - Tocate Sola (Extended).mp3", 176, "Lucho RK, La Pantera", "Tocate Sola");
        var choca = Etiquetada("Plan B - Choca (Hype Intro, Extended).mp3", 228, "Plan B", "Choca");
        var huellas = new Dictionary<Track, int[]> { [original] = tocate, [choca] = Recodificar(ConIntro(tocate, 54, 81), 82, 0.4, 1) };

        Assert.False(EdicionesPorAudio.PuedenSerElMismoTema(original, choca));
        Assert.Empty(Agrupar(huellas).Grupos);
    }

    // La guarda solo se fía del nombre para DESCARTAR una edición de DJ con otro título: la misma
    // intro con el título del tema, aunque el pool escriba distinto el resto, se sigue juntando.
    [Fact]
    public void La_hype_intro_del_mismo_tema_se_junta_aunque_el_pool_escriba_distinto()
    {
        var tema = Huella(83);
        var original = Etiquetada("Bad Bunny - Tití Me Preguntó.mp3", 243, "Bad Bunny", "Tití Me Preguntó");
        var intro = Etiquetada("TITI ME PREGUNTO - BAD BUNNY (Juankii Hype Intro) 107bpm.mp3", 275, "", "");
        var huellas = new Dictionary<Track, int[]> { [original] = tema, [intro] = Recodificar(ConIntro(tema, 32, 84), 85, 0.4, 1) };

        Assert.True(EdicionesPorAudio.PuedenSerElMismoTema(original, intro));
        var grupo = Assert.Single(Agrupar(huellas).Grupos);
        Assert.Equal(2, grupo.Tracks.Count);
    }

    [Theory]
    // Ninguna es edición de DJ: dos ediciones de catálogo con 30 s idénticos son el mismo tema aunque
    // el título no lo diga (un remake retitulado, una versión en otro idioma).
    [InlineData("Artista - Tema Uno (Extended).mp3", "Artista - Otro Nombre.mp3", true)]
    // Una es una intro de DJ y los títulos no comparten nada: es una intro hecha con otro tema.
    [InlineData("Ptazeta, Quevedo - Cuica (Extended).mp3", "Sean Paul - Get Busy (Hype Intro).mp3", false)]
    // Nombre al revés, típico de algunos pools: el título de una está en el nombre de la otra.
    [InlineData("Bad Bunny - Amorfoda.mp3", "Amorfoda - Bad Bunny (Antonio Guevara Live Edit 74Bpm) (Mashup).mp3", true)]
    // Un mashup que contiene el tema lo nombra: se junta, y la Ayuda lo explica.
    [InlineData("Quevedo - Columbia.mp3", "Quevedo x Panic - Columbia x High Hopes (Mashup 100Bpm).mp3", true)]
    // Las palabras vacías y los descriptores no cuentan como título compartido.
    [InlineData("Aitana - Que Bonito (Extended).mp3", "Nena Daconte - Que Me Quedes Tu (Hype Intro, Extended).mp3", false)]
    // Un título sin palabras que comparar («69», «X») no se puede juzgar: manda el audio.
    [InlineData("Nicky Jam, Feid - 69 (Extended).mp3", "Nicky Jam - 69 (Miguel Roma Hype Intro).mp3", true)]
    public void Solo_se_descarta_una_edicion_de_dj_cuyo_titulo_no_aparece_en_la_otra(string a, string b, bool mismoTema)
    {
        var ta = Cancion(a, 200);
        var tb = Cancion(b, 230);
        Assert.Equal(mismoTema, EdicionesPorAudio.PuedenSerElMismoTema(ta, tb));
        Assert.Equal(mismoTema, EdicionesPorAudio.PuedenSerElMismoTema(tb, ta));
    }

    // Las COPIAS no pasan por la guarda: dos copias del mismo archivo de DJ, con nombres que no se
    // parecen en nada, siguen siendo un duplicado.
    [Fact]
    public void La_guarda_del_titulo_no_afecta_a_las_copias()
    {
        var f = Huella(86);
        var a = Cancion("Live Mashups\\track07 (Hype Intro).mp3", 200);
        var b = Cancion("Plan B - Choca (Hype Intro).mp3", 201);
        Assert.False(EdicionesPorAudio.PuedenSerElMismoTema(a, b));
        Assert.Single(FingerprintDuplicates.Find(new[] { a, b }, t => t == a ? f : f[1..]));
    }

    // El grupo se titula con el tema, no con su intro ni con un mashup que lo contiene.
    [Fact]
    public void El_grupo_se_titula_con_el_tema_y_no_con_una_edicion_de_dj()
    {
        var tema = Huella(87);
        var original = Etiquetada("Quevedo - Columbia.mp3", 186, "Quevedo", "Columbia");
        var intro = Etiquetada("Quevedo - Columbia (F3LY Melodic Intro) [100 Bpm].mp3", 202, "Quevedo", "Columbia (F3LY Melodic Intro) [100BPM]");
        var mashup = Etiquetada("Quevedo x Panic - Columbia x High Hopes (Mashup).mp3", 240, "Quevedo x Panic", "Columbia x High Hopes (Juanjo Garcia Mashup 100Bpm)");
        var huellas = new Dictionary<Track, int[]>
        {
            [original] = tema,
            [intro] = Recodificar(ConIntro(tema, 16, 88), 89, 0.4, 1),
            [mashup] = Recodificar(ConIntro(tema, 13, 90), 91, 0.4, 1),
        };

        var grupo = Assert.Single(Agrupar(huellas).Grupos);
        Assert.Equal(3, grupo.Tracks.Count);
        Assert.Equal("Quevedo", grupo.Artist);
        Assert.Equal("Columbia", grupo.Title);

        // Entre dos ediciones de catálogo etiquetadas, la de título menos adornado; sin etiquetas, la que las tiene.
        var extended = Etiquetada("b.mp3", 200, "Quevedo", "Columbia (Extended)");
        var pelada = Cancion("a.mp3", 200);
        Assert.Same(original, FingerprintDuplicates.MuestraParaElTitulo(new[] { extended, intro, original, pelada }));
        Assert.Same(extended, FingerprintDuplicates.MuestraParaElTitulo(new[] { pelada, extended }));

        // Una copia sin artista no gana por tener el título más corto: el grupo saldría como «¿? - TUSA».
        var sinArtista = Etiquetada("TUSA.mp3", 200, "", "TUSA");
        var bien = Etiquetada("Karol G - Tusa.mp3", 200, "Karol G", "Tusa (feat. Nicki Minaj)");
        Assert.Same(bien, FingerprintDuplicates.MuestraParaElTitulo(new[] { sinArtista, bien }));
    }

    // ---------- Comparar en un desfase ----------

    // SimilitudEnDesfase da lo mismo que Similarity en el desfase que esta elige, con el signo
    // explicado: positivo, el audio común aparece más tarde en B.
    [Fact]
    public void Comparar_en_un_desfase_coincide_con_la_busqueda_completa()
    {
        var tema = Huella(70);
        var b = ConIntro(tema, 5, 71);   // B empieza 5 s (40 elementos) más tarde
        var d = ElementosDe(5);

        var (parecido, solape) = AudioFingerprint.SimilitudEnDesfase(tema, b, d);
        Assert.Equal(1.0, parecido, 6);
        Assert.Equal(Elementos - d, solape);
        Assert.Equal(AudioFingerprint.Similarity(tema, b), parecido, 6);
        Assert.True(AudioFingerprint.SimilitudEnDesfase(tema, b, -d).Parecido < 0.6);
        Assert.Equal((0.0, 10), AudioFingerprint.SimilitudEnDesfase(tema, b, Elementos - 10));
    }

    // ---------- Etiquetas ----------

    [Theory]
    [InlineData(32.4, 50, "Otra edición: empieza 32 s después")]
    [InlineData(2.6, 0, "Otra edición: empieza 3 s después")]
    [InlineData(1.0, 65, "Otra edición: mismo inicio, 1:05 más larga")]
    [InlineData(0.0, -40, "Otra edición: mismo inicio, 40 s más corta")]
    [InlineData(0.4, 5, "Otra edición")]          // misma entrada y casi la misma duración: cambia el contenido
    [InlineData(0.0, 12, "Otra edición")]
    public void La_etiqueta_dice_en_que_se_diferencia_de_la_referencia(double despues, int masLarga, string esperada)
        => Assert.Equal(esperada, EdicionesPorAudio.Texto(despues, masLarga));

    // Si se envía a la papelera la edición de referencia, la siguiente que empieza antes pasa a serlo.
    [Fact]
    public void Las_etiquetas_se_rehacen_con_lo_que_queda()
    {
        var original = Cancion("o.mp3", 200);
        var hype = Cancion("h.mp3", 216);
        var ext = Cancion("e.mp3", 232);
        var alineacion = new Dictionary<string, AlineacionAudio>(StringComparer.OrdinalIgnoreCase)
        {
            [original.FilePath] = new(1, 0),
            [hype.FilePath] = new(2, 16.1),
            [ext.FilePath] = new(3, 32.2),
        };

        var todas = EdicionesPorAudio.Etiquetas(new[] { original, hype, ext }, alineacion);
        Assert.Equal(EdicionesPorAudio.Referencia, todas[original.FilePath]);
        Assert.Equal("Otra edición: empieza 16 s después", todas[hype.FilePath]);
        Assert.Equal("Otra edición: empieza 32 s después", todas[ext.FilePath]);

        var sinOriginal = EdicionesPorAudio.Etiquetas(new[] { hype, ext }, alineacion);
        Assert.Equal(EdicionesPorAudio.Referencia, sinOriginal[hype.FilePath]);
        Assert.Equal("Otra edición: empieza 16 s después", sinOriginal[ext.FilePath]);

        // Con una sola edición son copias y no hay nada que distinguir.
        Assert.All(EdicionesPorAudio.Etiquetas(new[] { ext }, alineacion).Values, e => Assert.Equal("", e));
        Assert.Equal(1, EdicionesPorAudio.Contar(new[] { original, hype }, null));
    }
}
