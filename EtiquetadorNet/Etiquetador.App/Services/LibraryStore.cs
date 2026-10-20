using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Etiquetador.Core;

namespace Etiquetador.App.Services;

/// <summary>Una carpeta marcada que el último escaneo no pudo recorrer.</summary>
/// <param name="Raiz">La carpeta, tal como está en la lista.</param>
/// <param name="Motivo">Por qué («no existe o el disco no está conectado», el error del sistema…).</param>
/// <param name="Conservadas">Canciones suyas que se mantienen de la vez anterior.</param>
public sealed record CarpetaNoDisponible(string Raiz, string Motivo, int Conservadas);

/// <summary>
/// Biblioteca compartida por toda la app: una única lista de carpetas y un único conjunto de
/// tracks escaneados. Se escanea una vez y todas las pestañas consumen los mismos datos.
/// </summary>
public sealed class LibraryStore
{
    private readonly AppConfig _config;
    private readonly Action _saveConfig;
    private readonly ScanCache _cache;
    private readonly Func<string, CarpetaRecorrida> _recorrer;

    /// <summary>Log opcional (se asigna desde AppEngine tras construirlo).</summary>
    public Logger? Log { get; set; }

    /// <summary>Carpetas elegidas (con estado marcado/desmarcado). Misma instancia para todas las pestañas.</summary>
    public ObservableCollection<FolderItem> Folders { get; } = new();

    private readonly ListaPistas _tracks = new();

    /// <summary>Tracks escaneados (biblioteca en memoria).</summary>
    public ObservableCollection<Track> Tracks => _tracks;

    public bool IsScanned { get; private set; }
    public bool IsScanning { get; private set; }

    /// <summary>
    /// Carpetas marcadas que el último escaneo no pudo recorrer (disco desconectado, unidad de red
    /// caída…). Vacía si se pudieron leer todas.
    /// </summary>
    public IReadOnlyList<CarpetaNoDisponible> NoDisponibles { get; private set; } = Array.Empty<CarpetaNoDisponible>();

    /// <summary>"" si la caché de escaneo se leyó con normalidad; si no, qué pasó con el archivo.</summary>
    public string AvisoCache => _cache.AvisoCarga;

    /// <summary>Se dispara tras completar un escaneo (para que cada pestaña recalcule su vista).</summary>
    public event Action? Changed;

    /// <summary>Empieza o termina un escaneo. Sirve para enseñarlo sin bloquear la aplicación.</summary>
    public event Action? EscaneoCambiado;

    /// <param name="recorrer">
    /// Cómo se recorre una carpeta. Por defecto, <see cref="LibraryScanner.Recorrer"/>; las pruebas
    /// ponen uno que se puede retener a mitad para reproducir lo que pasa si el usuario toca las
    /// carpetas mientras se escanea.
    /// </param>
    public LibraryStore(AppConfig config, Action saveConfig, ScanCache cache, Func<string, CarpetaRecorrida>? recorrer = null)
    {
        _config = config;
        _saveConfig = saveConfig;
        _cache = cache;
        _recorrer = recorrer ?? (r => LibraryScanner.Recorrer(r, recursive: true));
        var disabled = new HashSet<string>(config.DisabledFolders, StringComparer.OrdinalIgnoreCase);
        foreach (var f in config.Folders) Add(new FolderItem(f, !disabled.Contains(f)));
    }

    private void Add(FolderItem item)
    {
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(FolderItem.Enabled)) return;
            Persist();

            // Desmarcar una carpeta tiene que sacar sus canciones AHORA. Antes solo se guardaba la
            // preferencia, así que hasta el siguiente escaneo la carpeta seguía saliendo en las
            // tablas y, peor, seguía analizándose: la casilla prometía excluirla y no excluía nada.
            if (item.Enabled) MarcarQueFaltaEscanear();
            else OlvidarTracksDe(item.Path);
        };
        Folders.Add(item);
    }

    private bool Has(string path) => Folders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Quita de la biblioteca en memoria el track cuyo archivo ya no existe (p. ej. tras papelera).</summary>
    public void RemoveTrack(string filePath)
    {
        var removed = false;
        for (int i = Tracks.Count - 1; i >= 0; i--)
            if (string.Equals(Tracks[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase)) { Tracks.RemoveAt(i); removed = true; }
        if (removed) Changed?.Invoke();
    }

    /// <summary>
    /// Quita varias canciones de una vez y avisa UNA sola vez al terminar. Hacerlo con RemoveTrack
    /// en bucle dispararía un recálculo completo por cada archivo, y como a Changed están suscritas
    /// todas las pestañas que analizan la biblioteca, borrar unos cientos dejaría la aplicación
    /// clavada un buen rato.
    /// </summary>
    public int RemoveTracks(IEnumerable<string> filePaths)
    {
        var quitar = new HashSet<string>(filePaths, StringComparer.OrdinalIgnoreCase);
        if (quitar.Count == 0) return 0;

        // Como al retirar una carpeta (OlvidarTracksDe): con muchas bajas se rehace la lista de una
        // vez. POR QUÉ (hallazgo rendimiento-2): enviar a la papelera miles de canciones desde la
        // Biblioteca (una carpeta entera, Ctrl+A) las quitaba una a una en el hilo de la interfaz, y
        // cada baja hace que las dos vistas agrupadas por carpeta rehagan sus grupos. MEDIDO en
        // revisión con 20.000 canciones: 0,25 s con 2.000 bajas y 1-2 s con 5.000-18.000; rehaciendo
        // la lista, 1-43 ms.
        var sobreviven = Tracks.Where(t => !quitar.Contains(t.FilePath)).ToList();
        var quitados = Tracks.Count - sobreviven.Count;
        if (quitados == 0) return 0;

        if (quitados > UmbralRehacerLista)
            _tracks.Reemplazar(sobreviven);
        else
        {
            for (int i = Tracks.Count - 1; i >= 0; i--)
                if (quitar.Contains(Tracks[i].FilePath)) Tracks.RemoveAt(i);
        }

        Changed?.Invoke();
        return quitados;
    }

    /// <summary>Vacía la caché de escaneo (memoria + archivo), para que el próximo escaneo relea todo.</summary>
    public void ClearScanCache() => _cache.Clear();

    /// <summary>
    /// Cómo ha ido un intento de añadir carpeta. El <see cref="Aviso"/> interesa incluso cuando sí
    /// se añade: puede haber absorbido otras, y eso hay que contarlo en vez de hacerlo callando.
    /// </summary>
    public readonly record struct AltaCarpeta(bool Anadida, string Aviso = "");

    /// <summary>
    /// Añade una carpeta raíz, siempre que no se solape con otra que ya esté.
    ///
    /// El solape importaba de verdad: con «Música» y «Música/House» a la vez, cada canción de House
    /// entraba DOS veces en la biblioteca. Eso falseaba el recuento y las estadísticas, inventaba
    /// duplicados y hacía que cualquier operación por lote tocara el mismo archivo dos veces.
    ///
    /// Se corta aquí, al elegir las carpetas, y no disimulándolo al escanear: así la lista de
    /// carpetas que se ve es exactamente lo que se recorre. El escaneo además se defiende por su
    /// cuenta, porque hay configuraciones ya guardadas por versiones anteriores.
    /// </summary>
    public AltaCarpeta AddFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return new AltaCarpeta(false);
        if (Has(folder)) return new AltaCarpeta(false, $"«{Nombre(folder)}» ya estaba en la lista.");

        // Ya la recoge otra: añadirla solo duplicaría sus canciones.
        var contenedora = Folders.FirstOrDefault(f => EstaDentroDe(folder, f.Path));
        if (contenedora != null)
        {
            Log?.Detail($"Biblioteca: «{folder}» no se añade; ya está dentro de «{contenedora.Path}».");
            return new AltaCarpeta(false,
                $"«{Nombre(folder)}» está dentro de «{Nombre(contenedora.Path)}», que ya está en la lista: sus canciones ya entran por ahí.");
        }

        // La nueva engloba a otras que ya estaban. Esas sobran… salvo que estén DESMARCADAS: meter
        // la de fuera las volvería a incluir sin decir nada, y desmarcar una carpeta es justo la
        // forma que tiene el usuario de dejar música fuera. Antes que deshacer esa decisión por su
        // cuenta, no se añade y se explica.
        var absorbidas = Folders.Where(f => EstaDentroDe(f.Path, folder)).ToList();
        var excluida = absorbidas.FirstOrDefault(f => !f.Enabled);
        if (excluida != null)
        {
            Log?.Detail($"Biblioteca: «{folder}» no se añade; contiene «{excluida.Path}», que está desmarcada.");
            return new AltaCarpeta(false,
                $"«{Nombre(excluida.Path)}» está desmarcada y quedaría dentro de «{Nombre(folder)}». Quítala de la lista si de verdad quieres escanear todo.");
        }

        foreach (var f in absorbidas)
        {
            Folders.Remove(f);
            Log?.Detail($"Biblioteca: «{f.Path}» sale de la lista; queda dentro de «{folder}».");
        }

        Add(new FolderItem(folder));
        Persist();
        MarcarQueFaltaEscanear();   // sus canciones todavía no están cargadas

        var aviso = absorbidas.Count switch
        {
            0 => "",
            1 => $"«{Nombre(absorbidas[0].Path)}» ya no hace falta por separado: queda dentro de «{Nombre(folder)}».",
            _ => $"{absorbidas.Count} carpetas ya no hacen falta por separado: quedan dentro de «{Nombre(folder)}».",
        };
        return new AltaCarpeta(true, aviso);
    }

    /// <summary>Solo el nombre de la carpeta, para poder nombrarla en un aviso sin soltar la ruta entera.</summary>
    private static string Nombre(string ruta)
    {
        var limpia = ruta.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var nombre = Path.GetFileName(limpia);
        return nombre.Length > 0 ? nombre : limpia;
    }

    /// <summary>
    /// <paramref name="ruta"/> cuelga de <paramref name="raiz"/>. Ser la MISMA carpeta no cuenta
    /// aquí: de eso se ocupa <see cref="Has"/>. La regla vive en <see cref="Rutas"/>, que también
    /// usa la bandeja de entrada.
    /// </summary>
    internal static bool EstaDentroDe(string ruta, string raiz) => Rutas.EstaDentroDe(ruta, raiz);

    public void RemoveFolder(string folder)
    {
        var item = Folders.FirstOrDefault(f => string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;
        Folders.Remove(item);
        Persist();
        OlvidarTracksDe(item.Path);
    }

    public void ClearFolders()
    {
        if (Folders.Count == 0) return;
        Folders.Clear();
        Persist();

        Tracks.Clear();
        IsScanned = false;
        NoDisponibles = Array.Empty<CarpetaNoDisponible>();
        Log?.Detail("Biblioteca: quitadas todas las carpetas; la lista de canciones queda vacía.");
        Changed?.Invoke();
    }

    /// <summary>
    /// Saca de la biblioteca en memoria las canciones de una carpeta que ya no cuenta.
    ///
    /// NO hace falta reescanear, y por eso es instantáneo: quitar una carpeta solo puede restar
    /// canciones, nunca añadirlas. Se compara con la carpeta raíz que se le asignó a cada canción
    /// al escanear, así que una carpeta anidada dentro de otra que siga configurada conserva sus
    /// canciones por la otra vía, que es lo correcto.
    ///
    /// Esto vive aquí y no en la pestaña Biblioteca a propósito: TODAS las pestañas trabajan sobre
    /// esta misma lista, así que dejarlo en la interfaz habría arreglado la tabla que se ve y no lo
    /// que de verdad importaba, que es que se seguían analizando archivos de una carpeta retirada.
    /// </summary>
    private void OlvidarTracksDe(string folder)
    {
        // Una carpeta que ya no cuenta tampoco puede seguir avisando de que no está disponible.
        if (NoDisponibles.Any(n => string.Equals(n.Raiz, folder, StringComparison.OrdinalIgnoreCase)))
            NoDisponibles = NoDisponibles.Where(n => !string.Equals(n.Raiz, folder, StringComparison.OrdinalIgnoreCase)).ToList();

        // Sin carpetas no queda nada escaneado, aunque el borrado no llegue a quitar ninguna
        // canción (por ejemplo, si todavía no se había escaneado).
        if (Folders.Count == 0)
        {
            var habia = Tracks.Count;
            Tracks.Clear();
            IsScanned = false;
            if (habia > 0) Log?.Detail($"Biblioteca: {habia} canciones fuera de la lista al retirar «{folder}».");
            Changed?.Invoke();
            return;
        }

        var sobreviven = Tracks.Where(t => !string.Equals(t.Folder, folder, StringComparison.OrdinalIgnoreCase)).ToList();
        var quitados = Tracks.Count - sobreviven.Count;
        if (quitados == 0) { Changed?.Invoke(); return; }

        // Cómo se quitan importa más de lo que parece. Esta lista está enlazada a varias tablas
        // AGRUPADAS, y cada baja suelta hace que la rejilla rehaga sus grupos: quitar una carpeta de
        // miles de canciones así deja la ventana clavada. Rehacer la lista de una vez cuesta un solo
        // aviso a las tablas.
        //
        // Por eso se elige según el tamaño: para unas pocas bajas sale más barato quitarlas en su
        // sitio; para muchas, rehacer la lista entera.
        if (quitados > UmbralRehacerLista)
            _tracks.Reemplazar(sobreviven);
        else
        {
            for (int i = Tracks.Count - 1; i >= 0; i--)
                if (string.Equals(Tracks[i].Folder, folder, StringComparison.OrdinalIgnoreCase))
                    Tracks.RemoveAt(i);
        }

        Log?.Detail($"Biblioteca: {quitados} canciones fuera de la lista al retirar «{folder}».");
        Changed?.Invoke();
    }

    /// <summary>
    /// A partir de cuántas bajas sale más barato rehacer la lista que ir quitándolas una a una.
    /// No es un número medido al detalle: basta con que separe «unas pocas» de «una carpeta entera».
    /// </summary>
    private const int UmbralRehacerLista = 200;

    /// <summary>
    /// La biblioteca en memoria se ha quedado corta: hay una carpeta marcada cuyas canciones no
    /// están cargadas. Se declara «sin escanear» en vez de cargarlas por sorpresa.
    ///
    /// Es el mismo estado por el que pasa la aplicación recién abierta, así que el resto de
    /// pestañas se bloquean solas y aparece el aviso de pulsar Escanear. Reescanear por nuestra
    /// cuenta al marcar una casilla sería meterse a hacer un trabajo largo que nadie ha pedido.
    /// </summary>
    private void MarcarQueFaltaEscanear()
    {
        if (!IsScanned) return;
        IsScanned = false;
        Log?.Detail("Biblioteca: hay una carpeta nueva marcada; hay que volver a escanear.");
        Changed?.Invoke();
    }

    /// <summary>Rutas de las carpetas MARCADAS (las que se analizan).</summary>
    public List<string> EnabledPaths() => Folders.Where(f => f.Enabled).Select(f => f.Path).ToList();

    /// <summary>
    /// Sube con cada cambio de la lista de carpetas o de sus casillas. Un escaneo apunta la que había
    /// al empezar y, si al terminar es otra, lo leído ya no describe la biblioteca.
    /// </summary>
    private int _generacion;

    private void Persist()
    {
        _generacion++;
        _config.Folders = Folders.Select(f => f.Path).ToList();
        _config.DisabledFolders = Folders.Where(f => !f.Enabled).Select(f => f.Path).ToList();
        _saveConfig();
    }

    // --- Escanear ---

    private Task? _escaneo;
    private bool _otraVuelta;
    private bool _publicando;
    private bool _avisarSiempre;

    /// <summary>
    /// Escanea todas las carpetas hacia <see cref="Tracks"/> y notifica a las pestañas.
    ///
    /// Si ya hay un escaneo en marcha, NO vuelve al instante como si hubiera terminado: devuelve ese
    /// mismo escaneo, que dará otra vuelta al acabar para recoger lo que haya cambiado desde que
    /// empezó. Antes, soltar una carpeta a mitad del primer escaneo volvía enseguida, la pestaña
    /// Biblioteca dejaba de estar ocupada y se desbloqueaba toda la aplicación con el escaneo aún en
    /// marcha (un primer escaneo real tardó 340.680 ms, registro del 25-09).
    /// </summary>
    public Task ScanAsync()
    {
        if (_escaneo is { IsCompleted: false })
        {
            _avisarSiempre = true;
            // Quien llega a mitad necesita un escaneo que empiece DESPUÉS de su llamada: puede que
            // acabe de renombrar archivos que el escaneo en curso ya había pasado. Si llega mientras
            // se publica (desde un aviso de Changed), lo que se publica ya es posterior a su llamada.
            if (!_publicando) _otraVuelta = true;
            return _escaneo;
        }
        _avisarSiempre = true;
        _escaneo = EscanearAsync();
        return _escaneo;
    }

    /// <summary>
    /// Comprueba en segundo plano que la biblioteca publicada sigue al día y aplica SOLO las
    /// diferencias. Avisa a las pestañas solo si algo ha cambiado, a diferencia de
    /// <see cref="ScanAsync"/>, que avisa siempre porque hay quien espera ese aviso para refrescarse.
    /// Pensado para el arranque, después de <see cref="CargarDesdeCache"/>.
    /// </summary>
    public Task VerificarAsync()
    {
        if (_escaneo is { IsCompleted: false }) return _escaneo;
        _avisarSiempre = false;
        _escaneo = EscanearAsync();
        return _escaneo;
    }

    /// <summary>
    /// Pone al día SOLO los archivos indicados, sin recorrer las carpetas (hallazgo M12). Es lo que
    /// conviene tras un cambio puntual -renombrar unas canciones desde el Asistente, aplicar unas
    /// pocas propuestas-, en lugar de <see cref="ScanAsync"/>, que vuelve a recorrer y a mirar la
    /// fecha de las 13.500 canciones (MEDIDO en los registros: 1-8 s cada vez).
    ///
    /// En un renombrado hay que pasar la ruta de ANTES y la de DESPUÉS: la de antes ya no existe y
    /// se quita; la de después se lee y entra. Igual que ScanAsync, avisa a las pestañas al
    /// terminar. Si la biblioteca no está escaneada, o ya se está escaneando, hace (o se suma a) un
    /// escaneo completo.
    /// </summary>
    public Task RefrescarAsync(IEnumerable<string> rutas)
    {
        var lista = rutas.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.Ordinal).ToList();   // «tema» y «Tema» se quedan las dos: ver Releer
        if (!IsScanned || _escaneo is { IsCompleted: false }) return ScanAsync();
        _avisarSiempre = true;
        _escaneo = EscanearAsync(lista);
        return _escaneo;
    }

    private async Task EscanearAsync(IReadOnlyCollection<string>? soloEstas = null)
    {
        IsScanning = true;
        EscaneoCambiado?.Invoke();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (true)
            {
                _otraVuelta = false;
                var generacion = _generacion;
                var roots = EnabledPaths();   // solo las carpetas marcadas
                Lectura leido;
                if (soloEstas != null)
                {
                    // Solo la primera vuelta: si hay que dar otra, ya es un escaneo completo.
                    var tocadas = soloEstas;
                    soloEstas = null;
                    var actuales = Tracks.ToList();
                    Log?.Detail($"Biblioteca: se releen {tocadas.Count} archivo(s) tocados, sin recorrer las carpetas.");
                    leido = await Task.Run(() => Releer(actuales, tocadas, roots));
                }
                else
                {
                    Log?.Head($"Escaneando {roots.Count} carpeta(s)…");
                    foreach (var r in roots) Log?.Detail($"    carpeta: {r}");
                    var skipped = Folders.Count - roots.Count;
                    if (skipped > 0) Log?.Detail($"    ({skipped} carpeta(s) desmarcada(s) que se omiten)");
                    leido = await Task.Run(() => Recorrer(roots));
                }

                // Las carpetas cambiaron mientras se leía: quitar, desmarcar, vaciar o soltar una
                // carpeta a mitad. Publicar esto devolvería a todas las pestañas las canciones de una
                // carpeta excluida, o daría por escaneada una que ni se ha mirado. Se tira y se
                // vuelve a leer; con la caché recién llenada, la segunda vuelta es rápida.
                if (generacion != _generacion)
                {
                    Log?.Detail("Biblioteca: las carpetas cambiaron durante el escaneo; se descarta lo leído y se vuelve a escanear.");
                    continue;
                }

                Publicar(roots, leido, sw.ElapsedMilliseconds);
                if (!_otraVuelta) break;
                Log?.Detail("Biblioteca: se pidió escanear mientras se escaneaba; otra vuelta para recoger los últimos cambios.");
            }
        }
        catch (Exception e) { Log?.Error("Error al escanear la biblioteca", e); throw; }
        finally
        {
            IsScanning = false;
            EscaneoCambiado?.Invoke();
        }
    }

    /// <summary>Lo que dio recorrer las carpetas marcadas.</summary>
    /// <param name="Parcial">Solo se releyeron unos archivos: lo demás, y las carpetas no disponibles, siguen como estaban.</param>
    private sealed record Lectura(List<Track> Pistas, int Repetidas, List<(string Raiz, string Motivo)> NoDisponibles, HashSet<string> Vistas,
                                  bool Parcial = false);

    /// <summary>
    /// La lista actual con unos archivos releídos: los que ya no existen salen, los que existen se
    /// leen de nuevo (o de la caché, si no cambiaron) y entran con su carpeta raíz. Fuera del hilo de
    /// la interfaz.
    /// </summary>
    private Lectura Releer(List<Track> actuales, IReadOnlyCollection<string> tocadas, IReadOnlyList<string> roots)
    {
        var quitar = new HashSet<string>(tocadas, StringComparer.OrdinalIgnoreCase);
        var pistas = actuales.Where(t => !quitar.Contains(t.FilePath)).ToList();
        var vistas = new HashSet<string>(pistas.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase);
        var ordenadas = roots.OrderBy(r => r.Length).ToList();
        // Del final al principio: en un renombrado llegan «antes, después», y si solo cambian las
        // mayúsculas («tema.mp3» → «Tema.mp3») las dos rutas valen para el mismo archivo; tiene que
        // entrar con la de después.
        foreach (var ruta in tocadas.Reverse())
        {
            // Solo lo que cae dentro de una carpeta marcada: lo movido fuera deja de contar.
            var raiz = ordenadas.FirstOrDefault(r => Rutas.EstaDentroDe(ruta, r));
            if (raiz == null || !LibraryScanner.EsAudio(ruta) || !File.Exists(ruta) || !vistas.Add(ruta)) continue;
            var t = _cache.Read(ruta);
            t.Folder = raiz;
            pistas.Add(t);
        }
        var error = _cache.Save();
        if (error.Length > 0) Log?.Err("No se pudo guardar la caché de escaneo: " + error);
        return new Lectura(pistas, 0, new List<(string, string)>(), vistas, Parcial: true);
    }

    /// <summary>Recorre las carpetas y lee cada archivo (o lo saca de la caché). Fuera del hilo de la interfaz.</summary>
    private Lectura Recorrer(IReadOnlyList<string> roots)
    {
        var acc = new List<Track>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var noDisponibles = new List<(string, string)>();
        var recorridas = new List<string>();
        var repes = 0;

        // Las raíces de fuera, primero. Con una carpeta dentro de otra, la de fuera es la
        // propietaria del archivo, y así quitar la de dentro no deja huérfana música que la
        // de fuera sigue cubriendo. Ordenar por longitud basta: si A contiene a B, la ruta
        // de A es siempre más corta que la de B.
        foreach (var root in roots.OrderBy(r => r.Length))
        {
            var recorrido = _recorrer(root);
            if (!recorrido.Disponible)
            {
                // Un disco desconectado no ha borrado su música: ni se poda su caché ni se dan sus
                // canciones por desaparecidas (hallazgo M7).
                Log?.Err($"Carpeta no disponible: {root} ({recorrido.Motivo})");
                noDisponibles.Add((root, recorrido.Motivo));
                continue;
            }
            recorridas.Add(root);
            foreach (var path in recorrido.Archivos)
            {
                // Cada archivo, una sola vez. Contarlo dos veces por estar bajo dos raíces
                // falseaba el recuento, inventaba duplicados y hacía que cada operación por
                // lote tocase el mismo archivo dos veces. AddFolder ya no deja configurar
                // carpetas solapadas; esto cubre lo que quedó guardado de antes.
                if (!seen.Add(path)) { repes++; continue; }

                var t = _cache.Read(path);   // caché por fecha+tamaño (rápido si no cambió)
                t.Folder = root;
                acc.Add(t);
            }
        }

        // Solo dentro de las carpetas recorridas de verdad: lo de una que no estaba se conserva.
        _cache.Prune(seen, recorridas);
        var error = _cache.Save();
        if (error.Length > 0) Log?.Err("No se pudo guardar la caché de escaneo: " + error);
        return new Lectura(acc, repes, noDisponibles, seen);
    }

    /// <summary>Deja en la lista lo leído y decide si la biblioteca queda lista. En el hilo de la interfaz.</summary>
    private void Publicar(IReadOnlyList<string> roots, Lectura leido, long ms)
    {
        // Las canciones de una carpeta que no se pudo leer se quedan como estaban: si el disco se
        // desconectó a media sesión, desaparecer de todas las pestañas sería tratarlas como
        // borradas. Al volver a conectarlo, el siguiente escaneo las pone al día.
        var caidas = new HashSet<string>(leido.NoDisponibles.Select(n => n.Raiz), StringComparer.OrdinalIgnoreCase);
        var conservadas = caidas.Count == 0
            ? new List<Track>()
            : Tracks.Where(t => caidas.Contains(t.Folder) && !leido.Vistas.Contains(t.FilePath)).ToList();

        var final = conservadas.Count == 0 ? leido.Pistas : leido.Pistas.Concat(conservadas).ToList();
        var cambios = Sustituir(final);

        var antes = (IsScanned, NoDisponibles.Count);
        // Una relectura de unos pocos archivos no dice nada de las carpetas: lo que había, se queda.
        if (!leido.Parcial)
        {
            NoDisponibles = leido.NoDisponibles
                .Select(n => new CarpetaNoDisponible(n.Raiz, n.Motivo, conservadas.Count(t => string.Equals(t.Folder, n.Raiz, StringComparison.OrdinalIgnoreCase))))
                .ToList();

            // Sin ninguna carpeta marcada no hay biblioteca que valga: darla por escaneada dejaba la
            // aplicación entera abierta sobre una lista vacía en vez de pedir marcar una carpeta. Y si
            // no ha llegado nada porque las carpetas no están (el disco externo sin enchufar al abrir),
            // tampoco: una biblioteca «escaneada» y vacía invitaba a analizar sobre nada.
            IsScanned = roots.Count > 0 && !(final.Count == 0 && leido.NoDisponibles.Count > 0);
        }

        if (leido.Repetidas > 0)
            Log?.Detail($"    ({leido.Repetidas} archivo(s) bajo dos carpetas configuradas; se cuentan una sola vez)");
        if (conservadas.Count > 0)
            Log?.Detail($"    ({conservadas.Count} canción(es) de carpetas no disponibles se conservan de la vez anterior)");
        Log?.Sum($"{(leido.Parcial ? "Biblioteca puesta al día" : "Escaneo terminado")}: {Tracks.Count} canciones en {ms} ms"
                 + (cambios > 0 ? $" ({cambios} cambio(s) en la lista)." : " (sin cambios)."));

        var avisar = _avisarSiempre || cambios > 0 || antes != (IsScanned, NoDisponibles.Count);
        if (!avisar) return;
        _publicando = true;
        try { Changed?.Invoke(); }
        finally { _publicando = false; }
    }

    /// <summary>
    /// Pone la lista nueva tocando solo lo que ha cambiado, y devuelve cuántas filas han cambiado.
    ///
    /// Antes se vaciaba y se volvían a añadir las 13.500 canciones una a una tras CADA escaneo,
    /// también tras renombrar una sola desde el Asistente: cada alta llegaba a dos tablas agrupadas,
    /// que rehacían sus grupos, todo en el hilo de la interfaz. Ahora, si no ha cambiado nada no se
    /// toca la lista; si han cambiado unas pocas, solo esas; y si son muchas, se cambia la lista de
    /// una vez con un único aviso. Las canciones que siguen igual conservan su objeto, y con él la
    /// selección de las tablas.
    /// </summary>
    private int Sustituir(List<Track> nuevas)
    {
        var actuales = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _tracks.Count; i++) actuales.TryAdd(_tracks[i].FilePath, i);

        var altas = new List<Track>();
        var cambiadas = new List<(int Indice, Track Nueva)>();
        var siguen = new HashSet<int>();
        var resultado = new List<Track>(nuevas.Count);
        foreach (var n in nuevas)
        {
            if (actuales.TryGetValue(n.FilePath, out var i) && siguen.Add(i))
            {
                if (MismosDatos(_tracks[i], n)) resultado.Add(_tracks[i]);
                else { cambiadas.Add((i, n)); resultado.Add(n); }
            }
            else { altas.Add(n); resultado.Add(n); }
        }
        var bajas = _tracks.Count - siguen.Count;
        var total = altas.Count + cambiadas.Count + bajas;
        if (total == 0) return 0;

        if (total > UmbralRehacerLista)
        {
            _tracks.Reemplazar(resultado);
            return total;
        }

        foreach (var (i, n) in cambiadas) _tracks[i] = n;
        for (var i = _tracks.Count - 1; i >= 0; i--)
            if (!siguen.Contains(i)) _tracks.RemoveAt(i);
        foreach (var n in altas) _tracks.Add(n);
        return total;
    }

    /// <summary>Lo que se enseña de una canción es igual. La ruta, distinguiendo mayúsculas: renombrar «tema» a «Tema» es un cambio.</summary>
    internal static bool MismosDatos(Track a, Track b)
        => string.Equals(a.FilePath, b.FilePath, StringComparison.Ordinal)
        && string.Equals(a.Folder, b.Folder, StringComparison.Ordinal)
        && a.Title == b.Title && a.Artist == b.Artist && a.Album == b.Album && a.Genre == b.Genre
        && a.Year == b.Year && a.Bpm == b.Bpm && a.Key == b.Key && a.DurationSeconds == b.DurationSeconds
        && a.Bitrate == b.Bitrate && a.SampleRate == b.SampleRate && a.Channels == b.Channels;

    /// <summary>
    /// Publica la biblioteca tal como quedó la última vez, sin leer el disco, para que la aplicación
    /// se pueda usar desde el primer momento. Después hay que llamar a <see cref="VerificarAsync"/>,
    /// que aplica en segundo plano lo que haya cambiado (hallazgo M12).
    ///
    /// Antes el arranque escaneaba con la aplicación bloqueada: MEDIDO en los registros, 7.774 ms
    /// con el disco de giro en frío y 1.039-4.883 ms en caliente, con todas las pestañas en gris.
    ///
    /// No publica nada (devuelve false) si alguna carpeta marcada no tiene NADA en la caché: sería
    /// abrir una biblioteca a la que le falta una carpeta entera, y en ese caso hay que escanear de
    /// la forma de siempre.
    /// </summary>
    public bool CargarDesdeCache()
    {
        if (IsScanning || IsScanned || Tracks.Count > 0) return false;
        var raices = EnabledPaths().OrderBy(r => r.Length).ToList();
        if (raices.Count == 0) return false;

        var guardadas = _cache.Guardadas(ruta => raices.FirstOrDefault(r => Rutas.EstaDentroDe(ruta, r)));
        var conCanciones = new HashSet<string>(guardadas.Select(t => t.Folder), StringComparer.OrdinalIgnoreCase);
        if (raices.Any(r => !conCanciones.Contains(r))) return false;

        _tracks.Reemplazar(guardadas.OrderBy(t => raices.IndexOf(t.Folder)).ThenBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase));
        IsScanned = true;
        Log?.Detail($"Biblioteca: {guardadas.Count} canciones publicadas desde la caché de escaneo; se comprueban los cambios en segundo plano.");
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Qué decir de las carpetas que no se han podido leer, para el aviso de la ventana. "" si se
    /// leyeron todas.
    /// </summary>
    public string AvisoNoDisponibles()
    {
        if (NoDisponibles.Count == 0) return "";
        var n = NoDisponibles[0];
        var nombre = Nombre(n.Raiz);
        var otras = NoDisponibles.Count > 1 ? $" (y {NoDisponibles.Count - 1} carpeta(s) más)" : "";
        var conservadas = NoDisponibles.Sum(x => x.Conservadas);
        return $"No se ha podido leer la carpeta «{nombre}»{otras}: {n.Motivo}. "
             + (conservadas > 0
                ? $"Se mantienen sus {conservadas} canciones de la vez anterior; vuelve a escanear cuando esté disponible."
                : "Sus canciones no aparecen hasta que esté disponible y vuelvas a escanear.");
    }
}

/// <summary>
/// La lista de canciones, con un cambio «en bloque»: sustituir el contenido entero con UN solo
/// aviso a las tablas en lugar de uno por canción.
/// </summary>
internal sealed class ListaPistas : ObservableCollection<Track>
{
    public void Reemplazar(IEnumerable<Track> nuevas)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var t in nuevas) Items.Add(t);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
