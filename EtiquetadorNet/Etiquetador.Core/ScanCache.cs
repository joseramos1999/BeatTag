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

        /// <summary>
        /// Un AIFF ya leído con el lector de cabecera (<see cref="LectorAiff"/>), saliera o no su
        /// frecuencia. Uno dañado, o que no es AIFF de verdad, sigue con frecuencia 0: sin esta marca
        /// se releía en cada escaneo y obligaba a reescribir entera la caché (15-20k entradas). Las
        /// guardadas antes de ese lector no la llevan, y esas sí se releen, una vez. Solo se escribe
        /// en los AIFF, para no engordar el archivo.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public bool La { get; set; }

        /// <summary>
        /// Un BPM de más de <see cref="BpmAltoDudoso"/> leído ya con <see cref="BpmEtiqueta"/>, sin la
        /// configuración regional: un tempo alto de verdad (speedcore, por ejemplo). En las entradas
        /// guardadas antes de ese lector, un BPM así puede ser una etiqueta con un decimal mal leída
        /// («95.5» quedaba como 955) que no llega a <see cref="BpmEtiqueta.Maximo"/>: esas no la llevan
        /// y se releen, una vez. Sin la marca, un tempo alto de verdad se releería en cada escaneo.
        /// Solo se escribe en las de BPM alto, para no engordar el archivo.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public bool Bi { get; set; }
    }

    /// <summary>
    /// Un BPM guardado por encima de esto y sin <see cref="Entry.Bi"/> no se da por bueno sin releerlo.
    ///
    /// POR QUÉ. Cuando TagLib leía el BPM con la configuración regional (ver <see cref="BpmEtiqueta"/>),
    /// si el decimal de la etiqueta era el separador de miles del sistema (el punto en es-ES, de-DE,
    /// it-IT o pt-BR; la coma, con «95,5», en en-US), el decimal multiplicaba el tempo por 10: «95.5»
    /// quedaba como 955, «90.0» como 900 y «85.5» como 855, tempos habituales en reggaeton y dembow.
    /// Con dos o más decimales ya pasaba de 999 y se releía, pero con uno, un tempo de más de 25 y
    /// menos de 100 BPM daba entre 251 y 999, y la caché lo seguía sirviendo mientras no cambiaran la
    /// fecha ni el tamaño del archivo. Un tempo real casi nunca pasa de 250, y el que sí (speedcore)
    /// se relee una sola vez y queda marcado. MEDIDO en una biblioteca real (14.477 entradas): ninguna
    /// entre 251 y 999 y 3 por encima de 999, así que allí no se relee nada de más.
    /// </summary>
    private const uint BpmAltoDudoso = 250;

    // Un BPM alto que no pasó por el lector invariable: puede ser un texto mal leído (ver BpmAltoDudoso).
    private static bool BpmDudoso(Entry e) => e.Bp > BpmAltoDudoso && !e.Bi;

    private readonly string _file;
    private Dictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    // Organizar por género mueve archivos desde un hilo de fondo (Mover) mientras la biblioteca puede
    // estar leyendo o guardando la caché en otro: todo lo que toca el mapa pasa por aquí.
    private readonly object _cerrojo = new();

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

        // Un AIFF guardado sin frecuencia y sin la marca del lector de cabecera es uno que TagLib no pudo
        // abrir (un AIFF-C de macOS) antes de que el escáner supiera leer su cabecera: se vuelve a leer
        // en lugar de seguir vacío. Con la marca ya se intentó, y releerlo no daría otra cosa.
        // Un BPM alto sin la marca del lector invariable puede ser de cuando el BPM se leía con la
        // configuración regional («102.000000» quedaba como 102.000.000 y «95.5» como 955; ver
        // BpmAltoDudoso): se relee una vez y la entrada se corrige, o se marca si el tempo es de verdad.
        Entry? e;
        lock (_cerrojo) _map.TryGetValue(path, out e);
        if (e != null && e.M == m && e.S == s && !(e.Sr == 0 && !e.La && LectorAiff.EsAiff(path))
            && !BpmDudoso(e))
            return new Track
            {
                FilePath = path,
                Title = e.Ti, Artist = e.Ar, Album = e.Al, Genre = e.Ge,
                Year = e.Yr, Bpm = e.Bp, Key = e.Ke, DurationSeconds = e.Du,
                Bitrate = e.Br, SampleRate = e.Sr, Channels = e.Ch,
            };

        // Leer el archivo, fuera del cerrojo: es lo que tarda.
        var t = LibraryScanner.ReadTrack(path);
        var nueva = new Entry
        {
            M = m, S = s, Ti = t.Title, Ar = t.Artist, Al = t.Album, Ge = t.Genre,
            Yr = t.Year, Bp = t.Bpm, Ke = t.Key, Du = t.DurationSeconds, Br = t.Bitrate, Sr = t.SampleRate, Ch = t.Channels,
            La = LectorAiff.EsAiff(path),   // ReadTrack ya prueba el lector de cabecera con los AIFF
            Bi = t.Bpm > BpmAltoDudoso,     // y lee el BPM con BpmEtiqueta: un BPM alto es de verdad
        };
        lock (_cerrojo)
        {
            _map[path] = nueva;
            _dirty = true;
        }
        return t;
    }

    /// <summary>
    /// La entrada de un archivo que la aplicación acaba de mover (Organizar por género) pasa a su ruta
    /// nueva, con su fecha y su tamaño: mover un archivo dentro de la misma unidad no cambia ninguno de
    /// los dos, así que sigue valiendo. Devuelve si había entrada.
    ///
    /// POR QUÉ. Sin esto, el escaneo de después veía 10.896 archivos nuevos y volvía a leerlos con
    /// TagLib: MEDIDO en una biblioteca real, releerla entera costó 340.680 ms frente a 7.774 ms con la caché.
    /// </summary>
    public bool Mover(string origen, string destino)
    {
        if (string.Equals(origen, destino, StringComparison.Ordinal)) return false;
        lock (_cerrojo)
        {
            if (!_map.Remove(origen, out var e)) return false;
            _map[destino] = e;
            _dirty = true;
            return true;
        }
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
        lock (_cerrojo)
        {
            foreach (var (path, e) in _map)
            {
                var raiz = raizDe(path);
                if (raiz == null) continue;
                // Un BPM que puede estar mal leído (955 por «95.5») no se enseña mientras llega la
                // comprobación, que lo relee (ver Read y BpmAltoDudoso).
                lista.Add(new Track
                {
                    FilePath = path, Folder = raiz,
                    Title = e.Ti, Artist = e.Ar, Album = e.Al, Genre = e.Ge,
                    Year = e.Yr, Bpm = BpmDudoso(e) ? 0 : e.Bp, Key = e.Ke, DurationSeconds = e.Du,
                    Bitrate = e.Br, SampleRate = e.Sr, Channels = e.Ch,
                });
            }
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
        lock (_cerrojo)
        {
            var stale = _map.Keys.Where(k => !livePaths.Contains(k) && raices.Any(r => Rutas.EstaDentroDe(k, r))).ToList();
            foreach (var k in stale) { _map.Remove(k); _dirty = true; }
        }
    }

    /// <summary>Vacía la caché en memoria Y borra el archivo (para "Vaciar caché" en caliente).</summary>
    public void Clear()
    {
        lock (_cerrojo)
        {
            _map.Clear();
            _dirty = false;
            try { if (File.Exists(_file)) File.Delete(_file); } catch { /* best-effort */ }
        }
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
        lock (_cerrojo)
        {
            if (!_dirty) return "";
            if (_noSobrescribir && !Releer()) return AvisoCarga;
            var error = ArchivoDatos.EscribirJson(_file, _map);
            if (error.Length == 0) _dirty = false;
            return error;
        }
    }

    /// <summary>
    /// Si al arrancar el archivo no se pudo leer (bloqueado un momento), se vuelve a intentar al
    /// guardar y se junta lo guardado con lo de esta sesión, que manda. Antes no se reintentaba
    /// nunca y la caché de la sesión se perdía al cerrar (T10).
    /// </summary>
    private bool Releer()
    {
        var leido = ArchivoDatos.LeerJson<Dictionary<string, Entry>>(_file);
        AvisoCarga = leido.Aviso;
        if (leido.NoSobrescribir) return false;
        if (leido.Datos != null)
            foreach (var (ruta, entrada) in leido.Datos) _map.TryAdd(ruta, entrada);
        _noSobrescribir = false;
        return true;
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
