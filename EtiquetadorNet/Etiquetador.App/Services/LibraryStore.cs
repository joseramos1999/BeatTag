using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Etiquetador.Core;

namespace Etiquetador.App.Services;

/// <summary>
/// Biblioteca compartida por toda la app: una única lista de carpetas y un único conjunto de
/// tracks escaneados. Se escanea una vez y todas las pestañas consumen los mismos datos.
/// </summary>
public sealed class LibraryStore
{
    private readonly AppConfig _config;
    private readonly Action _saveConfig;
    private readonly ScanCache _cache;

    /// <summary>Log opcional (se asigna desde AppEngine tras construirlo).</summary>
    public Logger? Log { get; set; }

    /// <summary>Carpetas elegidas (con estado marcado/desmarcado). Misma instancia para todas las pestañas.</summary>
    public ObservableCollection<FolderItem> Folders { get; } = new();

    /// <summary>Tracks escaneados (biblioteca en memoria).</summary>
    public ObservableCollection<Track> Tracks { get; } = new();

    public bool IsScanned { get; private set; }
    public bool IsScanning { get; private set; }

    /// <summary>Se dispara tras completar un escaneo (para que cada pestaña recalcule su vista).</summary>
    public event Action? Changed;

    public LibraryStore(AppConfig config, Action saveConfig, ScanCache cache)
    {
        _config = config;
        _saveConfig = saveConfig;
        _cache = cache;
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

        var quitados = 0;
        for (int i = Tracks.Count - 1; i >= 0; i--)
            if (quitar.Contains(Tracks[i].FilePath)) { Tracks.RemoveAt(i); quitados++; }

        if (quitados > 0) Changed?.Invoke();
        return quitados;
    }

    /// <summary>Vacía la caché de escaneo (memoria + archivo), para que el próximo escaneo relea todo.</summary>
    public void ClearScanCache() => _cache.Clear();

    public void AddFolder(string folder)
    {
        if (Has(folder)) return;
        Add(new FolderItem(folder));
        Persist();
        MarcarQueFaltaEscanear();   // sus canciones todavía no están cargadas
    }

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
        // miles de canciones así deja la ventana clavada. Vaciar y volver a poner cuesta un solo
        // aviso más un alta por superviviente, que es lo que ya hace el escaneo.
        //
        // Por eso se elige según el tamaño: para unas pocas bajas sale más barato quitarlas en su
        // sitio; para muchas, rehacer la lista entera.
        if (quitados > UmbralRehacerLista)
        {
            Tracks.Clear();
            foreach (var t in sobreviven) Tracks.Add(t);
        }
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

    private void Persist()
    {
        _config.Folders = Folders.Select(f => f.Path).ToList();
        _config.DisabledFolders = Folders.Where(f => !f.Enabled).Select(f => f.Path).ToList();
        _saveConfig();
    }

    /// <summary>Escanea todas las carpetas hacia <see cref="Tracks"/> y notifica a las pestañas.</summary>
    public async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var roots = EnabledPaths();   // solo las carpetas marcadas
            Log?.Head($"Escaneando {roots.Count} carpeta(s)…");
            foreach (var r in roots) Log?.Detail($"    carpeta: {r}");
            var skipped = Folders.Count - roots.Count;
            if (skipped > 0) Log?.Detail($"    ({skipped} carpeta(s) desmarcada(s) que se omiten)");
            var list = await Task.Run(() =>
            {
                var acc = new List<Track>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in roots)
                    foreach (var path in LibraryScanner.EnumerateFiles(root, recursive: true))
                    {
                        var t = _cache.Read(path);   // caché por fecha+tamaño (rápido si no cambió)
                        t.Folder = root;
                        acc.Add(t);
                        seen.Add(path);
                    }
                _cache.Prune(seen);
                _cache.Save();
                return acc;
            });
            Tracks.Clear();
            foreach (var t in list) Tracks.Add(t);
            IsScanned = true;
            Log?.Sum($"Escaneo terminado: {Tracks.Count} canciones en {sw.ElapsedMilliseconds} ms.");
            Changed?.Invoke();
        }
        catch (Exception e) { Log?.Error("Error al escanear la biblioteca", e); throw; }
        finally { IsScanning = false; }
    }
}
