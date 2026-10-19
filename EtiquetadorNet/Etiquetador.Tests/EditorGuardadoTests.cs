using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// E6 / T12 · Guardar en el Editor reescribía TODOS los campos. En «80 y 90s», abrir
/// «Earth, Wind &amp; Fire - September», corregir solo el año y guardar dejaba el archivo con dos
/// intérpretes, «Earth» y «Wind &amp; Fire»; y «Deshacer última» no lo arreglaba, porque el
/// manifiesto comparaba el texto ya unido. El comentario tampoco se anotaba (la pantalla decía «se
/// puede deshacer»), y los géneros se escribían como una sola cadena mientras se anotaban partidos.
/// Con archivos MP3 de verdad y el deshacer de verdad.
/// </summary>
public class EditorGuardadoTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    private readonly AppPaths _paths;

    public EditorGuardadoTests()
    {
        _paths = new AppPaths(_dir);
        _paths.EnsureDirectories();
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Cancion(string nombre, string[] artistas, string[] generos, string comentario = "", uint anio = 0)
    {
        var p = Path.Combine(_dir, nombre);
        Mp3Fixture.WriteMinMp3(p);
        using (var f = TagLib.File.Create(p))
        {
            f.Tag.Title = "September";
            f.Tag.Performers = artistas;
            f.Tag.Genres = generos;
            f.Tag.Comment = comentario.Length > 0 ? comentario : null;
            f.Tag.Year = anio;
            f.Save();
        }
        return p;
    }

    /// <summary>El formulario tal como lo deja el Editor al abrir la canción.</summary>
    private static FormularioEditor Abierto(string ruta, TagValues v)
        => new(Path.GetFileName(ruta), v.Title, v.Artist, v.Album, v.Genre,
               v.Year == 0 ? "" : v.Year.ToString(), v.Bpm == 0 ? "" : v.Bpm.ToString(), v.Comment);

    private GuardadoEditor Guardar(string ruta, TagValues cargado, FormularioEditor form)
    {
        Assert.True(EdicionManual.LeerAnio(form.Year, out var anio, out _));
        Assert.True(EdicionManual.LeerBpm(form.Bpm, out var bpm, out _, out _));
        return EdicionManual.Guardar(ruta, cargado, form, anio, bpm, _paths.NuevoManifiesto());
    }

    private static string[] Artistas(string p) { using var f = TagLib.File.Create(p); return f.Tag.Performers; }
    private static string[] Generos(string p) { using var f = TagLib.File.Create(p); return f.Tag.Genres; }
    private static string Comentario(string p) { using var f = TagLib.File.Create(p); return f.Tag.Comment ?? ""; }

    [Fact]
    public void Corregir_solo_el_anio_no_parte_Earth_Wind_and_Fire()
    {
        var p = Cancion("Earth, Wind & Fire - September.mp3", new[] { "Earth, Wind & Fire" }, new[] { "Funk" });
        var cargado = TagEditor.Read(p);
        Assert.Equal("Earth, Wind & Fire", cargado.Artist);

        var g = Guardar(p, cargado, Abierto(p, cargado) with { Year = "1978" });

        Assert.Equal(new[] { "Year" }, g.Cambios.Keys);
        Assert.Equal(new[] { "Earth, Wind & Fire" }, Artistas(p));
        Assert.True(g.Anotado);
    }

    // Escribir a mano un nombre conocido con coma tampoco lo parte; los demás sí se separan.
    [Theory]
    [InlineData("Tyler, The Creator", new[] { "Tyler, The Creator" })]
    [InlineData("Tyler, The Creator, Kali Uchis", new[] { "Tyler, The Creator", "Kali Uchis" })]
    [InlineData("Bad Bunny, Jhay Cortez", new[] { "Bad Bunny", "Jhay Cortez" })]
    [InlineData("crosby, stills & nash", new[] { "crosby, stills & nash" })]
    public void Separar_artistas_respeta_los_nombres_con_coma(string texto, string[] esperado)
        => Assert.Equal(esperado, EdicionManual.SepararArtistas(texto, Array.Empty<string>()));

    // Lo que ya era un solo intérprete en el archivo se conserva al añadir otro, con su grafía.
    [Fact]
    public void Al_anadir_un_artista_se_conserva_el_que_tenia_coma()
    {
        var p = Cancion("Earth, Wind & Fire - Boogie Wonderland.mp3", new[] { "Earth,Wind & Fire" }, Array.Empty<string>());
        var cargado = TagEditor.Read(p);

        Guardar(p, cargado, Abierto(p, cargado) with { Artist = "Earth,Wind & Fire, The Emotions" });

        Assert.Equal(new[] { "Earth,Wind & Fire", "The Emotions" }, Artistas(p));
    }

    // T12: el comentario se anota y «Deshacer última» lo revierte.
    [Fact]
    public void Deshacer_revierte_el_comentario()
    {
        var p = Cancion("Earth, Wind & Fire - September.mp3", new[] { "Earth, Wind & Fire" }, new[] { "Funk" }, "Del vinilo");
        var cargado = TagEditor.Read(p);

        var g = Guardar(p, cargado, Abierto(p, cargado) with { Comment = "Energía 8 · cierre" });
        Assert.True(g.Cambios.ContainsKey("Comment"));
        Assert.Equal("Energía 8 · cierre", Comentario(p));

        var u = new UndoEngine(_paths).UndoLastRun();
        Assert.Equal(1, u.Reverted);
        Assert.Equal("Del vinilo", Comentario(p));
    }

    // T12: «House; Tech House» son dos géneros, y deshacer devuelve el que había.
    [Fact]
    public void Dos_generos_se_escriben_separados_y_se_deshacen()
    {
        var p = Cancion("Purple Disco Machine - Hypnotized.mp3", new[] { "Purple Disco Machine" }, new[] { "Dance" });
        var cargado = TagEditor.Read(p);

        Guardar(p, cargado, Abierto(p, cargado) with { Genre = "House; Tech House" });
        Assert.Equal(new[] { "House", "Tech House" }, Generos(p));

        var u = new UndoEngine(_paths).UndoLastRun();
        Assert.Equal(1, u.Reverted);
        Assert.Equal(new[] { "Dance" }, Generos(p));
    }

    // Cambiar el artista y deshacer devuelve el intérprete único, no dos trozos.
    [Fact]
    public void Deshacer_un_cambio_de_artista_devuelve_el_original()
    {
        var p = Cancion("Earth, Wind & Fire - September.mp3", new[] { "Earth, Wind & Fire" }, new[] { "Funk" });
        var cargado = TagEditor.Read(p);

        Guardar(p, cargado, Abierto(p, cargado) with { Artist = "Earth, Wind & Fire, The Emotions" });
        Assert.Equal(new[] { "Earth, Wind & Fire", "The Emotions" }, Artistas(p));

        new UndoEngine(_paths).UndoLastRun();
        Assert.Equal(new[] { "Earth, Wind & Fire" }, Artistas(p));
    }

    [Fact]
    public void Sin_tocar_nada_no_se_escribe_ni_se_anota()
    {
        var p = Cancion("Earth, Wind & Fire - September.mp3", new[] { "Earth, Wind & Fire" }, new[] { "Funk" });
        var cargado = TagEditor.Read(p);
        var antes = File.GetLastWriteTimeUtc(p);

        var g = Guardar(p, cargado, Abierto(p, cargado));

        Assert.False(g.HuboCambios);
        Assert.False(g.Anotado);
        Assert.Equal(antes, File.GetLastWriteTimeUtc(p));
        Assert.Empty(Directory.GetFiles(_paths.UndoDir, "run_*.jsonl"));
        Assert.StartsWith("No había nada que guardar", EdicionManual.MensajeGuardado("x.mp3", g, ""));
    }

    // Renombrar desde el Editor sigue anotándose y deshaciéndose.
    [Fact]
    public void Renombrar_se_anota_y_se_puede_deshacer()
    {
        var p = Cancion("ewf_september_final.mp3", new[] { "Earth, Wind & Fire" }, new[] { "Funk" });
        var cargado = TagEditor.Read(p);

        var g = Guardar(p, cargado, Abierto(p, cargado) with { FileName = "Earth, Wind & Fire - September.mp3" });

        Assert.True(g.Renombrado);
        Assert.True(File.Exists(g.RutaFinal));
        Assert.EndsWith("se puede deshacer", EdicionManual.MensajeGuardado(Path.GetFileName(g.RutaFinal), g, ""));
        new UndoEngine(_paths).UndoLastRun();
        Assert.True(File.Exists(p));
    }

    // Si el manifiesto no se puede escribir, la pantalla no puede prometer que se puede deshacer.
    [Fact]
    public void Si_no_se_anota_no_promete_deshacer()
    {
        var p = Cancion("Earth, Wind & Fire - September.mp3", new[] { "Earth, Wind & Fire" }, new[] { "Funk" });
        var cargado = TagEditor.Read(p);
        var bloqueo = Path.Combine(_dir, "no-es-carpeta");
        File.WriteAllText(bloqueo, "");   // un archivo donde debería ir la carpeta del manifiesto

        var g = EdicionManual.Guardar(p, cargado, Abierto(p, cargado) with { Comment = "nuevo" }, 0, 0,
                                      Path.Combine(bloqueo, "run_20261019_120000.jsonl"));

        Assert.True(g.HuboCambios);
        Assert.False(g.Anotado);
        var mensaje = EdicionManual.MensajeGuardado("x.mp3", g, "");
        Assert.Contains("NO quedó anotado", mensaje);
        Assert.DoesNotContain("se puede deshacer", mensaje);
    }

    // El BPM con decimales se redondea y se avisa; antes «128.5» o «127,99» BORRABAN el BPM.
    [Theory]
    [InlineData("128", 128u, false)]
    [InlineData("128.5", 129u, true)]
    [InlineData("127,99", 128u, true)]
    [InlineData("", 0u, false)]
    public void El_bpm_admite_decimales(string texto, uint esperado, bool avisa)
    {
        Assert.True(EdicionManual.LeerBpm(texto, out var bpm, out var aviso, out var error));
        Assert.Equal(esperado, bpm);
        Assert.Equal(avisa, aviso.Length > 0);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData("128 bpm")]
    [InlineData("-5")]
    [InlineData("1280")]
    public void Un_bpm_ilegible_no_se_guarda(string texto)
    {
        Assert.False(EdicionManual.LeerBpm(texto, out _, out _, out var error));
        Assert.Contains("No se ha guardado nada", error);
    }

    [Theory]
    [InlineData("2004", true, 2004u)]
    [InlineData("", true, 0u)]
    [InlineData("20O4", false, 0u)]
    [InlineData("2004.5", false, 0u)]
    public void El_anio_se_valida(string texto, bool vale, uint esperado)
    {
        Assert.Equal(vale, EdicionManual.LeerAnio(texto, out var anio, out var error));
        Assert.Equal(esperado, anio);
        Assert.Equal(vale, error.Length == 0);
    }
}

/// <summary>
/// E4 · Si se cambiaba de canción mientras «Reanalizar…» buscaba (1-5 s con varias fuentes e IA), la
/// propuesta de A se volcaba en el formulario de B, incluido el NOMBRE DE ARCHIVO, y Guardar escribía
/// los datos de A en B y la renombraba como A.
/// </summary>
public class EditorPropuestaTests
{
    [Fact]
    public void La_propuesta_de_otra_cancion_se_descarta()
    {
        var a = new Track { FilePath = @"E:\Musica\80 y 90s\Earth, Wind & Fire - September.mp3" };
        var b = new Track { FilePath = @"E:\Musica\80 y 90s\Kool & The Gang - Celebration.mp3" };

        Assert.True(EdicionManual.PropuestaDeOtraCancion(a, b));
        Assert.True(EdicionManual.PropuestaDeOtraCancion(a, null));
        Assert.False(EdicionManual.PropuestaDeOtraCancion(a, a));
    }

    // Solo se rellena lo que trae la propuesta: lo demás del formulario se queda como estaba.
    [Fact]
    public void La_propuesta_solo_rellena_lo_que_trae()
    {
        var form = new FormularioEditor("track07.mp3", "track07", "", "Mi álbum", "Funk", "", "", "Del vinilo");
        var r = new ProcessResult { New = "Earth, Wind & Fire - September.mp3", Title = "September", Artist = "Earth, Wind & Fire", Year = "1978" };

        var f = EdicionManual.ConPropuesta(form, r);

        Assert.Equal("Earth, Wind & Fire - September.mp3", f.FileName);
        Assert.Equal("September", f.Title);
        Assert.Equal("1978", f.Year);
        Assert.Equal("Mi álbum", f.Album);   // la propuesta no traía álbum
        Assert.Equal("Funk", f.Genre);
        Assert.Equal("Del vinilo", f.Comment);
    }
}
