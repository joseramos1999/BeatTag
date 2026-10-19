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
        public string? Ke { get; set; }
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
                Year = e.Yr, Bpm = e.Bp, Key = e.Ke, DurationSeconds = e.Du,
                Bitrate = e.Br, SampleRate = e.Sr, Channels = e.Ch,
            };

        var t = LibraryScanner.ReadTrack(path);
        _map[path] = new Entry
        {
            M = m, S = s, Ti = t.Title, Ar = t.Artist, Al = t.Album, Ge = t.Genre,
            Yr = t.Year, Bp = t.Bpm, Ke = t.Key, Du = t.DurationSeconds, Br = t.Bitrate, Sr = t.SampleRate, Ch = t.Channels,
        };
        _dirty = true;
        return t;
    }

    /// <summary>
    /// Las canciones tal como quedaron guardadas, SIN ir al disco: ni se recorre la carpeta ni se
    /// mira la fecha de cada archivo. Es lo que permite abrir la aplicación con la biblioteca ya
    /// puesta y comprobar los cambios después, en segundo plano (hallazgo M12). MEDIDO en los
    /// registros: el escaneo de arranque costaba 7.774 ms con la caché y el disco de giro en frío,
    /// con todas las pestañas en gris mientras tanto.
    /// </summary>
    /// <param name="raizDe">
    /// A qué carpeta raíz pertenece cada ruta guardada, o null si a ninguna de las que cuentan: lo
    /// de fuera se queda en la caché pero no sale en la lista.
    /// </param>
    public List<Track> Guardadas(Func<string, string?> raizDe)
    {
        var lista = new List<Track>();
        foreach (var (path, e) in _map)
        {
            var raiz = raizDe(path);
            if (raiz == null) continue;
            lista.Add(new Track
            {
                FilePath = path, Folder = raiz,
                Title = e.Ti, Artist = e.Ar, Album = e.Al, Genre = e.Ge,
                Year = e.Yr, Bpm = e.Bp, Key = e.Ke, DurationSeconds = e.Du,
                Bitrate = e.Br, SampleRate = e.Sr, Channels = e.Ch,
            });
        }
        return lista;
    }

    /// <summary>
    /// Elimina de la caché los archivos que ya no existen DENTRO de las carpetas escaneadas.
    ///
    /// Lo de fuera se conserva. Antes se borraba todo lo que no estuviera en el escaneo, y medido
    /// en una biblioteca real: al dejar la biblioteca un rato con otra carpeta, se tiraron las
    /// 15.000 entradas de la de siempre, y volver a añadirla costó 331 s releyendo cada archivo en
    /// vez de ~4 s. Tampoco se mira si el archivo existe: con un disco externo desconectado todo
    /// parecería borrado.
    /// </summary>
    public void Prune(ISet<string> livePaths, IReadOnlyCollection<string> raices)
    {
        var stale = _map.Keys.Where(k => !livePaths.Contains(k) && raices.Any(r => Rutas.EstaDentroDe(k, r))).ToList();
        foreach (var k in stale) { _map.Remove(k); _dirty = true; }
    }

    /// <summary>Vacía la caché en memoria Y borra el archivo (para "Vaciar caché" en caliente).</summary>
    public void Clear()
    {
        _map.Clear();
        _dirty = false;
        try { if (File.Exists(_file)) File.Delete(_file); } catch { /* best-effort */ }
    }

    /// <summary>"" si se cargó con normalidad; si no, qué pasó con el archivo (apartado por ilegible…).</summary>
    public string AvisoCarga { get; private set; } = "";

    // El archivo existe pero no se pudo leer ni apartar: no se guarda encima.
    private bool _noSobrescribir;

    /// <summary>
    /// Guarda si hubo cambios, de forma atómica (temporal + sustitución). scan-cache.json pesa unos
    /// 4 MB, y MEDIDO: releer la biblioteca sin caché costó 340.680 ms para 13.464 canciones frente
    /// a 7.774 ms con ella; un corte a media escritura no puede tirarla. Devuelve "" si fue bien o
    /// no había nada que guardar, o el motivo del fallo.
    /// </summary>
    public string Save()
    {
        if (!_dirty) return "";
        if (_noSobrescribir) return AvisoCarga;
        var error = ArchivoDatos.EscribirJson(_file, _map);
        if (error.Length == 0) _dirty = false;
        return error;
    }

    private void Load()
    {
        // Una caché dañada se aparta como «.ilegible_fecha» en vez de ignorarse y pisarse.
        var leido = ArchivoDatos.LeerJson<Dictionary<string, Entry>>(_file);
        AvisoCarga = leido.Aviso;
        _noSobrescribir = leido.NoSobrescribir;
        if (leido.Datos != null) _map = new Dictionary<string, Entry>(leido.Datos, StringComparer.OrdinalIgnoreCase);
    }
}
