using System.Text.Json.Nodes;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// Puntuación de los catálogos con los casos reales de los registros: MusicBrainz sin mirar el
/// título (C1), la instrumental o la acapella tomada por la canción (C2), el respaldo aproximado
/// que rescataba lo rechazado (C3) e iTunes con los umbrales viejos (C5).
/// </summary>
public class CatalogosPuntuacionTests
{
    /// <summary>Umbral de revisión de Enriquecer: por debajo, la propuesta sale marcada para revisar.</summary>
    private const double UmbralRevision = 2;

    private static JsonArray Deezer(params (string Artista, string Titulo, int Dur)[] items)
    {
        var arr = new JsonArray();
        var id = 100;
        foreach (var (a, t, d) in items)
            arr.Add(new JsonObject
            {
                ["id"] = id++, ["title"] = t, ["duration"] = d,
                ["artist"] = new JsonObject { ["name"] = a },
                ["album"] = new JsonObject { ["title"] = "Album", ["id"] = 1, ["cover_big"] = "http://c/big.jpg" },
            });
        return arr;
    }

    private static JsonArray Itunes(params (string Artista, string Titulo, int Ms)[] items)
    {
        var arr = new JsonArray();
        foreach (var (a, t, ms) in items)
            arr.Add(new JsonObject
            {
                ["artistName"] = a, ["trackName"] = t, ["collectionName"] = "Album", ["trackTimeMillis"] = ms,
                ["releaseDate"] = "2010-01-01T00:00:00Z", ["primaryGenreName"] = "Pop",
            });
        return arr;
    }

    private static JsonArray Spotify(params (string Artista, string Titulo, int Ms)[] items)
    {
        var arr = new JsonArray();
        foreach (var (a, t, ms) in items)
            arr.Add(new JsonObject
            {
                ["name"] = t, ["duration_ms"] = ms,
                ["artists"] = new JsonArray { new JsonObject { ["name"] = a, ["id"] = "aid" } },
                ["album"] = new JsonObject { ["name"] = "Album", ["release_date"] = "2010-01-01" },
            });
        return arr;
    }

    private static JsonNode Mb(params (int Score, string Titulo, string[] Creditos)[] grabaciones)
    {
        var recs = new JsonArray();
        foreach (var (s, t, cr) in grabaciones)
        {
            var credito = new JsonArray();
            foreach (var c in cr) credito.Add(new JsonObject { ["name"] = c });
            recs.Add(new JsonObject
            {
                ["score"] = s, ["title"] = t, ["artist-credit"] = credito,
                ["releases"] = new JsonArray { new JsonObject { ["title"] = "Disco", ["date"] = "2008-05-01" } },
            });
        }
        return new JsonObject { ["recordings"] = recs };
    }

    // ------------------------------------------------------------------ C1 · MusicBrainz

    [Fact]
    public void MusicBrainz_no_acepta_otra_cancion_del_mismo_artista_aunque_saque_100()
    {
        // Consulta libre «Ñengo Flow Bellaqueo 4 Life» (cache/c7/c7736a74…): la primera, con 100,
        // era «Yomo con Ñengo»; las demás, 81. Se proponía con la casilla de aplicar marcada.
        var r = Mb((100, "Yomo con Ñengo", new[] { "Ñengo Flow", "Yomo" }),
                   (81, "Hoy Te Vi", new[] { "Ñengo Flow" }),
                   (81, "Dale Gas", new[] { "Ñengo Flow" }));
        Assert.Null(MusicBrainzProvider.PickBest(r, "Ñengo Flow", "Bellaqueo 4 Life", consultaLibre: true));
    }

    [Fact]
    public void MusicBrainz_en_la_consulta_libre_gana_la_que_casa_aunque_no_sea_la_primera()
    {
        // La puntuación de la consulta libre es relativa: no dice nada. Lo que decide es el título.
        var r = Mb((100, "Yomo con Ñengo", new[] { "Ñengo Flow", "Yomo" }),
                   (81, "Bellaqueo 4 Life", new[] { "Ñengo Flow" }));
        var pick = MusicBrainzProvider.PickBest(r, "Ñengo Flow", "Bellaqueo 4 Life", consultaLibre: true);
        Assert.NotNull(pick);
        Assert.Equal("Bellaqueo 4 Life", pick!.Title);
        Assert.True(pick.Score >= UmbralRevision);
    }

    [Theory]
    [InlineData("Garmiani", "Watch out for fogo", "Fogo", "Garmiani,Julimar Santos")]
    [InlineData("Gayle x Eric Prydz", "abcdepjanoo", "Eric Prydz", "Eric Prydz")]
    [InlineData("Tom Tyger", "Papi Bongo", "Coco Bongo (Mashup)", "Tom Tyger,Nico de Andrea")]
    [InlineData("Kartashow", "Золото (Rakurs & Ramirez Remix)", "(Rakurs & Ramirez Remix)", "KARTASHOW")]
    public void MusicBrainz_casos_reales_que_se_aplicaban_mal(string artista, string titulo, string titMb, string creditos)
    {
        var r = Mb((100, titMb, creditos.Split(',')));
        Assert.Null(MusicBrainzProvider.PickBest(r, artista, titulo, consultaLibre: true));
        Assert.Null(MusicBrainzProvider.PickBest(r, artista, titulo));
    }

    [Fact]
    public void MusicBrainz_sin_credito_de_artista_no_se_acepta()
    {
        // Con el crédito vacío, «nka.Contains("")» era cierto y pasaba la comprobación.
        var r = Mb((100, "Gasolina", Array.Empty<string>()));
        Assert.Null(MusicBrainzProvider.PickBest(r, "Daddy Yankee", "Gasolina"));
    }

    [Fact]
    public void MusicBrainz_el_corte_de_85_sigue_valiendo_en_la_consulta_estricta()
    {
        var r = Mb((84, "Gasolina", new[] { "Daddy Yankee" }));
        Assert.Null(MusicBrainzProvider.PickBest(r, "Daddy Yankee", "Gasolina"));
        Assert.NotNull(MusicBrainzProvider.PickBest(r, "Daddy Yankee", "Gasolina", consultaLibre: true));
    }

    [Fact]
    public void MusicBrainz_acepta_la_version_con_sufijo_del_mismo_tema()
    {
        var r = Mb((97, "Gasolina (DJ Buddha remix)", new[] { "Daddy Yankee" }));
        Assert.NotNull(MusicBrainzProvider.PickBest(r, "Daddy Yankee", "Gasolina"));
    }

    [Theory]
    [InlineData("Watch out for fogo", "Fogo", false)]     // «fogo» es menos de la mitad: otra canción
    [InlineData("Gasolina", "Gasolina (Remix)", true)]   // el limpiado deja los dos iguales
    [InlineData("Cuenta Regresiva", "CUENTA REGREVISA", true)]  // errata (0,987)
    [InlineData("Fie", "Fiel", false)]                     // demasiado corto para fiarse
    public void Titulo_verificado(string archivo, string catalogo, bool casa)
        => Assert.Equal(casa, ReglasCatalogo.TituloVerificado(archivo, catalogo, out _));

    // ------------------------------------------------------------------ C2 · instrumental / acapella

    [Fact]
    public void Deezer_Shut_Up_vocal_no_pierde_contra_la_instrumental()
    {
        // Traza real: «Shut Up» = 8 [titulo exacto +10, dur Δ15s -2] frente a «Shut Up
        // (Instrumental)» = 12,64 [titulo exacto sin el sufijo +8, largo -0,36, dur =2s +5].
        var data = Deezer(("Black Eyed Peas", "Shut Up", 311), ("Black Eyed Peas", "Shut Up (Instrumental)", 298));
        var pick = DeezerProvider.SelectBest(data, "Black Eyed Peas", "Shut Up", false, false, 296, false, out var sc);
        Assert.Equal("Shut Up", pick?["title"]?.ToString());
        Assert.Equal(8, sc, 2);
    }

    [Fact]
    public void Deezer_si_el_archivo_es_la_instrumental_gana_la_instrumental()
    {
        var data = Deezer(("Black Eyed Peas", "Shut Up", 311), ("Black Eyed Peas", "Shut Up (Instrumental)", 298));
        var pick = DeezerProvider.SelectBest(data, "Black Eyed Peas", "Shut Up", false, false, 296, false, out _, wantTool: true);
        Assert.Equal("Shut Up (Instrumental)", pick?["title"]?.ToString());
        // Y si el título que llega aún lo dice, se deduce solo.
        pick = DeezerProvider.SelectBest(data, "Black Eyed Peas", "Shut Up (Instrumental)", false, false, 296, false, out _);
        Assert.Equal("Shut Up (Instrumental)", pick?["title"]?.ToString());
    }

    [Fact]
    public void Deezer_Mal_De_Amores_la_acapella_sola_queda_para_revisar()
    {
        // «Juan Magan - Mal De Amores (Extended).mp3» -> «… (Acapella) (Extended)» con confianza 12,8.
        var data = Deezer(("Juan Magan", "Mal De Amores (Acapella)", 240));
        DeezerProvider.SelectBest(data, "Juan Magan", "Mal De Amores", false, false, 240, false, out var sc);
        Assert.True(sc < UmbralRevision, $"la acapella no debería aplicarse sola: {sc}");

        var conVocal = Deezer(("Juan Magan", "Mal De Amores (Acapella)", 240), ("Juan Magan", "Mal De Amores", 236));
        var pick = DeezerProvider.SelectBest(conVocal, "Juan Magan", "Mal De Amores", false, false, 240, false, out _);
        Assert.Equal("Mal De Amores", pick?["title"]?.ToString());
    }

    [Fact]
    public void Deezer_Timberland_el_respaldo_aproximado_tampoco_coge_la_instrumental()
    {
        // «Timberland» (errata de Timbaland) entra por el respaldo aproximado del artista.
        var soloInstrumental = Deezer(("Timbaland", "The Way I Are (Instrumental)", 0));
        Assert.Null(DeezerProvider.SelectBest(soloInstrumental, "Timberland", "The Way I Are", false, false, 0, false, out _));

        var ambas = Deezer(("Timbaland", "The Way I Are (Instrumental)", 0), ("Timbaland", "The Way I Are", 0));
        var pick = DeezerProvider.SelectBest(ambas, "Timberland", "The Way I Are", false, false, 0, false, out _);
        Assert.Equal("The Way I Are", pick?["title"]?.ToString());
    }

    [Fact]
    public void Itunes_y_Spotify_aplican_la_misma_regla()
    {
        var it = Itunes(("Black Eyed Peas", "Shut Up (Instrumental)", 298_000));
        ItunesProvider.SelectBest(it, "Black Eyed Peas", "Shut Up", 296, false, out var scIt);
        Assert.True(scIt < UmbralRevision, $"iTunes: {scIt}");

        var sp = Spotify(("Black Eyed Peas", "Shut Up (Instrumental)", 298_000));
        SpotifyProvider.SelectBest(sp, "Black Eyed Peas", "Shut Up", false, false, 296, false, out var scSp);
        Assert.True(scSp < UmbralRevision, $"Spotify: {scSp}");

        var ambas = Itunes(("Black Eyed Peas", "Shut Up (Instrumental)", 298_000), ("Black Eyed Peas", "Shut Up", 311_000));
        Assert.Equal("Shut Up", ItunesProvider.SelectBest(ambas, "Black Eyed Peas", "Shut Up", 296, false, out _)?["trackName"]?.ToString());
    }

    [Theory]
    [InlineData("Shut Up (Instrumental)", true)]
    [InlineData("Mal De Amores (Acapella)", true)]
    [InlineData("Feliz Navidad (A Cappella)", true)]
    [InlineData("Tema (Inst.)", true)]
    [InlineData("Shut Up", false)]
    [InlineData("Instinto", false)]
    [InlineData("Mr. Brightside", false)]
    public void Reconoce_las_versiones_herramienta(string titulo, bool es)
        => Assert.Equal(es, ReglasCatalogo.EsHerramienta(titulo));

    // ------------------------------------------------------------------ C3 · respaldo aproximado

    [Fact]
    public void Beso_a_Beso_el_respaldo_no_rescata_el_remix_rechazado()
    {
        // Traza real: «Beso a Beso (Hot Plug Beats Remix)» = -11,51 [remix no pedido -6, …, dur
        // Δ84s -5] -> RECHAZADO -> y aun así «Deezer=… (5, 247s)» por el respaldo.
        var data = Deezer(("Los Chicanos del Sur", "Beso a Beso (Hot Plug Beats Remix)", 247));
        var pick = DeezerProvider.SelectBest(data, "Los Chicanos del Sur", "Beso a Beso", false, false, 331, false, out _);
        Assert.Null(pick);
    }

    [Fact]
    public void Fie_el_respaldo_no_convierte_un_original_en_Fiel_Remix()
    {
        // «Wisin, Jhay Cortez x Axwell & Ingrosso - Fie.mp3» (229 s) -> «Fiel (Remix)» de 349 s.
        var data = Deezer(("Wisin", "Fiel (Remix)", 349));
        Assert.Null(DeezerProvider.SelectBest(data, "Wisin", "Fie", false, false, 229, false, out _));
    }

    [Fact]
    public void Una_errata_en_el_artista_sigue_entrando_si_la_duracion_cuadra()
    {
        var data = Deezer(("Bad Bunny", "Titi Me Pregunto", 243));
        Assert.NotNull(DeezerProvider.SelectBest(data, "Bad Buny", "Titi Me Pregunto", false, false, 244, false, out var sc));
        Assert.Equal(5, sc);
    }

    [Fact]
    public void Una_errata_en_el_artista_no_basta_si_la_duracion_contradice()
    {
        var data = Deezer(("Bad Bunny", "Titi Me Pregunto", 300));
        Assert.Null(DeezerProvider.SelectBest(data, "Bad Buny", "Titi Me Pregunto", false, false, 243, false, out _));
    }

    [Fact]
    public void El_respaldo_acepta_el_remix_cuando_se_pide()
    {
        var data = Deezer(("Bad Bunny", "Titi Me Pregunto (Remix)", 0));
        Assert.Null(DeezerProvider.SelectBest(data, "Bad Buny", "Titi Me Pregunto", false, false, 0, false, out _));
        Assert.NotNull(DeezerProvider.SelectBest(data, "Bad Buny", "Titi Me Pregunto", true, false, 0, false, out _));
    }

    // ------------------------------------------------------------------ C5 · iTunes

    [Theory]
    [InlineData("Fisher", "Just Feel Right", "FISHER", "Just Feels Tight")]               // 0,9227
    [InlineData("Amaral", "Sin ti no soy Reload", "Amaral", "Sin Ti No Soy Nada")]        // 0,9214
    [InlineData("Stromae", "Alors Danse B", "Stromae", "Alors on danse")]                 // 0,9185
    public void Itunes_ya_no_cuela_canciones_distintas_que_empiezan_igual(string art, string archivo, string artCat, string titCat)
    {
        var res = Itunes((artCat, titCat, 200_000));
        Assert.Null(ItunesProvider.SelectBest(res, art, archivo, 200, false, out _));
    }

    [Theory]
    [InlineData("Cosculluela", "Prrum", "Prrrum")]                     // 0,961
    [InlineData("Alguien", "CUENTA REGREVISA", "Cuenta Regresiva")]    // 0,987
    public void Itunes_sigue_aceptando_las_erratas_de_verdad(string art, string archivo, string catalogo)
    {
        var res = Itunes((art, catalogo, 200_000));
        Assert.NotNull(ItunesProvider.SelectBest(res, art, archivo, 200, false, out var sc));
        Assert.True(sc >= 4);
    }

    [Fact]
    public void Itunes_el_respaldo_tampoco_coge_un_remix_no_pedido()
    {
        var res = Itunes(("Bad Bunny", "Titi Me Pregunto (Remix)", 0));
        Assert.Null(ItunesProvider.SelectBest(res, "Bad Buny", "Titi Me Pregunto", 0, false, out _));
        Assert.NotNull(ItunesProvider.SelectBest(res, "Bad Buny", "Titi Me Pregunto", 0, false, out _, wantRemix: true));
    }
}
