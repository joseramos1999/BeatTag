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

    // Revisión: la búsqueda con una frase guarda cada género por su nombre unificado, y «breaks» dejó de
    // encontrar el de una biblioteca etiquetada «Breaks». Cuenta también como lo escribe la biblioteca.
    [Fact]
    public void La_busqueda_con_una_frase_encuentra_el_genero_como_lo_escribe_la_biblioteca()
    {
        var breaks = new VocabularioBiblioteca(Enumerable.Range(0, 5).Select(i => T($"b{i}", "Breaks")));
        Assert.Equal(new[] { "Breakbeat" }, breaks.GenerosEn("breaks para el pico"));
        Assert.Equal(new[] { "Breakbeat" }, breaks.GenerosEn("breakbeat y breaks"));

        var rap = new VocabularioBiblioteca(Enumerable.Range(0, 5).Select(i => T($"r{i}", "Rap; Latino")));
        Assert.Equal(new[] { "Hip Hop" }, rap.GenerosEn("rap de los 90"));
        Assert.Empty(rap.GenerosEn("trap de los 90"));   // por palabras: «rap» no está en «trap»

        // Lo que la biblioteca no escribe así no se añade: ninguna canción dice «perreo».
        var reggaeton = new VocabularioBiblioteca(Enumerable.Range(0, 5).Select(i => T($"g{i}", "Reggaeton")));
        Assert.Empty(reggaeton.GenerosEn("perreo"));
    }

    // Revisión: organizando en cada carpeta de la biblioteca y conservando las subcarpetas, una carpeta
    // «Breaks» dejó de contar como la de su género («Breaks» ya se unifica como Breakbeat) y sus canciones
    // acababan en «Breakbeat/Breaks/Pack 2023». Pasaba igual con «Rap» o «Reguetón».
    private static readonly string Raiz = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "Musica");

    private static Track EnCarpeta(string carpeta, string archivo, string genero) => new()
    {
        FilePath = Path.Combine(Raiz, carpeta, archivo), Folder = Raiz, Genre = genero,
        Title = Path.GetFileNameWithoutExtension(archivo),
    };

    private static PlanOrganizar EnLaRaiz(params Track[] canciones)
    {
        var disco = new DiscoEnMemoria();
        foreach (var t in canciones) disco.Archivo(t.FilePath);
        return OrganizarPorGenero.Planear(canciones, new[] { Raiz },
            new OpcionesOrganizar { Destino = DestinoOrganizar.EnLaRaiz, ConservarEstructura = true }, disco);
    }

    [Theory]
    [InlineData("Breaks")]
    [InlineData("Breakbeat")]
    public void Una_carpeta_con_otra_forma_del_genero_no_se_anida_dentro_de_la_nueva(string genero)
    {
        var plan = EnLaRaiz(EnCarpeta(Path.Combine("Breaks", "Pack 2023"), "a.mp3", genero));
        Assert.Equal(Path.Combine(Raiz, "Breakbeat", "Pack 2023", "a.mp3"), Assert.Single(plan.Movimientos).Destino);

        var suelta = EnLaRaiz(EnCarpeta("Breaks", "b.mp3", genero));
        Assert.Equal(Path.Combine(Raiz, "Breakbeat", "b.mp3"), Assert.Single(suelta.Movimientos).Destino);
    }

    [Theory]
    [InlineData("Rap", "Hip Hop")]
    [InlineData("Reguetón", "Reggaeton")]
    public void Tampoco_con_los_alias_que_ya_existian(string carpeta, string genero)
    {
        var plan = EnLaRaiz(EnCarpeta(Path.Combine(carpeta, "Pack"), "a.mp3", genero));
        Assert.Equal(Path.Combine(Raiz, genero, "Pack", "a.mp3"), Assert.Single(plan.Movimientos).Destino);
    }

    // Una carpeta que no es ninguna forma del género sigue conservándose entera.
    [Fact]
    public void Una_carpeta_que_no_es_un_genero_se_conserva()
    {
        var plan = EnLaRaiz(EnCarpeta(Path.Combine("Packs", "Enero"), "a.mp3", "Breaks"));
        Assert.Equal(Path.Combine(Raiz, "Breakbeat", "Packs", "Enero", "a.mp3"), Assert.Single(plan.Movimientos).Destino);
    }

    // Revisión 2: lo mismo con las formas que la tabla no conoce pero que se agrupan con un género de la
    // biblioteca. La carpeta se llama como la mayoría; la de la forma minoritaria es la misma carpeta de
    // género y no se anida.
    [Fact]
    public void Una_carpeta_con_la_forma_minoritaria_no_se_anida()
    {
        var plan = EnLaRaiz(
            EnCarpeta(Path.Combine("Electro-Latino", "Pack"), "a.mp3", "Electro-Latino"),
            EnCarpeta("Nuevas", "b.mp3", "Electro Latino"), EnCarpeta("Nuevas", "c.mp3", "Electro Latino"));

        Assert.Contains(plan.Movimientos, m => m.Destino == Path.Combine(Raiz, "Electro Latino", "Pack", "a.mp3"));
    }

    // Revisión 3: un nombre con varios géneros, o con letras que la clave no conserva, no es otra forma
    // de escribir un género: son carpetas tuyas y se conservan, como antes de reconocer las otras formas.
    [Theory]
    [InlineData("Salsa, Vol. 1", "Salsa")]
    [InlineData("Reggaeton, Dembow", "Reggaeton")]
    [InlineData("2023, Reggaeton", "Reggaeton")]
    [InlineData("邦楽 Pop", "Pop")]
    [InlineData("Русский Rap", "Hip Hop")]
    public void Una_carpeta_tuya_que_nombra_un_genero_entre_otras_cosas_se_conserva(string carpeta, string genero)
    {
        var plan = EnLaRaiz(EnCarpeta(Path.Combine(carpeta, "Pack"), "a.mp3", genero));
        Assert.Equal(Path.Combine(Raiz, genero, carpeta, "Pack", "a.mp3"), Assert.Single(plan.Movimientos).Destino);
    }

    [Fact]
    public void Dos_volumenes_con_el_mismo_archivo_no_chocan()
    {
        var plan = EnLaRaiz(EnCarpeta("Salsa, Vol. 1", "01 Intro.mp3", "Salsa"), EnCarpeta("Salsa, Vol. 2", "01 Intro.mp3", "Salsa"));
        Assert.Equal(2, plan.Movimientos.Count);
        Assert.Contains(plan.Movimientos, m => m.Destino == Path.Combine(Raiz, "Salsa", "Salsa, Vol. 2", "01 Intro.mp3"));
    }

    // Una carpeta que el propio BeatTag creó con letras que la clave no conserva se sigue reconociendo
    // por su nombre exacto.
    [Fact]
    public void La_carpeta_exacta_de_un_genero_con_otras_letras_se_sigue_reconociendo()
    {
        var plan = EnLaRaiz(EnCarpeta(Path.Combine("J-Pop 日本", "Pack"), "a.mp3", "J-Pop 日本"));
        Assert.Empty(plan.Movimientos);
        Assert.Equal(MotivoOmision.YaEnSuSitio, Assert.Single(plan.Omitidas).Motivo);
    }

    // Revisión 2: si una forma conocida minoritaria daba nombre al género, la carpeta de la mayoría
    // («Drum&Bass», que la tabla no conoce: el «&» va sin espacios) quedaba dentro de la nueva. La
    // carpeta se llama como la mayoría, así que lo que ya está en ella se queda.
    [Fact]
    public void La_carpeta_de_la_mayoria_se_queda_aunque_haya_una_forma_conocida()
    {
        var pack = Path.Combine("Drum&Bass", "Pack");
        var plan = EnLaRaiz(EnCarpeta(pack, "a.mp3", "Drum&Bass"), EnCarpeta(pack, "b.mp3", "Drum&Bass"),
                            EnCarpeta(pack, "c.mp3", "Drum&Bass"), EnCarpeta("Nuevas", "d.mp3", "Drum & Bass"));

        var m = Assert.Single(plan.Movimientos);
        Assert.Equal(Path.Combine(Raiz, "Drum&Bass", "Nuevas", "d.mp3"), m.Destino);
        Assert.Equal(3, plan.CuantasOmitidas(MotivoOmision.YaEnSuSitio));
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
        // «Latin Pop» y «Breaks» no están en la lista de preferencias: gana el preferido, como antes. Que
        // los estilos concretos que no están en la lista (Breaks, Drum n Bass, Trance…) ganen a Latin o a
        // Electro cambiaría muchas más elecciones, y no está medido.
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
