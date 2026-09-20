using Etiquetador.Core.Analysis;

namespace Etiquetador.Tests;

/// <summary>
/// Qué cuenta como «edición de DJ» al excluirlas de Duplicados. Los nombres son reales: salen de
/// una biblioteca de 15.000 canciones.
///
/// La línea importante: una edición para pinchar NO es una copia sobrante («Bichota (Hype Intro)»
/// se tiene ADEMÁS del original), pero «Extended» o «Remix» sí son versiones que publica el
/// catálogo, y excluirlas escondería duplicados de verdad.
/// </summary>
public class EdicionDjTests
{
    private static string R(params string[] tramos) => Path.Combine(Path.GetTempPath(), Path.Combine(tramos));

    [Theory]
    [InlineData("Daddy Yankee - Gasolina (Carlos Iniesta Hype Intro).mp3")]
    [InlineData("Yandar & Yostin - Te Pintaros Pajaritos (F3LY Melodic Intro) [96 Bpm].mp3")]
    [InlineData("El Punto40 - Mini Mini (William Garezz Break Intro).mp3")]
    [InlineData("Moscow Mule (Luigi Beltran Open Show Edit).mp3")]
    [InlineData("Don Omar - Taboo (F3LY Aca Out) [125 Bpm].mp3")]
    [InlineData("Bad Bunny - Monaco (Starter).mp3")]
    [InlineData("Tema (Quick Hit).mp3")]
    [InlineData("Tema (Redrum).mp3")]
    [InlineData("Callejero Fino - Tema (Acapella).mp3")]
    [InlineData("Vista Al Mar x Punto G (Try It Mashup).mp3")]
    [InlineData("Raw Alejandro - Buenos Terminos (Bayne Transition).mp3")]
    [InlineData("Tema (Outro).mp3")]
    public void Las_ediciones_para_pinchar_se_reconocen(string archivo) => Assert.True(EdicionDj.Es(R("Musica", archivo)));

    // Versiones que también existen en el catálogo: NO son ediciones de DJ. Excluirlas dejaría sin
    // detectar dos copias de la misma extended, que es justo lo que se busca en Duplicados.
    [Theory]
    [InlineData("Feid - Lady Mi Amor (Extended).mp3")]
    [InlineData("Bramsito, Niska - Criminel (Yaniss Remix).mp3")]
    [InlineData("Alvaro Soler - La Cintura (Radio Edit).mp3")]
    [InlineData("Ozuna - Alta Gama (Original Mix).mp3")]
    [InlineData("Fisher - Losing It (Club Mix).mp3")]
    [InlineData("Bad Bunny - Titi Me Pregunto.mp3")]
    public void Las_versiones_de_catalogo_no_se_excluyen(string archivo) => Assert.False(EdicionDj.Es(R("Musica", archivo)));

    // Quien guarda sus acapellas o sus intros en una carpeta propia no lo repite en cada archivo.
    [Fact]
    public void La_carpeta_tambien_cuenta()
    {
        Assert.True(EdicionDj.Es(R("Musica", "Acapellas", "Bad Bunny - Monaco.mp3")));
        Assert.True(EdicionDj.Es(R("Musica", "Hype Intros", "Karol G - Bichota.mp3")));
        Assert.False(EdicionDj.Es(R("Musica", "Reggaeton", "Karol G - Bichota.mp3")));
    }

    [Fact]
    public void Sin_nombre_no_es_nada()
    {
        Assert.False(EdicionDj.Es(""));
        Assert.False(EdicionDj.Es(null));
    }
}
