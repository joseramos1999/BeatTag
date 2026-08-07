using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Etiquetador.Core.Pipeline;

/// <summary>
/// Canciones que el usuario ha descartado a mano ("Quitar de la lista"). Se guardan por ruta y se
/// excluyen del análisis y de la carga de la caché, para que no vuelvan a aparecer en Enriquecer.
/// </summary>
public sealed class IgnoreList
{
    private readonly string _file;
    private HashSet<string> _set = new(System.StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    public IgnoreList(string file) { _file = file; Load(); }

    public int Count => _set.Count;

    public bool Contains(string path) => _set.Contains(path);

    public void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_set.Add(path)) _dirty = true;
    }

    public void Remove(string path)
    {
        if (_set.Remove(path)) _dirty = true;
    }

    /// <summary>Olvida las descartadas cuyos archivos ya no existen en la biblioteca.</summary>
    public void Prune(ISet<string> livePaths)
    {
        foreach (var k in _set.Where(k => !livePaths.Contains(k)).ToList()) { _set.Remove(k); _dirty = true; }
    }

    public void Clear()
    {
        _set.Clear();
        _dirty = false;
        try { if (File.Exists(_file)) File.Delete(_file); } catch { }
    }

    public void Save()
    {
        if (!_dirty) return;
        try
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_file, JsonSerializer.Serialize(_set), Encoding.UTF8);
            _dirty = false;
        }
        catch { }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var d = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(_file, Encoding.UTF8));
            if (d != null) _set = new HashSet<string>(d, System.StringComparer.OrdinalIgnoreCase);
        }
        catch { }
    }
}
