using System.Text.Json.Nodes;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// C4a · Discogs solo da género, álbum y año de un disco del MISMO artista. Casos reales, todos
/// identificados bien por Deezer con confianza 15-20 y que acababan con un género ajeno:
/// «Arcangel - Bali» -> Gamelan, «Mad Mind - Bien Duro» -> Medieval, «COQE, JC Reyes -
/// Multiplicando $$$» -> Educational.
/// </summary>
public class CatalogosDiscogsTests
{
    private static JsonArray Resultados(params (string Titulo, string Estilo, string Genero, string Anio)[] rs)
    {
        var arr = new JsonArray();
        foreach (var (t, s, g, y) in rs)
            arr.Add(new JsonObject
            {
                ["title"] = t, ["year"] = y,
                ["style"] = s.Length > 0 ? new JsonArray { s } : new JsonArray(),
                ["genre"] = g.Length > 0 ? new JsonArray { g } : new JsonArray(),
            });
        return arr;
    }

    private static string Json(JsonArray resultados) => new JsonObject { ["results"] = resultados }.ToJsonString();

    [Fact]
    public void Bali_no_coge_el_genero_de_un_disco_de_otro_artista()
    {
        var res = Resultados(("Eberhard Schoener - Bali-Agúng", "Gamelan", "Folk, World, & Country", "1976"));
        Assert.Null(DiscogsProvider.ElegirDelArtista(res, "Arcangel"));
    }

    [Fact]
    public void Bien_Duro_nunca_coge_un_recopilatorio_Various()
    {
        var res = Resultados(("Various - Medieval & Renaissance", "Medieval", "Classical", "1995"));
        Assert.Null(DiscogsProvider.ElegirDelArtista(res, "Mad Mind"));
    }

    [Fact]
    public void Multiplicando_sin_disco_del_artista_no_hay_genero_ni_anio()
    {
        var res = Resultados(("Unknown Artist - Multiplicando", "Educational", "Children's", "1959"));
        Assert.Null(DiscogsProvider.ElegirDelArtista(res, "COQE, JC Reyes"));
    }

    [Fact]
    public void Si_el_disco_es_del_artista_da_genero_album_y_anio()
    {
        var res = Resultados(
            ("Various - Reggaeton Hits 2014", "Reggaeton", "Latin", "2014"),
            ("Arcángel - Bali", "Reggaeton", "Latin", "2019"));
        var r = DiscogsProvider.ElegirDelArtista(res, "Arcangel");
        Assert.NotNull(r);
        Assert.Equal("Reggaeton", r!.Genre);
        Assert.Equal("Bali", r.Album);
        Assert.Equal("2019", r.Year);
        Assert.False(r.SoloArtista);
    }

    [Fact]
    public void Los_homonimos_numerados_de_Discogs_tambien_casan()
    {
        var res = Resultados(("Mad Mind (2) - Bien Duro", "Dembow", "Latin", "2021"));
        Assert.Equal("Dembow", DiscogsProvider.ElegirDelArtista(res, "Mad Mind")?.Genre);
    }

    [Fact]
    public void El_genero_del_artista_va_marcado_y_sin_album_ni_anio()
    {
        var res = Resultados(("Arcangel - Sentimiento, Elegancia & Maldad", "Reggaeton", "Hip Hop", "2013"));
        var r = DiscogsProvider.GeneroDelArtista(res, "Arcangel");
        Assert.NotNull(r);
        Assert.Equal("Reggaeton", r!.Genre);
        Assert.True(r.SoloArtista);
        Assert.Equal("", r.Album);
        Assert.Equal("", r.Year);
    }

    [Fact]
    public async Task Arcangel_Bali_ya_no_busca_por_el_titulo_suelto()
    {
        // Traza real: «q=Arcangel Bali» vacío, «q=Arcangel» sin género útil, «q=Bali» -> Gamelan.
        using var m = new MontajeApi();
        m.Red.Responde("q=Arcangel%20Bali", Json(new JsonArray()))
             .Responde("q=Arcangel&", Json(Resultados(("Arcangel - Sentimiento, Elegancia & Maldad", "Reggaeton", "Hip Hop", "2013"))))
             .Responde("q=Bali&", Json(Resultados(("Eberhard Schoener - Bali-Agúng", "Gamelan", "Folk", "1976"))));

        var r = await new DiscogsProvider(m.Api).SearchAsync("Arcangel", "Bali (Extended)", "Prueba/1.0", "");

        Assert.Equal("Reggaeton", r?.Genre);
        Assert.True(r!.SoloArtista);
        Assert.Equal(0, m.Red.Cuantas("q=Bali&"));
    }

    [Fact]
    public async Task Sin_artista_no_se_pregunta_a_Discogs()
    {
        using var m = new MontajeApi();
        Assert.Null(await new DiscogsProvider(m.Api).SearchAsync("", "Bali", "Prueba/1.0", ""));
        Assert.Empty(m.Red.Pedidas);
    }
}
