using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// N7 · La red de mashups solo miraba la segunda mitad del nombre, y en el formato invertido de los
/// pools («Título - Artista») el título va delante; además no conocía «Mashboot». Casos reales de los
/// registros, los tres renombrados como si fueran el original:
///   · «Cuando La Noche Arriba x Apex - Ana Mena, Um» → «Ana Mena - Cuando la noche arriba»
///   · «Starships X Kernkraft 400 - Nicky Minaj, Zombie Nation, James Hype (DOUBLE &amp; MATTIA
///     CIPRIANI MASHBOOT)» → «Nicki Minaj - Starships (Hype)»
///   · «FIEL (Ayuxx Mashboot) - Wisin, Jhay Cortez» → «Wisin Jhay Cortez - Fiel»
/// </summary>
public class MashupInvertidoTests
{
    [Theory]
    [InlineData("FIEL (Ayuxx Mashboot) - Wisin, Jhay Cortez")]
    [InlineData("Starships X Kernkraft 400 - Nicky Minaj, Zombie Nation, James Hype (DOUBLE & MATTIA CIPRIANI MASHBOOT)")]
    [InlineData("Tema (DJ X Mash-Boot)")]
    public void Mashboot_se_salta_como_mezcla(string nombre)
    {
        var p = FileNameParser.Parse(nombre + ".mp3");
        Assert.True(Matching.IsSkipMix(p.Base, p.FnTitle, p.FnArtist));
        Assert.True(Matching.IsMezclaDeVariosTemas(nombre));
    }

    [Fact]
    public void La_mitad_del_titulo_es_la_que_comparte_palabras_con_el_catalogo()
    {
        Assert.Equal("Cuando La Noche Arriba x Apex",
            Matching.MitadConElTitulo("Cuando La Noche Arriba x Apex", "Ana Mena, Um", "Cuando la noche arriba"));
        // Orden normal: la segunda.
        Assert.Equal("One More Time",
            Matching.MitadConElTitulo("DJ Allan x Daft Punk", "One More Time", "One More Time"));
        // Sin pistas, la segunda, como siempre.
        Assert.Equal("Ana Mena, Um", Matching.MitadConElTitulo("Cuando La Noche Arriba x Apex", "Ana Mena, Um", ""));
    }

    // La « x » de la mitad del artista une colaboradores: que el catálogo no recoja a uno no
    // convierte el tema en un mashup.
    [Fact]
    public void Un_colaborador_que_falta_no_es_un_mashup()
    {
        var mitad = Matching.MitadConElTitulo("DJ Allan x Daft Punk", "One More Time", "One More Time");
        Assert.False(Matching.PierdeUnaCancion(mitad, "Daft Punk - One More Time"));
    }

    // De punta a punta: el nombre invertido con dos canciones ya no se renombra como el original.
    [Fact]
    public async Task El_mashup_invertido_se_salta()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Cuando La Noche Arriba x Apex - Ana Mena, Um.mp3");
            Mp3Fixture.WriteMinMp3(p);
            var cat = new CatalogoFalso()
                .Responde(CatalogoFalso.BusquedaDeezer("Ana Mena", "Cuando"),
                          CatalogoFalso.DeezerBusqueda(60, "Ana Mena", "Cuando la noche arriba", 1))
                .Responde(CatalogoFalso.PistaDeezer(60), CatalogoFalso.DeezerPista(0, "2023-06-01", "Ana Mena"))
                .Responde(CatalogoFalso.AlbumDeezer(77), CatalogoFalso.DeezerAlbum("Pop"));

            var r = await cat.Procesador(dir).ProcessAsync(p, false, new ProcessOptions { Deezer = true });

            Assert.True(r.Skip);
            Assert.Equal("Mezcla", r.Source);
            Assert.Equal(r.Old, r.New);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
