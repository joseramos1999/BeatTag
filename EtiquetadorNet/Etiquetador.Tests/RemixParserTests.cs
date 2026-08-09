using Etiquetador.Core;
using Xunit;

namespace Etiquetador.Tests;

// Detección de la versión y de QUIÉN la firma (el remixer no debe perderse al limpiar el nombre).
public class RemixParserTests
{
    [Theory]
    [InlineData("Adele - Hello (Tiesto Remix)", "Tiesto", "Remix")]
    [InlineData("Bad Bunny - Titi Me Pregunto (Pedro Cabrera Remix)", "Pedro Cabrera", "Remix")]
    [InlineData("Rosalia - Despecha (DJ Nano Bootleg)", "DJ Nano", "Bootleg")]
    [InlineData("Karol G - Provenza (Sammy Deejay Flip)", "Sammy Deejay", "Flip")]
    [InlineData("Tema (David Guetta & Morten Remix)", "David Guetta & Morten", "Remix")]
    [InlineData("Tema (Joe Berte Rmx)", "Joe Berte", "Remix")]          // rmx -> Remix
    [InlineData("Track (Dimitri Vegas VIP Mix)", "Dimitri Vegas", "VIP")]
    [InlineData("Cancion - Pedro Cabrera Remix", "Pedro Cabrera", "Remix")]   // sin paréntesis
    [InlineData("Song (Remixed by Calvin Harris)", "Calvin Harris", "Remix")] // "remixed by"
    public void Detecta_al_remixer(string input, string remixer, string kind)
    {
        var r = RemixParser.Parse(input);
        Assert.True(r.IsRemix);
        Assert.Equal(remixer, r.Remixer);
        Assert.Equal(kind, r.Kind);
    }

    [Theory]
    [InlineData("Shakira - Monotonia (Extended Mix)")]
    [InlineData("Song (Original Mix)")]
    [InlineData("Song (Club Mix)")]
    [InlineData("Artist - Cancion normal")]
    [InlineData("Song (Acapella)")]
    public void No_inventa_remixer_en_versiones_genericas(string input)
        => Assert.False(RemixParser.Parse(input).HasRemixer);

    [Fact]
    public void Radio_edit_es_edit_pero_sin_autor()
    {
        var r = RemixParser.Parse("Daft Punk - One More Time (Radio Edit)");
        Assert.False(r.HasRemixer);   // no lo firma nadie
        Assert.Equal("Edit", r.Kind);
    }

    [Fact]
    public void La_etiqueta_junta_autor_y_tipo()
        => Assert.Equal("Tiesto Remix", RemixParser.Parse("Hello (Tiesto Remix)").Label);

    [Fact]
    public void Ignora_el_bpm_pegado_al_autor()
    {
        var r = RemixParser.Parse("Quevedo - Columbia (Alex Ferrer Edit) 128 BPM");
        Assert.Equal("Alex Ferrer", r.Remixer);
    }

    [Fact]
    public void Vacio_no_es_remix()
    {
        Assert.False(RemixParser.Parse("").IsRemix);
        Assert.False(RemixParser.Parse(null).IsRemix);
        Assert.Equal("", RemixParser.Parse(null).Label);   // no debe reventar con nulos
    }
}
