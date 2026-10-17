using System.Text;
using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// Los almacenes de fichas, colecciones y bandeja guardan trabajo hecho a mano que no se puede
/// regenerar. Lo que se prueba aquí es que ninguna operación lo pierda en silencio: ni un traslado
/// que cae encima de una ficha huérfana, ni un arranque en el que el archivo no se pudo leer.
/// </summary>
public class AlmacenesDjTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // --- I3: mover una ficha a una ruta que tiene una huérfana ---

    // El caso real: en Duplicados se manda a la papelera «Artista - Tema.mp3» a 128 kbps, que tenía
    // ficha (E7, Pico); la papelera no toca las fichas y la suya se queda huérfana. Se descarga la
    // versión buena con el mismo nombre, se le pone en la bandeja una ficha nueva (E9, Pico, «boda»)
    // y se pasa a la misma carpeta. Antes se conservaba la huérfana y la ficha nueva se perdía.
    [Fact]
    public void Mover_encima_de_una_ficha_huerfana_conserva_la_que_se_mueve()
    {
        var almacen = new AlmacenFichas(Path.Combine(_dir, "fichas.json"));
        var enBiblioteca = Path.Combine(_dir, "Musica", "Artista - Tema.mp3");
        var enBandeja = Path.Combine(_dir, "Bandeja", "Artista - Tema.mp3");
        almacen.Poner(enBiblioteca, new FichaDj { Energia = 7, Momentos = MomentoSet.Pico });
        almacen.Poner(enBandeja, new FichaDj { Energia = 9, Momentos = MomentoSet.Pico, Etiquetas = new() { "boda" } });

        almacen.Mover(enBandeja, enBiblioteca);

        var f = almacen.Obtener(enBiblioteca);
        Assert.NotNull(f);
        Assert.Equal(9, f!.Energia);
        Assert.Equal(new[] { "boda" }, f.Etiquetas);
        Assert.Null(almacen.Obtener(enBandeja));
        Assert.Equal(1, almacen.Count);
    }

    // Si el archivo que se mueve no tenía ficha, no hay nada que sustituya a la del destino.
    [Fact]
    public void Mover_un_archivo_sin_ficha_no_borra_la_del_destino()
    {
        var almacen = new AlmacenFichas(Path.Combine(_dir, "fichas.json"));
        almacen.Poner("destino.mp3", new FichaDj { Energia = 7 });

        almacen.Mover("origen.mp3", "destino.mp3");

        Assert.Equal(7, almacen.Obtener("destino.mp3")!.Energia);
    }

    // --- I6: un archivo que no se puede leer no se puede pisar ---

    private string ArchivoConFichas(int cuantas)
    {
        var archivo = Path.Combine(_dir, "fichas-dj.json");
        var almacen = new AlmacenFichas(archivo);
        for (var i = 0; i < cuantas; i++) almacen.Poner($@"D:\Musica\tema {i}.mp3", new FichaDj { Energia = 1 + i % 10 });
        Assert.Equal("", almacen.Guardar());
        return archivo;
    }

    // El escenario: otro proceso tiene fichas-dj.json abierto sin compartir al arrancar (otra
    // ventana de BeatTag a mitad de guardar, un antivirus). Antes se arrancaba con 0 fichas y el
    // primer guardado escribía un archivo con 1 ficha encima del que tenía cientos.
    // En macOS los bloqueos de archivo son orientativos, por eso solo se prueba en Windows.
    [WindowsFact]
    public void Un_archivo_bloqueado_al_abrir_no_se_pisa_al_guardar()
    {
        var archivo = ArchivoConFichas(300);
        var antes = File.ReadAllBytes(archivo);

        AlmacenFichas almacen;
        using (new FileStream(archivo, FileMode.Open, FileAccess.Read, FileShare.None))
            almacen = new AlmacenFichas(archivo);

        Assert.True(almacen.SoloLectura);
        Assert.NotEqual("", almacen.AvisoCarga);
        Assert.Empty(Directory.GetFiles(_dir, "fichas-dj.json.ilegible_*"));   // estaba bien: no se aparta

        almacen.Poner(@"D:\Musica\nueva.mp3", new FichaDj { Energia = 8 });
        Assert.NotEqual("", almacen.Guardar());

        Assert.Equal(antes, File.ReadAllBytes(archivo));
        Assert.Equal(300, new AlmacenFichas(archivo).Count);
    }

    // Dañado y además imposible de apartar (otro proceso lo tiene abierto para leer, sin dejar que se
    // renombre): tampoco se puede tratar como vacío.
    [WindowsFact]
    public void Un_archivo_danado_que_no_se_puede_apartar_tampoco_se_pisa()
    {
        var archivo = Path.Combine(_dir, "fichas-dj.json");
        File.WriteAllText(archivo, "{ esto no es json", new UTF8Encoding(false));

        AlmacenFichas almacen;
        using (new FileStream(archivo, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            almacen = new AlmacenFichas(archivo);
            almacen.Poner(@"D:\Musica\nueva.mp3", new FichaDj { Energia = 8 });
            Assert.NotEqual("", almacen.Guardar());
        }

        Assert.True(almacen.SoloLectura);
        Assert.Equal("{ esto no es json", File.ReadAllText(archivo));
    }

    // Un bloqueo de un momento no deja la sesión en solo lectura: se espera y se vuelve a intentar.
    [WindowsFact]
    public async Task Un_bloqueo_pasajero_se_espera_y_se_lee()
    {
        var archivo = ArchivoConFichas(5);

        var bloqueo = new FileStream(archivo, FileMode.Open, FileAccess.Read, FileShare.None);
        var abrir = Task.Run(() => new AlmacenFichas(archivo));
        await Task.Delay(100);
        bloqueo.Dispose();
        var almacen = await abrir;

        Assert.False(almacen.SoloLectura);
        Assert.Equal(5, almacen.Count);
        Assert.Equal("", almacen.Guardar());
    }

    // Lo de siempre sigue igual: un archivo dañado que SÍ se puede apartar se aparta, y se sigue
    // guardando con normalidad.
    [Fact]
    public void Un_archivo_danado_que_se_puede_apartar_no_deja_el_almacen_en_solo_lectura()
    {
        var archivo = Path.Combine(_dir, "fichas-dj.json");
        File.WriteAllText(archivo, "{ esto no es json");

        var almacen = new AlmacenFichas(archivo);

        Assert.False(almacen.SoloLectura);
        Assert.Single(Directory.GetFiles(_dir, "fichas-dj.json.ilegible_*"));
        almacen.Poner("a.mp3", new FichaDj { Energia = 5 });
        Assert.Equal("", almacen.Guardar());
    }

    // Las colecciones y la bandeja usan el mismo almacén en disco, y se protegen igual.
    [WindowsFact]
    public void Colecciones_y_bandeja_tampoco_pisan_un_archivo_bloqueado()
    {
        var colecciones = Path.Combine(_dir, "colecciones.json");
        var c = new AlmacenColecciones(colecciones);
        c.Todas.Add(new ColeccionInteligente { Nombre = "Boda", Genero = "cumbia" });
        Assert.Equal("", c.Guardar());

        var bandeja = Path.Combine(_dir, "bandeja.json");
        var b = new AlmacenBandeja(bandeja);
        b.Registrar(@"D:\Bandeja\tema.mp3", new DateTime(2026, 10, 1));
        Assert.Equal("", b.Guardar());

        AlmacenColecciones c2;
        AlmacenBandeja b2;
        using (new FileStream(colecciones, FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(bandeja, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            c2 = new AlmacenColecciones(colecciones);
            b2 = new AlmacenBandeja(bandeja);
        }

        Assert.True(c2.SoloLectura);
        Assert.True(b2.SoloLectura);
        Assert.NotEqual("", c2.Guardar());
        Assert.NotEqual("", b2.Guardar());
        Assert.Single(new AlmacenColecciones(colecciones).Todas);
        Assert.NotNull(new AlmacenBandeja(bandeja).Obtener(@"D:\Bandeja\tema.mp3"));
    }
}
