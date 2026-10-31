using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// N13 · El nombre corregía la grafía y los alias del artista, pero el tag Artista se escribía tal
/// cual llegaba del catálogo: 8 temas de Cruz Cafuné pasaban a llamarse «Cruz Cafune - …» y su tag
/// seguía en «Cruzzi», así que rekordbox, que ordena por el tag, partía al artista en dos.
///
/// C4 (parte de FileProcessor) · El género de Discogs mandaba sobre el de Deezer, Spotify e iTunes,
/// aunque viniera de un disco de otro artista: «Arcangel - Bali» se aplicaba como «Gamelan», «Mad
/// Mind - Bien Duro» como «Medieval», con la canción bien identificada por Deezer.
/// </summary>
public class ArtistaTagYGeneroTests
{
    [Fact]
    public async Task El_tag_artista_lleva_la_misma_grafia_que_el_nombre()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Cruzzi - 4 Prez (Sote Hype Intro).mp3");
            Mp3Fixture.WriteMinMp3(p);
            var cat = new CatalogoFalso()
                .Responde(CatalogoFalso.BusquedaDeezer("Cruzzi", "Prez"),
                          CatalogoFalso.DeezerBusqueda(80, "Cruzzi", "4 Prez", 1))
                .Responde(CatalogoFalso.PistaDeezer(80), CatalogoFalso.DeezerPista(0, "2018-01-01", "Cruzzi"))
                .Responde(CatalogoFalso.AlbumDeezer(77), CatalogoFalso.DeezerAlbum("Rap/Hip Hop"));

            var r = await cat.Procesador(dir).ProcessAsync(p, false, new ProcessOptions { Deezer = true });

            Assert.Equal("Cruz Cafune - 4 Prez (Sote Hype Intro).mp3", r.New);
            Assert.Equal("Cruz Cafuné", r.Artist);   // el tag conserva los acentos
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static CatalogoFalso ArcangelBali(string discogsTitulo, string discogsAnio, string discogsEstilo, params string[] generosDeezer)
        => new CatalogoFalso()
            .Responde(CatalogoFalso.BusquedaDeezer("Arcangel", "Bali"),
                      CatalogoFalso.DeezerBusqueda(90, "Arcángel", "Bali", 1, albumId: 91, album: "Bali"))
            .Responde(CatalogoFalso.PistaDeezer(90), CatalogoFalso.DeezerPista(0, "2019-05-10", "Arcángel"))
            .Responde(CatalogoFalso.AlbumDeezer(91), CatalogoFalso.DeezerAlbum(generosDeezer))
            .Responde(CatalogoFalso.BusquedaDiscogs(), CatalogoFalso.Discogs(discogsTitulo, discogsAnio, discogsEstilo));

    [Fact]
    public async Task El_genero_de_Deezer_manda_sobre_el_de_Discogs()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Arcangel - Bali.mp3");
            Mp3Fixture.WriteMinMp3(p);
            // Lo que devolvía Discogs de verdad para «Bali»: un disco de Eberhard Schoener.
            var cat = ArcangelBali("Eberhard Schoener - Bali-Agúng", "1976", "Gamelan", "Latino");

            var r = await cat.Procesador(dir).ProcessAsync(p, false,
                new ProcessOptions { Deezer = true, Discogs = true, DiscogsToken = "prueba" });

            Assert.Equal("Latin", r.Genre);   // en inglés, como el resto de géneros de Deezer
            Assert.Equal("2019", r.Year);
            // Con género, álbum y año ya resueltos, Discogs ni se consulta.
            Assert.DoesNotContain(cat.Pedidas, u => u.Contains("discogs", StringComparison.Ordinal));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Discogs_solo_rellena_el_genero_que_falta()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Arcangel - Bali.mp3");
            Mp3Fixture.WriteMinMp3(p);
            // El álbum de Deezer no trae género: ahí sí se pregunta a Discogs, con un disco del artista.
            var cat = ArcangelBali("Arcángel - Sentimiento, Elegancia Y Maldad", "2013", "Reggaeton");

            var r = await cat.Procesador(dir).ProcessAsync(p, false,
                new ProcessOptions { Deezer = true, Discogs = true, DiscogsToken = "prueba" });

            Assert.Contains(cat.Pedidas, u => u.Contains("discogs", StringComparison.Ordinal));
            Assert.Equal("Reggaeton", r.Genre);
            Assert.Equal("Bali", r.Album);      // lo que ya había no se toca
            Assert.Equal("2019", r.Year);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
