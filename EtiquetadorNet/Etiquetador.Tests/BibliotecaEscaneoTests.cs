using System.Collections.Specialized;
using Etiquetador.App.Services;
using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// El escaneo de la biblioteca frente a lo que pasa MIENTRAS escanea (hallazgos M6, E5 y T5), las
/// carpetas que no están (M7) y el arranque sin bloquear (M12).
///
/// Visto en los registros: un primer escaneo real tardó 340.680 ms (25-09, 19:48). En ese rato la
/// pestaña Biblioteca seguía abierta, con «Vaciar carpetas», la ✕ y la casilla de cada carpeta y el
/// soltar carpetas activos. Al terminar se publicaba la lista que se había empezado a leer, sin
/// mirar si las carpetas seguían siendo las mismas.
/// </summary>
public class BibliotecaEscaneoTests : IDisposable
{
    private readonly string _raiz;
    private readonly string _a;
    private readonly string _b;

    public BibliotecaEscaneoTests()
    {
        _raiz = Mp3Fixture.NewTempDir();
        _a = Path.Combine(_raiz, "Acapellas");
        _b = Path.Combine(_raiz, "Hits Mashups TOP");
        Directory.CreateDirectory(_a);
        Directory.CreateDirectory(_b);
        Mp3Fixture.WriteMinMp3(Path.Combine(_a, "a1.mp3"));
        Mp3Fixture.WriteMinMp3(Path.Combine(_a, "a2.mp3"));
        Mp3Fixture.WriteMinMp3(Path.Combine(_b, "b1.mp3"));
    }

    public void Dispose() { try { Directory.Delete(_raiz, true); } catch { } }

    private string CacheFile => Path.Combine(_raiz, "scan.json");

    /// <summary>Un recorrido que se queda parado en la primera carpeta hasta que la prueba lo suelta.</summary>
    private sealed class Retenido
    {
        public readonly SemaphoreSlim Dentro = new(0);
        public readonly ManualResetEventSlim Soltar = new(false);
        public int Llamadas;
        private int _retenidas;

        public CarpetaRecorrida Recorrer(string root)
        {
            Interlocked.Increment(ref Llamadas);
            if (Interlocked.Increment(ref _retenidas) == 1)
            {
                Dentro.Release();
                Soltar.Wait(TimeSpan.FromSeconds(20));
            }
            return LibraryScanner.Recorrer(root);
        }

        public async Task EsperarDentroAsync()
            => Assert.True(await Dentro.WaitAsync(TimeSpan.FromSeconds(20)), "el escaneo no llegó a empezar");
    }

    private LibraryStore Montar(Func<string, CarpetaRecorrida>? recorrer, params string[] carpetas)
    {
        var config = new AppConfig { Folders = carpetas.ToList() };
        return new LibraryStore(config, () => { }, new ScanCache(CacheFile), recorrer);
    }

    private static async Task Terminar(Task t) => await t.WaitAsync(TimeSpan.FromSeconds(30));

    // --- Tocar las carpetas a mitad de escaneo (M6/E5/T5) ---

    [Fact]
    public async Task Desmarcar_una_carpeta_a_mitad_no_la_devuelve_al_terminar()
    {
        var r = new Retenido();
        var store = Montar(r.Recorrer, _a, _b);
        var escaneo = store.ScanAsync();
        await r.EsperarDentroAsync();

        store.Folders.First(f => f.Path == _a).Enabled = false;   // «Acapellas», fuera
        r.Soltar.Set();
        await Terminar(escaneo);

        Assert.Single(store.Tracks);
        Assert.All(store.Tracks, t => Assert.Equal(_b, t.Folder));
        Assert.True(store.IsScanned);
    }

    [Fact]
    public async Task Quitar_una_carpeta_a_mitad_no_la_devuelve_al_terminar()
    {
        var r = new Retenido();
        var store = Montar(r.Recorrer, _a, _b);
        var escaneo = store.ScanAsync();
        await r.EsperarDentroAsync();

        store.RemoveFolder(_a);
        r.Soltar.Set();
        await Terminar(escaneo);

        Assert.All(store.Tracks, t => Assert.Equal(_b, t.Folder));
    }

    // «Vaciar carpetas» a mitad: la tabla enseñaba miles de canciones con el estado «Añade una o
    // varias carpetas», y la aplicación entera se abría sobre ellas.
    [Fact]
    public async Task Vaciar_las_carpetas_a_mitad_deja_la_biblioteca_vacia_y_sin_preparar()
    {
        var r = new Retenido();
        var store = Montar(r.Recorrer, _a, _b);
        var escaneo = store.ScanAsync();
        await r.EsperarDentroAsync();

        store.ClearFolders();
        r.Soltar.Set();
        await Terminar(escaneo);

        Assert.Empty(store.Tracks);
        Assert.False(store.IsScanned);
    }

    // Soltar una carpeta a mitad: la segunda llamada volvía al instante, la pestaña dejaba de estar
    // ocupada y se desbloqueaba toda la aplicación con el escaneo aún en marcha; al terminar, la
    // carpeta nueva se daba por escaneada sin sus canciones.
    [Fact]
    public async Task Soltar_una_carpeta_a_mitad_espera_al_escaneo_y_la_incluye()
    {
        var r = new Retenido();
        var store = Montar(r.Recorrer, _a);
        var primero = store.ScanAsync();
        await r.EsperarDentroAsync();

        Assert.True(store.AddFolder(_b).Anadida);
        var delSoltado = store.ScanAsync();   // lo que hace LibraryView al soltar
        Assert.False(delSoltado.IsCompleted);  // no vuelve como si ya hubiera escaneado

        r.Soltar.Set();
        await Terminar(delSoltado);
        await Terminar(primero);

        Assert.Equal(3, store.Tracks.Count);
        Assert.Contains(store.Tracks, t => t.Folder == _b);
        Assert.True(store.IsScanned);
        Assert.False(store.IsScanning);
    }

    [Fact]
    public async Task Marcar_una_carpeta_a_mitad_del_primer_escaneo_la_incluye()
    {
        var r = new Retenido();
        var store = Montar(r.Recorrer, _a, _b);
        store.Folders.First(f => f.Path == _b).Enabled = false;
        var escaneo = store.ScanAsync();
        await r.EsperarDentroAsync();

        store.Folders.First(f => f.Path == _b).Enabled = true;
        r.Soltar.Set();
        await Terminar(escaneo);

        Assert.Equal(3, store.Tracks.Count);
        Assert.True(store.IsScanned);
    }

    // Quien pide escanear mientras se escanea (tras renombrar desde otra pestaña, por ejemplo)
    // necesita un escaneo que empiece después de su petición: el que estaba en marcha pudo pasar
    // por sus archivos antes del cambio.
    [Fact]
    public async Task Pedir_escanear_mientras_se_escanea_da_otra_vuelta()
    {
        var r = new Retenido();
        var store = Montar(r.Recorrer, _a);
        var primero = store.ScanAsync();
        await r.EsperarDentroAsync();

        var segundo = store.ScanAsync();
        r.Soltar.Set();
        await Terminar(segundo);

        Assert.Same(primero, segundo);
        Assert.Equal(2, r.Llamadas);   // dos vueltas sobre la única carpeta
    }

    // --- Carpetas que no están (M7) ---

    // El disco externo desconectado: antes el escaneo devolvía 0 canciones, podaba su caché y daba
    // la biblioteca por escaneada.
    [Fact]
    public async Task Una_carpeta_desconectada_conserva_sus_canciones_y_su_cache()
    {
        var externo = Path.Combine(_raiz, "USB");
        Directory.CreateDirectory(externo);
        Mp3Fixture.WriteMinMp3(Path.Combine(externo, "u1.mp3"));
        var store = Montar(null, _a, externo);
        await store.ScanAsync();
        Assert.Equal(3, store.Tracks.Count);

        Directory.Move(externo, externo + "_desenchufado");   // se desconecta el disco
        await store.ScanAsync();

        Assert.Equal(3, store.Tracks.Count);                  // sus canciones siguen
        Assert.True(store.IsScanned);
        var caida = Assert.Single(store.NoDisponibles);
        Assert.Equal(externo, caida.Raiz);
        Assert.Equal(1, caida.Conservadas);
        Assert.Contains("USB", store.AvisoNoDisponibles());

        // Y su caché no se ha podado: al volver a conectarlo no hay que releer todo.
        var cache = new ScanCache(CacheFile);
        Assert.Single(cache.Guardadas(p => Etiquetador.Core.Rutas.EstaDentroDe(p, externo) ? externo : null));
    }

    [Fact]
    public async Task Si_no_llega_nada_porque_no_hay_carpetas_disponibles_no_se_da_por_escaneada()
    {
        var store = Montar(null, Path.Combine(_raiz, "no-conectado"));

        await store.ScanAsync();

        Assert.Empty(store.Tracks);
        Assert.False(store.IsScanned);
        Assert.Single(store.NoDisponibles);
    }

    [Fact]
    public async Task Cuando_vuelve_a_estar_disponible_el_aviso_desaparece()
    {
        var externo = Path.Combine(_raiz, "USB");
        var store = Montar(null, _a, externo);
        await store.ScanAsync();
        Assert.Single(store.NoDisponibles);

        Directory.CreateDirectory(externo);
        Mp3Fixture.WriteMinMp3(Path.Combine(externo, "u1.mp3"));
        await store.ScanAsync();

        Assert.Empty(store.NoDisponibles);
        Assert.Equal("", store.AvisoNoDisponibles());
        Assert.Equal(3, store.Tracks.Count);
    }

    // --- Arranque sin bloquear (M12) ---

    [Fact]
    public async Task Al_arrancar_se_publica_desde_la_cache_sin_tocar_el_disco()
    {
        await Montar(null, _a, _b).ScanAsync();   // la sesión anterior

        var llamadas = 0;
        var store = Montar(r => { llamadas++; return LibraryScanner.Recorrer(r); }, _a, _b);
        Assert.True(store.CargarDesdeCache());

        Assert.Equal(3, store.Tracks.Count);
        Assert.True(store.IsScanned);
        Assert.Equal(0, llamadas);
        Assert.All(store.Tracks, t => Assert.False(string.IsNullOrEmpty(t.Folder)));
    }

    [Fact]
    public async Task Si_una_carpeta_no_tiene_nada_en_la_cache_se_escanea_como_siempre()
    {
        await Montar(null, _a).ScanAsync();   // «Hits Mashups TOP» no se había escaneado nunca

        var store = Montar(null, _a, _b);

        Assert.False(store.CargarDesdeCache());
        Assert.Empty(store.Tracks);
        Assert.False(store.IsScanned);
    }

    // Lo normal al arrancar: no ha cambiado nada. La comprobación no debe hacer recalcular todas
    // las pestañas ni cambiar las filas que ya se están viendo.
    [Fact]
    public async Task La_comprobacion_sin_cambios_no_molesta_a_las_pestanas()
    {
        await Montar(null, _a, _b).ScanAsync();
        var store = Montar(null, _a, _b);
        store.CargarDesdeCache();
        var antes = store.Tracks.ToList();
        var avisos = 0;
        store.Changed += () => avisos++;

        await store.VerificarAsync();

        Assert.Equal(0, avisos);
        Assert.Equal(antes, store.Tracks.ToList());   // los mismos objetos: la selección se conserva
    }

    [Fact]
    public async Task La_comprobacion_aplica_solo_lo_que_ha_cambiado()
    {
        await Montar(null, _a, _b).ScanAsync();
        var store = Montar(null, _a, _b);
        store.CargarDesdeCache();
        var b1 = store.Tracks.Single(t => t.FileName == "b1.mp3");
        File.Move(Path.Combine(_a, "a2.mp3"), Path.Combine(_a, "a2 (Intro).mp3"));   // renombrado fuera
        var avisos = 0;
        var cambiosDeLista = new List<NotifyCollectionChangedAction>();
        store.Changed += () => avisos++;
        store.Tracks.CollectionChanged += (_, e) => cambiosDeLista.Add(e.Action);

        await store.VerificarAsync();

        Assert.Equal(1, avisos);
        Assert.Contains(store.Tracks, t => t.FileName == "a2 (Intro).mp3");
        Assert.DoesNotContain(store.Tracks, t => t.FileName == "a2.mp3");
        Assert.Same(b1, store.Tracks.Single(t => t.FileName == "b1.mp3"));
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, cambiosDeLista);   // dos cambios, en su sitio
    }

    // Tras renombrar unas canciones desde el Asistente se volvía a recorrer la biblioteca entera
    // (1-8 s en los registros). Releer solo lo tocado no pasa por las carpetas.
    [Fact]
    public async Task Refrescar_tras_un_renombrado_no_recorre_las_carpetas()
    {
        var recorridos = 0;
        var store = Montar(r => { recorridos++; return LibraryScanner.Recorrer(r); }, _a, _b);
        await store.ScanAsync();
        recorridos = 0;
        var b1 = store.Tracks.Single(t => t.FileName == "b1.mp3");
        var antes = Path.Combine(_a, "a1.mp3");
        var despues = Path.Combine(_a, "Bad Bunny - Tití Me Preguntó.mp3");
        File.Move(antes, despues);
        var avisos = 0;
        store.Changed += () => avisos++;

        await store.RefrescarAsync(new[] { antes, despues });

        Assert.Equal(0, recorridos);
        Assert.Equal(1, avisos);
        Assert.Equal(3, store.Tracks.Count);
        Assert.Contains(store.Tracks, t => t.FilePath == despues && t.Folder == _a);
        Assert.DoesNotContain(store.Tracks, t => t.FilePath == antes);
        Assert.Same(b1, store.Tracks.Single(t => t.FileName == "b1.mp3"));
    }

    [Fact]
    public async Task Refrescar_relee_las_etiquetas_que_se_acaban_de_escribir()
    {
        var store = Montar(null, _a);
        await store.ScanAsync();
        var ruta = Path.Combine(_a, "a2.mp3");
        Mp3Fixture.SetTags(ruta, "Provenza", "Karol G", null);

        await store.RefrescarAsync(new[] { ruta });

        var t = store.Tracks.Single(x => x.FilePath == ruta);
        Assert.Equal("Provenza", t.Title);
        Assert.Equal(2, store.Tracks.Count);
    }

    // Renombrar cambiando solo mayúsculas: en Windows las dos rutas son el mismo archivo, y la
    // canción tiene que quedar con el nombre nuevo, no con el de antes.
    [WindowsFact]
    public async Task Refrescar_un_cambio_solo_de_mayusculas_deja_el_nombre_nuevo()
    {
        var store = Montar(null, _a);
        await store.ScanAsync();
        var antes = Path.Combine(_a, "a1.mp3");
        var despues = Path.Combine(_a, "A1.mp3");
        File.Move(antes, despues);

        await store.RefrescarAsync(new[] { antes, despues });

        Assert.Contains(store.Tracks, t => string.Equals(t.FilePath, despues, StringComparison.Ordinal));
        Assert.Equal(2, store.Tracks.Count);
    }

    // Sin biblioteca escaneada no hay nada que poner al día: se escanea entera.
    [Fact]
    public async Task Refrescar_sin_escanear_hace_un_escaneo_completo()
    {
        var store = Montar(null, _a, _b);

        await store.RefrescarAsync(new[] { Path.Combine(_a, "a1.mp3") });

        Assert.True(store.IsScanned);
        Assert.Equal(3, store.Tracks.Count);
    }

    // Escanear de la forma de siempre sigue avisando aunque no cambie nada: hay pestañas que ponen
    // «Escaneando…» y esperan ese aviso para volver a su estado.
    [Fact]
    public async Task Escanear_avisa_siempre_aunque_no_haya_cambios()
    {
        var store = Montar(null, _a);
        await store.ScanAsync();
        var avisos = 0;
        store.Changed += () => avisos++;

        await store.ScanAsync();

        Assert.Equal(1, avisos);
    }

    // Antes se vaciaba la lista y se añadían las canciones una a una: 13.500 avisos a dos tablas
    // agrupadas en el hilo de la interfaz. Ahora una carga grande es un solo aviso.
    [Fact]
    public async Task Una_carga_grande_llega_a_las_tablas_de_una_sola_vez()
    {
        CarpetaRecorrida Falsa(string root) => new(
            Enumerable.Range(0, 450).Select(i => Path.Combine(root, $"Tema {i:000}.mp3")).ToList(), true, "");
        var store = Montar(Falsa, _a);
        var avisos = new List<NotifyCollectionChangedAction>();
        store.Tracks.CollectionChanged += (_, e) => avisos.Add(e.Action);

        await store.ScanAsync();

        Assert.Equal(450, store.Tracks.Count);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, avisos);
    }
}
