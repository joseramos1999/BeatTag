using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// «Ver sus versiones» (P10): todas las piezas que tienes de una canción, y sobre todo las que NO son
/// de ella aunque se llamen parecido. Los nombres son como los de una biblioteca real de DJ: pools que
/// escriben «Título - Artista», mashups sin etiquetas, el usuario que lo subió como artista.
/// </summary>
public class VersionesCancionTests
{
    private static readonly string Raiz = Path.Combine(Path.GetTempPath(), "Musica");

    private static Track T(string relativa, string? artista = null, string? titulo = null) => new()
    {
        FilePath = Path.Combine(Raiz, relativa.Replace('/', Path.DirectorySeparatorChar)),
        Artist = artista,
        Title = titulo,
    };

    private static List<string> Nombres(IEnumerable<PiezaVersion> piezas) => piezas.Select(p => p.Track.FileName).ToList();

    [Fact]
    public void Junta_original_intros_remix_acapella_instrumental_ediciones_y_mashups()
    {
        var original = T("Karol G - Bichota.mp3", "Karol G", "Bichota");
        var bib = new List<Track>
        {
            original,
            T("Karol G - Bichota (Hype Intro).mp3", "Karol G", "Bichota (Hype Intro)"),
            T("Karol G - Bichota (Extended).mp3", "Karol G", "Bichota (Extended)"),
            T("Karol G, Nicki Minaj - Bichota (Tainy Remix).mp3", "Karol G, Nicki Minaj", "Bichota (Tainy Remix)"),
            T("Karol G - Bichota (Acapella).mp3", "Karol G", "Bichota (Acapella)"),
            T("Karol G - Bichota (Instrumental).mp3", "Karol G", "Bichota (Instrumental)"),
            T("Karol G - Bichota (Quick Hit).mp3", "Karol G", "Bichota (Quick Hit)"),
            T("Mashups/Bichota x Provenza (DJ Eros Mashup).mp3"),            // sin ninguna etiqueta
            T("Karol G - Tusa.mp3", "Karol G", "Tusa"),
            T("Otro - Otra.mp3", "Otro", "Otra"),
        };
        var indice = new IndiceVersiones(bib);

        var piezas = indice.De(original);

        Assert.Equal(new[]
        {
            TipoPieza.Original, TipoPieza.Extendida, TipoPieza.Extendida, TipoPieza.Remix,
            TipoPieza.Instrumental, TipoPieza.Acapella, TipoPieza.EdicionDj, TipoPieza.Mezcla,
        }, piezas.Select(p => p.Tipo));
        Assert.True(piezas[0].EsLaConsultada);
        Assert.Equal(1, piezas.Count(p => p.EsLaConsultada));
        Assert.DoesNotContain("Karol G - Tusa.mp3", Nombres(piezas));
        Assert.All(piezas, p => Assert.Equal("", p.Cancion));
    }

    [Fact]
    public void Desde_cualquier_pieza_sale_el_mismo_grupo()
    {
        var bib = new List<Track>
        {
            T("Karol G - Bichota.mp3", "Karol G", "Bichota"),
            T("Karol G - Bichota (Acapella).mp3", "Karol G", "Bichota (Acapella)"),
            T("Karol G - Bichota (Hype Intro).mp3", "Karol G", "Bichota (Hype Intro)"),
        };
        var indice = new IndiceVersiones(bib);

        var desdeAcapella = Nombres(indice.De(bib[1])).OrderBy(x => x);
        var desdeOriginal = Nombres(indice.De(bib[0])).OrderBy(x => x);

        Assert.Equal(desdeOriginal, desdeAcapella);
    }

    [Fact]
    public void Un_titulo_corto_solo_agrupa_si_coincide_algun_artista()
    {
        // «Tusa» tiene cuatro letras: hay demasiadas canciones que se llaman así. Sin artista en
        // común no se da por la misma.
        var tusa = T("Karol G, Nicki Minaj - Tusa.mp3", "Karol G, Nicki Minaj", "Tusa");
        var bib = new List<Track>
        {
            tusa,
            T("Karol G - Tusa (Acapella).mp3", "Karol G", "Tusa (Acapella)"),
            T("Mashups/Karol G - Tusa x Provenza (DJ Mashup).mp3"),               // el artista va en el nombre
            T("Mashups/Tusa x Otra (DJ Mashup).mp3"),                            // sin nada que lo confirme
            T("Otro Artista - Tusa.mp3", "Otro Artista", "Tusa"),
        };
        var indice = new IndiceVersiones(bib);

        var nombres = Nombres(indice.De(tusa));

        Assert.Contains("Karol G - Tusa (Acapella).mp3", nombres);
        Assert.Contains("Karol G - Tusa x Provenza (DJ Mashup).mp3", nombres);
        Assert.DoesNotContain("Tusa x Otra (DJ Mashup).mp3", nombres);
        Assert.DoesNotContain("Otro Artista - Tusa.mp3", nombres);
    }

    [Fact]
    public void Mismo_titulo_y_artistas_distintos_son_canciones_distintas()
    {
        var chainsmokers = T("The Chainsmokers - Closer.mp3", "The Chainsmokers, Halsey", "Closer");
        var bib = new List<Track>
        {
            chainsmokers,
            T("Ne-Yo - Closer.mp3", "Ne-Yo", "Closer"),
            T("Acapellas/Closer (Acapella).mp3"),     // sin artista: el título largo basta
        };
        var indice = new IndiceVersiones(bib);

        var nombres = Nombres(indice.De(chainsmokers));

        Assert.DoesNotContain("Ne-Yo - Closer.mp3", nombres);
        Assert.Contains("Closer (Acapella).mp3", nombres);
    }

    [Fact]
    public void Un_mashup_que_nombra_a_un_artista_es_de_la_cancion_de_ese_artista()
    {
        // Visto en una biblioteca real: hay una «Soltera» de Lunay y otra de Shakira, y el mashup
        // «Shakira x Mohombi - Soltera x Bumpy Ride» es de la de Shakira.
        var lunay = T("Lunay - Soltera.mp3", "Lunay", "Soltera");
        var shakira = T("Shakira - Soltera.mp3", "Shakira", "Soltera");
        var deShakira = T("Mashups/Shakira x Mohombi - Soltera x Bumpy Ride (DJ Mashup).mp3", "Shakira x Mohombi");
        var sinArtista = T("Mashups/Soltera x Otra (DJ Mashup).mp3");
        var indice = new IndiceVersiones(new List<Track> { lunay, shakira, deShakira, sinArtista });

        var deLunay = Nombres(indice.De(lunay));
        var deLaDeShakira = Nombres(indice.De(shakira));

        Assert.DoesNotContain(deShakira.FileName, deLunay);
        Assert.DoesNotContain(shakira.FileName, deLunay);
        Assert.Contains(sinArtista.FileName, deLunay);          // sin nada que lo atribuya, cuenta
        Assert.Contains(deShakira.FileName, deLaDeShakira);
    }

    [Fact]
    public void Las_versiones_de_un_mashup_son_las_de_cada_una_de_sus_partes()
    {
        var mashup = T("Mashups/Karol G - Bichota x Tusa (DJ Mashup).mp3");
        var bib = new List<Track>
        {
            mashup,
            T("Karol G - Bichota.mp3", "Karol G", "Bichota"),
            T("Karol G - Tusa.mp3", "Karol G", "Tusa"),
            T("Karol G - Tusa (Acapella).mp3", "Karol G", "Tusa (Acapella)"),
            T("Karol G - Provenza.mp3", "Karol G", "Provenza"),
        };
        var indice = new IndiceVersiones(bib);

        var piezas = indice.De(mashup);

        Assert.Equal("", piezas.Single(p => p.EsLaConsultada).Cancion);
        Assert.Equal("Bichota", piezas.Single(p => p.Track.FileName == "Karol G - Bichota.mp3").Cancion);
        Assert.Equal("Tusa", piezas.Single(p => p.Track.FileName == "Karol G - Tusa (Acapella).mp3").Cancion);
        Assert.DoesNotContain("Karol G - Provenza.mp3", Nombres(piezas));
    }

    [Fact]
    public void El_artista_de_la_mitad_del_nombre_no_se_toma_por_titulo()
    {
        // Visto en una biblioteca real: «Bad Bunny» quedaba indexado como título de cientos de mashups,
        // y las versiones de cualquiera de ellos eran todos los mashups de Bad Bunny.
        var uno = T("Mashups/Bad Bunny x Daddy Yankee - Rompe x Diles (DJ Mashup).mp3");
        var bib = new List<Track>
        {
            uno,
            T("Mashups/Bad Bunny - Amorfoda x Where She Goes (DJ Mashup).mp3"),
            T("Mashups/Tu No Metes Cabra - Bad Bunny (Live Edit).mp3"),
            T("Bad Bunny - Amorfoda.mp3", "Bad Bunny", "Amorfoda"),
            T("Bad Bunny - Diles.mp3", "Bad Bunny, Ozuna", "Diles"),
            T("Daddy Yankee - Rompe.mp3", "Daddy Yankee", "Rompe"),
            T("Daddy Yankee - Gasolina.mp3", "Daddy Yankee", "Gasolina"),
        };
        var indice = new IndiceVersiones(bib);

        var nombres = Nombres(indice.De(uno));

        Assert.Contains("Bad Bunny - Diles.mp3", nombres);
        Assert.Contains("Daddy Yankee - Rompe.mp3", nombres);
        Assert.DoesNotContain("Bad Bunny - Amorfoda x Where She Goes (DJ Mashup).mp3", nombres);
        Assert.DoesNotContain("Tu No Metes Cabra - Bad Bunny (Live Edit).mp3", nombres);
        Assert.DoesNotContain("Daddy Yankee - Gasolina.mp3", nombres);
    }

    [Fact]
    public void Un_nombre_al_reves_sin_etiquetas_no_junta_canciones_del_mismo_artista()
    {
        // «Título - Artista», como lo dejan varios pools. Sin darse cuenta, «Karol G» sería el título
        // de las dos y saldrían como versiones la una de la otra.
        var bichota = T("Bichota - Karol G.mp3");
        var bib = new List<Track>
        {
            bichota,
            T("Tusa - Karol G.mp3"),
            T("Karol G - Bichota (Acapella).mp3", "Karol G", "Bichota (Acapella)"),
            T("Karol G - Provenza.mp3", "Karol G", "Provenza"),
        };
        var indice = new IndiceVersiones(bib);

        var nombres = Nombres(indice.De(bichota));

        Assert.Contains("Karol G - Bichota (Acapella).mp3", nombres);
        Assert.DoesNotContain("Tusa - Karol G.mp3", nombres);
    }

    [Fact]
    public void Con_etiquetas_el_nombre_al_reves_tampoco_engana()
    {
        var bichota = T("Bichota - Karol G.mp3", "Karol G", "Bichota");
        var tusa = T("Tusa - Karol G.mp3", "Karol G", null);     // sin título: sale del nombre, que va al revés
        var indice = new IndiceVersiones(new List<Track> { bichota, tusa });

        Assert.Single(indice.De(bichota));
        Assert.Single(indice.De(tusa));
    }

    [Fact]
    public void Los_titulos_genericos_no_agrupan_nada()
    {
        var bib = new List<Track>
        {
            T("Artista A - Intro.mp3", "Artista A", "Intro"),
            T("Artista B - Intro.mp3", "Artista B", "Intro"),
            T("Track 01.mp3"),
            T("Grupo C - Track 01.mp3", "Grupo C", "Track 01"),
        };
        var indice = new IndiceVersiones(bib);

        Assert.All(bib, t => Assert.Single(indice.De(t)));
    }

    [Theory]
    [InlineData("Karol G - Bichota.mp3", TipoPieza.Original)]
    [InlineData("Karol G - Bichota (Extended Mix).mp3", TipoPieza.Extendida)]
    [InlineData("Karol G - Bichota (Try It Hype Intro).mp3", TipoPieza.Extendida)]
    [InlineData("Aissa - MUCHACHA feat. Fly Project (Hype Intro).mp3", TipoPieza.Extendida)]   // el punto no es la extensión
    [InlineData("A - B (Tainy Remix).mp3", TipoPieza.Remix)]
    [InlineData("A - B (Extended Remix).mp3", TipoPieza.Remix)]
    [InlineData("A - B (Acapella).mp3", TipoPieza.Acapella)]
    [InlineData("A - B (Acapella Intro).mp3", TipoPieza.Acapella)]
    [InlineData("A - B (Instrumental).mp3", TipoPieza.Instrumental)]
    [InlineData("A - B (Quick Hit).mp3", TipoPieza.EdicionDj)]
    [InlineData("A x B (DJ Mashup).mp3", TipoPieza.Mezcla)]
    [InlineData("A - B (Transition 100-128).mp3", TipoPieza.Mezcla)]
    [InlineData("Mashups/A - B.mp3", TipoPieza.Mezcla)]
    [InlineData("Mashups/A - B (Acapella).mp3", TipoPieza.Acapella)]                        // manda el nombre
    [InlineData("Acapellas/A - B.mp3", TipoPieza.Acapella)]
    public void Que_pieza_es_cada_archivo(string relativa, TipoPieza esperado)
        => Assert.Equal(esperado, IndiceVersiones.Clasificar(T(relativa).FilePath));

    [Fact]
    public void El_indice_se_reutiliza_hasta_que_cambia_la_biblioteca()
    {
        var bib = new List<Track>
        {
            T("Karol G - Bichota.mp3", "Karol G", "Bichota"),
            T("Karol G - Bichota (Acapella).mp3", "Karol G", "Bichota (Acapella)"),
        };
        var cache = new CacheIndiceVersiones();

        var primero = cache.Obtener(bib, out var r1);
        var segundo = cache.Obtener(bib.ToList(), out var r2);    // otra lista con las mismas canciones
        bib[1].Title = "Tusa (Acapella)";                          // el Editor cambia el título sin reescanear
        var tercero = cache.Obtener(bib, out var r3);

        Assert.False(r1);
        Assert.True(r2);
        Assert.Same(primero, segundo);
        Assert.False(r3);
        Assert.NotSame(primero, tercero);
        Assert.Equal(2, cache.Construcciones);
        Assert.Single(tercero.De(bib[0]));
    }

    [Fact]
    public void Las_piezas_salen_con_el_bpm_y_la_tonalidad_de_ahora_aunque_el_indice_se_reutilice()
    {
        var original = T("Karol G - Bichota.mp3", "Karol G", "Bichota");
        var acapella = T("Karol G - Bichota (Acapella).mp3", "Karol G", "Bichota (Acapella)");   // sin BPM ni tonalidad
        var cache = new CacheIndiceVersiones();
        Assert.Equal(2, cache.Versiones(new List<Track> { original, acapella }, original).Count);

        // Se importan BPM y tonalidad de rekordbox: la biblioteca relee la acapella y es otro objeto,
        // con la misma ruta, artista y título. El índice sigue valiendo, pero los datos son los nuevos.
        var releida = T("Karol G - Bichota (Acapella).mp3", "Karol G", "Bichota (Acapella)");
        releida.Bpm = 96;
        releida.Key = "8A";
        var piezas = cache.Versiones(new List<Track> { original, releida }, original);

        Assert.Equal(1, cache.Construcciones);
        var pieza = piezas.Single(p => p.Tipo == TipoPieza.Acapella);
        Assert.Same(releida, pieza.Track);
        Assert.Equal("8A", pieza.Track.KeyCamelot);
        Assert.Same(original, piezas.Single(p => p.EsLaConsultada).Track);
    }
}
