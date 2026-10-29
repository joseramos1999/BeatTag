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
        /// null si nunca lo ha ajustado. Restándola de Lufs sale el nivel original, que es con el que
        /// se calcula la referencia automática (ver <see cref="ReferenciaVolumen"/>): sin ella, cada
        /// «Ajustar» movía la referencia y el siguiente volvía a tocar las mismas canciones. Un 0 no
        /// es lo mismo que null: es un archivo ajustado cuyos cambios se han compensado (ver
        /// <see cref="SumarGanancia"/>).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? G { get; set; }
    }

    /// <summary>
    /// Lo que se guarda de cada archivo ajustado en volumen-aplicado.json (ver
    /// <see cref="_archivoAplicada"/>): la ganancia aplicada y el nivel y la versión de la medida con
    /// la que se anotó, que es lo que necesita <see cref="GananciaTrasMedir"/> para conservarla al
    /// volver a medir.
    /// </summary>
    private sealed class Aplicada
    {
        public double G { get; set; }
        public double Lufs { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? V { get; set; }
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

    // --- La ganancia aplicada, también fuera de la caché ---
    //
    // sonoridad.json es una caché: no entra en la copia de seguridad y «Limpiar carpeta de datos» la
    // borra. Las medidas se regeneran analizando; la ganancia que BeatTag ya ha aplicado (G), no. Sin
    // ella, la referencia se calcula con el nivel ya ajustado: en el caso de
    // ReferenciaVolumenTests.Una_grabacion_ajustada_cuenta_con_su_nivel_original pasa de −12 a −15 y
    // el siguiente «Ajustar volumen» vuelve a bajar 3 dB lo que ya se había bajado. Por eso cada
    // entrada con G se copia en un archivo de trabajo aparte, pequeño (solo lo ajustado), que sí entra
    // en la copia y que «Limpiar» no toca. Al arrancar, lo que falte en sonoridad.json se recupera de
    // ahí como una medida que hay que repetir: al volver a analizar, la ganancia se conserva si el
    // audio es el mismo, como con cualquier otra medida caducada.

    /// <summary>volumen-aplicado.json, o null si no se lleva (las pruebas que no lo necesitan).</summary>
    private readonly string? _archivoAplicada;

    /// <summary>Lo último escrito en <see cref="_archivoAplicada"/>, para no reescribirlo si no cambia.</summary>
    private string? _aplicadaEscrita;

    /// <summary>
    /// <see cref="_archivoAplicada"/> refleja lo que hay en memoria. Empieza en false para que el
    /// primer guardado lo escriba si hace falta, y vuelve a false si una escritura falla, para
    /// reintentarla en el siguiente guardado aunque no haya cambiado nada más.
    /// </summary>
    private bool _aplicadaAlDia;

    /// <summary>Como <see cref="_noSobrescribir"/>, para <see cref="_archivoAplicada"/>.</summary>
    private bool _aplicadaNoSobrescribir;

    /// <param name="cacheFile">sonoridad.json: las medidas, que se regeneran.</param>
    /// <param name="log">Registro, si lo hay.</param>
    /// <param name="archivoAplicada">volumen-aplicado.json: la ganancia aplicada, que no se regenera.</param>
    public LoudnessScanner(string cacheFile, Logger? log = null, string? archivoAplicada = null)
    {
        _file = cacheFile;
        _log = log;
        _archivoAplicada = archivoAplicada;
        Load();
    }

    /// <summary>
    /// Para las pruebas: se llama al empezar cada <see cref="Get"/>, en el hilo que lo hace. Cada Get
    /// mira la fecha y el tamaño del archivo, y con 20.000 filas eso bloquea 0,2-0,3 s el hilo de la
    /// interfaz (ver LoudnessViewModel.MedidasGuardadasAsync).
    /// </summary>
    internal Action? AlConsultar { get; set; }

    /// <summary>Medida cacheada si el archivo no ha cambiado; si no, null.</summary>
    public LoudnessResult? Get(string path)
    {
        AlConsultar?.Invoke();
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
                G = SumarGanancia(anterior?.G, db),
            };
            _dirty = true;
        }
    }

    /// <summary>
    /// La ganancia aplicada tras sumarle <paramref name="db"/>. Null es «nunca ajustada», y solo sigue
    /// siéndolo si no se suma nada; una que vuelve a 0 se guarda como 0.
    ///
    /// POR QUÉ. Antes un 0 se guardaba como null, igual que la de un archivo sin tocar. Caso visto en
    /// revisión: el modo automático baja un archivo 1 paso (G −1,5), «Nivel de club» lo sube 1 paso
    /// (G 0, guardada como null) y «Deshacer última» deshace solo la subida. El archivo queda 1,5 dB
    /// por debajo de su original, pero al volver a medirlo no había ganancia que seguir
    /// (<see cref="GananciaTrasMedir"/> parte de la guardada) y su nivel ya ajustado contaba como el
    /// original: en una biblioteca simulada de 2.000 grabaciones, 178 la perdían, la referencia bajaba
    /// 0,2 dB y la siguiente pasada volvía a tocar 91 archivos.
    /// </summary>
    internal static double? SumarGanancia(double? aplicada, double db)
    {
        if (!double.IsFinite(db)) db = 0;
        if (aplicada is { } g && double.IsFinite(g)) return g + db;
        return db == 0 ? null : db;
    }

    /// <summary>
    /// La aplicación acaba de cambiar el volumen del archivo <paramref name="db"/> dB por su cuenta:
    /// el deshacer de un ajuste, que aplica los pasos inversos. Si la medida valía justo antes (fecha y
    /// tamaño <paramref name="mAntes"/> y <paramref name="sAntes"/>), se desplaza como al ajustar (ver
    /// <see cref="TrasAjustar"/>) y la ganancia aplicada se pone al día con los pasos que se conocen,
    /// sin depender de volver a medir ni de la tolerancia de <see cref="ToleranciaMismoAudio"/>. Si ya
    /// no valía, se deja caducada: al volver a medirla se decide con el audio. Devuelve si la puso al día.
    ///
    /// A la ganancia aplicada solo se suma <paramref name="dbAnotados"/>: lo que deshace ajustes que la
    /// anotaron. BeatTag 1.10 ajustaba sin anotarla (dejaba la medida desplazada y sin G). Caso visto en
    /// revisión: un MP3 a −10,76 LUFS bajado 2 pasos con 1.10 y deshecho después de actualizar quedaba
    /// con +3 dB «aplicados» y un nivel original calculado de −13,76; el siguiente «Ajustar» automático
    /// partía de una referencia equivocada, y el dato se guardaba en volumen-aplicado.json.
    ///
    /// Con la medida caducada pasaba lo mismo por otro camino: al volver a medir,
    /// <see cref="GananciaTrasMedir"/> suma a la ganancia todos los pasos de diferencia con lo guardado.
    /// Caduca si otro programa escribe el archivo, tras «Limpiar carpeta de datos» o al recuperar la
    /// ganancia de volumen-aplicado.json. Por eso, si la entrada tiene ganancia, la parte no anotada se
    /// suma también a su nivel guardado, sin tocar la fecha ni el tamaño: así solo cuentan los pasos
    /// anotados y los cambios ajenos.
    /// </summary>
    /// <param name="ruta">El archivo.</param>
    /// <param name="mAntes">Fecha de modificación justo antes del deshacer.</param>
    /// <param name="sAntes">Tamaño justo antes del deshacer.</param>
    /// <param name="db">Lo que ha cambiado el volumen: desplaza la medida.</param>
    /// <param name="dbAnotados">La parte de <paramref name="db"/> que deshace ajustes anotados en la ganancia aplicada.</param>
    public bool Reajustar(string ruta, long mAntes, long sAntes, double db, double dbAnotados)
    {
        if (!double.IsFinite(db) || db == 0 || !Stat(ruta, out var m, out var s)) return false;
        if (!double.IsFinite(dbAnotados)) dbAnotados = 0;
        lock (_lock)
        {
            if (!_map.TryGetValue(ruta, out var e)) return false;
            if (e.M != mAntes || e.S != sAntes)
            {
                // Caducada: se sigue volviendo a medir, pero los pasos no anotados se llevan a la medida
                // guardada para que GananciaTrasMedir no los sume a la ganancia.
                if (e.G != null && db != dbAnotados) { e.Lufs += db - dbAnotados; _dirty = true; }
                return false;
            }
            var (l, p, t) = TrasAjustar(e.Lufs, e.Peak, e.T, db);
            _map[ruta] = new Entry
            {
                M = m, S = s, Lufs = l, Peak = p, T = Finito(t), V = e.V,
                G = dbAnotados == 0 ? e.G : SumarGanancia(e.G, dbAnotados),
            };
            _dirty = true;
            return true;
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
    /// Diferencia de nivel por debajo de la cual dos medidas son del mismo audio (o del mismo audio
    /// con su global_gain cambiado un número exacto de pasos). Medir dos veces el mismo archivo da el
    /// mismo valor. Tras bajarlo con Mp3Gain no da exactamente lo guardado: el paso de verdad es 1,505
    /// dB y no 1,5, y si el pico estaba recortado por el decodificador, al bajarlo deja de estarlo y
    /// aparece algo de energía. MEDIDO en copias de 45 MP3 reales (35 con el pico recortado), bajando
    /// 1 y 2 pasos: entre −0,011 y +0,016 dB. Y entre la decodificación recortada y la sin recortar de
    /// una misma canción, hasta 0,067 dB (ver AudioSamples). Una décima cubre las dos cosas.
    ///
    /// La contrapartida: otra copia de la canción guardada en la misma ruta pasa por el mismo audio si
    /// su nivel cae, por casualidad, a menos de una décima de un número exacto de pasos (como mucho,
    /// una de cada siete). Entonces conserva la ganancia anterior, y lo único que se desvía es el nivel
    /// original de esa grabación en el cálculo de la referencia.
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
    ///
    /// Una ganancia de 0 (un archivo ajustado con los cambios compensados) se trata como cualquier
    /// otra: los pasos que se le hayan cambiado después se siguen sumando (ver <see cref="SumarGanancia"/>).
    /// </summary>
    /// <param name="aplicada">Ganancia guardada (null si nunca se ha ajustado).</param>
    /// <param name="version">Versión de la medida guardada.</param>
    /// <param name="lufsGuardado">Nivel guardado.</param>
    /// <param name="lufsMedido">Nivel recién medido.</param>
    /// <param name="canales">Canales del archivo recién medido.</param>
    internal static double? GananciaTrasMedir(double? aplicada, int? version, double lufsGuardado, double lufsMedido, int canales)
    {
        if (aplicada is not { } g || !double.IsFinite(g) || !double.IsFinite(lufsGuardado) || !double.IsFinite(lufsMedido)) return null;
        if (MonoSinDoble(version, canales)) lufsGuardado += 10 * Math.Log10(2);
        var diferencia = lufsMedido - lufsGuardado;
        var pasos = Math.Round(diferencia / PasoReal);
        if (Math.Abs(diferencia - pasos * PasoReal) > ToleranciaMismoAudio) return null;
        return g + pasos * Mp3Gain.DbPerStep;
    }

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
            // ~250 ms, en tramas completas (una muestra por canal). Antes era SampleRate·Channels/4,
            // y a 22,05 kHz en estéreo daba 11.025 muestras: un número impar. Un WAV así no se podía
            // medir («Must read complete blocks»), y en un MP3 los medidores perdían la muestra
            // sobrante de cada búfer y tomaban el canal derecho por el izquierdo en el siguiente.
            var buf = new float[Math.Max(1, fmt.SampleRate / 4) * fmt.Channels];
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
            // La ganancia aplicada va primero y aunque sonoridad.json no se pueda escribir: es lo que
            // no se regenera.
            if (_dirty || !_aplicadaAlDia) GuardarAplicada();
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

    /// <summary>
    /// Escribe volumen-aplicado.json con las entradas que tienen ganancia aplicada, si ha cambiado
    /// desde la última vez. Se llama con el cerrojo tomado.
    /// </summary>
    private void GuardarAplicada()
    {
        _aplicadaAlDia = true;
        if (_archivoAplicada == null || _aplicadaNoSobrescribir) return;
        var aplicadas = new Dictionary<string, Aplicada>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ruta, e) in _map)
            if (e.G is { } g) aplicadas[ruta] = new Aplicada { G = g, Lufs = e.Lufs, V = e.V };
        string texto;
        try { texto = JsonSerializer.Serialize(aplicadas); }
        catch (Exception x) { _log?.Err("Volumen: no se pudo preparar la ganancia aplicada: " + x.Message); _aplicadaAlDia = false; return; }
        if (texto == _aplicadaEscrita) return;
        // Sin nada ajustado y sin archivo, no se crea uno vacío.
        if (aplicadas.Count == 0 && !File.Exists(_archivoAplicada)) { _aplicadaEscrita = texto; return; }
        var error = ArchivoDatos.Escribir(_archivoAplicada, texto);
        if (error.Length == 0) _aplicadaEscrita = texto;
        else
        {
            _aplicadaAlDia = false;
            _log?.Err("Volumen: no se pudo guardar la ganancia aplicada: " + error);
        }
    }

    /// <summary>
    /// Se han borrado las mediciones (<see cref="Clear"/>). Se avisa en el hilo que la llama, después
    /// de borrarlas. Lo escucha la pestaña Volumen: sin el aviso seguía mostrando las filas medidas y
    /// marcadas, y «Ajustar volumen» decodificaba una a una las canciones que Analizar mide en paralelo.
    /// </summary>
    public event Action? Borradas;

    /// <summary>
    /// Borra las mediciones, en memoria y en disco («Limpiar carpeta de datos»). La ganancia que
    /// BeatTag ya ha aplicado se conserva, porque no se regenera (ver <see cref="_archivoAplicada"/>):
    /// sus entradas se quedan como medidas que hay que repetir, y al volver a analizar se sigue
    /// contando con ella.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            var conservadas = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ruta, e) in _map)
                if (e.G != null) conservadas[ruta] = SinMedidaVigente(e.Lufs, e.V, e.G);
            _map = conservadas;
            try { if (File.Exists(_file)) File.Delete(_file); } catch { }
            if (!File.Exists(_file)) _noSobrescribir = false;   // ya no hay nada que proteger
            _dirty = conservadas.Count > 0;
        }
        Borradas?.Invoke();
    }

    /// <summary>
    /// Una entrada que solo sirve para no perder la ganancia aplicada: sin fecha ni tamaño, así que
    /// <see cref="Get"/> no la da por vigente y el archivo se vuelve a medir.
    /// </summary>
    private static Entry SinMedidaVigente(double lufs, int? version, double? ganancia)
        => new() { M = 0, S = 0, Lufs = lufs, Peak = 0, V = version, G = ganancia };

    /// <summary>
    /// Lee las mediciones guardadas. Si el archivo está dañado se aparta como
    /// «sonoridad.json.ilegible_fecha» y se anota en el registro, en vez de tratarlo como vacío en
    /// silencio y pisarlo en el siguiente guardado. Lo que falte en él y esté en volumen-aplicado.json
    /// (tras «Limpiar», o al restaurar una copia de seguridad) se recupera como medida que hay que
    /// repetir, con su ganancia aplicada. Si un archivo está en los dos, manda sonoridad.json, que es
    /// el que sigue al archivo tal como está ahora, salvo que en él no tenga ganancia aplicada: entonces
    /// se vuelve a medir con la de volumen-aplicado.json, y al medirla se decide con el audio si sigue
    /// valiendo (ver <see cref="GananciaTrasMedir"/>).
    ///
    /// POR QUÉ. Antes mandaba sonoridad.json también sin ganancia, y el primer guardado reescribía
    /// volumen-aplicado.json sin ella. Pasaba al restaurar la copia en un equipo donde ya se había
    /// analizado (sonoridad.json no viaja en la copia) y cuando sonoridad.json no se podía escribir:
    /// la anotación que la copia debía proteger se perdía, y con ella el nivel original.
    ///
    /// Una entrada null (BeatTag nunca la escribe; solo una edición a mano o una sincronización) se
    /// salta: antes impedía arrancar si estaba en volumen-aplicado.json, y hacía fallar todos los
    /// guardados si estaba en sonoridad.json.
    /// </summary>
    private void Load()
    {
        var leido = ArchivoDatos.LeerJson<Dictionary<string, Entry?>>(_file);
        if (leido.Aviso.Length > 0) _log?.Err("Volumen: " + leido.Aviso);
        _noSobrescribir = leido.NoSobrescribir;
        if (leido.Datos != null)
            foreach (var (ruta, e) in leido.Datos)
                if (e != null) _map[ruta] = e;

        if (_archivoAplicada == null) return;
        var aplicadas = ArchivoDatos.LeerJson<Dictionary<string, Aplicada?>>(_archivoAplicada);
        if (aplicadas.Aviso.Length > 0) _log?.Err("Volumen: " + aplicadas.Aviso);
        _aplicadaNoSobrescribir = aplicadas.NoSobrescribir;
        if (aplicadas.Datos == null) return;
        foreach (var (ruta, a) in aplicadas.Datos)
        {
            if (a == null || !double.IsFinite(a.G) || !double.IsFinite(a.Lufs)) continue;
            if (!_map.TryGetValue(ruta, out var e) || e.G == null) _map[ruta] = SinMedidaVigente(a.Lufs, a.V, a.G);
        }
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
