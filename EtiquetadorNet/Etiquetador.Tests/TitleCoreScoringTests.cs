using System.Text.Json.Nodes;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// Crédito por "núcleo del título": cuando un título contiene al otro pero no es una versión
/// reconocida, antes valía CERO y la coincidencia correcta se quedaba por debajo del umbral de
/// revisión (2,0). Los casos vienen de un análisis real de 14.951 canciones.
/// </summary>
public class TitleCoreScoringTests
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

    // El catálogo trae el título completo y el archivo solo el principio. Es la misma canción.
    [Fact]
    public void Groovejet_supera_el_umbral_de_revision()
    {
        var data = Deezer(("Spiller", "Groovejet (If This Ain't Love)", 180));
        var pick = DeezerProvider.SelectBest(data, "Spiller", "Groovejet", false, false, 180, false, out var sc);
        Assert.NotNull(pick);
        Assert.True(sc > 2.0, $"deberia superar el umbral de revision, y saco {sc}");
    }

    [Fact]
    public void Relax_de_Mika_supera_el_umbral()
    {
        var data = Deezer(("MIKA", "Relax. Take It Easy", 220));
        var pick = DeezerProvider.SelectBest(data, "MIKA", "Relax", false, false, 220, false, out var sc);
        Assert.NotNull(pick);
        Assert.True(sc > 2.0, $"deberia superar el umbral de revision, y saco {sc}");
    }

    // Guarda: el credito parcial no puede igualar a un titulo exacto, o dejaria de distinguirse
    // una coincidencia segura de una probable.
    [Fact]
    public void El_titulo_exacto_siempre_puntua_mas()
    {
        var exacto = Deezer(("Spiller", "Groovejet", 180));
        DeezerProvider.SelectBest(exacto, "Spiller", "Groovejet", false, false, 180, false, out var scExacto);

        var parcial = Deezer(("Spiller", "Groovejet (If This Ain't Love)", 180));
        DeezerProvider.SelectBest(parcial, "Spiller", "Groovejet", false, false, 180, false, out var scParcial);

        Assert.True(scExacto > scParcial, $"exacto={scExacto} parcial={scParcial}");
    }

    // Guarda: entre dos candidatos se sigue prefiriendo el que coincide del todo.
    [Fact]
    public void Se_sigue_prefiriendo_el_titulo_completo()
    {
        var data = Deezer(
            ("Spiller", "Groovejet (If This Ain't Love)", 180),
            ("Spiller", "Groovejet", 180));
        var pick = DeezerProvider.SelectBest(data, "Spiller", "Groovejet", false, false, 180, false, out _);
        Assert.NotNull(pick);
        Assert.Equal("Groovejet", (string)pick!["title"]!);
    }

    // Guarda: un titulo que apenas explica al otro no merece credito. "Yo" dentro de un titulo
    // largo es coincidencia, no identificacion.
    // Se pasa duracion 0 (desconocida) a proposito: con duraciones iguales el bono de duracion
    // ya supera el umbral por si solo y la prueba no diria nada sobre el titulo.
    [Fact]
    public void Una_coincidencia_minima_no_recibe_credito()
    {
        var data = Deezer(("Alguien", "Yo quiero bailar contigo toda la noche entera", 200));
        DeezerProvider.SelectBest(data, "Alguien", "Yo", false, false, 0, false, out var sc);
        Assert.True(sc < 2.0, $"no deberia superar el umbral, y saco {sc}");
    }

    // El mismo caso pero con el nucleo compartido de verdad: ahi si debe puntuar, tambien sin
    // ayuda de la duracion. Es lo que separa un indicio de una coincidencia casual.
    [Fact]
    public void El_nucleo_compartido_puntua_sin_ayuda_de_la_duracion()
    {
        var data = Deezer(("Spiller", "Groovejet (If This Ain't Love)", 180));
        DeezerProvider.SelectBest(data, "Spiller", "Groovejet", false, false, 0, false, out var sc);
        Assert.True(sc > 0.0, $"el nucleo compartido deberia puntuar, y saco {sc}");
    }

    // Guarda: el credito NO debe rescatar lo que las penalizaciones descartan a proposito.
    // Un remix que no se ha pedido sigue quedando por debajo del umbral.
    [Fact]
    public void No_rescata_un_remix_no_pedido()
    {
        var data = Deezer(("Quevedo", "Gasolina (Some Guy Remix)", 200));
        DeezerProvider.SelectBest(data, "Quevedo", "Gasolina", false, false, 200, false, out var sc);
        Assert.True(sc < 2.0, $"un remix no pedido no deberia superar el umbral, y saco {sc}");
    }

    // ---- Casos sacados de una tirada real de 12.629 canciones ----

    // El catalogo anade "(feat. X)", que no forma parte de la identidad del tema. Sin ese sufijo
    // los titulos son identicos. Eran 36 de 139 coincidencias dudosas sin credito.
    [Fact]
    public void El_sufijo_feat_del_catalogo_no_debe_penalizar()
    {
        var data = Deezer(("Jordin Sparks", "No Air (feat. Chris Brown)", 260));
        DeezerProvider.SelectBest(data, "Jordin Sparks", "No Air", false, false, 260, false, out var sc);
        Assert.True(sc > 2.0, $"es la misma cancion, deberia superar el umbral, y saco {sc}");
    }

    [Fact]
    public void Un_subtitulo_del_catalogo_tampoco()
    {
        var data = Deezer(("Eiffel 65", "Blue (Da Ba Dee)", 225));
        DeezerProvider.SelectBest(data, "Eiffel 65", "Blue", false, false, 225, false, out var sc);
        Assert.True(sc > 2.0, $"deberia superar el umbral, y saco {sc}");
    }

    // Los record pool cortan el nombre: el titulo del archivo es el PRINCIPIO del real.
    // Eran otros 58 de aquellas 139.
    [Fact]
    public void Un_titulo_truncado_por_el_pool_se_reconoce()
    {
        var data = Deezer(("MC Fioti", "Bum Bum Tam Tam", 175));
        DeezerProvider.SelectBest(data, "MC Fioti", "Bu", false, false, 175, false, out var sc);
        Assert.True(sc > 2.0, $"'Bu' es el principio de 'Bum Bum Tam Tam', y saco {sc}");
    }

    [Fact]
    public void Otro_truncado_real()
    {
        var data = Deezer(("Tokischa", "Bandidaje", 190));
        DeezerProvider.SelectBest(data, "Tokischa", "Bandi", false, false, 190, false, out var sc);
        Assert.True(sc > 2.0, $"deberia superar el umbral, y saco {sc}");
    }

    // Guarda: quitar el sufijo NO puede rescatar una version que no se pedia. Sin parentesis
    // "Complicated (Fareoh Remix)" es "Complicated", pero sigue siendo otra version.
    [Fact]
    public void Quitar_el_sufijo_no_rescata_un_remix_ajeno()
    {
        var data = Deezer(("Dimitri Vegas & Like Mike", "Complicated (Fareoh Remix)", 177));
        DeezerProvider.SelectBest(data, "Dimitri Vegas & Like Mike", "Complicated", false, false, 186, false, out var sc);
        Assert.True(sc < 2.0, $"es otra version, no deberia superar el umbral, y saco {sc}");
    }

    // Guarda: un prefijo de una sola letra es demasiado poco para afirmar nada.
    [Fact]
    public void Un_prefijo_de_una_letra_no_basta()
    {
        var data = Deezer(("Alguien", "Bailando toda la noche entera", 200));
        DeezerProvider.SelectBest(data, "Alguien", "B", false, false, 0, false, out var sc);
        Assert.True(sc < 2.0, $"una sola letra no identifica nada, y saco {sc}");
    }
}
