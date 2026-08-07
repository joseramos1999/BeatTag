using System.Text;
using System.Text.Json;

namespace Etiquetador.Core;

/// <summary>
/// Caché de escaneo: recuerda los tags/propiedades leídos de cada archivo, validados por
/// fecha de modificación + tamaño. En re-escaneos, los archivos que no han cambiado se sirven
/// de la caché sin volver a leerlos con TagLib (mucho más rápido). Se persiste en disco (JSON).
/// </summary>
public sealed class ScanCache
{
    /// <summary>Entrada de caché por archivo (M = mtime ticks, S = tamaño; resto = tags/audio).</summary>
    public sealed class Entry
    {
        public long M { get; set; }
        public long S { get; set; }
        public string? Ti { get; set; }
        public string? Ar { get; set; }
        public string? Al { get; set; }
        public string? Ge { get; set; }
        public uint Yr { get; set; }
        public uint Bp { get; set; }
        public int Du { get; set; }
        public int Br { get; set; }
        public int Sr { get; set; }
        public int Ch { get; set; }
    }

    private readonly string _file;
    private Dictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    public ScanCache(string file)
    {
        _file = file;
        Load();
    }

    /// <summary>Devuelve el Track del archivo, desde caché si coincide fecha+tamaño; si no, lo lee y cachea.</summary>
    public Track Read(string path)
    {
        long m = 0, s = 0;
        try { var fi = new FileInfo(path); m = fi.LastWriteTimeUtc.Ticks; s = fi.Length; } catch { }

        if (_map.TryGetValue(path, out var e) && e.M == m && e.S == s)
            return new Track
            {
                FilePath = path,
                Title = e.Ti, Artist = e.Ar, Album = e.Al, Genre = e.Ge,
                Year = e.Yr, Bpm = e.Bp, DurationSeconds = e.Du,
                Bitrate = e.Br, SampleRate = e.Sr, Channels = e.Ch,
            };

        var t = LibraryScanner.ReadTrack(path);
        _map[path] = new Entry
        {
            M = m, S = s, Ti = t.Title, Ar = t.Artist, Al = t.Album, Ge = t.Genre,
            Yr = t.Year, Bp = t.Bpm, Du = t.DurationSeconds, Br = t.Bitrate, Sr = t.SampleRate, Ch = t.Channels,
        };
        _dirty = true;
        return t;
    }

    /// <summary>Elimina de la caché los archivos que ya no existen en el conjunto dado.</summary>
    public void Prune(ISet<string> livePaths)
    {
        var stale = _map.Keys.Where(k => !livePaths.Contains(k)).ToList();
        foreach (var k in stale) { _map.Remove(k); _dirty = true; }
    }

    /// <summary>Vacía la caché en memoria Y borra el archivo (para "Vaciar caché" en caliente).</summary>
    public void Clear()
    {
        _map.Clear();
        _dirty = false;
        try { if (File.Exists(_file)) File.Delete(_file); } catch { /* best-effort */ }
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
        catch { /* best-effort */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_file, Encoding.UTF8));
            if (d != null) _map = new Dictionary<string, Entry>(d, StringComparer.OrdinalIgnoreCase);
        }
        catch { /* caché corrupta -> se ignora */ }
    }
}
