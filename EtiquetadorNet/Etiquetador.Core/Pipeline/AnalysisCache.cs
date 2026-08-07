using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Etiquetador.Core.Pipeline;

/// <summary>
/// Caché persistente del RESULTADO del análisis (ProcessResult) por archivo, validada por
/// fecha+tamaño y por la firma de opciones. Evita reprocesar (red incluida) lo que no ha cambiado;
/// solo se rehace lo nuevo/modificado o cuando el usuario fuerza el reanálisis.
/// </summary>
public sealed class AnalysisCache
{
    private sealed class Entry
    {
        public long M { get; set; }
        public long S { get; set; }
        public string Sig { get; set; } = "";
        public ProcessResult R { get; set; } = new();
    }

    private readonly string _file;
    private Dictionary<string, Entry> _map = new(System.StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    public AnalysisCache(string file) { _file = file; Load(); }

    /// <summary>Resultado cacheado si el archivo no ha cambiado y la firma de opciones coincide; si no, null.</summary>
    public ProcessResult? Get(string path, string sig)
    {
        if (!Stat(path, out var m, out var s)) return null;
        if (_map.TryGetValue(path, out var e) && e.M == m && e.S == s && e.Sig == sig) return e.R;
        return null;
    }

    public void Set(string path, string sig, ProcessResult r)
    {
        if (!Stat(path, out var m, out var s)) return;
        _map[path] = new Entry { M = m, S = s, Sig = sig, R = r };
        _dirty = true;
    }

    /// <summary>Olvida el resultado de un archivo concreto (al descartarlo a mano).</summary>
    public void Remove(string path)
    {
        if (_map.Remove(path)) _dirty = true;
    }

    public void Prune(ISet<string> livePaths)
    {
        foreach (var k in _map.Keys.Where(k => !livePaths.Contains(k)).ToList()) { _map.Remove(k); _dirty = true; }
    }

    public void Clear()
    {
        _map.Clear();
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
            File.WriteAllText(_file, JsonSerializer.Serialize(_map), Encoding.UTF8);
            _dirty = false;
        }
        catch { }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_file, Encoding.UTF8));
            if (d != null) _map = new Dictionary<string, Entry>(d, System.StringComparer.OrdinalIgnoreCase);
        }
        catch { }
    }

    private static bool Stat(string path, out long m, out long s)
    {
        m = 0; s = 0;
        try { var fi = new FileInfo(path); if (!fi.Exists) return false; m = fi.LastWriteTimeUtc.Ticks; s = fi.Length; return true; }
        catch { return false; }
    }
}
