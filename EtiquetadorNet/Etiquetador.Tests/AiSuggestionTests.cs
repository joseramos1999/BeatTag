using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Sugerencias de la IA local para las canciones que ningun catalogo confirma. La IA propone y el
/// usuario decide, asi que lo que se filtra aqui no es "lo que puede estar mal" -eso lo juzga el-,
/// sino lo que se ha INVENTADO, que es justo lo que una persona no puede cazar de un vistazo.
/// Los casos salen de una tirada real de 12.428 canciones.
/// </summary>
public class AiSuggestionTests
{
    // Reordenar y limpiar es exactamente para lo que sirve: todas las palabras ya estaban.
    [Theory]
    [InlineData("DJ_Irwan_Ghetto_Flow_Kalibwoy_ft_Kempi_FRNKI.mp3", "Kalibwoy ft. Kempi", "Ghetto Flow")]
    [InlineData("Miau, Jose Rodriguez - Causers Original Mix.mp3", "Miau, Jose Rodriguez", "Causers")]
    [InlineData("Lirico-En-La-Casa-Ft.-Atomic-Otro-Way.mp3", "Atomic Otro Way", "Lirico En La Casa")]
    public void Reordenar_lo_que_ya_estaba_pasa_la_guarda(string archivo, string artista, string titulo)
        => Assert.True(Matching.SoloReordena(archivo, artista, titulo));

    // Inventarse un artista que no aparece por ningun lado, no. Es el error peligroso: suena
    // verosimil y el usuario no tiene como saber que es falso.
    [Theory]
    [InlineData("pista_04_sin_nombre.mp3", "Bad Bunny", "Titi Me Pregunto")]
    [InlineData("Quevedo - Columbia.mp3", "Quevedo", "Vista Al Mar")]
    public void Inventarse_datos_no_pasa_la_guarda(string archivo, string artista, string titulo)
        => Assert.False(Matching.SoloReordena(archivo, artista, titulo));

    // Corregir una errata SI pasa: es de lo mas util que hace la IA, y no esta inventando nada.
    [Fact]
    public void Corregir_una_errata_no_cuenta_como_invencion()
        => Assert.True(Matching.SoloReordena("Bad Bunny - Resentia (Hype Intro).mp3", "Bad Bunny", "Resentía"));

    // Sin nombre de origen no hay con que comparar: no se sugiere nada.
    [Fact]
    public void Sin_original_no_hay_sugerencia()
        => Assert.False(Matching.SoloReordena("", "Alguien", "Algo"));

    [Fact]
    public void El_nombre_propuesto_se_compone_y_se_limpia()
        => Assert.Equal("Kalibwoy ft. Kempi - Ghetto Flow (Extended)",
                        AiSuggestion.Compose("  Kalibwoy ft. Kempi ", "\"Ghetto Flow\"", "Extended"));

    [Fact]
    public void Sin_artista_el_nombre_es_solo_el_titulo()
        => Assert.Equal("Ghetto Flow", AiSuggestion.Compose("", "Ghetto Flow", ""));

    [Fact]
    public void Sin_titulo_no_hay_nombre_que_proponer()
        => Assert.Equal("", AiSuggestion.Compose("Kalibwoy", "", ""));

    // Si la sugerencia coincide con lo que la limpieza normal ya iba a dejar, callarse: enseñarla
    // solo gastaria la atencion del usuario.
    [Fact]
    public void No_se_sugiere_lo_que_ya_iba_a_quedar_igual()
    {
        var r = new ProcessResult
        {
            New = "Kalibwoy feat. Kempi - Ghetto Flow.mp3",
            AiArtist = "Kalibwoy feat. Kempi",
            AiTitle = "Ghetto Flow",
        };
        Assert.Equal("", AiSuggestion.For(r));
    }

    // Muchos archivos llegan con el nombre cortado a 43 caracteres y el titulo entero dentro del
    // tag. Completarlo con lo que dice el tag no es inventar: es justo lo que hace falta.
    [Fact]
    public void Completar_desde_los_tags_no_es_inventar()
        => Assert.True(Matching.SoloReordena(
            "Secreto El Famoso Biberon, El Experimento Ma.mp3 Secreto El Famoso Biberon, El Experimento Macgyver BIM BIM",
            "Secreto El Famoso Biberon, El Experimento Macgyver", "BIM BIM"));

    // Si el modelo devuelve el nombre sucio casi tal cual, la limpieza normal ya lo hace mejor:
    // ofrecer eso seria proponerle al usuario un cambio a peor.
    [Fact]
    public void No_se_sugiere_algo_que_sigue_llevando_los_bpm()
    {
        var r = new ProcessResult { New = "Mahmood - Soldi (Extended, Redrum, Dirty).mp3", AiArtist = "Mahmood", AiTitle = "Soldi (Extended Redrum) 95 Bpm Dirty" };
        Assert.Equal("", AiSuggestion.For(r));
    }

    [Fact]
    public void Se_sugiere_cuando_cambia_algo()
    {
        var r = new ProcessResult
        {
            New = "DJ Irwan Ghetto Flow Kalibwoy ft Kempi FRNKI.mp3",
            AiArtist = "Kalibwoy ft. Kempi",
            AiTitle = "Ghetto Flow",
        };
        Assert.Equal("Kalibwoy ft. Kempi - Ghetto Flow", AiSuggestion.For(r));
    }
}
