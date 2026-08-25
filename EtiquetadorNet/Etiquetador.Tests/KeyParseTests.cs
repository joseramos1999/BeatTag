using Etiquetador.Core.Analysis;

namespace Etiquetador.Tests;

/// <summary>
/// Lectura de la tonalidad escrita en los tags. Los valores salen de la biblioteca real del
/// usuario, donde rekordbox ha etiquetado 3000 de 3001 canciones.
/// </summary>
public class KeyParseTests
{
    [Theory]
    [InlineData("Am", "Am", "8A")]
    [InlineData("Gm", "Gm", "6A")]
    [InlineData("Dbm", "C#m", "12A")]     // bemol: mismo sonido que do sostenido
    [InlineData("E", "E", "12B")]
    [InlineData("B", "B", "1B")]
    [InlineData("A", "A", "11B")]
    [InlineData("F#m", "F#m", "11A")]
    [InlineData("Bbm", "A#m", "3A")]
    public void Lee_la_notacion_musical(string tag, string nombre, string camelot)
    {
        var k = MusicalKey.Parse(tag);
        Assert.NotNull(k);
        Assert.Equal(nombre, k!.Name);
        Assert.Equal(camelot, k.Camelot);
    }

    // Algunos programas escriben directamente el codigo Camelot.
    [Theory]
    [InlineData("8A", "Am")]
    [InlineData("8B", "C")]
    [InlineData("12A", "C#m")]
    [InlineData("1B", "B")]
    public void Lee_tambien_el_codigo_camelot(string tag, string nombre)
    {
        var k = MusicalKey.Parse(tag);
        Assert.NotNull(k);
        Assert.Equal(nombre, k!.Name);
    }

    // Ida y vuelta: lo que escribimos tenemos que saber leerlo.
    [Fact]
    public void Lo_que_escribimos_se_vuelve_a_leer()
    {
        for (var c = 0; c < 12; c++)
            foreach (var menor in new[] { false, true })
            {
                var k = new MusicalKey(c, menor, 1);
                var leido = MusicalKey.Parse(k.Name);
                Assert.NotNull(leido);
                Assert.Equal(c, leido!.PitchClass);
                Assert.Equal(menor, leido.IsMinor);

                var porCamelot = MusicalKey.Parse(k.Camelot);
                Assert.NotNull(porCamelot);
                Assert.Equal(k.Camelot, porCamelot!.Camelot);
            }
    }

    [Theory]
    [InlineData("A min", "Am")]
    [InlineData("F minor", "Fm")]
    [InlineData("  Em  ", "Em")]
    public void Tolera_las_formas_largas_y_los_espacios(string tag, string nombre)
        => Assert.Equal(nombre, MusicalKey.Parse(tag)!.Name);

    // Guarda: ante algo que no se entiende, NADA. Inventarse una clave es peor que dejarla vacia,
    // porque se mezcla en armonico fiandose de ella.
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("---")]
    [InlineData("13A")]      // fuera de la rueda
    [InlineData("0B")]
    [InlineData("H")]        // notacion alemana, no soportada
    [InlineData("128")]      // esto es un BPM, no una clave
    public void Lo_que_no_se_entiende_no_se_adivina(string? tag)
        => Assert.Null(MusicalKey.Parse(tag));
}
