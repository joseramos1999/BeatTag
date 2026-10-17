using Etiquetador.Core.Ai;

namespace Etiquetador.Tests;

/// <summary>
/// Devolver a la propuesta de la IA las tildes que el nombre ya tenía, sin inventar ni repartir
/// tildes de más.
///
/// En español hay palabras que solo se distinguen por la tilde (sé/se, tú/tu, él/el, sí/si, más/mas,
/// mí/mi). Antes bastaba con que el nombre trajera UNA de ellas con tilde para ponérsela a todas:
/// «Ya_Sé_Que_Se_Fue (Extended)» se renombraba como «Ya Sé Que Sé Fue (Extended)», con una falta de
/// ortografía que el nombre original no tenía.
/// </summary>
public class RestaurarAcentosTests
{
    [Fact]
    public void Una_palabra_que_aparece_con_y_sin_tilde_se_alinea_por_orden()
    {
        Assert.Equal("Ya Sé Que Se Fue (Extended)",
                     AiSuggestion.RestaurarAcentos("Ya Se Que Se Fue (Extended)", "Ya_Sé_Que_Se_Fue (Extended)"));
    }

    [Fact]
    public void Tu_y_tu_con_tilde_no_se_confunden()
    {
        Assert.Equal("Tú Me Dejaste Y Tu Amor (Intro)",
                     AiSuggestion.RestaurarAcentos("Tu Me Dejaste Y Tu Amor (Intro)", "Tú_Me_Dejaste_Y_Tu_Amor (Intro)"));
    }

    // Si la propuesta no trae la palabra las mismas veces, no hay forma segura de saber cuál es cuál:
    // se deja como la escribió la IA antes que poner una tilde donde no va.
    [Fact]
    public void Si_no_se_puede_alinear_se_deja_como_la_propuso_la_ia()
    {
        Assert.Equal("Se Fue (Extended)",
                     AiSuggestion.RestaurarAcentos("Se Fue (Extended)", "Ya_Sé_Que_Se_Fue (Extended)"));
    }

    // Lo de siempre sigue igual: una palabra que solo aparece con tilde la recupera en todas partes,
    // con las mayúsculas de la propuesta.
    [Fact]
    public void Una_palabra_sin_ambiguedad_recupera_su_tilde_como_siempre()
    {
        Assert.Equal("Rosalía - Despechá (DESPECHÁ Intro)",
                     AiSuggestion.RestaurarAcentos("Rosalia - Despecha (DESPECHA Intro)", "Rosalía - Despechá (Intr"));
    }

    // De punta a punta: el renombrado con IA ya no introduce la falta de ortografía.
    [Fact]
    public void El_renombrado_no_pone_una_tilde_que_el_nombre_no_tenia()
    {
        var (p, _) = RenombradoIa.Evaluar(Path.Combine(Path.GetTempPath(), "beattag-pruebas", "Ya_Sé_Que_Se_Fue (Extended).mp3"),
                                          "", "", new AiParse("", "Ya Se Que Se Fue", "Extended", false, 0.9));

        Assert.NotNull(p);
        Assert.Equal("Ya Sé Que Se Fue (Extended)", p!.Propuesto);
    }
}
