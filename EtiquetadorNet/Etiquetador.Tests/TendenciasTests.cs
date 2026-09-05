using Etiquetador.App.ViewModels;
using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// Qué cuenta como «esta canción del chart la tengo».
///
/// La pregunta no es la misma que en Comprobar audio. Allí se decide si un archivo se puede
/// identificar; aquí, si sirve para pinchar. Un remix de un tema del Top 50 sirve; su acapella, no,
/// y darla por buena es peor que no tener nada: el día de la sesión te encuentras con la voz sola.
/// </summary>
public class TendenciasTests
{
    private static Track De(string nombre, string carpeta = "Musica")
        => new() { FilePath = Rutas.De(carpeta, nombre), Folder = Rutas.De(carpeta) };

    // Lo que NO cuenta: la voz suelta y las mezclas de varios temas.
    [Theory]
    [InlineData("Bad Bunny - Monaco (Acapella).mp3")]
    [InlineData("Bad Bunny - Monaco (Acapella Studio).mp3")]
    [InlineData("Bad Bunny - Monaco (Percapella).mp3")]
    [InlineData("Bad Bunny - Monaco (Aca In).mp3")]
    [InlineData("Bad Bunny - Monaco x Otra Cosa (Mashup).mp3")]
    [InlineData("Bad Bunny - Monaco (Transition 128).mp3")]
    [InlineData("Bad Bunny - Monaco (Blend).mp3")]
    public void Una_acapella_o_una_mezcla_no_es_tener_la_cancion(string nombre)
        => Assert.True(TrendsViewModel.NoCuentaComoTenerla(De(nombre)));

    // Una carpeta entera de acapellas o de mashups, aunque el archivo no lo diga.
    [Theory]
    [InlineData("Acapellas")]
    [InlineData("Mashups 2024")]
    public void Una_carpeta_dedicada_tampoco_cuenta(string carpeta)
        => Assert.True(TrendsViewModel.NoCuentaComoTenerla(De("Bad Bunny - Monaco.mp3", carpeta)));

    // Lo que SÍ cuenta: sigue siendo la canción y se puede pinchar. Excluir esto dejaría la lista
    // diciendo que no tienes medio Top 50 teniéndolo.
    [Theory]
    [InlineData("Bad Bunny - Monaco.mp3")]
    [InlineData("Bad Bunny - Monaco (Extended Mix).mp3")]
    [InlineData("Bad Bunny - Monaco (Hype Intro).mp3")]
    [InlineData("Bad Bunny - Monaco (Yaniss Remix).mp3")]
    [InlineData("Bad Bunny - Monaco (Bootleg).mp3")]
    [InlineData("Bad Bunny - Monaco (Radio Edit).mp3")]
    [InlineData("Bad Bunny - Monaco (Dirty).mp3")]
    public void Un_remix_o_una_edicion_si_es_tener_la_cancion(string nombre)
        => Assert.False(TrendsViewModel.NoCuentaComoTenerla(De(nombre)));

    // La distinción que separa las dos listas: una mezcla de varios temas no es la canción, pero un
    // bootleg o un remix sí. IsSkipMix junta ambas cosas porque para IDENTIFICAR se saltan las dos.
    [Theory]
    [InlineData("Tema (Mashup)", true)]
    [InlineData("Tema (Transition)", true)]
    [InlineData("Tema (Blend)", true)]
    [InlineData("Tema (Bootleg)", false)]
    [InlineData("Tema (Remix)", false)]
    [InlineData("Tema (Edit)", false)]
    public void Solo_las_mezclas_de_varios_temas_dejan_de_ser_la_cancion(string nombre, bool esMezcla)
        => Assert.Equal(esMezcla, Matching.IsMezclaDeVariosTemas(nombre));
}
