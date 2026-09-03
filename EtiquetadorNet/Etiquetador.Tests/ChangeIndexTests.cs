using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Indice de cambio (0-10): cuanto cambia el archivo si se aplica la propuesta. Sirve para apartar
/// los retoques cosmeticos y dejar a la vista lo que merece una revision.
///
/// Lo que hay que fijar no son los numeros exactos, sino el ORDEN: un renombrado de verdad tiene
/// que puntuar mas que corregir un acento, y rellenar un tag vacio mas que retocar uno que ya
/// estaba. Si eso se cumple, el umbral hace su trabajo.
/// </summary>
public class ChangeIndexTests
{
    private static readonly FieldFlags Todos = FieldFlags.All;

    private static Track Actual(string archivo, string? titulo = "Titulo", string? artista = "Artista",
                                string? album = "Album", string? genero = "Reggaeton",
                                uint anio = 2022, uint bpm = 100)
        => new()
        {
            FilePath = Rutas.Archivo(archivo), Folder = Rutas.Carpeta,
            Title = titulo, Artist = artista, Album = album, Genre = genero, Year = anio, Bpm = bpm,
        };

    private static ProcessResult Prop(string nuevo, string titulo = "Titulo", string artista = "Artista",
                                      string album = "Album", string genero = "Reggaeton",
                                      string anio = "2022", string bpm = "100")
        => new()
        {
            FilePath = Rutas.Archivo("x.mp3"), Old = "x.mp3", New = nuevo,
            Title = titulo, Artist = artista, Album = album, Genre = genero, Year = anio, Bpm = bpm,
        };

    private static double Indice(ProcessResult p, Track a, bool overwrite = true)
        => Tagging.ChangeIndex(p, a, overwrite, Todos);

    [Fact]
    public void Sin_ningun_cambio_el_indice_es_cero()
        => Assert.Equal(0.0, Indice(Prop("Artista - Titulo.mp3"), Actual("Artista - Titulo.mp3")));

    [Fact]
    public void El_indice_nunca_pasa_de_diez()
    {
        var p = Prop("Bad Bunny - Titi Me Pregunto.mp3", "Otro", "Otro", "Otro", "Otro", "1999", "128");
        Assert.InRange(Indice(p, Actual("pista01.mp3")), 0, 10);
    }

    // LO ESENCIAL: renombrar de verdad puntua mucho mas que corregir un acento.
    [Fact]
    public void Un_renombrado_real_puntua_mas_que_un_retoque()
    {
        var deVerdad = Indice(Prop("Bad Bunny - Titi Me Pregunto.mp3"), Actual("pista01.mp3"));
        var acento = Indice(Prop("Artista - Titulo.mp3"), Actual("Artista - Titulo .mp3"));

        Assert.True(deVerdad > acento * 3, $"renombrado={deVerdad} retoque={acento}");
        Assert.True(deVerdad >= 4.0, $"un nombre inservible deberia puntuar alto, y sale {deVerdad}");
        Assert.True(acento < 1.0, $"un espacio de mas deberia puntuar casi nada, y sale {acento}");
    }

    // Lo cosmetico se aparta y lo importante se queda. Se comprueba con 5 aunque el umbral que
    // trae la aplicacion sea 3 (AppConfig.MinChangeIndex): asi queda margen por si alguien lo sube,
    // y sigue valiendo si baja. Medido: "pista01.mp3" -> "Bad Bunny - Titi Me Pregunto.mp3" da 9
    // (6 si solo cambia el nombre y los tags ya estaban bien); un espacio de mas da 0.
    [Fact]
    public void El_umbral_por_defecto_separa_bien_los_dos_casos()
    {
        const double umbral = 5.0;
        Assert.True(Indice(Prop("Bad Bunny - Titi Me Pregunto.mp3"), Actual("pista01.mp3")) >= umbral);
        Assert.True(Indice(Prop("Artista - Titulo.mp3"), Actual("Artista - Titulo .mp3")) < umbral);
    }

    // Rellenar un tag vacio aporta informacion nueva: pesa mas que retocar uno que ya estaba.
    [Fact]
    public void Rellenar_un_tag_vacio_pesa_mas_que_corregirlo()
    {
        var rellenar = Indice(Prop("a.mp3", genero: "Dembow"), Actual("a.mp3", genero: ""));
        var corregir = Indice(Prop("a.mp3", genero: "Dembow"), Actual("a.mp3", genero: "Reggaeton"));
        Assert.True(rellenar > corregir, $"rellenar={rellenar} corregir={corregir}");
    }

    // Varios tags vacios de golpe: un archivo sin etiquetar merece revisarse.
    [Fact]
    public void Un_archivo_sin_etiquetar_supera_el_umbral()
    {
        var pelado = Actual("a.mp3", titulo: "", artista: "", album: "", genero: "", anio: 0, bpm: 0);
        Assert.True(Indice(Prop("a.mp3"), pelado) >= 5.0);
    }

    // Coherencia con la regla de escritura: lo que no se va a escribir no puede sumar.
    [Fact]
    public void Sin_sobrescribir_lo_que_ya_tiene_valor_no_suma()
    {
        var p = Prop("a.mp3", titulo: "Muy distinto", artista: "Tambien distinto");
        Assert.Equal(0.0, Indice(p, Actual("a.mp3"), overwrite: false));
    }

    [Fact]
    public void Un_campo_apagado_no_suma()
    {
        var soloTitulo = new FieldFlags
        {
            Title = true, Artist = false, Album = false, Genre = false, Year = false, Bpm = false,
        };
        Assert.Equal(0.0, Tagging.ChangeIndex(Prop("a.mp3", genero: "Otro"), Actual("a.mp3"), true, soloTitulo));
    }

    // Guarda: si el indice supera el umbral, aplicar TIENE que cambiar algo. Serian filas que no
    // hacen nada, que es justo lo que se queria evitar.
    [Theory]
    [InlineData("pista01.mp3", "Bad Bunny - Titi.mp3")]
    [InlineData("a.mp3", "a.mp3")]
    public void Puntuar_implica_que_algo_cambia(string actual, string nuevo)
    {
        var a = Actual(actual);
        var p = Prop(nuevo);
        if (Indice(p, a) > 0) Assert.True(Tagging.WouldChange(p, a, true, Todos));
    }
}
