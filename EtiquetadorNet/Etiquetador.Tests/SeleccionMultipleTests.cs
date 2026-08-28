using Etiquetador.App.ViewModels;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Seleccion multiple en Enriquecer: marcar, desmarcar y quitar varias filas a la vez.
///
/// Las dos reglas que se comprueban aqui son las que se equivocan solas: sobre QUE actua una accion
/// cuando hay una seleccion o solo una fila en curso, y que hacer al marcar un bloque a medias.
/// </summary>
public class SeleccionMultipleTests
{
    private static PreviewRow Fila(string nombre, bool marcada = true)
        => new() { Old = nombre, Apply = marcada, Result = new ProcessResult { FilePath = @"C:\m\" + nombre } };

    // --- Sobre que actua ---

    [Fact]
    public void Con_varias_seleccionadas_actua_sobre_todas()
    {
        var a = Fila("a.mp3"); var b = Fila("b.mp3"); var c = Fila("c.mp3");
        var objetivo = EnrichViewModel.Objetivo(new[] { a, b }, c);

        Assert.Equal(new[] { a, b }, objetivo);   // manda la seleccion, no la fila en curso
    }

    // Sin seleccion, la fila en curso: pulsar el clic derecho sobre una fila y que no pase nada
    // seria desconcertante.
    [Fact]
    public void Sin_seleccion_actua_sobre_la_fila_en_curso()
    {
        var a = Fila("a.mp3");
        Assert.Equal(new[] { a }, EnrichViewModel.Objetivo(Array.Empty<PreviewRow>(), a));
    }

    [Fact]
    public void Sin_nada_no_actua_sobre_ninguna()
        => Assert.Empty(EnrichViewModel.Objetivo(Array.Empty<PreviewRow>(), null));

    // --- Que hace marcar/desmarcar un bloque ---

    // Un bloque a medias se MARCA: es lo que espera quien acaba de seleccionar para aplicar.
    // Invertir cada casilla lo dejaria igual de mezclado que estaba.
    [Fact]
    public void Un_bloque_a_medias_se_marca()
        => Assert.True(EnrichViewModel.DebeMarcar(new[] { Fila("a.mp3", true), Fila("b.mp3", false) }));

    [Fact]
    public void Un_bloque_sin_marcar_se_marca()
        => Assert.True(EnrichViewModel.DebeMarcar(new[] { Fila("a.mp3", false), Fila("b.mp3", false) }));

    // Solo se desmarca cuando ya estaban TODAS marcadas.
    [Fact]
    public void Un_bloque_entero_marcado_se_desmarca()
        => Assert.False(EnrichViewModel.DebeMarcar(new[] { Fila("a.mp3", true), Fila("b.mp3", true) }));

    // Y con una sola fila sigue siendo el interruptor de toda la vida.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Con_una_sola_fila_es_un_interruptor(bool estaba, bool queda)
        => Assert.Equal(queda, EnrichViewModel.DebeMarcar(new[] { Fila("a.mp3", estaba) }));
}
