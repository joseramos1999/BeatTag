using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// Nombres que PODRIAN ser una mezcla pero no esta claro. No deciden nada: solo marcan a quien
/// merece la pena preguntarle a la IA local, que si puede juzgar si son dos canciones o una con
/// varios artistas. Los casos salen de una tirada real de 12.428 canciones.
/// </summary>
public class AmbiguousMixTests
{
    // Varias "x" seguidas: puede ser una lista de colaboradores o dos temas unidos. Hay que
    // preguntar, porque el nombre solo no lo dice.
    [Theory]
    [InlineData("Daddy Yankee x SHAQI - Ella Me Levanto x Gulevando")]
    [InlineData("Tokischa x July Queen x Liss Doll RD - Bandidaje")]
    [InlineData("Delincuente - Tokischa x Anuel AA x Ñengo Flow")]
    public void Los_nombres_dudosos_se_marcan_para_preguntar(string nombre)
        => Assert.True(Matching.LooksAmbiguousMix(nombre));

    // Lo que ya resuelve IsSkipMix no es dudoso: se salta sin gastar una consulta.
    [Theory]
    [InlineData("THE-FINAL-COUNTDOWN-X-FEEL-GOOD MASHUP")]
    [InlineData("Algo - Otra cosa (Transition)")]
    [InlineData("Tema A - Tema B (Blend)")]
    public void Lo_que_ya_es_evidente_no_se_consulta(string nombre)
        => Assert.False(Matching.LooksAmbiguousMix(nombre));

    // Guarda: un nombre normal no puede acabar consultandose, o se gastaria una llamada por
    // cancion y el analisis se volveria lentisimo.
    [Theory]
    [InlineData("Bad Bunny - Tití Me Preguntó")]
    [InlineData("Quevedo, Bizarrap - Bzrp Music Sessions Vol. 52")]
    [InlineData("Karol G - Bichota (Intro Break)")]
    [InlineData("Anuel AA - Quiere Beber")]
    [InlineData("")]
    public void Un_nombre_normal_no_se_consulta(string nombre)
        => Assert.False(Matching.LooksAmbiguousMix(nombre));

    // Una sola "x" entre dos artistas es una colaboracion de toda la vida: no hay duda que
    // resolver, y preguntarlo seria gastar por gastar.
    [Fact]
    public void Una_sola_equis_entre_artistas_no_basta()
        => Assert.False(Matching.LooksAmbiguousMix("Nicky Jam x J. Balvin - X (EQUIS)"));

    // Pero si hay una "x" a cada lado del guion, sospechoso: puede unir dos canciones.
    [Fact]
    public void Una_equis_a_cada_lado_del_guion_si()
        => Assert.True(Matching.LooksAmbiguousMix("Artista A x Artista B - Tema Uno x Tema Dos"));
}
