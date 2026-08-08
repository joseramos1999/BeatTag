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
        item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FolderItem.Enabled)) Persist(); };
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

    /// <summary>Vacía la caché de escaneo (memoria + archivo), para que el próximo escaneo relea todo.</summary>
    public void ClearScanCache() => _cache.Clear();

    public void AddFolder(string folder) { if (!Has(folder)) { Add(new FolderItem(folder)); Persist(); } }

    public void RemoveFolder(string folder)
    {
        var item = Folders.FirstOrDefault(f => string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase));
        if (item != null) { Folders.Remove(item); Persist(); }
    }

    public void ClearFolders() { Folders.Clear(); Persist(); }

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
