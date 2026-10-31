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
    // Revisión 3: una clave Camelot acabada en A delante del estilo se leía como la preposición «a».
    [InlineData("Gasolina (8A Reggaeton Remix)", "Reggaeton")]
    [InlineData("Gasolina (12A Moombahton Edit)", "Moombahton")]
    [InlineData("Gasolina 8A Moombahton Edit", "Moombahton")]
    // Revisión 3: las formas adjetivas en español detrás de una palabra de versión, el trap latino y el
    // reggaeton «old school».
    [InlineData("Despacito (Versión Flamenca)", "Flamenco")]
    [InlineData("Despacito - Versión Rumbera", "Rumba")]
    [InlineData("Despacito (Version Salsera)", "Salsa")]
    [InlineData("Despacito (Versión Bachatera)", "Bachata")]
    [InlineData("Despacito (Mix Cumbiero)", "Cumbia")]
    [InlineData("Despacito (Versión Merenguera)", "Merengue")]
    [InlineData("Tema (Trap Latino Remix)", "Trap")]
    [InlineData("Tema (Reggaeton Old School Remix)", "Reggaeton")]
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
    // Revisión: delante de «DJ» solo va lo que acompaña a la versión (Extended, Clean, el BPM, Intro…),
    // o está escrito «D.J.» o «DJ-»: «DJ» sigue abriendo el nombre de quien hizo la versión.
    [InlineData("Gasolina (Extended DJ Mambo Remix)", "Daddy Yankee")]
    [InlineData("Gasolina (Clean DJ Mambo Remix)", "Daddy Yankee")]
    [InlineData("Gasolina (Dirty DJ Mambo Remix)", "Daddy Yankee")]
    [InlineData("Gasolina (95 BPM DJ Mambo Remix)", "Daddy Yankee")]
    [InlineData("Gasolina (Intro DJ Mambo Remix)", "Daddy Yankee")]
    [InlineData("Gasolina (Extended Intro DJ Mambo Remix 8A)", "Daddy Yankee")]
    [InlineData("Gasolina (D.J. Mambo Remix)", "Daddy Yankee")]
    [InlineData("Gasolina (DJ-Mambo Remix)", "Daddy Yankee")]
    [InlineData("Gasolina (Dj. Mambo Remix)", "Daddy Yankee")]
    // Detrás de un separador de créditos, «DJ» abre el nombre del segundo autor.
    [InlineData("Tema (DJ Mambo & DJ Trap Remix)", null)]
    [InlineData("Tema (DJ Pepe x DJ Salsa Remix)", null)]
    [InlineData("Tema (DJ Pepe, DJ Salsa Remix)", null)]
    [InlineData("Tema (DJ Pepe feat. MC Trap Remix)", null)]
    [InlineData("Tema (Prod. DJ Salsa Remix)", null)]
    [InlineData("Tema (DJ Pepe vs DJ Trap Remix)", null)]
    // Revisión 3: «b2b» no se reconocía (se comparaban solo las letras, «bb»), ni un separador sin espacio.
    [InlineData("Tema (DJ Pepe b2b DJ Trap Remix)", null)]
    [InlineData("Tema (DJ Pepe B2B MC Salsa Edit)", null)]
    [InlineData("Tema (DJ Pepe&DJ Mambo Remix)", null)]
    [InlineData("Tema (DJ Pepe,DJ Mambo Remix)", null)]
    [InlineData("Tema (DJ Pepe/DJ Mambo Remix)", null)]
    // Sin paréntesis y antes del primer guion, lo de delante de «DJ» es el nombre del tema.
    [InlineData("Gasolina DJ Mambo Remix", "Daddy Yankee")]
    [InlineData("Tema MC Trap Edit", null)]
    // Revisión 3: con «Artista - Título» en el título, como en muchos archivos descargados, lo de detrás
    // del primer guion también es el nombre del tema.
    [InlineData("Daddy Yankee - Gasolina DJ Mambo Remix", "Daddy Yankee")]
    [InlineData("Daddy Yankee - Gasolina MC Trap Edit", "Daddy Yankee")]
    [InlineData("Daddy Yankee - Gasolina Extended DJ Mambo Remix", "Daddy Yankee")]
    [InlineData("Daddy Yankee - Gasolina D.J. Mambo Remix", "Daddy Yankee")]
    [InlineData("Daddy Yankee - Gasolina 95 BPM DJ Mambo Remix", "Daddy Yankee x Don Omar")]
    // Revisión 3: la forma adjetiva solo cuenta detrás de una palabra de versión.
    [InlineData("La Flamenca Remix", null)]
    [InlineData("Tema (Flamenca Remix)", null)]
    [InlineData("Rumbera (Extended Mix)", null)]
    public void Un_estilo_fuera_del_contexto_de_version_no_cuenta(string titulo, string? artista)
        => Assert.Null(EstiloDeVersion.De(titulo, titulo, artista, null));

    // Revisión: en el nombre del archivo, lo de delante del segundo guion es «Artista - Título» (con
    // un número de pista o la clave delante, si los lleva), así que «DJ» abre el nombre del remixer.
    [Theory]
    [InlineData("Daddy Yankee - Gasolina DJ Mambo Remix")]
    [InlineData("01 - Daddy Yankee - Gasolina DJ Mambo Remix")]
    [InlineData("8A - 95 - Daddy Yankee - Gasolina DJ Mambo Remix")]
    [InlineData("Daddy Yankee - Gasolina - DJ Mambo Remix")]
    // Revisión 3: con el número de pista o el álbum entre el artista y el título.
    [InlineData("Daddy Yankee - 01 - Gasolina DJ Mambo Remix")]
    [InlineData("Daddy Yankee - Barrio Fino - 01 - Gasolina DJ Mambo Remix")]
    [InlineData("Daddy Yankee - Barrio Fino - Gasolina DJ Mambo Remix")]
    public void En_el_nombre_del_archivo_un_remixer_que_se_llama_como_un_estilo_no_cuenta(string archivo)
    {
        Assert.Null(EstiloDeVersion.De("Gasolina (DJ Mambo Remix)", archivo, "Daddy Yankee", null));
        Assert.Null(EstiloDeVersion.De("Gasolina", archivo, "Daddy Yankee", null));
        Assert.Null(EstiloDeVersion.De("Daddy Yankee - Gasolina DJ Mambo Remix", archivo, "Daddy Yankee", null));
    }

    // Revisión 3: si el título ya ha dicho que el estilo es el nombre de quien hizo la versión (va tras
    // «DJ» o «MC»), el nombre del archivo, el artista o el álbum no lo pueden volver a dar por estilo.
    [Fact]
    public void Lo_que_el_titulo_da_por_nombre_de_remixer_no_vuelve_como_estilo_desde_otro_campo()
    {
        Assert.Null(EstiloDeVersion.De("Gasolina (DJ Mambo Remix)", "Gasolina - Pack - Mambo Edit", null, null));
        Assert.Null(EstiloDeVersion.De("Gasolina (DJ Mambo Remix)", "Gasolina", null, "Pack (Mambo Version)"));
        // Otro estilo sí.
        Assert.Equal("Dembow", EstiloDeVersion.De("Gasolina (DJ Mambo Remix)", "Gasolina - Dembow Edit", null, null)?.Estilo);
    }

    // Si el título no dice el estilo, el nombre del archivo sí cuenta, también detrás del número de pista o
    // de una clave Camelot acabada en A.
    [Fact]
    public void En_el_nombre_del_archivo_el_estilo_tras_la_pista_o_la_clave_cuenta()
    {
        var v = EstiloDeVersion.De("Gasolina", "Daddy Yankee - Gasolina - 8A Moombahton Edit", "Daddy Yankee", null);
        Assert.Equal(("Moombahton", FuenteEstilo.Archivo), (v?.Estilo, v?.Fuente));
        Assert.Equal("Moombahton", EstiloDeVersion.De("Gasolina", "Daddy Yankee - Barrio Fino - 01 - Gasolina (Moombahton Edit)", "Daddy Yankee", null)?.Estilo);
        // Las dos primeras partes del nombre del archivo se toman siempre por «Artista - Título»: un «DJ» en
        // la segunda abre el nombre de quien hizo la versión, que es lo seguro.
        Assert.Null(EstiloDeVersion.De("Gasolina", "Gasolina - Alex Gramage DJ Mambo Remix", null, null));
    }

    // Y detrás del título, tras otro guion, el estilo que cierra el nombre del remixer sí cuenta.
    [Fact]
    public void En_el_nombre_del_archivo_el_estilo_detras_del_remixer_cuenta()
    {
        var v = EstiloDeVersion.De("Bad Boy", "Daddy Yankee - Bad Boy - Alex Gramage DJ Mambo Remix", "Daddy Yankee", null);
        Assert.Equal(("Mambo", FuenteEstilo.Archivo), (v?.Estilo, v?.Fuente));
        Assert.Equal("Mambo", EstiloDeVersion.De("Bad Boy", "Daddy Yankee - Bad Boy (Alex Gramage DJ Mambo Remix)", null, null)?.Estilo);
    }

    // El estilo disco sí cuenta cuando el paréntesis no dice otra cosa, o como Nu Disco; y el estilo que
    // va detrás del nombre de quien hizo la versión, también.
    [Theory]
    [InlineData("Got To Be Real (Disco)", "Disco")]
    [InlineData("Midnight City (Nu Disco Remix)", "Nu Disco")]
    [InlineData("Tema (DJ Pepe Techno Remix)", "Techno")]
    // Visto al validar en una biblioteca real: detrás de otro nombre, «DJ» lo cierra («Alex Gramage DJ»).
    [InlineData("Bad Boy (Alex Gramage DJ Mambo Remix)", "Mambo")]
    [InlineData("Bad Boy - Alex Gramage DJ Mambo Remix", "Mambo")]
    [InlineData("Bad Boy (DJ Pepe & Alex Gramage DJ Mambo Remix)", "Mambo")]
    [InlineData("Bad Boy (Extended Alex Gramage DJ Mambo Remix)", "Mambo")]
    public void Disco_suelto_Nu_Disco_y_el_estilo_detras_del_remixer_si_cuentan(string titulo, string estilo)
        => Assert.Equal(estilo, EstiloDeVersion.De(titulo, null, null, null)?.Estilo);

    [Fact]
    public void Afro_Electro_y_Tribal_son_ambiguos()
    {
        var afro = EstiloDeVersion.De("Afro Edit", null, null, null);
        Assert.Equal("Afro", afro?.Estilo);
        Assert.True(afro!.Ambiguo);
        Assert.True(EstiloDeVersion.De("Tema (Electro Mix)", null, null, null)!.Ambiguo);
        // Revisión 3: «Tribal Mix» es el nombre clásico de una mezcla de tribal house, y «tribal» es también
        // el tribal guarachero mexicano.
        var tribal = EstiloDeVersion.De("Saturday Night (Tribal Mix)", null, null, null);
        Assert.Equal(("Tribal", true), (tribal?.Estilo, tribal?.Ambiguo));
        // Con su segunda palabra ya no lo son.
        Assert.False(EstiloDeVersion.De("Tema (Electro House Remix)", null, null, null)!.Ambiguo);
        Assert.False(EstiloDeVersion.De("Tema (Tribal House Mix)", null, null, null)!.Ambiguo);
    }

    [Fact]
    public void Se_busca_en_el_titulo_el_archivo_el_artista_y_el_album_por_ese_orden()
    {
        var album = EstiloDeVersion.De("Dividimos", "Dividimos", "Romeo Santos", "Bachata Hits (Bachata Version)");
        Assert.Equal(FuenteEstilo.Album, album!.Fuente);
        Assert.Equal("Bachata", album.Estilo);

        var archivo = EstiloDeVersion.De("Dividimos", "Romeo Santos - Dividimos (Bachata Version)", null, null);
        Assert.Equal(FuenteEstilo.Archivo, archivo!.Fuente);

        // Revisión 3: lo que solo dice el artista (también la parte del nombre del archivo que es el
        // artista) sale con su fuente, el artista, y no como si fuera del título o del archivo.
        var artista = EstiloDeVersion.De("Tema", "Bachata Mix 2023 - Tema", "Bachata Mix 2023", null);
        Assert.Equal(("Bachata", FuenteEstilo.Artista), (artista?.Estilo, artista?.Fuente));
        Assert.Equal(FuenteEstilo.Artista, EstiloDeVersion.De("Bachata Mix 2023 - Tema", null, "Bachata Mix 2023", null)?.Fuente);

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
