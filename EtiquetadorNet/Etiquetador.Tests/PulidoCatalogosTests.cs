using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Pipeline;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// Tanda 2, equipo «pulido_nucleo»: los detalles que dejaron los revisores de la tanda 1 en la capa
/// de catálogos y red, y su integración en FileProcessor. Todo sobre redes y catálogos falsos:
/// ninguna prueba sale a internet.
/// </summary>
public class PulidoCatalogosTests
{
    private const string BusquedaDeezer = "https://api.deezer.com/search?q=Black%20Eyed%20Peas%20Shut%20Up&limit=8";

    private static async Task<ProcessResult> Procesar(CatalogoFalso cat, string nombre, ProcessOptions o, string? subcarpeta = null)
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var carpeta = subcarpeta == null ? dir : Directory.CreateDirectory(Path.Combine(dir, subcarpeta)).FullName;
            var p = Path.Combine(carpeta, nombre);
            Mp3Fixture.WriteMinMp3(p);
            return await cat.Procesador(dir).ProcessAsync(p, false, o);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static double Confianza(ProcessResult r) => double.Parse(r.Score, CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ C2 · wantTool en FileProcessor

    // El título con el que se busca llega sin «(Acapella)»: sin decírselo al catálogo, una acapella
    // de verdad cuya única coincidencia es esa versión salía castigada como «no pedida».
    [Fact]
    public async Task Una_acapella_de_verdad_casa_con_la_acapella_del_catalogo()
    {
        var cat = new CatalogoFalso()
            .Responde(CatalogoFalso.BusquedaDeezer("Juan Magan", "Mal De Amores"),
                      CatalogoFalso.DeezerBusqueda(70, "Juan Magan", "Mal De Amores (Acapella)", 1));

        var r = await Procesar(cat, "Juan Magan - Mal De Amores (Acapella).mp3", new ProcessOptions { Deezer = true });

        Assert.True(r.Found);
        Assert.True(Confianza(r) >= 2, r.Score);
        Assert.Equal("Juan Magan - Mal De Amores (Acapella).mp3", r.New);
    }

    // Igual con la carpeta: lo que vive en «ACAPELLAS» es una acapella aunque el nombre no lo diga.
    [Fact]
    public async Task La_carpeta_de_acapellas_tambien_cuenta()
    {
        var cat = new CatalogoFalso()
            .Responde(CatalogoFalso.BusquedaDeezer("Enur", "Calabria"),
                      CatalogoFalso.DeezerBusqueda(71, "Enur", "Calabria 2007 (Acapella)", 1));

        var r = await Procesar(cat, "Enur - Calabria 2007.mp3", new ProcessOptions { Deezer = true }, subcarpeta: "ACAPELLAS");

        Assert.True(r.Found);
        Assert.True(Confianza(r) >= 2, r.Score);
    }

    // Una «Acapella Intro» lleva la canción entera: no es una herramienta, y la acapella del
    // catálogo NO es la canción del archivo. Sale, como mucho, con confianza baja para revisar.
    [Fact]
    public async Task Una_edicion_acapella_intro_no_casa_con_la_acapella_del_catalogo()
    {
        var cat = new CatalogoFalso()
            .Responde(CatalogoFalso.BusquedaDeezer("Juan Magan", "Mal De Amores"),
                      CatalogoFalso.DeezerBusqueda(72, "Juan Magan", "Mal De Amores (Acapella)", 1));

        var r = await Procesar(cat, "Juan Magan - Mal De Amores (Acapella Intro).mp3", new ProcessOptions { Deezer = true });

        Assert.True(!r.Found || Confianza(r) < 2, $"confianza {r.Score}");
    }

    // Y la versión vocal sigue sin coger la instrumental (C2 de la tanda 1): FileProcessor no pide
    // herramienta para un archivo normal.
    [Fact]
    public async Task Un_archivo_normal_sigue_cogiendo_la_vocal()
    {
        var data = new JsonArray(
            new JsonObject { ["id"] = 1, ["title"] = "Shut Up", ["duration"] = 1, ["artist"] = new JsonObject { ["name"] = "Black Eyed Peas" }, ["album"] = new JsonObject { ["title"] = "Elephunk", ["id"] = 5 } },
            new JsonObject { ["id"] = 2, ["title"] = "Shut Up (Instrumental)", ["duration"] = 1, ["artist"] = new JsonObject { ["name"] = "Black Eyed Peas" }, ["album"] = new JsonObject { ["title"] = "Shut Up", ["id"] = 6 } });
        var cat = new CatalogoFalso().Responde(CatalogoFalso.BusquedaDeezer("Black Eyed Peas", "Shut Up"), new JsonObject { ["data"] = data });

        var r = await Procesar(cat, "Black Eyed Peas - Shut Up.mp3", new ProcessOptions { Deezer = true });

        Assert.Equal("Black Eyed Peas - Shut Up.mp3", r.New);
        Assert.Equal("Elephunk", r.Album);
    }

    [Theory]
    [InlineData("Mal De Amores (Acapella)", true)]
    [InlineData("Popular (A Cappella)", true)]
    [InlineData("Eat Sleep Rave Repeat (Acappella)", true)]
    [InlineData("Faith (Instrumental)", true)]
    [InlineData("Tusa (Clean Acapella)", true)]
    [InlineData("Mal De Amores (Acapella In)", false)]
    [InlineData("Mal De Amores (Acapella Intro)", false)]
    [InlineData("Mal De Amores (Acapella In & Out)", false)]
    [InlineData("Mal De Amores (Aca Out)", false)]
    [InlineData("Mal De Amores (Intro Acapella)", false)]
    [InlineData("Mal De Amores (Extended)", false)]
    public void Que_es_una_herramienta_y_que_no(string texto, bool herramienta)
        => Assert.Equal(herramienta, ReglasCatalogo.EsHerramienta(texto));

    [Fact]
    public void La_carpeta_dice_si_es_una_herramienta()
    {
        Assert.True(FileProcessor.DescriptorDeHerramienta("Enur - Calabria 2007", @"E:\Musica\ACAPELLAS\Enur - Calabria 2007.mp3"));
        Assert.True(FileProcessor.DescriptorDeHerramienta("Faith", @"E:\Musica\Instrumentales\Faith.mp3"));
        Assert.False(FileProcessor.DescriptorDeHerramienta("Faith", @"E:\Musica\COMERCIAL\Faith.mp3"));
    }

    // ------------------------------------------------------------------ C5 · wantRemix a iTunes

    // El respaldo aproximado de iTunes descarta los remixes salvo que el archivo lo sea; FileProcessor
    // no se lo decía, y un remix con una errata en el artista ya no entraba por iTunes.
    [Fact]
    public async Task Un_remix_con_una_errata_en_el_artista_entra_por_itunes()
    {
        var resultado = new JsonObject
        {
            ["results"] = new JsonArray(new JsonObject
            {
                ["artistName"] = "Bad Bunny", ["trackName"] = "Titi Me Pregunto (Tiesto Remix)", ["collectionName"] = "Titi Me Pregunto (Remixes)",
                ["trackTimeMillis"] = 1000, ["releaseDate"] = "2022-09-01T00:00:00Z", ["primaryGenreName"] = "Latin",
            }),
        };
        var cat = new CatalogoFalso().Responde(
            u => u.StartsWith("https://itunes.apple.com/search", StringComparison.Ordinal) && u.Contains("Titi", StringComparison.OrdinalIgnoreCase),
            resultado);

        var r = await Procesar(cat, "Bad Buny - Titi Me Pregunto (Tiesto Remix).mp3", new ProcessOptions { Deezer = false, Itunes = true });

        Assert.True(r.Found);
        Assert.Equal("AppleMusic", r.Source);
    }

    // ------------------------------------------------------------------ C3 · ediciones con erratas

    // «Bad Buny - Titi Me Pregunto (Extended)»: 300 s frente a los 243 s del catálogo. Una edición
    // dura más por definición; la vía normal no se lo penaliza y el respaldo aproximado tampoco.
    [Fact]
    public void Una_edicion_con_una_errata_en_el_artista_no_se_pierde_por_la_duracion()
    {
        var data = new JsonArray(new JsonObject
        {
            ["id"] = 1, ["title"] = "Tití Me Preguntó", ["duration"] = 243,
            ["artist"] = new JsonObject { ["name"] = "Bad Bunny" }, ["album"] = new JsonObject { ["title"] = "Un Verano Sin Ti", ["id"] = 1 },
        });

        Assert.NotNull(DeezerProvider.SelectBest(data, "Bad Buny", "Titi Me Pregunto", false, false, 300, isEdit: true, out _));
        // Sin ser una edición, 57 s de diferencia sí contradicen.
        Assert.Null(DeezerProvider.SelectBest(data, "Bad Buny", "Titi Me Pregunto", false, false, 300, isEdit: false, out _));
    }

    // ------------------------------------------------------------------ C4a · Discogs, artista por artista

    private static JsonArray Discogs(params (string Titulo, string Estilo)[] r)
    {
        var a = new JsonArray();
        foreach (var (t, e) in r)
            a.Add(new JsonObject { ["title"] = t, ["year"] = "2020", ["style"] = new JsonArray(e), ["genre"] = new JsonArray() });
        return a;
    }

    // Con el crédito concatenado («anuelaanengoflowsinfonico») casaba cualquier artista de Discogs
    // contenido ahí; «Arca» casaba con «Arcangel» por el StartsWith.
    [Theory]
    [InlineData("Anuel AA, Ñengo Flow, Sinfonico", "Flow - Algo", false)]
    [InlineData("Anuel AA, Ñengo Flow, Sinfonico", "Sin - Otro Disco", false)]
    [InlineData("Anuel AA, Ñengo Flow, Sinfonico", "Ñengo Flow - Real G4 Life", true)]
    [InlineData("Anuel AA, Ñengo Flow, Sinfonico", "Anuel AA & Ñengo Flow - Jefe", true)]
    [InlineData("Arca", "Arcángel - Sentimiento, Elegancia Y Maldad", false)]
    [InlineData("Arcangel", "Arcángel - Sentimiento, Elegancia Y Maldad", true)]
    [InlineData("Mad Mind", "Mad Mind (2) - Bien Duro", true)]           // los homónimos numerados de Discogs
    [InlineData("Weeknd", "The Weeknd - After Hours", true)]
    [InlineData("Daddy Yankee", "Various - Reggaeton Hits", false)]
    public void Discogs_compara_artista_por_artista(string artista, string resultado, bool casa)
        => Assert.Equal(casa, DiscogsProvider.ElegirDelArtista(Discogs((resultado, "Reggaeton")), artista) != null);

    // Integración en FileProcessor: el género de OTRO disco del artista rellena el hueco, pero su año
    // no es el de esta canción.
    [Fact]
    public async Task El_genero_de_otro_disco_del_artista_rellena_el_hueco_pero_no_el_anio()
    {
        var cat = new CatalogoFalso()
            .Responde(CatalogoFalso.BusquedaDeezer("Arcangel", "Bali"),
                      CatalogoFalso.DeezerBusqueda(90, "Arcángel", "Bali", 1, albumId: 91, album: "Bali"))
            .Responde(CatalogoFalso.PistaDeezer(90), CatalogoFalso.DeezerPista(0, "", "Arcángel"))
            .Responde(CatalogoFalso.AlbumDeezer(91), CatalogoFalso.DeezerAlbum())
            .Responde(u => u.StartsWith("https://api.discogs.com/database/search", StringComparison.Ordinal) && u.Contains("Bali", StringComparison.Ordinal),
                      new JsonObject { ["results"] = new JsonArray() })
            .Responde(CatalogoFalso.BusquedaDiscogs(), CatalogoFalso.Discogs("Arcángel - Sentimiento, Elegancia Y Maldad", "2013", "Reggaeton"));

        var r = await Procesar(cat, "Arcangel - Bali.mp3", new ProcessOptions { Deezer = true, Discogs = true, DiscogsToken = "prueba" });

        Assert.Equal("Reggaeton", r.Genre);
        Assert.Equal("", r.Year);
        Assert.Equal("Bali", r.Album);
    }

    // Pendiente de la tanda 1 (equipo «nombres»): partir el crédito por comas rompía en dos a los
    // artistas que llevan una coma en el nombre.
    [Theory]
    [InlineData("Tyler, The Creator, Kali Uchis", new[] { "Tyler, The Creator", "Kali Uchis" })]
    [InlineData("Earth, Wind & Fire", new[] { "Earth, Wind & Fire" })]
    [InlineData("Bad Bunny, Jhay Cortez", new[] { "Bad Bunny", "Jhay Cortez" })]
    [InlineData("Drake, The Weeknd", new[] { "Drake", "The Weeknd" })]
    [InlineData("Anuel AA", new[] { "Anuel AA" })]
    public void El_tag_artista_no_parte_los_nombres_que_llevan_coma(string credito, string[] valores)
        => Assert.Equal(valores, Tagging.PartirInterpretes(credito));

    [Fact]
    public void El_genero_solo_del_artista_tampoco_casa_por_un_trozo_del_nombre()
    {
        Assert.Null(DiscogsProvider.GeneroDelArtista(Discogs(("Arcángel - Sentimiento, Elegancia Y Maldad", "Reggaeton")), "Arca"));
        var g = DiscogsProvider.GeneroDelArtista(Discogs(("Arca - KiCk i", "Experimental")), "Arca");
        Assert.NotNull(g);
        Assert.True(g!.SoloArtista);
        Assert.Equal("", g.Album);   // ni álbum ni año: son de otro disco
        Assert.Equal("", g.Year);
    }

    // ------------------------------------------------------------------ C10 · la cuota de Deezer en la caché

    [Theory]
    [InlineData(4)]
    [InlineData(700)]
    public async Task Una_cuota_de_Deezer_guardada_por_una_version_anterior_se_borra_y_se_pregunta(int codigo)
    {
        using var m = new MontajeApi(cache: true);
        var cp = m.Api.CachePath(BusquedaDeezer)!;
        m.Api.CacheStore(cp, $$$"""{"error":{"type":"Exception","message":"Quota limit exceeded","code":{{{codigo}}}}}""");
        m.Red.Responde("api.deezer.com", """{"data":[{"id":3135556,"title":"Shut Up"}]}""");

        var r = await m.Api.GetAsync(BusquedaDeezer);

        Assert.Equal("Shut Up", r?["data"]?[0]?["title"]?.ToString());
        Assert.Equal(1, m.Red.Cuantas("api.deezer.com"));
        Assert.Contains("Shut Up", File.ReadAllText(cp));
    }

    // De paso: lo servido de la caché se interpreta una sola vez.
    [Fact]
    public async Task Lo_que_sale_de_la_cache_se_interpreta_una_vez()
    {
        using var m = new MontajeApi(cache: true);
        m.Api.CacheStore(m.Api.CachePath(BusquedaDeezer)!, """{"data":[]}""");
        var r = await m.Api.GetRespuestaAsync(BusquedaDeezer);
        Assert.True(r.DeCache);
        Assert.Same(r.Json(), r.Json());
    }

    // ------------------------------------------------------------------ C8a · huecos del contrato

    // (a) Una página de mantenimiento con HTTP 200 no es una búsqueda vacía.
    [Fact]
    public async Task Un_200_que_no_es_JSON_cuenta_como_fallo_de_red()
    {
        using var m = new MontajeApi(cache: true);
        m.Red.Responde("musicbrainz.org", "<html><body>Estamos en mantenimiento</body></html>");
        const string url = "https://musicbrainz.org/ws/2/recording?query=x&fmt=json";

        var r = await m.Api.GetRespuestaAsync(url);

        Assert.True(r.FalloDeRed);
        Assert.Null(r.Texto);
        Assert.Equal(2, m.Red.Cuantas("musicbrainz.org"));   // y se reintentó
        Assert.Equal(1, m.Api.FallosDeRed);
        Assert.False(File.Exists(m.Api.CachePath(url)));
    }

    // (b) El 403 de iTunes es «te has pasado del límite»; el de otro servidor, «sin permiso».
    [Fact]
    public async Task El_403_de_iTunes_es_un_fallo_de_red_y_el_de_Discogs_una_respuesta()
    {
        using var m = new MontajeApi();
        m.Red.Responde("itunes.apple.com", "", HttpStatusCode.Forbidden)
             .Responde("api.discogs.com", """{"message":"You must authenticate"}""", HttpStatusCode.Forbidden);

        var it = await m.Api.GetRespuestaAsync("https://itunes.apple.com/search?term=x&entity=song&limit=8");
        Assert.True(it.FalloDeRed);
        Assert.Equal(2, m.Red.Cuantas("itunes.apple.com"));

        var dc = await m.Api.GetRespuestaAsync("https://api.discogs.com/database/search?q=x");
        Assert.False(dc.FalloDeRed);
        Assert.Equal(1, m.Red.Cuantas("api.discogs.com"));
        Assert.Equal(1, m.Api.FallosDeRed);
    }

    // (c) Una carátula que ya no existe (404) es una respuesta, no un fallo de red.
    [Fact]
    public async Task Una_caratula_que_no_existe_no_cuenta_como_fallo_de_red()
    {
        using var m = new MontajeApi();
        m.Red.Responde("cdn.test/no-existe", "", HttpStatusCode.NotFound)
             .Responde("cdn.test/caido", "", HttpStatusCode.ServiceUnavailable)
             .Responde("cdn.test/bien", "JPEG");

        Assert.Null(await m.Api.GetBytesAsync("https://cdn.test/no-existe.jpg"));
        Assert.Equal(0, m.Api.FallosDeRed);
        Assert.Null(await m.Api.GetBytesAsync("https://cdn.test/caido.jpg"));
        Assert.Equal(1, m.Api.FallosDeRed);
        Assert.Equal("JPEG"u8.ToArray(), await m.Api.GetBytesAsync("https://cdn.test/bien.jpg"));
    }

    // ------------------------------------------------------------------ C8 · integración con la caché de análisis

    private static FileProcessor Procesador(MontajeApi m)
        => new(new DeezerProvider(m.Api), new ItunesProvider(m.Api), new SpotifyProvider(m.Api),
               new MusicBrainzProvider(m.Api), new DiscogsProvider(m.Api), new AcoustIdProvider(m.Api),
               new OllamaClient(m.Api), new Fingerprint(m.Paths), m.Http, new ArtistExceptions(null, new ArtistAliases()));

    // Se cae la Wi-Fi a mitad de un análisis largo: esa canción NO puede quedar guardada como «no
    // encontrada» para siempre (la caché de análisis no caduca).
    [Fact]
    public async Task Un_no_encontrada_por_falta_de_red_no_se_guarda_en_la_cache_de_analisis()
    {
        using var m = new MontajeApi();
        m.Red.SinConexion("api.deezer.com");
        var p = Path.Combine(m.Dir, "Bad Bunny - Titi Me Pregunto.mp3");
        Directory.CreateDirectory(m.Dir);
        Mp3Fixture.WriteMinMp3(p);

        var r = await Procesador(m).ProcessAsync(p, false, new ProcessOptions { Deezer = true });

        Assert.False(r.Found);
        Assert.True(r.FallosDeRed > 0);
        Assert.True(r.IncompletoPorLaRed);
        var cache = new AnalysisCache(Path.Combine(m.Dir, "analysis-cache.json"));
        cache.Set(p, "sig", r);
        Assert.Equal(0, cache.Count);
    }

    // Un «no está en el catálogo» de verdad (el servidor respondió) sí se guarda, como siempre.
    [Fact]
    public async Task Un_no_encontrada_de_verdad_si_se_guarda()
    {
        using var m = new MontajeApi();
        m.Red.Responde("api.deezer.com", """{"data":[],"total":0}""");
        var p = Path.Combine(m.Dir, "Bad Bunny - Titi Me Pregunto.mp3");
        Directory.CreateDirectory(m.Dir);
        Mp3Fixture.WriteMinMp3(p);

        var r = await Procesador(m).ProcessAsync(p, false, new ProcessOptions { Deezer = true });

        Assert.False(r.Found);
        Assert.Equal(0, r.FallosDeRed);
        var cache = new AnalysisCache(Path.Combine(m.Dir, "analysis-cache.json"));
        cache.Set(p, "sig", r);
        Assert.Equal(1, cache.Count);
    }

    // ------------------------------------------------------------------ C7 · cortacircuitos

    [Fact]
    public void El_cortacircuitos_se_abre_tras_tres_fallos_y_dobla_la_pausa()
    {
        var ahora = TimeSpan.FromHours(1);
        var c = new CortacircuitosRed(() => ahora);

        Assert.Null(c.Fallo("api.discogs.com"));
        Assert.Null(c.Fallo("api.discogs.com"));
        Assert.Equal(CortacircuitosRed.PausaInicial, c.Fallo("api.discogs.com"));
        Assert.False(c.PuedePasar("api.discogs.com", out var falta));
        Assert.Equal(TimeSpan.FromSeconds(30), falta);
        Assert.True(c.PuedePasar("api.deezer.com", out _));   // los demás servidores, ni se enteran

        ahora += falta;
        Assert.True(c.PuedePasar("api.discogs.com", out _));   // se prueba con una
        Assert.Equal(TimeSpan.FromSeconds(60), c.Fallo("api.discogs.com"));   // recae: el doble
        for (var i = 0; i < 6; i++) c.Fallo("api.discogs.com");
        Assert.Equal(CortacircuitosRed.PausaMaxima, c.Fallo("api.discogs.com"));

        c.Exito("api.discogs.com");
        Assert.True(c.PuedePasar("api.discogs.com", out _));
        Assert.Null(c.Fallo("api.discogs.com"));   // la cuenta empieza de cero
    }

    // Un Discogs caído: antes cada petición costaba 30 s + 2 s + 30 s y una tirada de 15.000
    // canciones se iba a días. Con la pausa, mientras no responde se falla al instante.
    [Fact]
    public async Task Un_servidor_que_no_responde_deja_de_consultarse_un_rato()
    {
        using var m = new MontajeApi();
        m.Red.SinConexion("api.discogs.com");
        string Url(int i) => $"https://api.discogs.com/database/search?q=tema{i}";

        for (var i = 0; i < CortacircuitosRed.FallosParaAbrir; i++)
            Assert.True((await m.Api.GetRespuestaAsync(Url(i))).FalloDeRed);
        Assert.Equal(2 * CortacircuitosRed.FallosParaAbrir, m.Red.Cuantas("api.discogs.com"));

        var enPausa = await m.Api.GetRespuestaAsync(Url(10));
        Assert.True(enPausa.FalloDeRed);                                                         // sigue sin ser «no encontrada»
        Assert.Equal(2 * CortacircuitosRed.FallosParaAbrir, m.Red.Cuantas("api.discogs.com"));   // ni se ha preguntado
        Assert.Contains("no responde", m.Api.LastApiError);
        Assert.Equal(CortacircuitosRed.FallosParaAbrir + 1, m.Api.FallosDeRed);

        // Pasada la pausa se vuelve a probar.
        m.Reloj.Avanzar(CortacircuitosRed.PausaInicial);
        await m.Api.GetRespuestaAsync(Url(11));
        Assert.Equal(2 * CortacircuitosRed.FallosParaAbrir + 2, m.Red.Cuantas("api.discogs.com"));
    }

    // Un 429 SÍ es una respuesta («más despacio»): de eso se ocupan el limitador y el bloqueo propio
    // de Spotify, no el cortacircuitos.
    [Fact]
    public async Task Un_limite_de_peticiones_no_abre_el_cortacircuitos()
    {
        using var m = new MontajeApi();
        m.Red.Responde("api.spotify.com", "{}", HttpStatusCode.TooManyRequests);
        for (var i = 0; i < 5; i++) await m.Api.GetRespuestaAsync($"https://api.spotify.com/v1/search?q=t{i}");
        Assert.Equal(10, m.Red.Cuantas("api.spotify.com"));
    }

    // ------------------------------------------------------------------ C9 · esperas largas en el registro

    [Fact]
    public async Task Una_espera_larga_del_limitador_queda_en_el_registro()
    {
        using var m = new MontajeApi();
        var log = Path.Combine(m.Dir, "beattag.log");
        Directory.CreateDirectory(m.Dir);
        m.Api.Log = new Logger { LogFile = log };
        m.Red.Responde("itunes.apple.com", """{"results":[]}""");

        // iTunes publica unas 20 por minuto: la 21.ª espera a que salga la primera de la ventana.
        for (var i = 0; i < 21; i++) await m.Api.GetAsync($"https://itunes.apple.com/search?term=tema{i}&entity=song&limit=8");

        Assert.Contains("espera de 60 s por el límite de peticiones de itunes.apple.com", File.ReadAllText(log));
    }

    // ------------------------------------------------------------------ C13 · enlaces cortos

    [Fact]
    public async Task Un_enlace_corto_sin_conexion_no_dice_que_es_una_lista()
    {
        using var m = new MontajeApi();
        m.Red.SinConexion("deezer.page.link");
        var r = await new LinkResolver(m.Api, new SpotifyProvider(m.Api)).ResolveAsync("https://deezer.page.link/AbCdEfGh123");

        Assert.Null(r.Candidate);
        Assert.Contains("No se pudo abrir el enlace corto", r.Error);
        Assert.DoesNotContain("Playlists", r.Error);
    }

    [Theory]
    [InlineData("https://www.deezer.com/es/playlist/1479458365", "es de una lista")]
    [InlineData("https://www.deezer.com/es/album/302127", "es de un álbum")]
    [InlineData("https://www.deezer.com/es/artist/27", "no lleva a una canción")]
    public async Task Un_enlace_corto_dice_a_que_lleva(string destino, string esperado)
    {
        using var m = new MontajeApi();
        m.Red.Redirige("deezer.page.link/AbCdEfGh123", destino);
        var r = await new LinkResolver(m.Api, new SpotifyProvider(m.Api)).ResolveAsync("https://deezer.page.link/AbCdEfGh123");

        Assert.Null(r.Candidate);
        Assert.Contains(esperado, r.Error);
    }

    // ------------------------------------------------------------------ C1 · lo que solo da MusicBrainz

    // Ya resuelto en la integración de la tanda 1 (N1): lo que solo identifica una fuente de rescate
    // sale con confianza y para revisar. Se fija aquí para que no vuelva.
    [Fact]
    public async Task Lo_que_solo_identifica_MusicBrainz_sale_con_confianza_y_para_revisar()
    {
        var cat = new CatalogoFalso().Responde(CatalogoFalso.BusquedaMusicBrainz(),
            CatalogoFalso.MusicBrainz(100, "Titi Me Pregunto", "Bad Bunny", "Un Verano Sin Ti", "2022-05-06"));

        var r = await Procesar(cat, "Bad Bunny - Titi Me Pregunto.mp3", new ProcessOptions { Deezer = false, MusicBrainz = true });

        Assert.True(r.Found);
        Assert.NotEqual("", r.Score);
        Assert.Contains(FileProcessor.MotivoSoloRescate("MusicBrainz"), r.Revisar);
    }

    // ------------------------------------------------------------------ general · lo guardado antes de C2

    // Los cambios de puntuación no invalidan la caché de análisis: lo guardado con la casación
    // anterior («Shut Up» → «Shut Up (Instrumental)», confianza 17,6) sale ahora para revisar.
    [Fact]
    public void Lo_guardado_que_anade_instrumental_sin_que_el_archivo_lo_diga_sale_para_revisar()
    {
        var r = new ProcessResult
        {
            FilePath = @"E:\Musica\COMERCIAL\Black Eyed Peas - Shut Up.mp3",
            Old = "Black Eyed Peas - Shut Up.mp3", New = "Black Eyed Peas - Shut Up (Instrumental).mp3",
            Found = true, Source = "Deezer", Score = "17.6", Why = "titulo exacto sin el sufijo +8", Reglas = 1,
        };

        Assert.True(ResultadoAnterior.Actualizar(r));

        Assert.Equal(ProcessResult.ReglasActuales, r.Reglas);
        Assert.Equal(ResultadoAnterior.MotivoHerramientaNoNombrada, r.Revisar);
        Assert.Equal($"revisar: {ResultadoAnterior.MotivoHerramientaNoNombrada} · titulo exacto sin el sufijo +8", r.Why);
        Assert.False(ResultadoAnterior.Actualizar(r));   // una sola vez
    }

    [Fact]
    public void Una_acapella_que_lo_dice_no_sale_para_revisar()
    {
        var r = new ProcessResult
        {
            FilePath = @"E:\Musica\Juan Magan - Mal De Amores (Acapella).mp3",
            Old = "Juan Magan - Mal De Amores (Acapella).mp3", New = "Juan Magan - Mal De Amores (Acapella).mp3",
            Found = true, Source = "Deezer", Score = "11", Reglas = 1,
        };
        ResultadoAnterior.Actualizar(r);
        Assert.Equal("", r.Revisar);
    }

    // Con otro motivo ya anotado (reglas 0), los dos quedan, y el «por qué» no los repite.
    [Fact]
    public void Con_otro_motivo_ya_anotado_se_suman_sin_repetirse()
    {
        var r = new ProcessResult
        {
            FilePath = @"E:\Musica\Timberland - The Way I Are.mp3",
            Old = "Timberland - The Way I Are.mp3", New = "Timbaland - The Way I Are (Instrumental).mp3",
            Found = true, Source = "MusicBrainz", Why = "", Reglas = 0,
        };
        ResultadoAnterior.Actualizar(r);

        Assert.Contains(FileProcessor.MotivoSoloRescate("MusicBrainz"), r.Revisar);
        Assert.Contains(ResultadoAnterior.MotivoHerramientaNoNombrada, r.Revisar);
        Assert.Equal($"revisar: {r.Revisar}", r.Why);
    }
}
