using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// N6 · En el formato de pool con doble espacio («Título  Editor Open Show») el editor forma parte
/// del título leído del nombre, y la regla que descarta al «autor» cuando es el propio título lo
/// tiraba siempre. Trazas reales: «remix -> 'Alex Cortes' descartado (es el propio título)» y la
/// propuesta «Duki - GIVENCHY (Open Show).mp3»; igual con Alex Torres, Alberto Rodrigo, Nico Guerra…
/// </summary>
public class EditorDobleEspacioTests
{
    [Theory]
    [InlineData("Alex Cortes", "GIVENCHY", "Givenchy Alex Cortes")]
    [InlineData("Alex Torres", "El Baifo", "EL BAIFO Alex Torres")]
    [InlineData("Alberto Rodrigo", "I Know You Want Me (Calle Ocho)", "I Know You Want Me Alberto Rodrigo")]
    [InlineData("Nico Guerra", "Bellaqueo", "Bellaqueo Nico Guerra")]
    public void El_editor_detras_del_titulo_no_es_el_titulo(string editor, string catalogo, string tituloNombre)
        => Assert.False(FileProcessor.EsElPropioTitulo(editor, catalogo, tituloNombre));

    // Para lo que se hizo la regla, sigue valiendo: el título repetido va delante.
    [Theory]
    [InlineData("Stereo Love", "Stereo Love", "Stereo Love")]
    [InlineData("Look At Me Now", "Look At Me Now", "Look At Me Now")]
    [InlineData("Esclava", "Esclava (Remix)", "Esclava")]
    [InlineData("Givenchy Alex Cortes", "GIVENCHY", "Givenchy Alex Cortes")]
    public void El_titulo_repetido_sigue_sin_ser_un_autor(string autor, string catalogo, string tituloNombre)
        => Assert.True(FileProcessor.EsElPropioTitulo(autor, catalogo, tituloNombre));

    // El autor que el catálogo pone entre paréntesis es legítimo, no «el propio título».
    [Fact]
    public void El_autor_dentro_del_parentesis_del_catalogo_no_es_el_titulo()
        => Assert.False(FileProcessor.EsElPropioTitulo("Rio & Le Jean", "Get Ready For This (Rio & Le Jean '92 Remix)", "Get Ready For This"));

    // Que el título del catálogo ya nombre la versión se mira por palabras enteras: «Animals» no
    // contiene a un remixer llamado «Ani».
    [Fact]
    public async Task Un_autor_que_solo_coincide_con_parte_de_una_palabra_se_conserva()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Martin Garrix - Animals (Ani Remix).mp3");
            Mp3Fixture.WriteMinMp3(p);
            var cat = new CatalogoFalso()
                .Responde(CatalogoFalso.BusquedaDeezer("Martin Garrix", "Animals"),
                          CatalogoFalso.DeezerBusqueda(41, "Martin Garrix", "Animals", 1))
                .Responde(CatalogoFalso.PistaDeezer(41), CatalogoFalso.DeezerPista(0, "2013-06-17", "Martin Garrix"))
                .Responde(CatalogoFalso.AlbumDeezer(77), CatalogoFalso.DeezerAlbum("Dance"));

            var r = await cat.Procesador(dir).ProcessAsync(p, false, new ProcessOptions { Deezer = true });

            Assert.Equal("Martin Garrix - Animals (Ani Remix).mp3", r.New);
            Assert.Equal("Ani", r.Remixer);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // De punta a punta con el caso real de los registros.
    [Fact]
    public async Task Givenchy_conserva_a_Alex_Cortes()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Duki x Pickle - Givenchy  Alex Cortes Open Show.mp3");
            Mp3Fixture.WriteMinMp3(p);
            var cat = new CatalogoFalso()
                .Responde(CatalogoFalso.BusquedaDeezer("Duki", "Givenchy"),
                          CatalogoFalso.DeezerBusqueda(40, "Duki", "GIVENCHY", 1))
                .Responde(CatalogoFalso.PistaDeezer(40), CatalogoFalso.DeezerPista(0, "2022-03-31", "Duki"))
                .Responde(CatalogoFalso.AlbumDeezer(77), CatalogoFalso.DeezerAlbum("Rap/Hip Hop"));

            var r = await cat.Procesador(dir).ProcessAsync(p, false, new ProcessOptions { Deezer = true });

            Assert.Equal("Duki - GIVENCHY (Alex Cortes Open Show).mp3", r.New);
            Assert.Equal("Alex Cortes", r.Remixer);
            Assert.Equal("Open Show", r.RemixKind);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
