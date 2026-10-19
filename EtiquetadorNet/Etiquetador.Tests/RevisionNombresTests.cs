using System.Net;
using System.Text.Json.Nodes;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// Lo que encontró la revisión del cambio de nombres (N4, N5, N8, N11, N13, N14), con los casos de
/// los registros reproducidos contra un catálogo de mentira.
/// </summary>
public class RevisionNombresTests
{
    private static async Task<(ProcessResult R, CatalogoFalso Cat)> Procesar(string archivo, CatalogoFalso cat, ProcessOptions? o = null)
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, archivo);
            Mp3Fixture.WriteMinMp3(p);
            var r = await cat.Procesador(dir).ProcessAsync(p, false, o ?? new ProcessOptions { Deezer = true });
            return (r, cat);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>Catálogo con una sola pista de Deezer para cualquier búsqueda que lleve <paramref name="palabra"/>.</summary>
    private static CatalogoFalso Deezer(string palabra, string titulo, int bpm, params string[] contribuidores)
        => new CatalogoFalso()
            .Responde(CatalogoFalso.BusquedaDeezer(palabra), CatalogoFalso.DeezerBusqueda(10, contribuidores[0], titulo, 1))
            .Responde(CatalogoFalso.PistaDeezer(10), CatalogoFalso.DeezerPista(bpm, "2019-01-01", contribuidores))
            .Responde(CatalogoFalso.AlbumDeezer(77), CatalogoFalso.DeezerAlbum("Pop"));

    // ---------- N5 · se busca con la primera firma, la que puede tener el catálogo ----------

    [Fact]
    public void La_primera_firma_es_la_publicada_y_la_ultima_la_del_archivo()
    {
        const string nombre = "Martin Solveig - Hello (Sidney Samson Remix) (Cesar Vilo Remix)";
        Assert.Equal("Sidney Samson", RemixParser.PrimeraFirma(nombre).Remixer);
        Assert.Equal("Cesar Vilo", RemixParser.Parse(nombre).Remixer);
        // Con una sola firma, las dos coinciden; con «Remix by», manda lo explícito.
        Assert.Equal("Bayne", RemixParser.PrimeraFirma("Titanium (Remix) (Bayne Remix 128bpm)").Remixer);
        Assert.Equal("Tiesto", RemixParser.PrimeraFirma("Tema (Remixed by Tiesto) (Juan Edit)").Remixer);
        Assert.False(RemixParser.PrimeraFirma("Daddy Yankee - Rompe").HasRemixer);
    }

    // Sin el original entre los resultados, se elige la versión publicada que nombra el archivo y no
    // el primer remix que salga. Antes ganaba «Hello (Bob Sinclar Remix)».
    [Fact]
    public async Task Deezer_reconoce_la_version_publicada_aunque_la_firme_otro_editor()
    {
        static JsonObject Pista(long id, string titulo) => new()
        {
            ["id"] = id, ["title"] = titulo, ["duration"] = 1,
            ["artist"] = new JsonObject { ["name"] = "Martin Solveig" },
            ["album"] = new JsonObject { ["title"] = "Hello", ["id"] = 77, ["cover_big"] = "http://portadas/big.jpg" },
        };
        var cat = new CatalogoFalso()
            .Responde(CatalogoFalso.BusquedaDeezer("Hello"),
                      new JsonObject { ["data"] = new JsonArray(Pista(80, "Hello (Bob Sinclar Remix)"), Pista(81, "Hello (Sidney Samson Remix)")) })
            .Responde(CatalogoFalso.PistaDeezer(80), CatalogoFalso.DeezerPista(126, "2010-01-01", "Martin Solveig"))
            .Responde(CatalogoFalso.PistaDeezer(81), CatalogoFalso.DeezerPista(128, "2010-01-01", "Martin Solveig"))
            .Responde(CatalogoFalso.AlbumDeezer(77), CatalogoFalso.DeezerAlbum("Dance"));

        var (r, _) = await Procesar("Martin Solveig - Hello (Sidney Samson Remix) (Cesar Vilo Remix).mp3", cat);

        Assert.Contains("https://api.deezer.com/track/81", cat.Pedidas);
        Assert.DoesNotContain("https://api.deezer.com/track/80", cat.Pedidas);
        Assert.Equal("Cesar Vilo", r.RemixerTag);   // el archivo lo sigue firmando el editor del pool
    }

    // ---------- N4 · el «Remix» del propio título del catálogo no es otra versión ----------

    [Theory]
    [InlineData("Los Del Rio - Macarena Bayside Boys (Remix).mp3", "Macarena", "Macarena Bayside Boys Remix", 103, "Los Del Río")]
    [InlineData("Marracash - Scooteroni (RMX).mp3", "Scooteroni", "Scooteroni (RMX)", 115, "Marracash")]
    public async Task La_version_que_ya_nombra_el_catalogo_hereda_su_bpm(string archivo, string palabra, string titulo, int bpm, string artista)
    {
        var (r, _) = await Procesar(archivo, Deezer(palabra, titulo, bpm, artista));
        Assert.Equal(bpm.ToString(), r.Bpm);
        Assert.Equal(bpm.ToString(), r.BpmCatalogo);
    }

    [Fact]
    public async Task Una_version_que_el_catalogo_no_trae_sigue_sin_su_bpm()
    {
        var (r, _) = await Procesar("Daddy Yankee - Rompe (Bayne Remix).mp3", Deezer("Rompe", "Rompe", 176, "Daddy Yankee"));
        Assert.Equal("", r.Bpm);
    }

    // ---------- N14 · títulos cortos y la versión pegada ----------

    // Con títulos de tres letras o menos, «el propio título» no se comprobaba y el título acababa en
    // el tag Remixer.
    [Theory]
    [InlineData("Nio Garcia - AM Remix.mp3", "Nio", "AM Remix", "Nio Garcia, J Balvin, Bad Bunny - AM (Remix).mp3", "Nio Garcia|J Balvin|Bad Bunny")]
    [InlineData("Katteyes ft Sinaka - Katteyes ft Sinaka - ALO  Open Show   Extended Latino  102 Bpm.mp3", "ALO", "ALO",
                "Katteyes, Sinaka - ALO (Open Show, Extended).mp3", "Katteyes|Sinaka")]
    public async Task Un_titulo_corto_no_es_quien_firma_la_version(string archivo, string palabra, string titulo, string esperado, string artistas)
    {
        var (r, _) = await Procesar(archivo, Deezer(palabra, titulo, 0, artistas.Split('|')));
        Assert.Equal(esperado, r.New);
        Assert.Equal("", r.RemixerTag);
    }

    [Fact]
    public async Task El_47_no_firma_el_47_remix()
    {
        var (r, _) = await Procesar("Anuel AA - 47 Remix  Latin Box Open Show .mp3",
            Deezer("47", "47", 0, "Anuel AA", "Ñengo Flow", "Sinfonico"));
        Assert.Equal("", r.RemixerTag);
    }

    [Theory]
    [InlineData("AM", "AM", "AM", true)]
    [InlineData("47", "47", "47", true)]
    [InlineData("Alo Open Show", "ALO", "ALO", true)]
    [InlineData("Ani", "Animals", "Animals", false)]          // por palabras enteras
    [InlineData("Alex Cortes", "Givenchy", "Givenchy Alex Cortes", false)]
    [InlineData("Stereo Love", "Stereo Love", "Stereo Love", true)]
    public void Es_el_propio_titulo_tambien_con_titulos_cortos(string autor, string catalogo, string delNombre, bool es)
        => Assert.Equal(es, FileProcessor.EsElPropioTitulo(autor, catalogo, delNombre));

    // Con el límite de palabra delante del tipo, «(Smurfy x B00STaVIPaEdit)» se quedaba sin versión y
    // se proponía con el nombre del tema original.
    [Fact]
    public void La_version_pegada_tras_un_cambio_de_caja_cuenta()
    {
        var r = RemixParser.Parse("Eminem - The Real Slim Shady (Smurfy x B00STaVIPaEdit)");
        Assert.Equal("Edit", r.Kind);
        Assert.StartsWith("Smurfy X", r.Remixer);
    }

    [Fact]
    public async Task La_edicion_pegada_no_se_pierde_en_el_nombre()
    {
        var (r, _) = await Procesar("Eminem - The Real Slim Shady (Smurfy x B00STaVIPaEdit).mp3",
            Deezer("Shady", "The Real Slim Shady", 104, "Eminem"));
        Assert.Equal("Eminem - The Real Slim Shady (Smurfy X B00stavipa Edit).mp3", r.New);
    }

    // Pegado sin cambio de caja sigue sin contar: no es el principio de una palabra.
    [Theory]
    [InlineData("Tema (Firestarter)")]
    [InlineData("Envolver (@SAMUVIP)")]
    public void Pegado_sin_cambio_de_caja_no_es_una_version(string nombre)
        => Assert.False(RemixParser.Parse(nombre).HasRemixer);

    // ---------- N8 · «ª» y «º» no obligan a dejar el campo en Unicode ----------

    [Theory]
    [InlineData("La 5ª Estación", "La 5 Estacion")]
    [InlineData("30ºC", "30C")]
    [InlineData("Золото", "Золото")]   // lo que sí pierde letras se sigue conservando
    public void Los_ordinales_no_son_letras_perdidas(string campo, string esperado)
        => Assert.Equal(esperado, TextUtils.AsciiSinPerderLetras(campo));

    [Fact]
    public void El_nombre_de_La_5_Estacion_es_el_de_siempre()
        => Assert.Equal("La 5 Estacion - Me Muero", FileProcessor.ComponerNombre("La 5ª Estación", "Me Muero", null));

    // ---------- N13 · el tag Artista ----------

    // «FT Kings» es un grupo: un «ft» detrás de una coma empieza el nombre del siguiente intérprete.
    [Fact]
    public async Task Ft_al_principio_de_un_interprete_no_es_un_invitado()
    {
        var (r, _) = await Procesar("Kid Yugi - Berserker.mp3", Deezer("Berserker", "Berserker", 0, "Kid Yugi", "Ill Santo", "FT Kings"));
        Assert.Equal("Kid Yugi, Ill Santo, FT Kings", r.Artist);
        Assert.Equal("Kid Yugi, Ill Santo, FT Kings - Berserker.mp3", r.New);
    }

    // El nombre pasa las MAYÚSCULAS a tipo título, como siempre; el tag respeta la grafía del catálogo.
    [Fact]
    public async Task El_tag_respeta_las_mayusculas_del_catalogo()
    {
        var (r, _) = await Procesar("TRIANGLE DES BERMUDES - Balek.mp3",
            Deezer("Balek", "Balek", 0, "TRIANGLE DES BERMUDES", "Aya Nakamura", "MC YOSHI"));
        Assert.Equal("TRIANGLE DES BERMUDES, Aya Nakamura, MC YOSHI", r.Artist);
        Assert.Equal("Triangle Des Bermudes, Aya Nakamura, Mc Yoshi - Balek.mp3", r.New);
    }

    [Fact]
    public void Normalizar_sin_desgritar_conserva_la_grafia_y_aplica_las_excepciones()
    {
        var exc = new ArtistExceptions(null, new ArtistAliases());
        Assert.Equal("MC YOSHI, Arcángel", exc.NormalizeArtists("MC YOSHI, Arcangel", desgritar: false));
        Assert.Equal("Mc Yoshi, Arcángel", exc.NormalizeArtists("MC YOSHI, Arcangel"));
    }

    // ---------- N11 · la caché de carátulas tiene un tope ----------

    private sealed class Portadas : HttpMessageHandler
    {
        public int Pedidas;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Pedidas);
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) });
        }
    }

    [Fact]
    public async Task La_cache_de_caratulas_olvida_las_mas_antiguas()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var red = new Portadas();
            var api = new ApiClient(new AppPaths(dir), new HttpClient(red)) { CacheOn = false };
            var portadas = new CoverFetcher(api);
            const int max = CoverFetcher.MaxEnMemoria;

            for (int i = 0; i <= max; i++) Assert.NotNull(await portadas.FetchAsync($"http://portadas/{i}.jpg"));
            Assert.Equal(max + 1, red.Pedidas);
            Assert.Equal(max, portadas.EnMemoria);

            await portadas.FetchAsync($"http://portadas/{max}.jpg");   // reciente: sigue en memoria
            Assert.Equal(max + 1, red.Pedidas);
            await portadas.FetchAsync("http://portadas/0.jpg");         // la más antigua: se volvió a pedir
            Assert.Equal(max + 2, red.Pedidas);
            Assert.Equal(max, portadas.EnMemoria);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
