using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;
using NAudio.Wave;

namespace Etiquetador.App.Services;

/// <summary>
/// Mide la sonoridad (EBU R128) de los archivos de la biblioteca. Decodifica el audio completo,
/// así que se cachea por archivo (fecha+tamaño) y se reparte entre varios núcleos: medido en la
/// biblioteca real, ~440x tiempo real por hilo.
///
/// Solo MIDE: no toca ningún archivo.
/// </summary>
public sealed class LoudnessScanner
{
    /// <summary>
    /// Versión de la medida que se guarda ahora: 2 = con pico real (dBTP) y con los archivos mono
    /// medidos como mono doble. Las medidas sin versión son de BeatTag 1.10 o anterior: su sonoridad
    /// sigue valiendo, salvo la de los archivos mono (ver <see cref="MonoPorVolverAMedir"/>).
    /// </summary>
    public const int VersionActual = 2;

    /// <summary>
    /// Un pico de muestra a partir de aquí es el tope del decodificador (16 bits): la señal estaba
    /// recortada y su pico de verdad se desconoce.
    /// </summary>
    internal const double PicoRecortado = -0.01;

    /// <summary>
    /// Lo que se guarda de cada archivo en sonoridad.json. T, V y G son nuevos y opcionales: las
    /// entradas antiguas se leen sin ellos, y una versión anterior de BeatTag los ignora. No se
    /// escriben cuando no hay valor, para no engordar el archivo con nulos.
    /// </summary>
    private sealed class Entry
    {
        public long M { get; set; }
        public long S { get; set; }
        public double Lufs { get; set; }
        public double Peak { get; set; }

        /// <summary>Pico real (dBTP), o null si no se conoce.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? T { get; set; }

        /// <summary>Versión de la medida (<see cref="VersionActual"/>), o null si es anterior.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? V { get; set; }

        /// <summary>
        /// Ganancia que BeatTag ha aplicado al archivo desde que se midió su audio original (dB), o
        /// null si ninguna. Restándola de Lufs sale el nivel original, que es con el que se calcula
        /// la referencia automática (ver <see cref="ReferenciaVolumen"/>): sin ella, cada «Ajustar»
        /// movía la referencia y el siguiente volvía a tocar las mismas canciones.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? G { get; set; }
    }

    private readonly string _file;
    private readonly Logger? _log;
    private Dictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _dirty;

    /// <summary>
    /// El archivo existe pero no se pudo leer ni apartar (bloqueado por otro programa, sin
    /// permiso). No se guarda encima: perderíamos unas mediciones que quizá están bien.
    /// </summary>
    private bool _noSobrescribir;

    public LoudnessScanner(string cacheFile, Logger? log = null)
    {
        _file = cacheFile;
        _log = log;
        Load();
    }

    /// <summary>Medida cacheada si el archivo no ha cambiado; si no, null.</summary>
    public LoudnessResult? Get(string path)
    {
        if (!Stat(path, out var m, out var s)) return null;
        lock (_lock)
            if (_map.TryGetValue(path, out var e) && e.M == m && e.S == s)
                return new LoudnessResult(e.Lufs, e.Peak, 0, e.T, e.G ?? 0);
        return null;
    }

    /// <summary>
    /// Actualiza la medida tras haber cambiado el volumen del archivo, sin volver a analizarlo:
    /// una ganancia conocida desplaza el nivel y el pico en la misma cantidad. La versión de la
    /// medida no cambia (la sonoridad de un mono sigue medida como estaba), y la ganancia aplicada
    /// tampoco: para sumarle la de un ajuste está <see cref="Ajustado"/>.
    /// </summary>
    public void Update(string path, double lufs, double peak, double? truePeak = null)
        => Guardar(path, lufs, peak, truePeak, 0);

    private void Guardar(string path, double lufs, double peak, double? truePeak, double db)
    {
        if (!Stat(path, out var m, out var s)) return;
        lock (_lock)
        {
            _map.TryGetValue(path, out var anterior);
            _map[path] = new Entry
            {
                M = m, S = s, Lufs = lufs, Peak = peak, T = Finito(truePeak), V = anterior?.V,
                G = SinCero((anterior?.G ?? 0) + db),
            };
            _dirty = true;
        }
    }

    /// <summary>
    /// La medida tras aplicar <paramref name="db"/> de ganancia a un archivo que medía
    /// <paramref name="lufs"/>, con pico de muestra <paramref name="pico"/> y pico real
    /// <paramref name="picoReal"/> (ver <see cref="TrasAjustar"/>). La ganancia se suma a la ya
    /// aplicada, para poder calcular siempre con el nivel original.
    /// </summary>
    public void Ajustado(string path, double lufs, double pico, double? picoReal, double db)
    {
        var (l, p, t) = TrasAjustar(lufs, pico, picoReal, db);
        Guardar(path, l, p, t, db);
    }

    /// <summary>Lo que mueve de verdad un paso de global_gain: 2^(1/4) en amplitud, 5·log10(2) dB.</summary>
    private static readonly double PasoReal = 5 * Math.Log10(2);

    /// <summary>
    /// Diferencia de nivel por debajo de la cual dos medidas son del mismo audio. Medir dos veces el
    /// mismo archivo da el mismo valor; dos copias o versiones distintas de una canción rara vez
    /// quedan a menos de una décima de un número exacto de pasos.
    /// </summary>
    internal const double ToleranciaMismoAudio = 0.1;

    /// <summary>
    /// La ganancia aplicada que sigue valiendo tras volver a medir un archivo que la tenía. Se vuelve
    /// a medir cuando se pide («Analizar de nuevo», el pico real) o cuando el archivo ha cambiado de
    /// fecha o tamaño, y eso pasa también al escribirle etiquetas con otro programa o al deshacer un
    /// ajuste. Lo que cuenta es el audio:
    ///   · mismo nivel que el guardado (a 0,1 dB): el audio no ha cambiado y la ganancia sigue igual;
    ///   · un número exacto de pasos de diferencia: se ha cambiado su global_gain (el deshacer de
    ///     BeatTag, o MP3Gain), y esos pasos se suman a la ganancia;
    ///   · cualquier otra diferencia: es otro audio (otra copia, otra versión), y su nivel original es
    ///     el que se acaba de medir.
    /// Una medida de un mono anterior al mono doble se compara con lo que habría medido como mono
    /// doble (+3,01 dB), para no tomar esa diferencia por dos pasos.
    /// </summary>
    /// <param name="aplicada">Ganancia guardada (null si ninguna).</param>
    /// <param name="version">Versión de la medida guardada.</param>
    /// <param name="lufsGuardado">Nivel guardado.</param>
    /// <param name="lufsMedido">Nivel recién medido.</param>
    /// <param name="canales">Canales del archivo recién medido.</param>
    internal static double? GananciaTrasMedir(double? aplicada, int? version, double lufsGuardado, double lufsMedido, int canales)
    {
        if (aplicada is not { } g || !double.IsFinite(lufsGuardado) || !double.IsFinite(lufsMedido)) return null;
        if (MonoSinDoble(version, canales)) lufsGuardado += 10 * Math.Log10(2);
        var diferencia = lufsMedido - lufsGuardado;
        var pasos = Math.Round(diferencia / PasoReal);
        if (Math.Abs(diferencia - pasos * PasoReal) > ToleranciaMismoAudio) return null;
        return SinCero(g + pasos * Mp3Gain.DbPerStep);
    }

    private static double? SinCero(double g) => !double.IsFinite(g) || Math.Abs(g) < 1e-6 ? null : g;

    /// <summary>
    /// Lo que se guarda tras ajustar sin volver a medir. El nivel se desplaza exactamente la ganancia
    /// aplicada, y los picos también, porque el audio decodificado escala igual... salvo si estaba
    /// recortado: con el pico de muestra en el tope del decodificador (≥ −0,01 dBFS) el pico de verdad
    /// se desconoce. MEDIDO: el 89 % de las canciones ajustadas por BeatTag tenía el pico recortado, y
    /// el pico guardado tras ajustar se quedaba corto hasta 4,5 dB. En ese caso se guarda el pico en
    /// el tope y sin pico real, que es lo conservador: no deja subir esa canción sin volver a medirla.
    /// </summary>
    internal static (double Lufs, double Pico, double? PicoReal) TrasAjustar(double lufs, double pico, double? picoReal, double db)
        => pico >= PicoRecortado
            ? (lufs + db, 0.0, null)
            : (lufs + db, pico + db, picoReal + db);

    /// <summary>Versión de la medida guardada (null si no hay, o es anterior a las versiones).</summary>
    internal int? VersionDe(string path)
    {
        lock (_lock) return _map.TryGetValue(path, out var e) ? e.V : null;
    }

    /// <summary>
    /// Pistas mono cuya medida guardada es anterior al mono doble: hay que volver a medirlas, y
    /// solo a ellas. Su sonoridad se midió 3,01 LU por debajo de cómo suenan. Las demás medidas
    /// antiguas siguen valiendo.
    /// </summary>
    public HashSet<string> MonoPorVolverAMedir(IEnumerable<(string Ruta, int Canales)> pistas)
    {
        var r = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
            foreach (var (ruta, canales) in pistas)
                if (_map.TryGetValue(ruta, out var e) && MonoSinDoble(e.V, canales)) r.Add(ruta);
        return r;
    }

    /// <summary>La regla de <see cref="MonoPorVolverAMedir"/>: un mono medido antes de la versión 2.</summary>
    internal static bool MonoSinDoble(int? version, int canales) => canales == 1 && (version ?? 1) < VersionActual;

    private static double? Finito(double? v) => v is { } x && double.IsFinite(x) ? x : null;

    /// <summary>Mide un archivo (o devuelve lo cacheado). Devuelve null si no se pudo leer.</summary>
    public LoudnessResult? Measure(string path, bool force = false, CancellationToken ct = default)
    {
        if (!force)
        {
            var cached = Get(path);
            if (cached != null) return cached;
        }

        try
        {
            // Decodifica a float -1..1. Va por AudioSamples y no por AudioFileReader directamente
            // para que también funcione fuera de Windows: NAudio decodifica MP3 con códecs del
            // sistema, y en macOS no los hay.
            var reader = AudioSamples.Abrir(path, out var recurso);
            using var _ = recurso;
            var fmt = reader.WaveFormat;
            var meter = new LoudnessMeter(fmt.SampleRate, fmt.Channels);
            // El pico real va en la misma pasada y sobre el mismo búfer: no hay que decodificar dos veces.
            var picoReal = new TruePeakMeter(fmt.Channels);
            var buf = new float[fmt.SampleRate * fmt.Channels / 4];   // ~250 ms
            int n;
            while ((n = reader.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                meter.Add(buf, n);
                picoReal.Add(buf, n);
            }

            var r = meter.GetResult();
            if (!r.Ok) return r;   // silencio: se devuelve pero no se cachea
            r = r with { TruePeakDb = Finito(picoReal.Db) };

            if (Stat(path, out var m, out var s))
            {
                lock (_lock)
                {
                    // La ganancia que BeatTag ya le había aplicado sigue valiendo si el audio es el mismo.
                    var g = _map.TryGetValue(path, out var anterior)
                        ? GananciaTrasMedir(anterior.G, anterior.V, anterior.Lufs, r.Lufs, fmt.Channels)
                        : null;
                    _map[path] = new Entry { M = m, S = s, Lufs = r.Lufs, Peak = r.PeakDb, T = r.TruePeakDb, V = VersionActual, G = g };
                    _dirty = true;
                    r = r with { GananciaAplicada = g ?? 0 };
                }
            }
            return r;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            _log?.Detail($"    sonoridad: no se pudo medir '{Path.GetFileName(path)}': {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Mide muchos archivos repartiendo el trabajo entre núcleos. Con <paramref name="force"/> se
    /// vuelven a medir todos; <paramref name="forzar"/> vuelve a medir solo esos (por ejemplo, los
    /// mono medidos antes del mono doble) y aprovecha lo guardado del resto.
    /// </summary>
    public async Task MeasureManyAsync(IReadOnlyList<string> paths, bool force,
        IProgress<(int done, int total, string file)>? progress, CancellationToken ct = default,
        IReadOnlySet<string>? forzar = null)
    {
        var hechos = 0;
        await Task.Run(() =>
        {
            var opts = new ParallelOptions
            {
                CancellationToken = ct,
                // Se deja un núcleo libre para que la interfaz siga fluida.
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
            };
            Parallel.ForEach(paths, opts, p =>
            {
                Measure(p, force || (forzar?.Contains(p) ?? false), ct);
                var n = Interlocked.Increment(ref hechos);
                if (n % 10 == 0 || n == paths.Count) progress?.Report((n, paths.Count, Path.GetFileName(p)));
            });
        }, ct).ConfigureAwait(false);
        Save();
    }

    /// <summary>
    /// Guarda las mediciones. Se escribe a un temporal y se intercambia de una vez
    /// (<see cref="ArchivoDatos.EscribirJson"/>), como las demás cachés.
    ///
    /// Antes se escribía directamente encima de sonoridad.json (3,3 MB, 17.000 entradas en una
    /// biblioteca real). Un cierre o un corte de luz a mitad lo dejaba truncado, el arranque
    /// siguiente lo leía como vacío sin decir nada y el primer guardado lo pisaba: había que volver
    /// a decodificar la biblioteca entera, decenas de minutos con todos los núcleos.
    /// </summary>
    public void Save()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            if (_noSobrescribir)
            {
                _log?.Err("Volumen: sonoridad.json no se pudo leer al arrancar, así que las mediciones nuevas no se guardan encima.");
                return;
            }
            var error = ArchivoDatos.EscribirJson(_file, _map);
            if (error.Length == 0) _dirty = false;
            else _log?.Err("Volumen: no se pudieron guardar las mediciones: " + error);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _dirty = false;
            try { if (File.Exists(_file)) File.Delete(_file); } catch { }
        }
    }

    /// <summary>
    /// Lee las mediciones guardadas. Si el archivo está dañado se aparta como
    /// «sonoridad.json.ilegible_fecha» y se anota en el registro, en vez de tratarlo como vacío en
    /// silencio y pisarlo en el siguiente guardado.
    /// </summary>
    private void Load()
    {
        var leido = ArchivoDatos.LeerJson<Dictionary<string, Entry>>(_file);
        if (leido.Aviso.Length > 0) _log?.Err("Volumen: " + leido.Aviso);
        _noSobrescribir = leido.NoSobrescribir;
        if (leido.Datos != null) _map = new Dictionary<string, Entry>(leido.Datos, StringComparer.OrdinalIgnoreCase);
    }

    private static bool Stat(string path, out long m, out long s)
    {
        m = 0; s = 0;
        try { var fi = new FileInfo(path); if (!fi.Exists) return false; m = fi.LastWriteTimeUtc.Ticks; s = fi.Length; return true; }
        catch { return false; }
    }

    // --- Seguir al archivo cuando lo cambia la propia aplicación (hallazgo A2) ---
    //
    // La medida se valida por ruta, fecha y tamaño. MEDIDO en sonoridad.json (solo lectura): de
    // 17.313 medidas solo seguían valiendo 11.357. Renombrar o escribir etiquetas no cambia el audio
    // y no debería obligar a decodificar la canción entera otra vez. El ajuste de volumen sí lo
    // cambia: ahí no se usa esto, sino Update con la ganancia aplicada.

    /// <summary>
    /// La medida acompaña al archivo que la aplicación acaba de renombrar o mover (se llama DESPUÉS
    /// de moverlo). Devuelve si había algo que mover.
    /// </summary>
    public bool Mover(string origen, string destino)
    {
        if (string.IsNullOrWhiteSpace(origen) || string.IsNullOrWhiteSpace(destino)) return false;
        if (string.Equals(origen, destino, StringComparison.Ordinal)) return false;
        lock (_lock)
        {
            if (!_map.Remove(origen, out var e)) return false;
            _map[destino] = e;
            _dirty = true;
            return true;
        }
    }

    /// <summary>
    /// La aplicación acaba de escribir SOLO etiquetas en el archivo. Si la medida valía justo antes
    /// (fecha y tamaño <paramref name="mAntes"/> y <paramref name="sAntes"/>), se pone al día con
    /// los de ahora; si ya no valía, se deja. Devuelve si la puso al día.
    /// </summary>
    public bool Retocar(string ruta, long mAntes, long sAntes)
    {
        if (!Stat(ruta, out var m, out var s)) return false;
        lock (_lock)
        {
            if (!_map.TryGetValue(ruta, out var e) || e.M != mAntes || e.S != sAntes) return false;
            if (e.M == m && e.S == s) return true;   // no se llegó a escribir: nada que guardar
            e.M = m;
            e.S = s;
            _dirty = true;
            return true;
        }
    }
}
