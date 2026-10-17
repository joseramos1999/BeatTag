using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// N10 · Deezer y Spotify escriben la versión tras un guion («El Motorcito - Remix»), y la limpieza
/// del título del catálogo solo miraba los paréntesis. Visto en una biblioteca real: «Gusty DJ, Lolo
/// OG, DobleP - El Motorcito - (Remix).mp3» y «J Balvin, KAROL G, … - Poblado - (Remix).mp3», con el
/// guion colgando también en el tag Título.
/// </summary>
public class ColetillaTrasGuionTests
{
    [Theory]
    [InlineData("El Motorcito - Remix", "El Motorcito (Remix)")]
    [InlineData("Poblado - Remix", "Poblado (Remix)")]
    [InlineData("Get Ready For This - Rio & Le Jean '92 Remix", "Get Ready For This (Rio & Le Jean '92 Remix)")]
    [InlineData("Levels - Extended Mix", "Levels (Extended Mix)")]
    [InlineData("Titi Me Pregunto - Re-Edit", "Titi Me Pregunto (Re-Edit)")]
    public void La_version_tras_guion_pasa_a_ir_entre_parentesis(string catalogo, string esperado)
        => Assert.Equal(esperado, Descriptors.CleanDbTitle(catalogo, ""));

    // Lo mismo que entre paréntesis se quita: remaster, «radio edit», «single version»…
    [Theory]
    [InlineData("Sweet Child O' Mine - Remastered 1987", "Sweet Child O' Mine")]
    [InlineData("Billie Jean - 2008 Remaster", "Billie Jean")]
    [InlineData("Diamonds - Radio Edit", "Diamonds")]
    [InlineData("Maneater - Single Version", "Maneater")]
    public void Las_coletillas_que_se_quitan_entre_parentesis_tambien_se_quitan_tras_guion(string catalogo, string esperado)
        => Assert.Equal(esperado, Descriptors.CleanDbTitle(catalogo, ""));

    // El directo no pedido se quita, como entre paréntesis; si el archivo lo pide, se conserva.
    [Fact]
    public void El_directo_tras_guion_respeta_lo_que_pide_el_nombre()
    {
        Assert.Equal("Despacito", Descriptors.CleanDbTitle("Despacito - Live", "Luis Fonsi - Despacito"));
        Assert.Equal("Despacito (Live)", Descriptors.CleanDbTitle("Despacito - Live", "Luis Fonsi - Despacito (Live)"));
    }

    // De punta a punta, con el archivo tal cual quedó en disco y el título que da Deezer.
    [Fact]
    public async Task El_Motorcito_ya_no_deja_un_guion_colgando()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Gusty DJ, Lolo OG, DobleP - El Motorcito - (Remix).mp3");
            Mp3Fixture.WriteMinMp3(p);
            var cat = new CatalogoFalso()
                .Responde(CatalogoFalso.BusquedaDeezer("Gusty DJ", "El Motorcito"),
                          CatalogoFalso.DeezerBusqueda(15, "Gusty DJ", "El Motorcito - Remix", 1))
                .Responde(CatalogoFalso.PistaDeezer(15), CatalogoFalso.DeezerPista(0, "2024-01-01", "Gusty DJ", "Lolo OG", "DobleP"))
                .Responde(CatalogoFalso.AlbumDeezer(77), CatalogoFalso.DeezerAlbum("Latino"));

            var r = await cat.Procesador(dir).ProcessAsync(p, false, new Etiquetador.Core.Pipeline.ProcessOptions { Deezer = true });

            Assert.Equal("Gusty DJ, Lolo OG, DobleP - El Motorcito (Remix).mp3", r.New);
            Assert.Equal("El Motorcito (Remix)", r.Title);
            Assert.Equal("", r.Revisar);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Solo la cola que nombra una versión: un subtítulo cualquiera no se toca.
    [Theory]
    [InlineData("Barbie World - From Barbie The Album")]
    [InlineData("Part 1 - Intro")]
    [InlineData("Lo Que Pasó, Pasó")]
    public void Un_subtitulo_tras_guion_no_se_toca(string catalogo)
        => Assert.Equal(catalogo, Descriptors.CleanDbTitle(catalogo, ""));
}
