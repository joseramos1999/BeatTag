using System.Text.Json.Nodes;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// Camino difuso: mismo artista y un titulo PARECIDO pero no incluido. Sirve para las erratas del
/// nombre del archivo, pero tenia el umbral tan bajo (0,88) que colaba canciones distintas que
/// empiezan igual, y con confianza 4 -por encima del umbral de revision- se aplicaban solas.
/// Todos los casos salen de una tirada real de 12.428 canciones.
/// </summary>
public class TypoTitleTests
{
    private static JsonArray Deezer(string artist, string title, int dur)
        => new()
        {
            new JsonObject
            {
                ["id"] = 1, ["title"] = title, ["duration"] = dur,
                ["artist"] = new JsonObject { ["name"] = artist },
                ["album"] = new JsonObject { ["title"] = "A", ["id"] = 1, ["cover_big"] = "http://c/b.jpg" },
            }
        };

    [Theory]
    [InlineData("Cosculluela", "Prrum", "Prrrum")]                              // 0,961
    [InlineData("Alguien", "CUENTA REGREVISA", "Cuenta Regresiva")]             // 0,987
    [InlineData("Alguien", "Fuego De Calo", "Fuego Del Calo")]                  // 0,956
    public void Una_errata_no_impide_la_coincidencia(string art, string enElArchivo, string enElCatalogo)
    {
        var data = Deezer(art, enElCatalogo, 200);
        var pick = DeezerProvider.SelectBest(data, art, enElArchivo, false, false, 200, false, out _);
        Assert.NotNull(pick);
    }

    // Guardas: parecido NO es igual. Estos pares son canciones DISTINTAS que empezaban igual y
    // entraban con el umbral anterior.
    [Theory]
    [InlineData("Alguien", "Carnavalito Style", "Carnavalito Soleado")]         // 0,931
    [InlineData("Alguien", "Dodo Suga", "Dodo Siya")]                           // 0,900
    public void Dos_canciones_distintas_que_empiezan_igual_se_descartan(string art, string enElArchivo, string enElCatalogo)
    {
        var data = Deezer(art, enElCatalogo, 200);
        var pick = DeezerProvider.SelectBest(data, art, enElArchivo, false, false, 200, false, out _);
        Assert.Null(pick);
    }
}
