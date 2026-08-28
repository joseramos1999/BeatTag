using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// El filtro de texto de las tablas. Lo que se fija aqui es como busca de verdad un DJ: a trozos,
/// sin acentos y sin acordarse del orden. Si esto falla, el buscador estorba en vez de ayudar.
/// </summary>
public class BusquedaTextoTests
{
    private const string Archivo = "Bad_Bunny-Titi_Me_Pregunto.mp3";

    // Sin consulta, la lista entera: borrar el cuadro tiene que devolverlo todo.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Sin_consulta_pasa_todo(string? consulta)
        => Assert.True(BusquedaTexto.Coincide(consulta, Archivo));

    // Por palabras sueltas y en cualquier orden.
    [Theory]
    [InlineData("bunny")]
    [InlineData("bad bunny")]
    [InlineData("bunny bad")]
    [InlineData("titi bunny")]
    [InlineData("pregunto")]
    public void Encuentra_por_palabras_en_cualquier_orden(string consulta)
        => Assert.True(BusquedaTexto.Coincide(consulta, Archivo));

    // Los separadores del nombre de archivo no cuentan: "bad bunny" encuentra "Bad_Bunny".
    [Fact]
    public void Los_guiones_bajos_no_estorban()
        => Assert.True(BusquedaTexto.Coincide("bad bunny", "Bad_Bunny-Titi.mp3"));

    // Sin acentos y sin mayusculas: escribirlos para buscar es un impuesto absurdo.
    [Theory]
    [InlineData("titi", "Tití Me Preguntó")]
    [InlineData("rosalia", "ROSALÍA - Malamente")]
    [InlineData("MALAMENTE", "ROSALÍA - Malamente")]
    [InlineData("cafune", "Cruz Cafuné")]
    public void Ni_acentos_ni_mayusculas(string consulta, string campo)
        => Assert.True(BusquedaTexto.Coincide(consulta, campo));

    // Todas las palabras tienen que estar: escribir mas acota, nunca amplia.
    [Fact]
    public void Anadir_palabras_acota()
    {
        Assert.True(BusquedaTexto.Coincide("bad", Archivo));
        Assert.False(BusquedaTexto.Coincide("bad quevedo", Archivo));
    }

    [Fact]
    public void Lo_que_no_esta_no_aparece()
        => Assert.False(BusquedaTexto.Coincide("rosalia", Archivo));

    // Busca en TODOS los campos que se le pasen, no solo en el primero.
    [Fact]
    public void Busca_en_todos_los_campos()
        => Assert.True(BusquedaTexto.Coincide("reggaeton", "pista01.mp3", "Bad Bunny", "Reggaeton"));

    // Campos vacios o nulos no rompen nada.
    [Fact]
    public void Los_campos_vacios_no_molestan()
        => Assert.True(BusquedaTexto.Coincide("bunny", null, "", "Bad Bunny"));

    [Fact]
    public void Sin_ningun_campo_no_coincide_nada()
        => Assert.False(BusquedaTexto.Coincide("bunny", Array.Empty<string?>()));
}
