using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// N9 · Los títulos numéricos («44», «47», «22», «3») y la «X» de Nicky Jam se borraban al leer el
/// nombre: las reglas que quitan la copia numerada («… - 2») y el conector colgando («Artista x») no
/// miraban si lo que quitaban era el título entero. Visto en una biblioteca real: «Bad Gyal - 44» se
/// buscaba como título «Bad Gyal» sin artista y Deezer proponía «SAIKO, JC Reyes, Dei V - BADGYAL».
/// </summary>
public class TitulosNumericosTests
{
    [Theory]
    [InlineData("Bad Gyal - 44.mp3", "Bad Gyal", "44")]
    [InlineData("Anuel AA, Ñengo Flow, Sinfonico - 47.mp3", "Anuel AA, Ñengo Flow, Sinfonico", "47")]
    [InlineData("Taylor Swift - 22.mp3", "Taylor Swift", "22")]
    [InlineData("Britney Spears - 3.mp3", "Britney Spears", "3")]
    [InlineData("Nicky Jam - X.mp3", "Nicky Jam", "X")]
    [InlineData("Nicky Jam, J Balvin - X.mp3", "Nicky Jam, J Balvin", "X")]
    public void El_numero_o_la_x_tras_el_separador_es_el_titulo(string archivo, string artista, string titulo)
    {
        var r = FileNameParser.Parse(archivo);
        Assert.Equal(artista, r.FnArtist);
        Assert.Equal(titulo, r.FnTitle);
        Assert.Equal(titulo, r.QTitle);
    }

    // La copia numerada se sigue quitando: pegada al número es siempre copia, y con espacios solo
    // cuando delante queda un «Artista - Título» entero.
    [Theory]
    [InlineData("Dime Si Te Vas Con El (Edit)-1.mp3", "", "Dime Si Te Vas Con El (Edit)")]
    [InlineData("Rauw Alejandro x J Balvin x Zion Y Lennox -1.mp3", "", "Rauw Alejandro x J Balvin x Zion Y Lennox")]
    [InlineData("Axel Caram, Peipper - Sesion - 6.mp3", "Axel Caram, Peipper", "Sesion")]
    [InlineData("Bad Gyal - 44 - 2.mp3", "Bad Gyal", "44")]
    public void La_copia_numerada_se_sigue_quitando(string archivo, string artista, string titulo)
    {
        var r = FileNameParser.Parse(archivo);
        Assert.Equal(artista, r.FnArtist);
        Assert.Equal(titulo, r.FnTitle);
    }

    [Fact]
    public void El_conector_colgando_tras_el_titulo_se_sigue_quitando()
    {
        var r = FileNameParser.Parse("Bad Bunny - Monaco x.mp3");
        Assert.Equal("Bad Bunny", r.FnArtist);
        Assert.Equal("Monaco", r.FnTitle);
    }

    // La consulta al catálogo lleva el número: sin él se buscaba solo el nombre del artista.
    [Fact]
    public void La_consulta_conserva_el_titulo_numerico()
    {
        var r = FileNameParser.Parse("Bad Gyal - 44.mp3");
        Assert.Equal("Bad Gyal 44", Etiquetador.Core.Descriptors.BuildKw(r.FnArtist, r.QTitle));
    }
}
