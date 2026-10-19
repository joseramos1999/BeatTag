using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// N2 · Los descriptores se sacaban también del nombre del artista: palabras como «Hype», «Dirty»,
/// «Clean» o «Live» están en la lista de descriptores. Casos reales de los registros: «Born Dirty -
/// Samba Sujo» → «… Samba Sujo (Dirty)», «James Hype - Ferrari (Remix)» → «… (Remix) (Hype)» (al
/// menos 8 propuestas así), «Behaviour (Extended Mix) - James Hype ft A.D.O.R.» → «… (Extended Mix,
/// Hype)».
/// </summary>
public class DescriptoresDelArtistaTests
{
    [Theory]
    [InlineData("Born Dirty - Samba Sujo", "Samba Sujo", "Born Dirty, Diplo", "")]
    [InlineData("James Hype - Ferrari (Remix)", "Ferrari", "James Hype, Miggy Dela Rosa", "Remix")]
    [InlineData("Behaviour (Extended Mix) - James Hype ft A.D.O.R. - 134bpm", "Behaviour", "James Hype, A.D.O.R.", "Extended Mix")]
    [InlineData("Dirty Minds - i'm for pleasure (claptone remix)", "I'm for Pleasure (Claptone Remix)", "Dirty Minds", "")]
    [InlineData("Clean Bandit - Rather Be", "Rather Be", "Clean Bandit, Jess Glynne", "")]
    [InlineData("Live - Lightning Crashes", "Lightning Crashes", "Live", "")]
    public void Las_palabras_del_artista_no_son_descriptores(string nombre, string titulo, string artista, string esperado)
    {
        var fnArtist = FileNameParser.Parse(nombre + ".mp3").FnArtist;
        var otros = Descriptors.ExtractOtros(nombre, titulo, new[] { artista }, fnArtist);
        Assert.Equal(esperado, string.Join(",", otros));
    }

    // Sin catálogo (solo limpieza), el artista del nombre de archivo solo descarta las palabras que
    // suelen ser parte de un nombre: en los nombres invertidos del pool esa mitad es el título, y su
    // versión no puede perderse.
    [Theory]
    [InlineData("Born Dirty - Samba Sujo", "Samba Sujo", "")]
    [InlineData("La Inocente x Si Tu Te Vas Remix - Mora, Fei", "Mora, Fei", "Remix")]
    [InlineData("Una Noche en Medellin x Tattoo Intro - Cris", "Cris", "Intro")]
    [InlineData("Karixon, richie loop, Tribal Kush, Shalanda - Big Batty", "Big Batty", "")]
    public void Sin_catalogo_el_artista_del_nombre_solo_quita_lo_que_suele_ser_un_nombre(string nombre, string titulo, string esperado)
    {
        var fnArtist = FileNameParser.Parse(nombre + ".mp3").FnArtist;
        Assert.Equal(esperado, string.Join(",", Descriptors.ExtractOtros(nombre, titulo, null, fnArtist)));
    }

    // Dentro de paréntesis sí es la versión, aunque la palabra coincida con el artista.
    [Fact]
    public void Entre_parentesis_sigue_siendo_la_version()
    {
        var otros = Descriptors.ExtractOtros("Clean Bandit - Rather Be (Clean)", "Rather Be", new[] { "Clean Bandit" });
        Assert.Equal(new[] { "Clean" }, otros);
    }

    // Sin artistas, el comportamiento de siempre.
    [Fact]
    public void Sin_artistas_no_cambia_nada()
        => Assert.Equal(new[] { "Hype", "Remix" }, Descriptors.ExtractOtros("James Hype - Ferrari (Remix)", "Ferrari"));

    // De punta a punta: «Born Dirty - Samba Sujo» ya no lleva «(Dirty)».
    [Fact]
    public async Task Born_Dirty_no_acaba_con_Dirty_en_el_nombre()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Born Dirty - Samba Sujo.mp3");
            Mp3Fixture.WriteMinMp3(p);
            var cat = new CatalogoFalso()
                .Responde(CatalogoFalso.BusquedaDeezer("Born Dirty", "Samba Sujo"),
                          CatalogoFalso.DeezerBusqueda(50, "Born Dirty", "Samba Sujo", 1))
                .Responde(CatalogoFalso.PistaDeezer(50), CatalogoFalso.DeezerPista(0, "2019-01-01", "Born Dirty", "Diplo"))
                .Responde(CatalogoFalso.AlbumDeezer(77), CatalogoFalso.DeezerAlbum("Dance"));

            var r = await cat.Procesador(dir).ProcessAsync(p, false, new ProcessOptions { Deezer = true });

            Assert.Equal("Born Dirty, Diplo - Samba Sujo.mp3", r.New);
            Assert.Equal("Samba Sujo", r.Title);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
