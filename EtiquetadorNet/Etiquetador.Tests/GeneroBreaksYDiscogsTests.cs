using System.Text.Json.Nodes;
using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// Dos arreglos que salieron de medir Discogs frente a las carpetas de una biblioteca real:
/// - «Breaks», el nombre que dan Discogs y Beatport al breakbeat, no se reconocía: en las canciones
///   archivadas como breakbeat, Discogs dio «Breaks» en 7 de 14 y «Breakbeat» en 3.
/// - El elector de Discogs se quedaba con «Latin» cuando era el primer estilo: «Feid - Vente
///   Conmigo», con los estilos [Latin, Reggaeton], y «Toman - Verano En NY», con [Latin, Tech House],
///   se etiquetaban Latin.
/// </summary>
public class GeneroBreaksYDiscogsTests
{
    // =================================================================================================
    // «Breaks» es Breakbeat
    // =================================================================================================

    [Theory]
    [InlineData("Breaks")]
    [InlineData("breaks")]
    [InlineData("BREAKS")]
    [InlineData("Break Beat")]
    [InlineData("Breakbeats")]
    [InlineData("breakbeat")]
    [InlineData("Breakbeat")]
    [InlineData("Breaks; House")]   // con una lista, el primero
    [InlineData("Breaks/Electro")]
    public void Breaks_y_sus_grafias_son_Breakbeat(string genero)
        => Assert.Equal("Breakbeat", GenreNormalizer.Canonical(genero));

    [Theory]
    [InlineData("Nu Breaks", "Nu Breaks")]
    [InlineData("Progressive Breaks", "Progressive Breaks")]
    [InlineData("Breakcore", "Breakcore")]
    [InlineData("Break", "Break")]
    public void Los_estilos_que_solo_contienen_la_palabra_no_cambian(string genero, string esperado)
        => Assert.Equal(esperado, GenreNormalizer.Canonical(genero));

    [Fact]
    public void Breakbeat_es_un_genero_conocido()
        => Assert.Contains("Breakbeat", GenreNormalizer.Conocidos);

    [Fact]
    public void Breaks_es_de_la_familia_electronica_y_no_es_generico()
    {
        Assert.Equal(FamiliasGenero.Electronica, FamiliasGenero.Familia("Breaks"));
        Assert.True(FamiliasGenero.MismaFamilia("Breaks", "Breakbeat"));
        Assert.Equal(FamiliasGenero.Clave("Breakbeat"), FamiliasGenero.Clave("Breaks"));
        Assert.False(FamiliasGenero.EsGenerico("Breaks"));
    }

    [Fact]
    public void Unificar_generos_propone_Breakbeat_por_regla()
    {
        var p = GenerosIa.PorReglas("Breaks", 12);
        Assert.Equal(OrigenGenero.Regla, p.Origen);
        Assert.Equal("Breakbeat", p.Propuesto);

        // Lo que ya está escrito así no se toca, y una lista conserva sus géneros.
        Assert.Equal(OrigenGenero.SinCambio, GenerosIa.PorReglas("Breakbeat", 3).Origen);
        Assert.Equal("Breakbeat; House", GenerosIa.PorReglas("Breaks; House", 1).Propuesto);
    }

    [Fact]
    public void El_mapa_escribe_el_genero_como_lo_escribe_la_biblioteca()
    {
        Assert.Equal("Breakbeat", GeneroPorReglas.ComoEnLaBiblioteca("Breaks", new[] { "Reggaeton", "Breakbeat" }));
        Assert.Equal("Breakbeat", GeneroPorReglas.ComoEnLaBiblioteca("breaks", new[] { "Reggaeton" }));
    }

    private static Track T(string archivo, string genero) => new()
    {
        Artist = "A", Title = archivo, Genre = genero, FilePath = $@"C:\Musica\{archivo}.mp3",
    };

    [Fact]
    public void Breaks_y_Breakbeat_cuentan_como_un_solo_genero_de_la_biblioteca()
    {
        var generos = GeneroPorReglas.GenerosDeLaBiblioteca(new[]
        {
            T("uno", "Breaks"), T("dos", "Breakbeat"), T("tres", "breaks"), T("cuatro", "Reggaeton"),
        });

        Assert.Contains(("Breakbeat", 3), generos);
        Assert.DoesNotContain(generos, g => g.Genero == "Breaks");
    }

    [Fact]
    public void Una_coleccion_de_Breakbeat_incluye_las_canciones_etiquetadas_Breaks()
    {
        var c = new ColeccionInteligente { Nombre = "Breaks", Genero = "Breakbeat" };
        Assert.True(FiltroColeccion.Cumple(c, T("uno", "Breaks"), null));
        Assert.True(FiltroColeccion.Cumple(c, T("dos", "Breakbeat"), null));
        Assert.False(FiltroColeccion.Cumple(c, T("tres", "Drum & Bass"), null));

        var alReves = new ColeccionInteligente { Nombre = "Breaks", Genero = "breaks" };
        Assert.True(FiltroColeccion.Cumple(alReves, T("dos", "Breakbeat"), null));
    }

    // =================================================================================================
    // El elector de Discogs: un estilo concreto manda sobre uno genérico
    // =================================================================================================

    [Fact]
    public void Un_estilo_concreto_manda_sobre_Latin_o_Electro_aunque_vayan_antes()
    {
        Assert.Equal("Reggaeton", GenrePicker.Pick(new[] { "Latin", "Reggaeton" }, new[] { "Latin", "Hip Hop" }));
        Assert.Equal("Tech House", GenrePicker.Pick(new[] { "Latin", "Tech House" }, new[] { "Electronic", "Latin" }));
        Assert.Equal("House", GenrePicker.Pick(new[] { "Electro", "House" }, new[] { "Electronic" }));
        Assert.Equal("Dancehall", GenrePicker.Pick(new[] { "Latin", "Electro", "Dancehall" }, null));
    }

    [Fact]
    public void Con_solo_estilos_genericos_sigue_eligiendo_como_antes()
    {
        Assert.Equal("Latin", GenrePicker.Pick(new[] { "Latin" }, new[] { "Latin" }));
        Assert.Equal("Electro", GenrePicker.Pick(new[] { "Electro" }, new[] { "Electronic" }));
        // «Latin Pop» y «Breaks» no están en la lista de preferencias: gana el preferido.
        Assert.Equal("Latin", GenrePicker.Pick(new[] { "Latin", "Latin Pop" }, null));
        Assert.Equal("Latin", GenrePicker.Pick(new[] { "Latin", "Breaks" }, null));
        // Sin nada preferido, el primer estilo; FileProcessor lo normaliza después («Breaks» → Breakbeat).
        Assert.Equal("Breaks", GenrePicker.Pick(new[] { "Breaks" }, new[] { "Electronic" }));
        Assert.Equal("Breakbeat", GenreNormalizer.Canonical(GenrePicker.Pick(new[] { "Breaks" }, new[] { "Electronic" })));
    }

    // Los géneros de Discogs son amplios: sin estilos, [Latin, Hip Hop] puede ser un disco de reggaeton,
    // y Latin se puede afinar después sin preguntar; un «Hip Hop» equivocado, no.
    [Fact]
    public void Entre_los_generos_no_cambia_nada()
    {
        Assert.Equal("Latin", GenrePicker.Pick(Array.Empty<string>(), new[] { "Latin", "Hip Hop" }));
        Assert.Equal("Latin", GenrePicker.Pick(null, new[] { "Latin", "Reggaeton" }));
        Assert.Equal("Hip Hop", GenrePicker.Pick(Array.Empty<string>(), new[] { "Hip Hop", "Latin" }));
        // Un estilo genérico seguido de un género concreto: tampoco cambia.
        Assert.Equal("Latin", GenrePicker.Pick(new[] { "Latin" }, new[] { "Hip Hop", "Latin" }));
    }

    [Fact]
    public void Lo_que_ya_elegia_bien_sigue_igual()
    {
        Assert.Equal("Reggaeton", GenrePicker.Pick(new[] { "Indie XYZ" }, new[] { "Reggaeton" }));
        Assert.Equal("Pop", GenrePicker.Pick(new[] { "Pop" }, new[] { "Reggaeton" }));
        Assert.Equal("Reggaeton", GenrePicker.Pick(new[] { "Reggaeton", "Latin" }, new[] { "Latin" }));
        Assert.Equal("Trap", GenrePicker.Pick(new[] { "Trap", "Reggaeton" }, new[] { "Hip Hop" }));
        Assert.Equal("", GenrePicker.Pick(null, null));
    }

    [Fact]
    public void Enriquecer_con_Discogs_escribe_el_estilo_concreto()
    {
        var res = new JsonArray
        {
            new JsonObject
            {
                ["title"] = "Feid - Vente Conmigo", ["year"] = "2023",
                ["style"] = new JsonArray { "Latin", "Reggaeton" },
                ["genre"] = new JsonArray { "Hip Hop", "Latin" },
            },
        };
        Assert.Equal("Reggaeton", DiscogsProvider.ElegirDelArtista(res, "Feid")?.Genre);
        Assert.Equal("Reggaeton", DiscogsProvider.GeneroDelArtista(res, "Feid")?.Genre);
    }

    // Deezer llama a Pick sin estilos cuando no hay nada latino: sus gruesos se eligen como antes. Pop,
    // Rock o Rap/Hip Hop son gruesos de Deezer y no ganan a Electro, que se puede afinar después.
    [Fact]
    public void Deezer_no_cambia_con_el_arreglo_de_Discogs()
    {
        Assert.Equal("Electro", GenrePicker.PickDeezer(new[] { "Electro", "Pop" }));
        Assert.Equal("Electro", GenrePicker.PickDeezer(new[] { "Electro", "Rock" }));
        Assert.Equal("Electro", GenrePicker.PickDeezer(new[] { "Electro", "Rap/Hip Hop" }));
        Assert.Equal("Reggaeton", GenrePicker.PickDeezer(new[] { "Latin", "Reggaeton" }));
        Assert.Equal("Latin Urban", GenrePicker.PickDeezer(new[] { "Rap/Hip Hop", "Latin" }));
    }
}
