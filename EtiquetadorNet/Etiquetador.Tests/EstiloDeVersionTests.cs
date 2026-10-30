using System.Diagnostics;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// Género por artista (A2): el estilo que nombra la propia versión manda sobre el del tema original.
/// Los casos son los de una biblioteca real: los positivos se revisaron a mano, y los negativos son
/// los falsos positivos que daba buscar el estilo sin exigir el contexto de versión.
/// </summary>
public class EstiloDeVersionTests
{
    [Theory]
    [InlineData("Mas Rica Que Ayer (Sandunga Moombahton Remix)", "Moombahton")]
    [InlineData("Dividimos (Bachata Version)", "Bachata")]
    [InlineData("No Te Hagas (Dembow Remix, Extended)", "Dembow")]
    [InlineData("Despechá (Versión Rumba)", "Rumba")]
    [InlineData("Junto al Amanecer (Salsa)", "Salsa")]
    [InlineData("Ratones Coloraos (Sevillanas)", "Sevillanas")]
    [InlineData("Bonita (Tech House Remix)", "Tech House")]
    [InlineData("Dolce Vita (Antonio Guevara Afro House Remix)", "Afro House")]
    [InlineData("Mi Gente (Intensa Dnb Bootleg 175bpm)", "Drum & Bass")]
    [InlineData("Dakiti [Mambo Remix] X Komodo (Alex Ferrer Mashup)", "Mambo")]
    [InlineData("COCHINAE GUARACHA REMIX DJ PROF EDIT", "Guaracha")]
    [InlineData("Toxic Givenchy (F3LY Mashup Techno)", "Techno")]
    [InlineData("Ven Bailalo - Reggaeton Mix", "Reggaeton")]
    [InlineData("Tu Jardin Con Enanitos (Suel Rkt Edit) 100", "RKT")]
    [InlineData("Starboy (Vaskan Hardstyle Remix)", "Hardstyle")]
    [InlineData("La Plena (Versión Flamenquito)", "Flamenco")]
    [InlineData("Tusa (Hypecloud Rumbaton Edit 100 Bpm)", "Rumba")]
    [InlineData("Gasolina (SAMI Dancehall Edit)", "Dancehall")]
    [InlineData("Lose Control (RUSHMORE & CIBUS Techno VIP)", "Techno")]
    [InlineData("Sopa de Caracol (Merengue)", "Merengue")]
    public void El_estilo_de_la_version_se_lee_del_titulo(string titulo, string estilo)
    {
        var v = EstiloDeVersion.De(titulo, null, null, null);
        Assert.NotNull(v);
        Assert.Equal(estilo, v!.Estilo);
        Assert.Equal(FuenteEstilo.Titulo, v.Fuente);
        Assert.False(v.Ambiguo);
    }

    [Theory]
    [InlineData("La Disco Resplandece", null)]
    [InlineData("El Mambo x Estrellita (Alex Ferrer Mashup)", null)]
    [InlineData("Ella Quiere Hmm", "Daddy Yankee x Anuel AA, Mambo Kingz, DJ Luian")]
    [InlineData("Tribal Kush", null)]
    [InlineData("Jump Around (House Of Pain)", null)]
    [InlineData("Trap Queen", null)]
    [InlineData("PERRO NEGRO x Reggaeton (Latin Box Mashup)", null)]
    [InlineData("Flamenco y Bachata x Despacito", null)]
    [InlineData("Dembow 2020", null)]
    [InlineData("Afro Trap Pt.7", null)]
    [InlineData("Gatubela (Mashup) (Extended Latino)", null)]
    [InlineData("Gasolina (Mashup Latino)", null)]
    [InlineData("Trap To Mambo Transition", null)]
    [InlineData("Transition House To Reggaeton 128-95", null)]
    [InlineData("Titanium (Extended Mix)", null)]
    [InlineData("Lovumba (Club Mix)", null)]
    [InlineData("Hello (Radio Edit)", null)]
    // Visto al validar en una biblioteca real: «La Disco» es parte del nombre de la canción.
    [InlineData("Que Le De x Llegamos A La Disco Remix - Rauw", null)]
    [InlineData("El Mambo Remix", null)]
    // «Disco Mix» y «Disco Version» son el nombre clásico de la versión de 12" (la extendida) de
    // cualquier tema de los 80, no el estilo disco; en español, «Versión Disco».
    [InlineData("Smalltown Boy (Disco Mix)", "Bronski Beat")]
    [InlineData("Tainted Love (Disco Version)", "Soft Cell")]
    [InlineData("You Spin Me Round (Extended Disco Mix)", "Dead Or Alive")]
    [InlineData("Bailar Pegados - Versión Disco", "Sergio Dalma")]
    [InlineData("Vamos A La Playa [Disco Edit]", "Righeira")]
    // Un remixer que se llama como un estilo: lo que va detrás de «DJ» o de «MC» es un nombre.
    [InlineData("Gasolina (DJ Mambo Remix)", "Daddy Yankee")]
    [InlineData("Tema (MC Trap Edit)", null)]
    public void Un_estilo_fuera_del_contexto_de_version_no_cuenta(string titulo, string? artista)
        => Assert.Null(EstiloDeVersion.De(titulo, titulo, artista, null));

    // El estilo disco sí cuenta cuando el paréntesis no dice otra cosa, o como Nu Disco.
    [Theory]
    [InlineData("Got To Be Real (Disco)", "Disco")]
    [InlineData("Midnight City (Nu Disco Remix)", "Nu Disco")]
    [InlineData("Tema (DJ Pepe Techno Remix)", "Techno")]
    public void Disco_suelto_y_Nu_Disco_si_son_estilos(string titulo, string estilo)
        => Assert.Equal(estilo, EstiloDeVersion.De(titulo, null, null, null)?.Estilo);

    [Fact]
    public void Afro_y_Electro_son_ambiguos()
    {
        var afro = EstiloDeVersion.De("Afro Edit", null, null, null);
        Assert.Equal("Afro", afro?.Estilo);
        Assert.True(afro!.Ambiguo);
        Assert.True(EstiloDeVersion.De("Tema (Electro Mix)", null, null, null)!.Ambiguo);
        // Con su segunda palabra ya no lo son.
        Assert.False(EstiloDeVersion.De("Tema (Electro House Remix)", null, null, null)!.Ambiguo);
    }

    [Fact]
    public void Se_busca_en_el_titulo_el_archivo_el_artista_y_el_album_por_ese_orden()
    {
        var album = EstiloDeVersion.De("Dividimos", "Dividimos", "Romeo Santos", "Bachata Hits (Bachata Version)");
        Assert.Equal(FuenteEstilo.Album, album!.Fuente);
        Assert.Equal("Bachata", album.Estilo);

        var archivo = EstiloDeVersion.De("Dividimos", "Romeo Santos - Dividimos (Bachata Version)", null, null);
        Assert.Equal(FuenteEstilo.Archivo, archivo!.Fuente);

        // Si el título ya lo dice, manda el título.
        var titulo = EstiloDeVersion.De("Dividimos (Salsa Version)", "Dividimos (Bachata Version)", null, "Pack (Merengue)");
        Assert.Equal(("Salsa", FuenteEstilo.Titulo), (titulo!.Estilo, titulo.Fuente));
        Assert.Equal("Salsa Version", titulo.Texto);
    }

    [Theory]
    [InlineData("Despacito (Reguetón Remix)", "Reggaeton")]
    [InlineData("Despacito (Reggaetón Mix)", "Reggaeton")]
    [InlineData("Lean On (Moombah Edit)", "Moombahton")]
    [InlineData("Tema (Drum & Bass Remix)", "Drum & Bass")]
    [InlineData("Tema (Cumbiaton Remix)", "Cumbia")]
    [InlineData("Tema (Aleteo Remix)", "Guaracha")]
    [InlineData("Tema - Versión Salsa", "Salsa")]
    public void Cada_forma_de_escribirlo_da_el_mismo_estilo(string titulo, string estilo)
        => Assert.Equal(estilo, EstiloDeVersion.De(titulo, null, null, null)?.Estilo);

    // Prueba de humo del coste: casi ningún título nombra un estilo y esos se descartan con un único
    // filtro. Sin aserción de tiempo estricta (la CI comparte máquina).
    [Fact]
    public void Veinte_mil_titulos_se_revisan_enseguida()
    {
        var titulos = Enumerable.Range(0, 20_000).Select(i => i % 10 == 0
            ? $"Tema {i} (DJ {i} Reggaeton Remix)"
            : $"Artista {i} - Tema número {i} (Extended Mix)").ToList();
        var reloj = Stopwatch.StartNew();
        var hallados = titulos.Count(t => EstiloDeVersion.De(t, t, "Artista", "Álbum") != null);
        reloj.Stop();
        Assert.Equal(2_000, hallados);
        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(30), $"tardó {reloj.Elapsed.TotalSeconds:0.0} s");
    }
}
