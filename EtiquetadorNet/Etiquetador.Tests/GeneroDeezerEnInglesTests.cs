using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// El género del álbum de Deezer que escribe Enriquecer. Deezer da los nombres en el idioma de la IP:
/// desde España, «Latino», «Música Latina» o «Alternativo», que el selector no reconocía. Y un álbum
/// [Rap/Hip Hop, Latino] se etiquetaba «Hip Hop»: medido en una biblioteca real, de ahí salían muchos
/// «Hip Hop» en canciones de reggaeton. Ahora se traduce por el identificador del género, y los
/// gruesos de Deezer (Latin, Dance, Electro, Rap/Hip Hop) ceden ante un estilo concreto.
/// </summary>
public class GeneroDeezerEnInglesTests
{
    private static (int Id, string Nombre)[] G(params (int, string)[] generos) => generos;

    // Lo que acaba en la etiqueta: el elegido, normalizado como en FileProcessor.
    private static string Etiqueta(params (int, string)[] generos)
        => GenreNormalizer.Canonical(DeezerProvider.GeneroDelAlbum(G(generos)));

    [Fact]
    public void Se_traduce_por_el_identificador_sea_cual_sea_el_idioma()
    {
        Assert.Equal("Latin", GeneroDeezer.EnIngles(197, "Latino"));
        Assert.Equal("Latin", GeneroDeezer.EnIngles(197, "Latin Music"));
        Assert.Equal("Alternative", GeneroDeezer.EnIngles(85, "Alternativo"));
        Assert.Equal("Latin Pop", GeneroDeezer.EnIngles(138, "Pop latino"));
        Assert.Equal("Soundtrack", GeneroDeezer.EnIngles(173, "Películas/Juegos"));
        Assert.Equal("Rap/Hip Hop", GeneroDeezer.EnIngles(116, "Rap/Hip Hop"));
        // Manda el identificador, no el nombre.
        Assert.Equal("Reggaeton", GeneroDeezer.EnIngles(122, "Reggaetón"));
    }

    [Fact]
    public void Sin_identificador_conocido_se_traduce_el_nombre_en_espanol()
    {
        Assert.Equal("Latin", GeneroDeezer.EnIngles(0, "Música Latina"));
        Assert.Equal("Latin", GeneroDeezer.EnIngles(0, "musica latina"));
        Assert.Equal("Latin", GeneroDeezer.EnIngles(9999, "Latino"));
        Assert.Equal("Classical", GeneroDeezer.EnIngles(0, "Clásica"));
        // Lo que no se reconoce se deja como llega.
        Assert.Equal("Bachata", GeneroDeezer.EnIngles(9999, "Bachata"));
        Assert.Equal("", GeneroDeezer.EnIngles(9999, ""));
    }

    [Fact]
    public void Latino_con_Rap_Hip_Hop_es_urbano_latino_y_no_Hip_Hop()
    {
        Assert.Equal("Latin Urban", Etiqueta((116, "Rap/Hip Hop"), (197, "Latino")));
        Assert.Equal("Latin Urban", Etiqueta((197, "Latino"), (116, "Rap/Hip Hop")));
        Assert.Equal("Latin Urban", Etiqueta((116, "Rap/Hip Hop"), (197, "Latin Music")));
        Assert.Equal("Latin Urban", Etiqueta((116, "Rap/Hip Hop"), (138, "Pop latino")));
        // Es un género genérico: «Género por carpeta y artista» lo puede afinar sin preguntar.
        Assert.True(FamiliasGenero.EsGenerico(Etiqueta((116, "Rap/Hip Hop"), (197, "Latino"))));
        // Rap/Hip Hop sin nada latino sigue siendo Hip Hop.
        Assert.Equal("Hip Hop", Etiqueta((116, "Rap/Hip Hop")));
        Assert.Equal("Hip Hop", Etiqueta((116, "Rap/Hip Hop"), (132, "Pop")));
    }

    [Fact]
    public void Latino_con_otro_grueso_queda_en_Latin()
    {
        Assert.Equal("Latin", Etiqueta((197, "Latino")));
        Assert.Equal("Latin", Etiqueta((132, "Pop"), (197, "Latino")));
        Assert.Equal("Latin", Etiqueta((113, "Dance"), (197, "Latino")));
        Assert.Equal("Latin", Etiqueta((106, "Electro"), (197, "Música Latina")));
        // Pop latino es más concreto que Latin.
        Assert.Equal("Latin Pop", Etiqueta((197, "Latino"), (138, "Pop latino")));
        Assert.Equal("Latin Pop", Etiqueta((132, "Pop"), (138, "Pop latino")));
    }

    [Fact]
    public void Un_estilo_concreto_manda_sobre_los_gruesos()
    {
        Assert.Equal("Reggaeton", Etiqueta((197, "Latino"), (122, "Reggaeton")));
        Assert.Equal("Reggaeton", Etiqueta((116, "Rap/Hip Hop"), (197, "Latino"), (122, "Reggaeton")));
        Assert.Equal("Salsa", Etiqueta((197, "Latino"), (67, "Salsa")));
        Assert.Equal("R&B", Etiqueta((113, "Dance"), (165, "R&B")));
        Assert.Equal("Reggae", Etiqueta((106, "Electro"), (144, "Reggae")));
        // Con los nombres en inglés, Pick se quedaría con «Latin» por ir el primero.
        Assert.Equal("Latin", GenrePicker.Pick(null, new[] { "Latin", "Reggaeton" }));
        Assert.Equal("Reggaeton", GenrePicker.PickDeezer(new[] { "Latin", "Reggaeton" }));
    }

    [Fact]
    public void Sin_nada_latino_los_gruesos_se_eligen_como_siempre()
    {
        Assert.Equal("Dance", Etiqueta((113, "Dance")));
        Assert.Equal("Electro", Etiqueta((106, "Electro"), (113, "Dance")));
        Assert.Equal("Pop", Etiqueta((113, "Dance"), (132, "Pop")));
        Assert.Equal("Alternative", Etiqueta((85, "Alternativo")));
        Assert.Equal("Rock", Etiqueta((85, "Alternativo"), (152, "Rock")));
        Assert.Equal("", Etiqueta());
    }

    // =================================================================================================
    // Con la red falsa: lo que llega de /album/{id}
    // =================================================================================================

    private const string AlbumEnEspanol = """{"genres":{"data":[{"id":116,"name":"Rap/Hip Hop"},{"id":197,"name":"Latino"}]}}""";
    private const string AlbumEnIngles = """{"genres":{"data":[{"id":116,"name":"Rap/Hip Hop"},{"id":197,"name":"Latin Music"}]}}""";

    [Theory]
    [InlineData(AlbumEnEspanol)]
    [InlineData(AlbumEnIngles)]
    public async Task El_genero_del_album_no_depende_del_idioma_de_la_respuesta(string album)
    {
        using var m = new MontajeApi();
        m.Red.Responde("api.deezer.com/album/77", album);

        var genero = await new DeezerProvider(m.Api).AlbumGenreAsync("77");

        Assert.Equal("Latin Urban", genero);
        Assert.Equal(1, m.Red.Cuantas("api.deezer.com/album/77"));
    }

    [Fact]
    public async Task Un_album_sin_generos_no_da_genero()
    {
        using var m = new MontajeApi();
        m.Red.Responde("api.deezer.com/album/77", """{"genres":{"data":[]}}""");
        var deezer = new DeezerProvider(m.Api);

        Assert.Equal("", await deezer.AlbumGenreAsync("77"));
        Assert.Equal("", await deezer.AlbumGenreAsync(""));
        Assert.Equal(1, m.Red.Cuantas("api.deezer.com/album/"));
    }

    // El Asistente ve los mismos géneros que antes: solo cambia lo que escribe Enriquecer.
    [Fact]
    public async Task Los_generos_del_album_siguen_llegando_como_los_da_Deezer()
    {
        using var m = new MontajeApi();
        m.Red.Responde("api.deezer.com/album/77", AlbumEnEspanol);

        var generos = await new DeezerProvider(m.Api).AlbumGenresAsync("77");

        Assert.Equal(new[] { (116, "Rap/Hip Hop"), (197, "Latino") }, generos);
        Assert.Null(GeneroDeezer.Traducir(generos));
    }

    [Fact]
    public async Task Enriquecer_escribe_Latin_Urban_en_una_cancion_de_un_album_Rap_Hip_Hop_y_Latino()
    {
        using var m = new MontajeApi();
        m.Red.Responde("api.deezer.com/search", CatalogoFalso.DeezerBusqueda(70, "Bad Bunny", "Tití Me Preguntó", 1).ToJsonString())
             .Responde("api.deezer.com/album/77", AlbumEnEspanol);
        Directory.CreateDirectory(m.Dir);
        var p = Path.Combine(m.Dir, "Bad Bunny - Titi Me Pregunto.mp3");
        Mp3Fixture.WriteMinMp3(p);

        var procesador = new FileProcessor(new DeezerProvider(m.Api), new ItunesProvider(m.Api), new SpotifyProvider(m.Api),
            new MusicBrainzProvider(m.Api), new DiscogsProvider(m.Api), new AcoustIdProvider(m.Api),
            new OllamaClient(m.Api), new Fingerprint(m.Paths), m.Http, new ArtistExceptions(null, new ArtistAliases()));
        var r = await procesador.ProcessAsync(p, false, new ProcessOptions { Deezer = true });

        Assert.True(r.Found);
        Assert.Equal("Latin Urban", r.Genre);
    }
}
