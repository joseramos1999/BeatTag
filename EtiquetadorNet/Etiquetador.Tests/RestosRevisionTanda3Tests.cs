using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Restos de la revisión de la tanda 3 (equipo «volumen_copias», tanda 4): guardados simultáneos de
/// bandeja-sustituidas.json (resto-a), concordancia de dos avisos (resto-b y resto-c) y el camino
/// completo de un traslado hasta las sustituciones de la Bandeja (resto-d).
/// </summary>
public class RestosRevisionTanda3Tests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Ruta(params string[] partes) => Path.Combine(new[] { _dir }.Concat(partes).ToArray());

    private SustitucionHecha Registro(int i) => new()
    {
        Fecha = DateTime.Now,
        Bandeja = Ruta("Bandeja", $"d{i}.mp3"),
        Destino = Ruta("Musica", $"c{i}.mp3"),
        Vieja = Ruta("Musica", $"c{i}.mp3"),
        NuevaBytes = 10 + i,
    };

    // ------------------------------------------------------------------ resto-a · guardados a la vez

    // Varios hilos siguen traslados y guardan a la vez, como hacen el hilo que aplica (SeguirArchivo)
    // y el de la interfaz (conciliar, Bandeja). MEDIDO antes del arreglo con esta misma prueba: los
    // guardados devolvían «The process cannot access the file … .tmp because it is being used by
    // another process» o «Could not find file … .tmp» en casi todas las vueltas.
    [Fact]
    public async Task Guardados_simultaneos_no_fallan_y_dejan_en_disco_lo_ultimo()
    {
        var archivo = AlmacenSustituciones.RutaEn(Ruta("datos"));
        var almacen = new AlmacenSustituciones(archivo);
        const int hilos = 8, vueltas = 40;
        for (var i = 0; i < hilos; i++) almacen.Anotar(Registro(i));
        Assert.Equal("", almacen.Guardar());

        var errores = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var salida = new Barrier(hilos);
        var tareas = Enumerable.Range(0, hilos).Select(h => Task.Run(() =>
        {
            salida.SignalAndWait();
            var actual = Ruta("Musica", $"c{h}.mp3");
            for (var v = 0; v < vueltas; v++)
            {
                var siguiente = Ruta("Musica", $"c{h}-v{v}.mp3");
                almacen.Seguir(actual, siguiente);   // lo que hace SeguirArchivo al renombrar
                actual = siguiente;
                var e = almacen.Guardar();
                if (e.Length > 0) errores.Add(e);
            }
        })).ToArray();
        await Task.WhenAll(tareas).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Empty(errores);
        Assert.False(File.Exists(archivo + ".tmp"));
        // Lo que queda en disco es el estado final, no la foto de un guardado que terminó tarde.
        var leido = new AlmacenSustituciones(archivo);
        Assert.Equal("", leido.AvisoCarga);
        Assert.Equal(
            almacen.Todas.Select(s => s.Destino).OrderBy(x => x, StringComparer.Ordinal),
            leido.Todas.Select(s => s.Destino).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(leido.Todas, s => Assert.EndsWith($"-v{vueltas - 1}.mp3", s.Destino));
    }

    // ------------------------------------------------------------------ resto-b · carpetas no disponibles

    [Fact]
    public void El_aviso_de_carpetas_no_disponibles_concuerda_en_numero()
    {
        static CarpetaNoDisponible C(string raiz, int n) => new(raiz, "no existe", n);
        var usb = Path.Combine("E:", "USB");

        Assert.Equal("", LibraryStore.TextoNoDisponibles(Array.Empty<CarpetaNoDisponible>()));

        var una = LibraryStore.TextoNoDisponibles(new[] { C(usb, 1) });
        Assert.Contains("«USB»: no existe.", una);
        Assert.Contains("Se mantiene su única canción de la vez anterior; vuelve a escanear cuando esté disponible.", una);
        Assert.DoesNotContain("(s)", una);

        var dos = LibraryStore.TextoNoDisponibles(new[] { C(usb, 340), C(Path.Combine("F:", "Pool"), 0) });
        Assert.Contains("«USB» (y 1 carpeta más)", dos);
        Assert.Contains("Se mantienen las 340 canciones que tenían la vez anterior; vuelve a escanear cuando estén disponibles.", dos);

        var cuatro = LibraryStore.TextoNoDisponibles(new[] { C(usb, 0), C("b", 0), C("c", 0), C("d", 0) });
        Assert.Contains("(y 3 carpetas más)", cuatro);
        Assert.EndsWith("Sus canciones no aparecen hasta que estén disponibles y vuelvas a escanear.", cuatro);

        Assert.EndsWith("Sus canciones no aparecen hasta que esté disponible y vuelvas a escanear.",
                        LibraryStore.TextoNoDisponibles(new[] { C(usb, 0) }));
        Assert.Contains("Se mantienen sus 12 canciones de la vez anterior",
                        LibraryStore.TextoNoDisponibles(new[] { C(usb, 12) }));
    }

    // ------------------------------------------------------------------ resto-c · Pendientes

    // Tras «3 archivos descartados vuelven a contar», un «1 sigue conseguida» suelto parecía hablar de
    // un archivo. Ahora lleva el sujeto.
    [Fact]
    public void Recuperar_descartados_nombra_la_cancion_que_sigue_conseguida()
    {
        Assert.Equal("4 archivos descartados vuelven a contar; 1 canción sigue conseguida con otro archivo.",
                     PendientesViewModel.TextoRecuperados(4, 0, yaConseguidas: 1));
        Assert.Equal("4 archivos descartados vuelven a contar; 2 canciones vuelven a estar conseguidas; 2 canciones siguen conseguidas con otro archivo.",
                     PendientesViewModel.TextoRecuperados(4, 2, yaConseguidas: 2));
    }

    // ------------------------------------------------------------------ resto-d · del traslado al almacén

    /// <summary>Lo que hace la Bandeja al sustituir, reducido a lo que importa aquí: el registro y su manifiesto.</summary>
    private (SustitucionHecha Registro, string Manifiesto, AppPaths Paths) Sustituir(AlmacenSustituciones almacen)
    {
        var paths = new AppPaths(Ruta("datos"));
        var nueva = Path.Combine(Directory.CreateDirectory(Ruta("Bandeja")).FullName, "fisher 320.mp3");
        var vieja = Path.Combine(Directory.CreateDirectory(Ruta("Musica")).FullName, "Fisher - Losing It.mp3");
        File.WriteAllText(nueva, "nueva, de 320");
        File.WriteAllText(vieja, "vieja");
        var man = paths.NuevoManifiesto();
        var r = SustitucionBandeja.Ejecutar(nueva, vieja, ruta =>
        {
            var papelera = Directory.CreateDirectory(Ruta("Papelera")).FullName;
            File.Move(ruta, Path.Combine(papelera, Path.GetFileName(ruta)));
            return (true, "");
        }, rec =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(man)!);
            File.AppendAllText(man, JsonSerializer.Serialize(rec) + "\n");
            return "";
        });
        Assert.True(r.Ok, r.Error);
        var registro = new SustitucionHecha
        {
            Fecha = DateTime.Now, Bandeja = nueva, Vieja = vieja, Destino = r.Destino,
            ViejaBytes = 5, NuevaBytes = 0,   // etiquetas copiadas: sin tamaño fiable, solo vale el historial
        };
        almacen.Anotar(registro);
        Assert.Equal("", almacen.Guardar());
        return (registro, man, paths);
    }

    /// <summary>
    /// El motor real, sin su constructor. AppEngine solo se construye sobre la carpeta de datos de
    /// Documentos (new AppPaths() sin parámetro), y una prueba no puede tocar la del usuario; así que
    /// se crea sin constructor y se le pone únicamente lo que usa <see cref="AppEngine.SeguirArchivo"/>
    /// en un traslado. Identificación y huellas se quedan sin poner A PROPÓSITO: SeguirArchivo atrapa
    /// y anota los fallos de esas cachés, que no pueden impedir seguir las sustituciones.
    /// </summary>
    private AppEngine MotorMinimo(AlmacenSustituciones almacen, Logger log)
    {
        var motor = (AppEngine)RuntimeHelpers.GetUninitializedObject(typeof(AppEngine));
        void Poner(string campo, object valor)
        {
            var f = typeof(AppEngine).GetField(campo, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(f != null, $"AppEngine ya no tiene el campo {campo}: actualiza MotorMinimo");
            f!.SetValue(motor, valor);
        }
        Poner("<Paths>k__BackingField", new AppPaths(Ruta("datos")));
        Poner("<Logger>k__BackingField", log);
        Poner("<Avisos>k__BackingField", new AvisosApp());
        Poner("<AudioAceptadas>k__BackingField", new IgnoreList(Ruta("datos", "audio-aceptadas.json")));
        Poner("<Loudness>k__BackingField", new LoudnessScanner(Ruta("datos", "sonoridad.json"), log));
        Poner("_cerrojoSustituciones", new object());
        Poner("_sustituciones", almacen);
        Poner("_cerrojoHuellas", new object());
        Poner("_colaHuellas", Task.CompletedTask);
        Poner("_cerrojoEspectro", new object());   // la calidad real (P4) también sigue al archivo
        Poner("_cerrojoCargaEspectro", new object());
        return motor;
    }

    // El camino entero de la aplicación: deshacer la sustitución con el UndoEngine de verdad, cuyo
    // aviso llega a AppEngine.SeguirDeshecho y de ahí al SeguirArchivo del motor, que lleva el traslado
    // a las sustituciones y lo guarda. Lo que queda en disco dice que la sustitución está deshecha.
    [Fact]
    public void Deshacer_la_sustitucion_llega_por_SeguirArchivo_hasta_el_almacen_y_queda_guardado()
    {
        var archivo = AlmacenSustituciones.RutaEn(Ruta("datos"));
        var almacen = new AlmacenSustituciones(archivo);
        var (registro, man, paths) = Sustituir(almacen);
        var log = new Logger();
        var motor = MotorMinimo(almacen, log);

        var deshacer = new UndoEngine(paths)
        {
            AlDeshacer = a => AppEngine.SeguirDeshecho(a, motor.SeguirArchivo, (_, _, _) => { }),
        };
        Assert.Equal(1, deshacer.UndoLastRun(man).Reverted);

        Assert.True(registro.VueltaALaBandeja);
        var enDisco = new AlmacenSustituciones(archivo);
        Assert.True(Assert.Single(enDisco.Todas).VueltaALaBandeja);
    }

    // Y un renombrado posterior (Enriquecer sobre la descarga ya sustituida) mueve el destino apuntado.
    [Fact]
    public void Un_renombrado_por_SeguirArchivo_mueve_el_destino_y_lo_guarda()
    {
        var archivo = AlmacenSustituciones.RutaEn(Ruta("datos"));
        var almacen = new AlmacenSustituciones(archivo);
        var (registro, _, _) = Sustituir(almacen);
        var motor = MotorMinimo(almacen, new Logger());
        var renombrada = Ruta("Musica", "Fisher - Losing It (Extended Mix).mp3");

        motor.SeguirArchivo(registro.Destino, renombrada, default);

        Assert.Equal(renombrada, Assert.Single(new AlmacenSustituciones(archivo).Todas).Destino);
        // Sin nada que seguir no se guarda: la regla devuelve null y no se toca el archivo.
        var antes = File.GetLastWriteTimeUtc(archivo);
        Assert.Null(AppEngine.SeguirSustituciones(almacen, Ruta("Otra", "a.mp3"), Ruta("Otra", "b.mp3")));
        Assert.Equal(antes, File.GetLastWriteTimeUtc(archivo));
    }
}
