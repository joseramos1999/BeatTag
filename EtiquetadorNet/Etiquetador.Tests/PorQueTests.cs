using System.Text.Json.Nodes;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// El "por que" de cada propuesta. Ese razonamiento se calculaba desde siempre, pero solo se
/// escribia en el registro y se perdia: en pantalla salia un numero de confianza y poco mas, asi que
/// juzgar una propuesta dudosa obligaba a salir de la aplicacion y abrir un archivo de texto.
///
/// Lo que se fija aqui es que el motivo del candidato GANADOR sea el que sale, no el del ultimo
/// examinado, que es el error facil de cometer al recorrer una lista.
/// </summary>
public class PorQueTests
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
    public void Se_explica_por_que_gano_el_titulo_exacto()
    {
        var data = Deezer(("Quevedo", "Gasolina", 180));
        var pick = DeezerProvider.SelectBest(data, "Quevedo", "Gasolina", false, false, 180, false,
                                             out _, out var porQue);
        Assert.NotNull(pick);
        Assert.Contains("titulo exacto", porQue);
        Assert.Contains("dur", porQue);          // y que la duracion cuadra
    }

    // El motivo tiene que ser el del GANADOR. Con el candidato bueno en segundo lugar, quedarse con
    // el motivo del ultimo examinado seria explicar una propuesta que no es la que se muestra.
    [Fact]
    public void El_motivo_es_el_del_ganador_no_el_del_ultimo()
    {
        var data = Deezer(
            ("Quevedo", "Gasolina", 180),               // este gana
            ("Quevedo", "Gasolina (Sped Up)", 150));    // este se examina despues y pierde
        var pick = DeezerProvider.SelectBest(data, "Quevedo", "Gasolina", false, false, 180, false,
                                             out _, out var porQue);
        Assert.Equal("Gasolina", (string)pick!["title"]!);
        Assert.Contains("titulo exacto", porQue);
        Assert.DoesNotContain("basura", porQue);   // la penalizacion del perdedor no se cuela
    }

    // Sin coincidencia no hay nada que explicar: un motivo colgado de la nada confundiria mas que
    // ayudar.
    [Fact]
    public void Sin_coincidencia_no_hay_motivo()
    {
        var data = Deezer(("Otro Artista", "Otra Cancion", 200));
        var pick = DeezerProvider.SelectBest(data, "Quevedo", "Gasolina", false, false, 0, false,
                                             out _, out var porQue);
        Assert.Null(pick);
        Assert.Equal("", porQue);
    }

    // Cuando se pide una version concreta y el catalogo solo tiene esa, el motivo lo dice. Es justo
    // el caso en que uno duda de si le van a cambiar el remix por el original.
    //
    // OJO con lo que NO se afirma aqui: si el catalogo trae ADEMAS el titulo exacto, gana el
    // original (+10) sobre el remix pedido (+6) aunque el archivo sea el remix. Es el
    // comportamiento actual del scoring, no algo que decida esta prueba.
    [Fact]
    public void Se_explica_cuando_gana_la_version_pedida()
    {
        var data = Deezer(("Bad Bunny", "Tití Me Preguntó (Remix)", 245));
        var pick = DeezerProvider.SelectBest(data, "Bad Bunny", "Tití Me Preguntó", wantRemix: true,
                                             wantLive: false, 245, false, out _, out var porQue);
        Assert.Contains("Remix", (string)pick!["title"]!);
        Assert.Contains("remix pedido", porQue);
    }

    // Y el motivo tambien explica lo que RESTA, no solo lo que suma: una version que no se pidio
    // penaliza, y verlo escrito es lo que permite entender una confianza baja de un vistazo.
    [Fact]
    public void El_motivo_tambien_dice_lo_que_resta()
    {
        var data = Deezer(("Quevedo", "Gasolina (Live)", 200));
        DeezerProvider.SelectBest(data, "Quevedo", "Gasolina", wantRemix: false, wantLive: false,
                                  200, false, out _, out var porQue);
        Assert.Contains("live no pedido", porQue);
    }
}
