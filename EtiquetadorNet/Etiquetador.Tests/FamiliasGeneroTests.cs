using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// Género por artista (A1): qué géneros son genéricos (se pueden sustituir sin preguntar) y qué
/// estilos son de la misma familia (no se cambian entre sí). Las tablas salen de lo que significa
/// cada género, no de una biblioteca concreta.
/// </summary>
public class FamiliasGeneroTests
{
    [Theory]
    [InlineData("Latin")]
    [InlineData("latino")]
    [InlineData("Latin Music")]
    [InlineData("Música Latina")]
    [InlineData("Urbano latino")]
    [InlineData("Dance")]
    [InlineData("Electro")]
    [InlineData("Electrónica")]
    [InlineData("Electronic")]
    [InlineData("World")]
    [InlineData("Mashup")]
    [InlineData("Transition")]
    [InlineData("Extended")]
    [InlineData("@usuario")]
    [InlineData("www.pool.com")]
    [InlineData("Desconocido")]
    public void Un_mercado_un_espectro_o_un_tipo_de_version_es_generico(string genero)
        => Assert.True(FamiliasGenero.EsGenerico(genero), genero);

    [Theory]
    [InlineData("EDM")]
    [InlineData("Pop")]
    [InlineData("Hip Hop")]
    [InlineData("Reggaeton")]
    [InlineData("Electro House")]
    [InlineData("Electro Latino")]
    [InlineData("Latin House")]
    [InlineData("Trap")]
    [InlineData("")]
    [InlineData(null)]
    public void Un_estilo_que_se_mezcla_no_es_generico(string? genero)
        => Assert.False(FamiliasGenero.EsGenerico(genero), genero);

    [Theory]
    [InlineData("reguetón")]
    [InlineData("Reggaeton")]
    [InlineData("Moombahton")]
    [InlineData("Dembow")]
    [InlineData("Mambo")]
    [InlineData("Guaracha")]
    [InlineData("RKT")]
    [InlineData("Latin")]
    [InlineData("Verbena")]
    public void Los_estilos_que_un_DJ_distingue_y_lo_desconocido_no_tienen_familia(string genero)
        => Assert.Null(FamiliasGenero.Familia(genero));

    [Theory]
    [InlineData("Trap", "Hip Hop")]
    [InlineData("Rap", "Drill")]
    [InlineData("Flamenco", "Rumba")]
    [InlineData("Sevillanas", "Rumba Catalana")]
    [InlineData("Pop", "Rock")]
    [InlineData("Dance-Pop", "Pop")]
    [InlineData("Electro House", "EDM")]
    [InlineData("Tech House", "Melodic Techno")]
    [InlineData("Afro House", "House")]
    [InlineData("Bachata", "Salsa")]
    [InlineData("R&B", "Soul")]
    [InlineData("Afrobeats", "Amapiano")]
    [InlineData("Dancehall", "Reggae")]
    public void Misma_familia(string a, string b)
        => Assert.True(FamiliasGenero.MismaFamilia(a, b), $"{a} y {b}");

    [Theory]
    [InlineData("Bachata", "Reggaeton")]
    [InlineData("Moombahton", "Reggaeton")]   // sin familia: siempre distintos
    [InlineData("Reggaeton", "Reggaeton")]    // el mismo se compara aparte, por su clave
    [InlineData("Soul", "EDM")]
    [InlineData("Trap", "Reggaeton")]
    [InlineData("Afro House", "Afrobeats")]   // Afro House se pincha con la electrónica
    [InlineData("Pop", "Latin")]
    public void Distinta_familia(string a, string b)
        => Assert.False(FamiliasGenero.MismaFamilia(a, b), $"{a} y {b}");

    [Fact]
    public void Afro_House_es_electronica_y_no_afro()
    {
        Assert.Equal(FamiliasGenero.Electronica, FamiliasGenero.Familia("Afro House"));
        Assert.Equal(FamiliasGenero.Afro, FamiliasGenero.Familia("Afro"));
        Assert.Equal(FamiliasGenero.HipHop, FamiliasGenero.Familia("hip-hop"));   // con la grafía unificada
    }
}
