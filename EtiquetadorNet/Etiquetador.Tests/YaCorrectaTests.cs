using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Canciones que Enriquecer se salta porque el nombre y los tags ya coinciden. Los nombres son de
/// una biblioteca real de 20.216 archivos.
/// </summary>
public class YaCorrectaTests
{
    private static string R(string archivo) => Path.Combine(Path.GetTempPath(), "beattag-pruebas", archivo);

    private static Track Pista(string archivo, string artista, string titulo,
                               string genero = "House", uint anio = 2020, string album = "Single", uint bpm = 124)
        => new() { FilePath = R(archivo), Artist = artista, Title = titulo, Genre = genero, Year = anio, Album = album, Bpm = bpm };

    // --- Cuándo concuerdan ---

    [Theory]
    [InlineData("Feid - Lady Mi Amor.mp3", "Feid", "Lady Mi Amor")]
    [InlineData("Rosalía - Despechá.mp3", "Rosalia", "DESPECHÁ")]                        // acentos y mayúsculas no cuentan
    // El nombre lleva el artista principal y los tags añaden a los invitados: es la misma canción.
    [InlineData("Basstyler - Bad Bass.mp3", "Basstyler; Bad Legs", "Bad Bass")]
    [InlineData("Armin van Buuren - Magico.mp3", "Armin van Buuren; Giuseppe Ottaviani", "Magico")]
    [InlineData("Bad Bunny x Feid - Perro Negro.mp3", "Bad Bunny, Feid", "Perro Negro")]
    public void Nombre_y_tags_concuerdan(string archivo, string tagA, string tagT)
        => Assert.True(YaCorrecta.Concuerda(R(archivo), tagA, tagT));

    [Theory]
    [InlineData("Feid - Lady Mi Amor.mp3", "Feid", "Lady Mi Amor (Extended)")]          // otra versión
    [InlineData("Feid - Lady Mi Amor.mp3", "Karol G", "Lady Mi Amor")]                  // otro artista
    [InlineData("Basstyler, Bad Legs - Bad Bass.mp3", "Basstyler", "Bad Bass")]         // el nombre dice más que los tags
    [InlineData("Feid - Lady Mi Amor.mp3", "", "Lady Mi Amor")]                         // sin tags no se puede afirmar nada
    [InlineData("Lady Mi Amor.mp3", "Feid", "Lady Mi Amor")]                            // sin «Artista - Título»
    public void No_concuerdan(string archivo, string tagA, string tagT)
        => Assert.False(YaCorrecta.Concuerda(R(archivo), tagA, tagT));

    // Aunque los tags digan lo mismo, un nombre sucio todavía tiene trabajo: limpiarlo.
    [Theory]
    [InlineData("Feid - Lady Mi Amor [128 BPM].mp3", "Feid", "Lady Mi Amor [128 BPM]")]
    [InlineData("Feid_-_Lady_Mi_Amor.mp3", "Feid", "Lady Mi Amor")]
    [InlineData("Feid - Lady Mi Amor (Extended.mp3", "Feid", "Lady Mi Amor (Extended")]
    public void Un_nombre_sucio_nunca_se_salta(string archivo, string tagA, string tagT)
        => Assert.False(YaCorrecta.Concuerda(R(archivo), tagA, tagT));

    // --- Lo que falta ---

    [Fact]
    public void Si_esta_todo_se_salta()
        => Assert.True(YaCorrecta.SeSalta(Pista("Feid - Lady Mi Amor.mp3", "Feid", "Lady Mi Amor"), FieldFlags.All));

    // En la biblioteca real, 628 de las que concuerdan no tenían género y 341 no tenían año:
    // rellenar eso es para lo que sirve Enriquecer, así que esas NO se saltan.
    [Fact]
    public void Si_le_falta_el_genero_no_se_salta()
        => Assert.False(YaCorrecta.SeSalta(Pista("Feid - Lady Mi Amor.mp3", "Feid", "Lady Mi Amor", genero: ""), FieldFlags.All));

    [Fact]
    public void Si_le_falta_el_anio_no_se_salta()
        => Assert.False(YaCorrecta.SeSalta(Pista("Feid - Lady Mi Amor.mp3", "Feid", "Lady Mi Amor", anio: 0), FieldFlags.All));

    // Solo cuentan los campos que el usuario ha pedido escribir.
    [Fact]
    public void Lo_que_no_se_escribe_no_cuenta_como_falta()
    {
        var sinBpm = Pista("Feid - Lady Mi Amor.mp3", "Feid", "Lady Mi Amor", bpm: 0);

        Assert.False(YaCorrecta.SeSalta(sinBpm, FieldFlags.All));
        Assert.True(YaCorrecta.SeSalta(sinBpm, new FieldFlags { Bpm = false }));
    }
}
