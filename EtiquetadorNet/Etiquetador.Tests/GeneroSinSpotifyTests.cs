using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Pipeline;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// Género por artista (A10): Spotify ya no publica los géneros del artista (medido en octubre de
/// 2026: 0 de 75 artistas los traen), así que Enriquecer ya no gasta una petición por artista en
/// pedirlos. El género sale de Deezer, iTunes o Discogs, como cuando Spotify no los daba.
/// </summary>
public class GeneroSinSpotifyTests
{
    private const string Token = """{"access_token":"tok","token_type":"Bearer","expires_in":3600}""";

    // Una pista de Spotify sin carátula: así Enriquecer busca los huecos en Deezer.
    private const string BusquedaSpotify = """
        {"tracks":{"items":[{"name":"Titi Me Pregunto","duration_ms":1000,
          "artists":[{"name":"Bad Bunny","id":"4q3ewBCX7sLwd24euuV69X"}],
          "album":{"name":"Un Verano Sin Ti","release_date":"2022-05-06","images":[]}}]}}
        """;

    private static FileProcessor Procesador(MontajeApi m)
        => new(new DeezerProvider(m.Api), new ItunesProvider(m.Api), new SpotifyProvider(m.Api),
               new MusicBrainzProvider(m.Api), new DiscogsProvider(m.Api), new AcoustIdProvider(m.Api),
               new OllamaClient(m.Api), new Fingerprint(m.Paths), m.Http, new ArtistExceptions(null, new ArtistAliases()));

    [Fact]
    public async Task Una_cancion_identificada_por_Spotify_no_pide_los_generos_del_artista()
    {
        using var m = new MontajeApi();
        var spotifyBuscado = false;
        m.Red.Responde("accounts.spotify.com/api/token", Token)
             .Cuando("api.spotify.com/v1/search", (_, req, _) =>
             {
                 spotifyBuscado = true;
                 return Task.FromResult(RedFalsa.Respuesta(req, BusquedaSpotify));
             })
             // Si se pidiera, daría un género: la prueba comprueba que no se pide.
             .Responde("api.spotify.com/v1/artists/", """{"genres":["trap latino"]}""")
             // Deezer no la encuentra por el nombre del archivo, pero sí con los datos de Spotify.
             .Cuando("api.deezer.com/search", (_, req, _) => Task.FromResult(RedFalsa.Respuesta(req, spotifyBuscado
                 ? CatalogoFalso.DeezerBusqueda(70, "Bad Bunny", "Tití Me Preguntó", 1).ToJsonString()
                 : """{"data":[],"total":0}""")))
             .Responde("api.deezer.com/album/77", CatalogoFalso.DeezerAlbum("Reggaeton", "Latino").ToJsonString());
        Directory.CreateDirectory(m.Dir);
        var p = Path.Combine(m.Dir, "Bad Bunny - Titi Me Pregunto.mp3");
        Mp3Fixture.WriteMinMp3(p);

        var r = await Procesador(m).ProcessAsync(p, false, new ProcessOptions
        {
            Deezer = true, Spotify = true, SpotifyId = "id", SpotifySecret = "secreto",
        });

        Assert.True(r.Found);
        Assert.True(spotifyBuscado);
        Assert.Equal(0, m.Red.Cuantas("api.spotify.com/v1/artists/"));
        Assert.Equal("Reggaeton", r.Genre);
    }
}
