using Etiquetador.Core.Ai;

namespace Etiquetador.Tests;

/// <summary>
/// «Unificar géneros» con valores que listan varios géneros: «Rap; Hip Hop; Latino».
///
/// GenreNormalizer.Canonical se queda con el PRIMER género de la lista, y la regla lo proponía
/// como cambio fiable, marcado por defecto. MEDIDO en una biblioteca real: 159 canciones tienen
/// varios géneros, y unas 21 perdían uno de verdad al aplicar lo marcado: «Reggaeton; Latino» (8)
/// pasaba a «Reggaeton», «Rap; Hip Hop; Latino» (6) a «Hip Hop», «Rap; Hip Hop; Pop» (3), «Pop;
/// Latino» (2) y «Pop; Rock» (1) perdían el segundo. Ahora se unifica cada género de la lista y la
/// lista se conserva.
/// </summary>
public class GenerosVariosTests
{
    [Theory]
    [InlineData("Reggaeton; Latino", 8)]
    [InlineData("Pop; Latino", 2)]
    [InlineData("Pop; Rock", 1)]
    [InlineData("Pop; Pop latino; Latino", 1)]
    public void Una_lista_sin_nada_que_unificar_no_cambia(string original, int canciones)
    {
        var p = GenerosIa.PorReglas(original, canciones);

        Assert.Equal(OrigenGenero.SinCambio, p.Origen);
        Assert.False(p.Cambia);
    }

    [Theory]
    [InlineData("Rap; Hip Hop; Latino", "Hip Hop; Latino")]
    [InlineData("Rap; Hip Hop; Pop", "Hip Hop; Pop")]
    [InlineData("hip-hop; Latino", "Hip Hop; Latino")]
    public void Se_unifica_cada_genero_y_se_conserva_la_lista(string original, string esperado)
    {
        var p = GenerosIa.PorReglas(original, 6);

        Assert.Equal(OrigenGenero.Regla, p.Origen);
        Assert.Equal(esperado, p.Propuesto);
    }

    // «Rap; Hip Hop» (77 canciones en la biblioteca medida) son sinónimos: quedarse con «Hip Hop»
    // no pierde nada, y sigue saliendo como regla.
    [Theory]
    [InlineData("Rap; Hip Hop")]
    [InlineData("Hip-Hop/Rap")]
    public void Dos_sinonimos_quedan_en_uno(string original)
    {
        var p = GenerosIa.PorReglas(original, 77);

        Assert.Equal(OrigenGenero.Regla, p.Origen);
        Assert.Equal("Hip Hop", p.Propuesto);
    }

    // Lo que no es un género sale de la lista, pero los géneros que la acompañan se quedan: antes
    // «Pop; z1.fm» entero contaba como «no es un género» y se proponía vaciarlo.
    [Fact]
    public void De_una_lista_solo_se_quita_lo_que_no_es_un_genero()
    {
        var p = GenerosIa.PorReglas("Pop; z1.fm", 4);

        Assert.Equal(OrigenGenero.Regla, p.Origen);
        Assert.Equal("Pop", p.Propuesto);
    }

    [Fact]
    public void N_barra_a_sigue_sin_ser_un_genero()
    {
        var p = GenerosIa.PorReglas("n/a", 4);

        Assert.Equal(OrigenGenero.NoEsGenero, p.Origen);
        Assert.Equal("", p.Propuesto);
    }

    // La IA solo puede contestar UN género de la lista de destinos: preguntarle por una lista solo
    // podría reducirla a uno. Y una lista no es un destino al que llevar otros géneros.
    [Fact]
    public void Las_listas_ni_se_preguntan_a_la_ia_ni_son_destino()
    {
        var propuestas = new[]
        {
            GenerosIa.PorReglas("Reggaeton", 3000),
            GenerosIa.PorReglas("Pop; Latino", 400),
            GenerosIa.PorReglas("Rap; Hip Hop; Latino", 300),
            GenerosIa.PorReglas("Latijnse muziek", 12),
        };

        Assert.DoesNotContain("Pop; Latino", GenerosIa.ParaIa(propuestas));
        Assert.Contains("Latijnse muziek", GenerosIa.ParaIa(propuestas));

        var destinos = GenerosIa.Destinos(propuestas);
        Assert.DoesNotContain("Pop; Latino", destinos);
        Assert.DoesNotContain("Hip Hop; Latino", destinos);
        Assert.Contains("Reggaeton", destinos);
    }
}
