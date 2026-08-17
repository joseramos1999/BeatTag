using Etiquetador.Core;

namespace Etiquetador.Tests;

// Todo lo que vive en una carpeta de mashups se salta: es material mezclado y no existe como
// lanzamiento, así que buscarlo en el catálogo solo puede dar una identificación equivocada.
public class MashupFolderTests
{
    [Theory]
    [InlineData(@"E:\Musica\COMERCIAL\Mashups")]
    [InlineData(@"E:\Musica\COMERCIAL\Mashup")]
    [InlineData(@"E:\Musica\MASHUPS\2024")]                    // subcarpeta: cuenta la ruta entera
    [InlineData(@"E:\Musica\Mashups\Reggaeton\Nuevos")]
    [InlineData(@"E:\Musica\mash ups")]
    [InlineData(@"E:\Musica\Mash-Ups\Verano")]
    public void Carpetas_de_mashups_se_reconocen(string carpeta)
        => Assert.True(Matching.IsMashupFolder(carpeta));

    [Theory]
    [InlineData(@"E:\Musica\COMERCIAL\Reggaeton1 A-D")]
    [InlineData(@"E:\Musica\COMERCIAL\Hits Verbeneo")]
    [InlineData(@"E:\Musica\Acapellas")]
    [InlineData("")]
    [InlineData(null)]
    public void Carpetas_normales_no(string? carpeta)
        => Assert.False(Matching.IsMashupFolder(carpeta));

    // Guarda: la palabra tiene que ir suelta. Una carpeta que la lleve pegada dentro de otra
    // palabra no debe vaciar media biblioteca por accidente.
    [Fact]
    public void No_casa_dentro_de_otra_palabra()
    {
        Assert.False(Matching.IsMashupFolder(@"E:\Musica\Mashupedia"));
        Assert.False(Matching.IsMashupFolder(@"E:\Musica\Remashuped"));
    }
}
