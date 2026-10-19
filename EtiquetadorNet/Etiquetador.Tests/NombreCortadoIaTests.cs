using Etiquetador.Core.Ai;

namespace Etiquetador.Tests;

/// <summary>
/// Completar con la IA un nombre cortado tiene que CONSERVAR el nombre tal cual y añadirle solo la
/// cola que le falta.
///
/// Visto en una biblioteca real (beattag_20260920_235554.log): la IA devolvía la versión con sus
/// propios paréntesis, AiSuggestion.Compose la envolvía en otros, y como el empalme empezaba en la
/// primera palabra, el nombre entero se sustituía por el texto del modelo. Salieron paréntesis
/// anidados, un «Artista - Título» sin su guion y la web del pool dentro del nombre. Las respuestas
/// de la IA de estas pruebas son las que producen exactamente lo que se vio en el log.
/// </summary>
public class NombreCortadoIaTests
{
    private static string R(string archivo) => Path.Combine(Path.GetTempPath(), "beattag-pruebas", archivo);

    // Log, 00:01:24: «Bzrp 48 x Aprovecha ((97-90 Bpm) (Try It Mashup))».
    [Fact]
    public void No_anida_la_version_que_trae_sus_propios_parentesis()
    {
        var p = NombreCortado.DesdeIa(R("Bzrp 48 x Aprovecha (97-90 Bpm) (Try It Mash.mp3"),
                                      "Bizarrap", "Bzrp 48 x Aprovecha", "(97-90 Bpm) (Try It Mashup)");

        Assert.Equal("Bzrp 48 x Aprovecha (97-90 Bpm) (Try It Mashup)", p);
    }

    // Log: «Todo De Ti X Se Vuelve Loca (v2 (TRY IT MASHUP))». La palabra cortada estaba en
    // mayúsculas y se completa en mayúsculas.
    [Fact]
    public void Solo_se_anade_lo_que_faltaba_con_las_mayusculas_del_nombre()
    {
        var p = NombreCortado.DesdeIa(R("Todo De Ti X Se Vuelve Loca v2 (TRY IT MASHU.mp3"),
                                      "Rauw Alejandro", "Todo De Ti X Se Vuelve Loca", "v2 (TRY IT MASHUP)");

        Assert.Equal("Todo De Ti X Se Vuelve Loca v2 (TRY IT MASHUP)", p);
    }

    // Log: «La Prendo (Bad Gyal (AgA Tech Remix) Extended)». Perdía el « - » y reescribía el nombre
    // entero. El corchete que el nombre dejó abierto se cierra con un corchete, no con el «)» del modelo.
    [Fact]
    public void Conserva_el_separador_y_cierra_el_corchete_que_abrio_el_nombre()
    {
        var p = NombreCortado.DesdeIa(R("LA PRENDO - BAD GYAL (AGA TECH REMIX) [EXTEN.mp3"),
                                      "Bad Gyal", "La Prendo", "Bad Gyal (AgA Tech Remix) Extended");

        Assert.Equal("LA PRENDO - BAD GYAL (AGA TECH REMIX) [EXTENDED]", p);
    }

    // Log, tanda de las 23:59: «Doja Cat - Woman ([The Shooters Bootleg] [EdmPacks.com])». Lo que
    // faltaba era la web del pool: completar eso no es completar nada, así que no hay propuesta.
    [Fact]
    public void La_web_del_pool_que_propone_la_ia_no_entra_en_el_nombre()
    {
        var p = NombreCortado.DesdeIa(R("Doja Cat - Woman [The Shooters Bootleg] [Edm.mp3"),
                                      "Doja Cat", "Woman", "[The Shooters Bootleg] [EdmPacks.com]");

        Assert.Null(p);
    }

    // Log, línea 478: «Quiero Repetir (x Luces De Neon (Try It Mashup))».
    [Fact]
    public void Una_segunda_cancion_no_se_mete_entre_parentesis()
    {
        var p = NombreCortado.DesdeIa(R("Quiero Repetir x Luces De Neon (Try It Mashu.mp3"),
                                      "Ozuna", "Quiero Repetir", "x Luces De Neon (Try It Mashup)");

        Assert.Equal("Quiero Repetir x Luces De Neon (Try It Mashup)", p);
    }

    // Si la cola que propone el modelo trae paréntesis dentro de un paréntesis que el nombre ya
    // tenía abierto, el resultado quedaría anidado: mejor no proponer nada.
    [Fact]
    public void Una_cola_que_anidaria_parentesis_no_se_acepta()
    {
        Assert.Null(NombreCortado.DesdeIa(R("Rauw Alejandro - Todo De Ti (Intr.mp3"),
                                          "Rauw Alejandro", "Todo De Ti", "Intro (Clean)"));
    }

    // Lo mismo con las etiquetas: completar no puede quitarle al nombre su « - » ni sus paréntesis.
    // Con la etiqueta de título sola, el empalme empezaba en la primera palabra y dejaba «La Prendo
    // (Bad Gyal AgA Tech Remix) [Extended]»; empezando en «BAD», «LA PRENDO - Bad Gyal AgA Tech Remix
    // [Extended]». Con las etiquetas, como siempre, las palabras empalmadas se escriben como en la
    // etiqueta: lo que no se puede perder es la estructura del nombre.
    [Fact]
    public void Las_etiquetas_tampoco_pueden_quitar_el_separador_ni_los_parentesis()
    {
        var p = NombreCortado.DesdeEtiquetas(R("LA PRENDO - BAD GYAL (AGA TECH REMIX) [EXTEN.mp3"),
                                             "Bad Gyal", "La Prendo (Bad Gyal AgA Tech Remix) [Extended]");

        Assert.Equal("LA PRENDO - BAD GYAL (AgA Tech Remix) [Extended]", p);
    }

    // Si ningún empalme con la etiqueta conserva el nombre, se completa en modo literal: el nombre
    // tal cual y la cola que faltaba, con el paréntesis que el nombre dejó abierto ya cerrado.
    [Fact]
    public void Si_la_etiqueta_no_trae_parentesis_se_cierra_el_que_abrio_el_nombre()
    {
        var p = NombreCortado.DesdeEtiquetas(R("Tego Calderon - Pa Que Retocen (Intro Privad.mp3"),
                                             "Tego Calderon", "Pa Que Retocen Intro Privado");

        Assert.Equal("Tego Calderon - Pa Que Retocen (Intro Privado)", p);
    }
}
