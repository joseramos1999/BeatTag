using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// N3 · El «ft» sin límite de palabra inventaba invitados con el final de Drift, Swift o Daft, y de
/// paso borraba al remixer. Casos reales de los registros: «Teriyaki Boyz - Tokyo Drift (Sam Collins
/// Remix)» → «… feat. Sam Collins (Remix)», «ZAYN, Taylor Swift - I Dont Wanna Live Forever (Fifty
/// Shades Darker)» → «… feat. I Dont Wanna Forever», «DJ Allan x Daft Punk - One More Time» →
/// «… One More Time feat. Punk».
/// </summary>
public class FeatDelNombreTests
{
    [Theory]
    [InlineData("Teriyaki Boyz - Tokyo Drift (Sam Collins Remix)")]
    [InlineData("ZAYN, Taylor Swift - I Dont Wanna Live Forever (Fifty Shades Darker)")]
    [InlineData("DJ Allan x Daft Punk - One More Time")]
    [InlineData("Kraftwerk - The Model")]
    [InlineData("Faithless - Insomnia (Shift K3Y Remix)")]
    public void El_ft_dentro_de_una_palabra_no_es_un_invitado(string nombre)
        => Assert.Equal("", FileProcessor.FeatDelNombre(nombre));

    [Theory]
    [InlineData("Bad Bunny ft. Feid - Perro Negro", "Feid")]
    [InlineData("Aissa - Tuuuuuuuu Tu Tu Tu Ft Daddy Yankee (Antonio Guevara Hype Intro 125bpm)", "Daddy Yankee")]
    [InlineData("Song (feat. Natti Natasha)", "Natti Natasha")]
    [InlineData("Daddy Yankee featuring Snoop Dogg - Gangsta Zone", "Snoop Dogg")]
    public void El_feat_de_verdad_se_sigue_leyendo(string nombre, string invitado)
        => Assert.Equal(invitado, FileProcessor.FeatDelNombre(nombre));

    // Dentro de un paréntesis, si lo que sigue al «ft.» nombra una versión, es quién la firma: eso es
    // cosa de RemixParser.
    [Fact]
    public void No_se_toma_por_invitado_al_que_firma_la_version()
        => Assert.Equal("", FileProcessor.FeatDelNombre("Tema (ft. Sam Collins Remix)"));

    // Pero un invitado de verdad dentro del paréntesis se conserva.
    [Fact]
    public void El_invitado_dentro_de_un_parentesis_se_conserva()
        => Assert.Equal("Daddy Yankee", FileProcessor.FeatDelNombre("Omega - Estrellita De Madrugada (Original 2022 Remaster Ft. Daddy Yankee)"));

    // De punta a punta: el remixer de «Tokyo Drift» se conserva y no aparece ningún invitado.
    [Fact]
    public async Task Tokyo_Drift_conserva_al_remixer_y_no_inventa_invitado()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Teriyaki Boyz - Tokyo Drift (Sam Collins Remix).mp3");
            Mp3Fixture.WriteMinMp3(p);
            var cat = new CatalogoFalso()
                .Responde(CatalogoFalso.BusquedaDeezer("Teriyaki", "Tokyo Drift"),
                          CatalogoFalso.DeezerBusqueda(10, "Teriyaki Boyz", "Tokyo Drift (Fast & Furious)", 1))
                .Responde(CatalogoFalso.PistaDeezer(10), CatalogoFalso.DeezerPista(0, "2006-06-20", "Teriyaki Boyz"))
                .Responde(CatalogoFalso.AlbumDeezer(77), CatalogoFalso.DeezerAlbum("Rap/Hip Hop"));

            var r = await cat.Procesador(dir).ProcessAsync(p, false, new ProcessOptions { Deezer = true });

            Assert.Equal("Teriyaki Boyz - Tokyo Drift (Fast & Furious) (Sam Collins Remix).mp3", r.New);
            Assert.DoesNotContain("feat", r.Title);
            Assert.Equal("Sam Collins", r.Remixer);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
