using System.Text.Json.Nodes;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

// Selección/scoring pura de Spotify, MusicBrainz (sin red).
public class ProviderScoringTests2
{
    private static JsonArray Spotify(params (string artist, string name, int durMs)[] items)
    {
        var arr = new JsonArray();
        foreach (var (artist, name, durMs) in items)
            arr.Add(new JsonObject
            {
                ["name"] = name,
                ["duration_ms"] = durMs,
                ["artists"] = new JsonArray { new JsonObject { ["name"] = artist, ["id"] = "aid1" } },
                ["album"] = new JsonObject
                {
                    ["name"] = "Album",
                    ["release_date"] = "2021-03-01",
                    ["images"] = new JsonArray { new JsonObject { ["url"] = "http://img/cover.jpg" } },
                },
            });
        return arr;
    }

    [Fact]
    public void Spotify_prefiere_original_sobre_live()
    {
        var items = Spotify(
            ("Karol G", "Provenza - Live", 160000),
            ("Karol G", "Provenza", 150000));
        var pick = SpotifyProvider.SelectBest(items, "Karol G", "Provenza", false, false, 0, false, out var sc);
        Assert.NotNull(pick);
        Assert.Equal("Provenza", (string)pick!["name"]!);
        Assert.True(sc >= 10);
    }

    [Fact]
    public void Spotify_sin_fuzzy_no_casa_typo()
    {
        // Spotify NO tiene rescate fuzzy (a diferencia de Deezer/iTunes): un typo no debe casar.
        var items = Spotify(("Bad Bunny", "Titi Me Pregunto", 200000));
        var pick = SpotifyProvider.SelectBest(items, "Bad Buny", "Titi Me Pregunto", false, false, 0, false, out _);
        Assert.Null(pick);
    }

    private static JsonNode Mb(int score, string title, string artist, string date)
        => new JsonObject
        {
            ["recordings"] = new JsonArray
            {
                new JsonObject
                {
                    ["score"] = score,
                    ["title"] = title,
                    ["artist-credit"] = new JsonArray { new JsonObject { ["name"] = artist } },
                    ["releases"] = new JsonArray { new JsonObject { ["title"] = "El Album", ["date"] = date } },
                },
            },
        };

    [Fact]
    public void Mb_acepta_score_alto_y_artista_coincide()
    {
        var pick = MusicBrainzProvider.PickBest(Mb(96, "Gasolina", "Daddy Yankee", "2004-06-01"), "Daddy Yankee");
        Assert.NotNull(pick);
        Assert.Equal("Gasolina", pick!.Title);
        Assert.Equal("2004", pick.Year);
        Assert.Equal("El Album", pick.Album);
    }

    [Fact]
    public void Mb_rechaza_score_bajo()
        => Assert.Null(MusicBrainzProvider.PickBest(Mb(70, "Gasolina", "Daddy Yankee", "2004"), "Daddy Yankee"));

    [Fact]
    public void Mb_rechaza_artista_distinto()
        => Assert.Null(MusicBrainzProvider.PickBest(Mb(96, "Gasolina", "Otro Artista", "2004"), "Daddy Yankee"));

    // MusicBrainz trae su propia puntuación: si no se traslada, sus coincidencias (que ya exigen
    // score >= 85) se quedaban en 0 y se marcaban como dudosas sin motivo.
    [Theory]
    [InlineData(96, 8.0)]
    [InlineData(100, 10.0)]
    [InlineData(90, 5.0)]
    [InlineData(85, 2.5)]
    public void Mb_traslada_su_puntuacion(int mbScore, double esperado)
    {
        var pick = MusicBrainzProvider.PickBest(Mb(mbScore, "Gasolina", "Daddy Yankee", "2004"), "Daddy Yankee");
        Assert.NotNull(pick);
        Assert.Equal(esperado, pick!.Score, 1);
        Assert.True(pick.Score >= 2, "no debería quedar por debajo del umbral de revisión");
    }
}
