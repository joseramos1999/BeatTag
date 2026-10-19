using System.Text.Json.Nodes;
using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// «Buscar con una frase»: los rangos de BPM y de años escritos con guion, y las décadas.
///
/// La frase se pasaba entera por VocabularioBiblioteca.Clave antes de buscar los rangos, y Clave
/// cambia los guiones por espacios: «house vocal 120-124 bpm» se leía como «… 120 124 bpm», el rango
/// no encajaba y se cogía «124 bpm» suelto, con su margen de pitch: una colección de 122 a 126 BPM.
/// Se perdían las de 120-121, entraban las de 125-126, y como el BPM ya salía de una regla, el
/// correcto que propusiera la IA se descartaba.
/// </summary>
public class BusquedaRangosTests
{
    private static Track T(string genero, uint bpm = 0, uint anio = 0, string titulo = "Tema") => new()
    {
        Artist = "Artista", Title = titulo, Genre = genero, Bpm = bpm, Year = anio,
        FilePath = $@"C:\Musica\Artista - {titulo}.mp3",
    };

    private static VocabularioBiblioteca Voc()
        => new(Enumerable.Range(0, 3).Select(i => T("House", titulo: $"House {i}"))
                         .Concat(Enumerable.Range(0, 3).Select(i => T("Bolero", titulo: $"Bolero {i}"))));

    private static string? Valor(IEnumerable<FiltroPropuesto> fs, string campo)
        => fs.FirstOrDefault(f => f.Campo == campo && f.Valido)?.Valor;

    [Theory]
    [InlineData("house vocal 120-124 bpm")]
    [InlineData("house vocal 120 - 124 BPM")]
    [InlineData("house vocal 120–124 bpm")]        // con raya, como la escribe el corrector de algunos teclados
    [InlineData("house vocal de 120 a 124 bpm")]
    [InlineData("house vocal entre 120 y 124 bpm")]
    public void El_rango_de_bpm_con_guion_se_lee_como_rango(string frase)
        => Assert.Equal("120-124", Valor(BusquedaIa.DesdeFrase(frase, Voc()), "bpm"));

    // Un tempo suelto sigue teniendo su margen de pitch.
    [Fact]
    public void Un_bpm_suelto_conserva_su_margen()
        => Assert.Equal("126-130", Valor(BusquedaIa.DesdeFrase("house a 128 bpm", Voc()), "bpm"));

    [Fact]
    public void La_coleccion_del_rango_con_guion_incluye_los_extremos()
    {
        var c = BusquedaIa.AColeccion(BusquedaIa.DesdeFrase("house vocal 120-124 bpm", Voc()), "Apertura");
        var vocal = new FichaDj { Voz = TipoVoz.Vocal };   // «vocal» también es una condición

        Assert.True(FiltroColeccion.Cumple(c, T("House", bpm: 120), vocal));
        Assert.True(FiltroColeccion.Cumple(c, T("House", bpm: 124), vocal));
        Assert.False(FiltroColeccion.Cumple(c, T("House", bpm: 125), vocal));
    }

    // Con «2010-2015» no salía ningún filtro de año por regla.
    [Theory]
    [InlineData("temas de 2010-2015")]
    [InlineData("temas del 2010 al 2015")]
    [InlineData("temas de 2010 a 2015")]
    public void El_rango_de_anos_con_guion_se_lee_como_rango(string frase)
        => Assert.Equal("2010-2015", Valor(BusquedaIa.DesdeFrase(frase, Voc()), "anio"));

    // «de los 40» daba 2040-2049: una colección siempre vacía.
    [Fact]
    public void Los_40_son_los_anos_cuarenta_y_no_el_futuro()
        => Assert.Equal("1940-1949", Valor(BusquedaIa.DesdeFrase("boleros de los 40", Voc()), "anio"));

    [Theory]
    [InlineData("30", "1930-1939")]
    [InlineData("40", "1940-1949")]
    [InlineData("50", "1950-1959")]
    [InlineData("80", "1980-1989")]
    [InlineData("00", "2000-2009")]
    [InlineData("20", "2020-2029")]       // en 2026, los 20 ya son estos
    [InlineData("1920", "1920-1929")]
    [InlineData("2030", null)]            // una década que no ha empezado no tiene canciones
    public void Una_decada_nunca_cae_en_el_futuro(string d, string? esperado)
        => Assert.Equal(esperado, BusquedaIa.RangoDecada(d, anioActual: 2026));

    // Si la IA devuelve el mínimo y el máximo al revés, la colección salía vacía.
    [Fact]
    public void El_minimo_y_el_maximo_de_la_ia_se_ordenan()
    {
        var json = new JsonObject
        {
            ["filtros"] = new JsonArray
            {
                new JsonObject { ["campo"] = "bpm_min", ["valor"] = 128, ["cita"] = "entre 120 y 128" },
                new JsonObject { ["campo"] = "bpm_max", ["valor"] = 120, ["cita"] = "entre 120 y 128" },
                new JsonObject { ["campo"] = "anio_min", ["valor"] = 2015, ["cita"] = "de 2010 a 2015" },
                new JsonObject { ["campo"] = "anio_max", ["valor"] = 2010, ["cita"] = "de 2010 a 2015" },
            },
        };

        var fs = BusquedaIa.DesdeIa(json, "house entre 120 y 128 de 2010 a 2015", Voc());

        Assert.Equal("120-128", Valor(fs, "bpm"));
        Assert.Equal("2010-2015", Valor(fs, "anio"));
    }
}
