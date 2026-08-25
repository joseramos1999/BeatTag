using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Si aplicar una propuesta no cambiaria NADA del archivo, no se enseña: en una biblioteca ya
/// ordenada esas filas son la mayoria y tapan las que si hay que revisar.
///
/// Lo delicado es que la regla tiene que coincidir EXACTAMENTE con la de ApplyTags. Sobre todo con
/// "Sobrescribir" desactivado: ahi un campo que ya tiene valor no se llega a escribir, asi que una
/// diferencia en el no es un cambio.
/// </summary>
public class WouldChangeTests
{
    private static readonly FieldFlags Todos = new()
    {
        Title = true, Artist = true, Album = true, Genre = true, Year = true, Bpm = true,
    };

    private static Track Actual(string archivo = "Bad Bunny - Tití Me Preguntó.mp3",
                                string? titulo = "Tití Me Preguntó", string? artista = "Bad Bunny",
                                string? album = "Un Verano Sin Ti", string? genero = "Reggaeton",
                                uint anio = 2022, uint bpm = 106)
        => new()
        {
            FilePath = @"C:\m\" + archivo,
            Folder = @"C:\m",
            Title = titulo, Artist = artista, Album = album, Genre = genero, Year = anio, Bpm = bpm,
        };

    private static ProcessResult Propuesta(string nuevo = "Bad Bunny - Tití Me Preguntó.mp3",
                                           string titulo = "Tití Me Preguntó", string artista = "Bad Bunny",
                                           string album = "Un Verano Sin Ti", string genero = "Reggaeton",
                                           string anio = "2022", string bpm = "106")
        => new()
        {
            FilePath = @"C:\m\x.mp3", Old = "x.mp3", New = nuevo,
            Title = titulo, Artist = artista, Album = album, Genre = genero, Year = anio, Bpm = bpm,
        };

    // El caso que motiva la funcion: todo coincide, no hay nada que hacer.
    [Fact]
    public void Si_todo_coincide_no_hay_cambio()
        => Assert.False(Tagging.WouldChange(Propuesta(), Actual(), overwrite: true, Todos));

    [Fact]
    public void Un_nombre_de_archivo_distinto_si_es_cambio()
        => Assert.True(Tagging.WouldChange(Propuesta(nuevo: "Otro nombre.mp3"), Actual(), true, Todos));

    [Theory]
    [InlineData("titulo")]
    [InlineData("artista")]
    [InlineData("album")]
    [InlineData("genero")]
    public void Un_tag_distinto_si_es_cambio(string campo)
    {
        var p = campo switch
        {
            "titulo" => Propuesta(titulo: "Otro"),
            "artista" => Propuesta(artista: "Otro"),
            "album" => Propuesta(album: "Otro"),
            _ => Propuesta(genero: "Otro"),
        };
        Assert.True(Tagging.WouldChange(p, Actual(), overwrite: true, Todos));
    }

    [Fact]
    public void El_anio_y_el_bpm_tambien_cuentan()
    {
        Assert.True(Tagging.WouldChange(Propuesta(anio: "1999"), Actual(), true, Todos));
        Assert.True(Tagging.WouldChange(Propuesta(bpm: "128"), Actual(), true, Todos));
    }

    // LO IMPORTANTE: sin sobrescribir, un campo que YA tiene valor no se escribe, asi que la
    // diferencia no llega a aplicarse y no es un cambio.
    [Fact]
    public void Sin_sobrescribir_lo_que_ya_tiene_valor_no_cuenta()
    {
        var p = Propuesta(titulo: "Titulo distinto", artista: "Artista distinto");
        Assert.False(Tagging.WouldChange(p, Actual(), overwrite: false, Todos));
        Assert.True(Tagging.WouldChange(p, Actual(), overwrite: true, Todos));
    }

    // Pero si el campo esta VACIO, se rellena aunque no se sobrescriba: eso si es un cambio.
    [Fact]
    public void Sin_sobrescribir_un_campo_vacio_si_se_rellena()
        => Assert.True(Tagging.WouldChange(Propuesta(), Actual(genero: ""), overwrite: false, Todos));

    // Un campo desactivado no se escribe, asi que su diferencia no cuenta.
    [Fact]
    public void Un_campo_desactivado_no_cuenta()
    {
        // FieldFlags trae TODO a true por defecto: hay que apagar lo demas expresamente.
        var soloTitulo = new FieldFlags
        {
            Title = true, Artist = false, Album = false, Genre = false, Year = false, Bpm = false,
        };
        Assert.False(Tagging.WouldChange(Propuesta(genero: "Otro"), Actual(), true, soloTitulo));
        Assert.True(Tagging.WouldChange(Propuesta(titulo: "Otro"), Actual(), true, soloTitulo));
    }

    // Una propuesta vacia no borra nada: ApplyTags exige que el valor propuesto tenga contenido.
    [Fact]
    public void Una_propuesta_vacia_no_es_un_cambio()
        => Assert.False(Tagging.WouldChange(Propuesta(titulo: "", artista: "", album: "", genero: "", anio: "", bpm: ""),
                                            Actual(), true, Todos));
}
