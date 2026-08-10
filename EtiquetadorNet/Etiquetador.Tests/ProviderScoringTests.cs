using System.Text.Json.Nodes;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

// Prueba la selección/scoring pura de los proveedores (sin red) con JSON de ejemplo.
public class ProviderScoringTests
{
    private static JsonArray Deezer(params (string artist, string title, int dur)[] items)
    {
        var arr = new JsonArray();
        int id = 100;
        foreach (var (artist, title, dur) in items)
            arr.Add(new JsonObject
            {
                ["id"] = id++,
                ["title"] = title,
                ["duration"] = dur,
                ["artist"] = new JsonObject { ["name"] = artist },
                ["album"] = new JsonObject { ["title"] = "Album", ["id"] = 1, ["cover_big"] = "http://c/big.jpg" },
            });
        return arr;
    }

    [Fact]
    public void Deezer_prefiere_original_limpio_sobre_spedup()
    {
        var data = Deezer(
            ("Quevedo", "Gasolina (Sped Up)", 150),
            ("Quevedo", "Gasolina", 180));
        var pick = DeezerProvider.SelectBest(data, "Quevedo", "Gasolina", false, false, 0, false, out var sc);
        Assert.NotNull(pick);
        Assert.Equal("Gasolina", (string)pick!["title"]!);
        Assert.True(sc >= 10);
    }

    [Fact]
    public void Deezer_rechaza_si_solo_hay_version_penalizada()
    {
        // Único candidato es un "sped up" no pedido: mejor no tocar (bestSc <= -8 -> null)
        var data = Deezer(("Quevedo", "Gasolina (Sped Up) - Karaoke Live", 150));
        var pick = DeezerProvider.SelectBest(data, "Quevedo", "Gasolina", false, false, 0, false, out _);
        Assert.Null(pick);
    }

    [Fact]
    public void Deezer_fuzzy_typo_en_artista()
    {
        var data = Deezer(("Bad Bunny", "Titi Me Pregunto", 200));
        var pick = DeezerProvider.SelectBest(data, "Bad Buny", "Titi Me Pregunto", false, false, 0, false, out _);
        Assert.NotNull(pick);
        Assert.Equal("Titi Me Pregunto", (string)pick!["title"]!);
    }

    [Fact]
    public void Deezer_respeta_remix_si_se_pide()
    {
        var data = Deezer(
            ("Feid", "Classy 101", 190),
            ("Feid", "Classy 101 (Remix)", 195));
        var pick = DeezerProvider.SelectBest(data, "Feid", "Classy 101 Remix", true, false, 0, false, out _);
        Assert.NotNull(pick);
        Assert.Contains("Remix", (string)pick!["title"]!);
    }

    private static JsonArray Itunes(params (string artist, string track, int ms)[] items)
    {
        var arr = new JsonArray();
        foreach (var (artist, track, ms) in items)
            arr.Add(new JsonObject
            {
                ["artistName"] = artist,
                ["trackName"] = track,
                ["collectionName"] = "Album",
                ["trackTimeMillis"] = ms,
                ["releaseDate"] = "2022-05-06T12:00:00Z",
                ["artworkUrl100"] = "http://x/100x100bb.jpg",
                ["primaryGenreName"] = "Reggaeton",
            });
        return arr;
    }

    [Fact]
    public void Itunes_titulo_exacto_gana()
    {
        var res = Itunes(
            ("Rosalia", "Despecha (Live)", 160000),
            ("Rosalia", "Despecha", 158000));
        var pick = ItunesProvider.SelectBest(res, "Rosalia", "Despecha", 0, false, out var sc);
        Assert.NotNull(pick);
        Assert.Equal("Despecha", (string)pick!["trackName"]!);
        Assert.True(sc >= 10);
    }

    [Fact]
    public void Itunes_sin_match_devuelve_null()
    {
        var res = Itunes(("Otro Artista", "Otra Cancion", 200000));
        var pick = ItunesProvider.SelectBest(res, "Shakira", "Monotonia", 0, false, out _);
        Assert.Null(pick);
    }

    [Fact]  // "Indie XYZ" no está en la lista de preferencia; "Reggaeton" sí -> gana el de DJ
    public void GenrePicker_prefiere_genero_dj()
        => Assert.Equal("Reggaeton", GenrePicker.Pick(new[] { "Indie XYZ" }, new[] { "Reggaeton" }));

    [Fact]  // fiel al .ps1: "Pop" está en la preferencia, así que gana aunque venga en style
    public void GenrePicker_pop_es_preferente()
        => Assert.Equal("Pop", GenrePicker.Pick(new[] { "Pop" }, new[] { "Reggaeton" }));

    // --- Puntuación de las versiones (remixes/edits) ---

    [Fact]
    public void Sin_artista_en_el_nombre_ya_no_puntua_cero()
    {
        // Material de pool: "Bye bye (YANISS Remix).mp3" no trae artista. Antes esta vía elegía la
        // canción SIN asignar puntuación y salía 0 -> se marcaba como dudosa aunque fuera correcta.
        var data = Deezer(("T2R", "Bye Bye", 168));
        var pick = DeezerProvider.SelectBest(data, "", "Bye bye", wantRemix: true, wantLive: false,
            localDur: 170, isEdit: true, out var sc);
        Assert.NotNull(pick);
        Assert.True(sc >= 2, $"debería superar el umbral de revisión, pero fue {sc}");
    }

    [Fact]
    public void Prefiere_la_version_del_remixer_que_nombra_el_archivo()
    {
        var data = Deezer(("T2R", "Bye Bye", 168), ("T2R", "Bye Bye (Yaniss Remix)", 200));
        var pick = DeezerProvider.SelectBest(data, "", "Bye bye", wantRemix: true, wantLive: false,
            localDur: 200, isEdit: true, out var sc, null, expectedRemixer: "Yaniss");
        Assert.Equal("Bye Bye (Yaniss Remix)", pick!["title"]!.ToString());
        Assert.True(sc >= 9);
    }

    [Fact]
    public void El_remix_pedido_recibe_credito_parcial()
    {
        // El catálogo trae el título + el descriptor que el archivo ya pedía: no es un match a medias.
        var data = Deezer(("Darude", "Feel the Beat (Yaniss Remix)", 200));
        var pick = DeezerProvider.SelectBest(data, "Darude", "Feel the Beat", wantRemix: true,
            wantLive: false, localDur: 200, isEdit: true, out var sc, null, expectedRemixer: "Yaniss");
        Assert.NotNull(pick);
        Assert.True(sc >= 9, $"esperaba crédito por la versión pedida, fue {sc}");
    }

    [Fact]
    public void Un_remix_no_pedido_sigue_penalizado()
    {
        var data = Deezer(("Darude", "Sandstorm (Otro Remix)", 200));
        DeezerProvider.SelectBest(data, "Darude", "Sandstorm", wantRemix: false, wantLive: false,
            localDur: 200, isEdit: false, out var sc);
        Assert.True(sc < 2, $"no se pedía remix: debería quedar bajo, fue {sc}");
    }
}
