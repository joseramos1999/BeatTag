using System.Net;
using System.Text.Json.Nodes;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// Enlaces y listas: el enlace corto de una canción (C13) y la lista de Deezer que se cortaba a
/// medias sin decir nada (C12). Sobre una red falsa.
/// </summary>
public class CatalogosEnlacesTests
{
    // ------------------------------------------------------------------ C13

    private const string PistaDeezer = """
        {"id":3135556,"title":"Harder, Better, Faster, Stronger","duration":224,"release_date":"2001-03-07",
         "artist":{"name":"Daft Punk"},"album":{"title":"Discovery"}}
        """;

    [Fact]
    public async Task El_enlace_corto_de_Deezer_del_boton_Compartir_se_sigue()
    {
        // Antes: «Enlace no reconocido», con el enlace más exacto que puede dar el usuario.
        using var m = new MontajeApi();
        m.Red.Redirige("link.deezer.com/s/30Bbv3Yq1lJZkG3", "https://www.deezer.com/es/track/3135556?host=0&utm_campaign=clipboard-generic")
             .Responde("api.deezer.com/track/3135556", PistaDeezer);
        var resolver = new LinkResolver(m.Api, new SpotifyProvider(m.Api));

        var r = await resolver.ResolveAsync("https://link.deezer.com/s/30Bbv3Yq1lJZkG3");

        Assert.Equal("", r.Error);
        Assert.Equal("Deezer", r.Candidate?.Source);
        Assert.Equal("Harder, Better, Faster, Stronger", r.Candidate?.Title);
        Assert.Equal("Daft Punk", r.Candidate?.Artist);
    }

    [Fact]
    public async Task El_enlace_corto_de_Spotify_que_redirige_por_script_tambien()
    {
        using var m = new MontajeApi();
        m.Red.Redirige("spotify.link/AbCdEf1234", "https://spotify.link/AbCdEf1234",
            "<html><script>window.location='https://open.spotify.com/track/0DiWol3AO6WpXZgp0goxAV?si=x'</script></html>");
        var resolver = new LinkResolver(m.Api, new SpotifyProvider(m.Api));

        var r = await resolver.ResolveAsync("spotify.link/AbCdEf1234");

        // Lo reconoce como canción de Spotify: lo que falta ya es la clave, no el enlace.
        Assert.Contains("clave de Spotify", r.Error);
    }

    [Fact]
    public async Task Un_enlace_corto_que_lleva_a_una_lista_lo_dice()
    {
        using var m = new MontajeApi();
        m.Red.Redirige("deezer.page.link/AbCdEfGh123", "https://www.deezer.com/es/playlist/1479458365");
        var resolver = new LinkResolver(m.Api, new SpotifyProvider(m.Api));

        var r = await resolver.ResolveAsync("https://deezer.page.link/AbCdEfGh123");

        Assert.Null(r.Candidate);
        Assert.Contains("Playlists", r.Error);
    }

    [Fact]
    public async Task Las_listas_siguen_resolviendo_sus_enlaces_cortos()
    {
        using var m = new MontajeApi();
        m.Red.Redirige("link.deezer.com/s/31CkPxYzAbCd", "https://www.deezer.com/es/playlist/1479458365")
             .Responde("api.deezer.com/playlist/1479458365/tracks", Pagina(0, 2, next: null))
             .Responde("api.deezer.com/playlist/1479458365", """{"title":"Lista corta","nb_tracks":2}""");
        var r = await new PlaylistProvider(m.Api, m.Http).LeerAsync("https://link.deezer.com/s/31CkPxYzAbCd");
        Assert.True(r.Ok);
        Assert.Equal(2, r.Pistas.Count);
    }

    // ------------------------------------------------------------------ C12

    private const string Lista = "api.deezer.com/playlist/908622995";

    private static string Pagina(int desde, int cuantas, string? next)
    {
        var data = new JsonArray();
        for (var i = 0; i < cuantas; i++)
            data.Add(new JsonObject
            {
                ["title"] = $"Tema {desde + i + 1}", ["duration"] = 200,
                ["artist"] = new JsonObject { ["name"] = "Artista" }, ["link"] = "https://www.deezer.com/track/1",
            });
        var o = new JsonObject { ["data"] = data, ["total"] = 456 };
        if (next != null) o["next"] = next;
        return o.ToJsonString();
    }

    private static MontajeApi ListaDe456(Action<RedFalsa> pagina3)
    {
        var m = new MontajeApi();
        m.Red.Responde("index=0", Pagina(0, 100, $"https://{Lista}/tracks?limit=100&index=100"))
             .Responde("index=100", Pagina(100, 100, $"https://{Lista}/tracks?limit=100&index=200"));
        pagina3(m.Red);
        m.Red.Responde("index=300", Pagina(300, 100, $"https://{Lista}/tracks?limit=100&index=400"))
             .Responde("index=400", Pagina(400, 56, null))
             .Responde(Lista, """{"title":"Mi lista","nb_tracks":456}""");
        return m;
    }

    [Fact]
    public async Task Si_Deezer_deja_de_responder_a_mitad_la_lista_sale_marcada_como_incompleta()
    {
        // Una lista de 456 canciones (5 páginas) y un fallo en la página 3: antes quedaban 200
        // canciones y «tienes X de 200» parecía la lista entera.
        using var m = ListaDe456(red => red.SinConexion("index=200"));
        var r = await new PlaylistProvider(m.Api, m.Http).LeerAsync("https://www.deezer.com/es/playlist/908622995");

        Assert.True(r.Ok);
        Assert.Equal(200, r.Pistas.Count);
        Assert.True(r.Recortada);
        Assert.Contains("página 3", r.Aviso);
        Assert.Contains("faltan 256 de las 456", r.Aviso);
    }

    [Fact]
    public async Task La_cuota_de_Deezer_en_una_pagina_tampoco_pasa_en_silencio()
    {
        using var m = ListaDe456(red => red.Responde("index=200",
            """{"error":{"type":"Exception","message":"Quota limit exceeded","code":4}}"""));
        var r = await new PlaylistProvider(m.Api, m.Http).LeerAsync("https://www.deezer.com/es/playlist/908622995");
        Assert.True(r.Recortada);
        Assert.Equal(200, r.Pistas.Count);
    }

    [Fact]
    public async Task Un_fallo_puntual_se_reintenta_y_la_lista_sale_entera()
    {
        using var m = ListaDe456(red => red.Secuencia("index=200",
            (HttpStatusCode.ServiceUnavailable, ""), (HttpStatusCode.ServiceUnavailable, ""),
            (HttpStatusCode.OK, Pagina(200, 100, $"https://{Lista}/tracks?limit=100&index=300"))));
        var r = await new PlaylistProvider(m.Api, m.Http).LeerAsync("https://www.deezer.com/es/playlist/908622995");

        Assert.False(r.Recortada);
        Assert.Equal(456, r.Pistas.Count);
        Assert.Equal("", r.Aviso);
        Assert.Equal(456, r.Pistas[^1].Position);
    }

    [Fact]
    public async Task Si_falla_ya_la_primera_pagina_es_un_error_y_no_una_lista_vacia()
    {
        using var m = new MontajeApi();
        m.Red.SinConexion("index=0").Responde(Lista, """{"title":"Mi lista","nb_tracks":456}""");
        var r = await new PlaylistProvider(m.Api, m.Http).LeerAsync("https://www.deezer.com/es/playlist/908622995");
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Spotify_cortada_en_100_lleva_su_propio_aviso()
    {
        // Cada plataforma se corta por su motivo: la pantalla ya no da por hecho que es Spotify.
        var pistas = string.Join(",", Enumerable.Range(1, PlaylistProvider.LimiteSpotify).Select(i =>
            $$"""{"uri":"spotify:track:{{i.ToString().PadLeft(22, 'a')}}","title":"Tema {{i}}","subtitle":"Artista","duration":200000}"""));
        var html = "<html><body><script id=\"__NEXT_DATA__\" type=\"application/json\">"
                 + "{\"props\":{\"pageProps\":{\"state\":{\"data\":{\"entity\":{\"type\":\"playlist\",\"name\":\"Larga\",\"trackList\":["
                 + pistas + "]}}}}}}</script></body></html>";
        using var m = new MontajeApi();
        m.Red.Cuando("open.spotify.com/embed/playlist/", (_, req, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html), RequestMessage = req }));

        var r = await new PlaylistProvider(m.Api, m.Http).LeerAsync("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M");

        Assert.True(r.Recortada);
        Assert.Contains("100 primeras", r.Aviso);
    }
}
