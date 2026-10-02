using System.Text.Json.Nodes;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// Pestaña Playlists: reconocer el enlace que pega el usuario y leer la respuesta de cada
/// plataforma. Las estructuras son las medidas contra Spotify y Deezer en octubre de 2026.
/// </summary>
public class PlaylistTests
{
    // --- Enlaces ---

    [Theory]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=8a2b3c4d5e6f", "37i9dQZF1DXcBWIGoYBM5M")]   // «Compartir» añade ?si=
    [InlineData("https://open.spotify.com/intl-es/playlist/79emz4GMzwgJaytiuczD0D", "79emz4GMzwgJaytiuczD0D")]
    [InlineData("https://open.spotify.com/embed/playlist/79emz4GMzwgJaytiuczD0D", "79emz4GMzwgJaytiuczD0D")]
    [InlineData("open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "37i9dQZF1DXcBWIGoYBM5M")]
    public void Reconoce_una_playlist_de_Spotify(string enlace, string id)
    {
        var l = PlaylistLinkParser.Parse(enlace);
        Assert.Equal(new PlaylistLink(PlataformaLista.Spotify, "playlist", id), l);
    }

    [Theory]
    [InlineData("https://www.deezer.com/es/playlist/1914768222", "1914768222")]
    [InlineData("https://www.deezer.com/playlist/1914768222", "1914768222")]
    [InlineData("deezer.com/en/playlist/1914768222?utm_source=deezer", "1914768222")]
    public void Reconoce_una_playlist_de_Deezer(string enlace, string id)
        => Assert.Equal(new PlaylistLink(PlataformaLista.Deezer, "playlist", id), PlaylistLinkParser.Parse(enlace));

    [Theory]
    [InlineData("https://www.deezer.com/en/album/302127", PlataformaLista.Deezer, "302127")]
    [InlineData("https://open.spotify.com/album/4aawyAB9vmqN3uQ7FjRGTy", PlataformaLista.Spotify, "4aawyAB9vmqN3uQ7FjRGTy")]
    public void Tambien_reconoce_un_album(string enlace, PlataformaLista plataforma, string id)
    {
        var l = PlaylistLinkParser.Parse(enlace);
        Assert.Equal(new PlaylistLink(plataforma, "album", id), l);
        Assert.True(l!.Value.EsAlbum);
    }

    [Theory]
    [InlineData("https://open.spotify.com/track/11hcBLPtbMp4aQI6zGQLub")]   // una canción, no una lista
    [InlineData("https://www.deezer.com/track/3135556")]
    [InlineData("https://www.youtube.com/playlist?list=PL123")]
    [InlineData("reggaeton 2026")]
    [InlineData("")]
    public void No_confunde_otra_cosa_con_una_playlist(string texto)
        => Assert.Null(PlaylistLinkParser.Parse(texto));

    // Los botones «Compartir» del móvil dan enlaces cortos que hay que seguir.
    [Theory]
    [InlineData("https://link.deezer.com/s/31CkPxYzAbCd", true)]
    [InlineData("https://deezer.page.link/AbCdEfGh123", true)]
    [InlineData("https://spotify.link/AbCdEf1234", true)]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", false)]
    public void Reconoce_los_enlaces_cortos(string enlace, bool esCorto)
        => Assert.Equal(esCorto, PlaylistLinkParser.EsCorto(enlace));

    // --- Respuestas ---

    private const string EmbedSpotify = """
        <html><head></head><body><div id="__next"></div>
        <script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"state":{"data":{"entity":{
          "type":"playlist","name":"Today’s Top Hits","uri":"spotify:playlist:37i9dQZF1DXcBWIGoYBM5M",
          "trackList":[
            {"uri":"spotify:track:11hcBLPtbMp4aQI6zGQLub","title":"Patient Zero","subtitle":"Taylor Swift","duration":225868},
            {"uri":"spotify:track:3h5T5JypYU7huFiVYhv1dr","title":"BbY WOW","subtitle":"KAROL G, Judeline, rusowsky","duration":226000}
          ]}}}}}}</script></body></html>
        """;

    [Fact]
    public void Lee_la_pagina_embed_de_Spotify()
    {
        var (nombre, pistas) = PlaylistProvider.LeerEmbedSpotify(EmbedSpotify);

        Assert.Equal("Today’s Top Hits", nombre);
        Assert.Equal(2, pistas.Count);
        Assert.Equal(new[] { 1, 2 }, pistas.Select(p => p.Position));
        Assert.Equal("KAROL G, Judeline, rusowsky", pistas[1].Artist);
        Assert.Equal("BbY WOW", pistas[1].Title);
        Assert.Equal("3h5T5JypYU7huFiVYhv1dr", pistas[1].SpotifyId);   // con él se piden las duraciones exactas
        Assert.Equal("3:46", pistas[0].DurText);
    }

    [Theory]
    [InlineData("<html><body>Page not found</body></html>")]
    [InlineData("")]
    [InlineData("<script id=\"__NEXT_DATA__\" type=\"application/json\">{no es json</script>")]
    public void Una_pagina_embed_sin_lista_no_da_canciones(string html)
        => Assert.Empty(PlaylistProvider.LeerEmbedSpotify(html).Pistas);

    [Fact]
    public void Lee_una_pagina_de_Deezer_y_sigue_la_numeracion()
    {
        var pagina = JsonNode.Parse("""
            {"data":[
              {"id":1,"title":"Gasolina","duration":192,"link":"https://www.deezer.com/track/1","artist":{"name":"Daddy Yankee"}},
              {"id":2,"title":"","duration":100,"artist":{"name":"Sin título"}},
              {"id":3,"title":"Tusa","duration":200,"link":"https://www.deezer.com/track/3","artist":{"name":"KAROL G"}}
            ],"total":456,"next":"https://api.deezer.com/playlist/1/tracks?index=200"}
            """)!;

        var pistas = PlaylistProvider.PistasDeezer(pagina, desde: 100);

        Assert.Equal(2, pistas.Count);                              // la que no tiene título se salta
        Assert.Equal(new[] { 101, 102 }, pistas.Select(p => p.Position));
        Assert.Equal("Daddy Yankee", pistas[0].Artist);
        Assert.Equal("3:12", pistas[0].DurText);
    }
}
