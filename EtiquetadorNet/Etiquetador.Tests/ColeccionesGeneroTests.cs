using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// El género de una colección se busca por PALABRAS, no como trozo de texto.
///
/// MEDIDO en una biblioteca real (237 géneros distintos): comparando sin espacios, «rap» metía además
/// 432 canciones de «Trap», y «dance» -que el Asistente reconoce como género- 82 de «Dancehall» y 2
/// de «Eurodance». Lo que salía de ahí acababa en el M3U exportado y en «Ordenar para mezclar».
/// </summary>
public class ColeccionesGeneroTests
{
    private static Track Tema(string genero, string titulo = "Tema") => new()
    {
        Artist = "Artista", Title = titulo, Genre = genero, FilePath = $@"C:\Musica\Artista - {titulo} ({genero}).mp3",
    };

    private static bool Entra(string pedido, string generoDelTema)
        => FiltroColeccion.Cumple(new ColeccionInteligente { Genero = pedido }, Tema(generoDelTema), null);

    [Theory]
    [InlineData("rap", "Trap", false)]            // 432 canciones de más en la biblioteca medida
    [InlineData("rap", "trap", false)]
    [InlineData("rap", "Rap", true)]
    [InlineData("rap", "Rap; Hip Hop", true)]
    [InlineData("dance", "Dancehall", false)]     // 82 de más
    [InlineData("dance", "Eurodance", false)]     // 2 de más
    [InlineData("dance", "Dance", true)]
    [InlineData("house", "Deep House", true)]     // lo que sí se quiere: un estilo dentro de otro
    [InlineData("house", "Tech-House", true)]
    [InlineData("techno", "Deep House", false)]
    public void El_genero_se_compara_por_palabras_enteras(string pedido, string generoDelTema, bool entra)
        => Assert.Equal(entra, Entra(pedido, generoDelTema));

    // «latin» entraba en «Latino» por ser un trozo de la palabra (27 canciones en la biblioteca medida).
    // Por palabras enteras se perdían; se admite la misma palabra con su terminación de género o número.
    [Theory]
    [InlineData("latin", "Latino", true)]
    [InlineData("latin", "Música Latina", true)]
    [InlineData("latino", "Latin Pop", true)]
    [InlineData("cumbia", "Cumbias", true)]
    public void Se_admite_la_terminacion_de_genero_o_numero(string pedido, string generoDelTema, bool entra)
        => Assert.Equal(entra, Entra(pedido, generoDelTema));

    // El Asistente cuenta «Rap» como «Hip Hop» (GenreNormalizer los da por sinónimos) y crea la
    // colección con «Hip Hop»: las canciones etiquetadas «Rap» tienen que entrar.
    [Theory]
    [InlineData("Hip Hop", "Rap", true)]
    [InlineData("hip-hop", "Hip Hop", true)]
    [InlineData("Drum and Bass", "Drum & Bass", true)]
    [InlineData("Hip Hop", "Trap", false)]
    public void Los_sinonimos_conocidos_cuentan_como_el_mismo_genero(string pedido, string generoDelTema, bool entra)
        => Assert.Equal(entra, Entra(pedido, generoDelTema));

    // De punta a punta, como lo hace «Buscar con una frase»: «dance para el pico» no puede traer dancehall.
    [Fact]
    public void Buscar_con_una_frase_dance_no_trae_dancehall()
    {
        var canciones = new List<Track>();
        for (var i = 0; i < 3; i++)
        {
            canciones.Add(Tema("Dance", $"Dance {i}"));
            canciones.Add(Tema("Dancehall", $"Dancehall {i}"));
        }
        var voc = new VocabularioBiblioteca(canciones);

        var c = BusquedaIa.AColeccion(BusquedaIa.DesdeFrase("dance para el pico", voc), "Pico");

        Assert.Equal("Dance", c.Genero);
        Assert.True(FiltroColeccion.Cumple(c, Tema("Dance"), new FichaDj { Momentos = MomentoSet.Pico }));
        Assert.False(FiltroColeccion.Cumple(c, Tema("Dancehall"), new FichaDj { Momentos = MomentoSet.Pico }));
    }

    // Varios géneros separados por comas siguen valiendo como «cualquiera de ellos».
    [Fact]
    public void Varios_generos_siguen_siendo_cualquiera_de_ellos()
    {
        Assert.True(Entra("cumbia, merengue", "Merengue"));
        Assert.False(Entra("cumbia, merengue", "Salsa"));
    }
}
