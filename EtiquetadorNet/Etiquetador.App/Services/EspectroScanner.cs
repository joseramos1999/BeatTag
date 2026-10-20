using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;
using NAudio.Wave;

namespace Etiquetador.App.Services;

/// <summary>
/// Mide la calidad real de los archivos por su espectro (P4, equipo audio_medido): hasta qué
/// frecuencia llega el audio, para señalar los «320 kbps» que vienen de un 128. La medida está en
/// <see cref="CorteEspectral"/>; esto la aplica a archivos y la guarda en caché por archivo (fecha y
/// tamaño), con el mismo patrón que <see cref="LoudnessScanner"/>.
///
/// Solo MIDE: no toca ningún archivo. Lee tres tramos de 10 s (al 25, 50 y 75 %); MEDIDO en la
/// biblioteca real con 2 hilos, 325 ms por archivo (mediana), casi todo disco: para saltar a un punto
/// de un MP3 el lector recorre antes las cabeceras de todo el archivo.
///
/// Se guarda el corte medido, no la valoración: los umbrales que lo convierten en «sospechoso» pueden
/// afinarse en una versión futura sin tener que volver a analizar nada.
/// </summary>
public sealed class EspectroScanner
{
    /// <summary>
    /// Versión del método de medida. Si cambia la forma de medir (no los umbrales de la valoración),
    /// se sube y las medidas anteriores dejan de valer.
    /// </summary>
    public const int VersionMetodo = 1;

    /// <summary>
    /// Nombre del archivo de caché, junto a las demás cachés de la aplicación. Lo declara
    /// <see cref="AppPaths"/> para que la copia de seguridad la reconozca como caché y la deje fuera.
    /// </summary>
    public const string NombreCache = AppPaths.EspectroCacheNombre;

    public static string RutaCache(AppPaths paths) => paths.EspectroCachePath;

    private sealed class Entry
    {
        public long M { get; set; }
        public long S { get; set; }
        /// <summary>Estado (<see cref="EstadoCorte"/>).</summary>
        public int E { get; set; }
        /// <summary>Corte medido, en Hz (0 si no hay).</summary>
        public int C { get; set; }
        /// <summary>Hasta dónde se midió, en Hz.</summary>
        public int T { get; set; }
        /// <summary>Versión del método con el que se midió.</summary>
        public int V { get; set; }
    }

    private readonly string _file;
    private readonly Logger? _log;
    private Dictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _dirty;

    /// <summary>El archivo existe pero no se pudo leer ni apartar: no se guarda encima.</summary>
    private bool _noSobrescribir;

    public EspectroScanner(string cacheFile, Logger? log = null)
    {
        _file = cacheFile;
        _log = log;
        Load();
    }

    /// <summary>Cuántas medidas hay guardadas (valgan o no todavía).</summary>
    public int Count { get { lock (_lock) return _map.Count; } }

    /// <summary>
    /// ¿Se puede analizar este archivo en este sistema? En Windows, todo lo que sabe abrir el
    /// decodificador del sistema; fuera de Windows, solo MP3, WAV y AIFF (lo que lee
    /// <see cref="AudioSamples"/> sin códecs del sistema).
    /// </summary>
    public static bool Analizable(string ruta) => Analizable(ruta, OperatingSystem.IsWindows());

    internal static bool Analizable(string ruta, bool windows)
        => windows || Path.GetExtension(ruta).ToLowerInvariant() is ".mp3" or ".wav" or ".aif" or ".aiff";

    /// <summary>Medida guardada si el archivo no ha cambiado; si no, null.</summary>
    public MedidaEspectro? Get(string path)
    {
        if (!Stat(path, out var m, out var s)) return null;
        lock (_lock)
            if (_map.TryGetValue(path, out var e) && e.M == m && e.S == s && e.V == VersionMetodo)
                return new MedidaEspectro((EstadoCorte)e.E, e.C, e.T);
        return null;
    }

    /// <summary>
    /// Mide un archivo (o devuelve lo guardado). Null si no se pudo leer o el formato no se puede
    /// decodificar en este sistema.
    /// </summary>
    public MedidaEspectro? Measure(string path, bool force = false, CancellationToken ct = default)
    {
        if (!force)
        {
            var guardada = Get(path);
            if (guardada != null) return guardada;
        }
        if (!Analizable(path)) return null;

        var antes = SelloArchivo.De(path);
        try
        {
            var medida = Medir(path, ct);
            // Solo se guarda si el archivo sigue siendo el que se leyó: si cambió mientras tanto,
            // la medida podría ser de una versión que ya no existe.
            if (medida.Estado != EstadoCorte.SinDatos && antes.Existe && SelloArchivo.De(path) == antes)
            {
                lock (_lock)
                {
                    _map[path] = new Entry
                    {
                        M = antes.M, S = antes.S, E = (int)medida.Estado,
                        C = (int)medida.CorteHz, T = (int)medida.TechoHz, V = VersionMetodo,
                    };
                    _dirty = true;
                }
            }
            return medida;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            _log?.Detail($"    espectro: no se pudo analizar '{Path.GetFileName(path)}': {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Decodifica los tramos (<see cref="CorteEspectral.Tramos"/>) y mide. Lanza si el archivo no
    /// se puede abrir; quien llama ya lo trata.
    /// </summary>
    internal static MedidaEspectro Medir(string path, CancellationToken ct)
    {
        var lector = AudioSamples.Abrir(path, out var recurso);
        using var _ = recurso;
        var fmt = lector.WaveFormat;
        var acu = new EspectroMedio(fmt.SampleRate, fmt.Channels);
        var buf = new float[Math.Max(fmt.Channels, fmt.SampleRate * fmt.Channels / 4)];   // ~250 ms

        // Saltar a cada tramo exige poder posicionarse. Los lectores de AudioSamples lo permiten
        // todos; si alguno no, se lee desde el principio solo el primer tramo, que también vale.
        var flujo = recurso as WaveStream;
        var duracion = flujo is { CanSeek: true } ? flujo.TotalTime.TotalSeconds : 0;
        foreach (var (inicio, segundos) in CorteEspectral.Tramos(duracion))
        {
            ct.ThrowIfCancellationRequested();
            if (flujo is { CanSeek: true }) flujo.CurrentTime = TimeSpan.FromSeconds(inicio);
            acu.Cortar();
            var falta = (long)(segundos * fmt.SampleRate) * fmt.Channels;
            int n;
            while (falta > 0 && (n = lector.Read(buf, 0, (int)Math.Min(buf.Length, falta))) > 0)
            {
                acu.Add(buf, n);
                falta -= n;
            }
            if (flujo is not { CanSeek: true }) break;
        }
        return CorteEspectral.Medir(acu);
    }

    /// <summary>
    /// Mide muchos archivos en paralelo. Los que ya tienen medida válida no se vuelven a leer.
    ///
    /// Como mucho 4 hilos, y no todos los núcleos como la sonoridad: aquí se decodifican 30 s por
    /// canción y casi todo el tiempo es leer el archivo del disco. Más lectores a la vez sobre un disco
    /// duro no leen más rápido, se estorban, y el equipo se queda sin respuesta mientras tanto.
    /// </summary>
    public async Task MeasureManyAsync(IReadOnlyList<string> paths, bool force,
        IProgress<(int done, int total, string file)>? progress, CancellationToken ct = default)
    {
        var hechos = 0;
        var desdeGuardado = 0;
        try
        {
            await Task.Run(() =>
            {
                var opts = new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 4),
                };
                Parallel.ForEach(paths, opts, p =>
                {
                    Measure(p, force, ct);
                    var n = Interlocked.Increment(ref hechos);
                    // Cada 500 canciones se guarda lo hecho: una pasada entera puede llevar un buen
                    // rato y un cierre a la fuerza no debería perderla.
                    if (Interlocked.Increment(ref desdeGuardado) >= 500)
                    {
                        Interlocked.Exchange(ref desdeGuardado, 0);
                        Save();
                    }
                    if (n % 10 == 0 || n == paths.Count) progress?.Report((n, paths.Count, Path.GetFileName(p)));
                });
            }, ct).ConfigureAwait(false);
        }
        finally { Save(); }
    }

    /// <summary>Guarda las medidas, de una vez y sin dejar el archivo a medias (<see cref="ArchivoDatos.EscribirJson"/>).</summary>
    public void Save()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            if (_noSobrescribir)
            {
                _log?.Err($"Calidad: {NombreCache} no se pudo leer al arrancar, así que las medidas nuevas no se guardan encima.");
                return;
            }
            var error = ArchivoDatos.EscribirJson(_file, _map);
            if (error.Length == 0) _dirty = false;
            else _log?.Err("Calidad: no se pudieron guardar las medidas del espectro: " + error);
        }
    }

    /// <summary>
    /// Lee las medidas guardadas. Un archivo dañado se aparta como «espectro.json.ilegible_fecha» en
    /// vez de tratarse como vacío y pisarse en el siguiente guardado.
    /// </summary>
    private void Load()
    {
        var leido = ArchivoDatos.LeerJson<Dictionary<string, Entry>>(_file);
        if (leido.Aviso.Length > 0) _log?.Err("Calidad: " + leido.Aviso);
        _noSobrescribir = leido.NoSobrescribir;
        if (leido.Datos != null) _map = new Dictionary<string, Entry>(leido.Datos, StringComparer.OrdinalIgnoreCase);
    }

    private static bool Stat(string path, out long m, out long s)
    {
        m = 0; s = 0;
        try { var fi = new FileInfo(path); if (!fi.Exists) return false; m = fi.LastWriteTimeUtc.Ticks; s = fi.Length; return true; }
        catch { return false; }
    }

    // --- Seguir al archivo cuando lo cambia la propia aplicación (como la sonoridad, hallazgo A2) ---
    //
    // Renombrar, mover o escribir etiquetas no cambia el audio, y por tanto tampoco su espectro: la
    // medida acompaña al archivo en vez de caducar por la fecha. Ajustar el volumen tampoco cambia
    // dónde está el corte (sube o baja todo el espectro a la vez), así que también se pone al día.

    /// <summary>
    /// La aplicación acaba de escribir etiquetas, renombrar o ajustar el volumen de
    /// <paramref name="origen"/>, que ahora está en <paramref name="destino"/> (puede ser el mismo).
    /// <paramref name="antes"/> es el sello justo antes de escribir: la medida se pone al día solo si
    /// valía entonces.
    /// </summary>
    public void Seguir(string origen, string destino, SelloArchivo antes)
    {
        if (!string.Equals(origen, destino, StringComparison.Ordinal)) Mover(origen, destino);
        if (antes.Existe) Retocar(destino, antes.M, antes.S);
    }

    /// <summary>La medida acompaña al archivo renombrado o movido (DESPUÉS de moverlo). Devuelve si había algo que mover.</summary>
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
    /// La aplicación acaba de cambiar el archivo sin cambiar su espectro. Si la medida valía justo
    /// antes (fecha y tamaño <paramref name="mAntes"/> y <paramref name="sAntes"/>), se pone al día con
    /// los de ahora. Devuelve si la puso al día.
    /// </summary>
    public bool Retocar(string ruta, long mAntes, long sAntes)
    {
        if (!Stat(ruta, out var m, out var s)) return false;
        lock (_lock)
        {
            if (!_map.TryGetValue(ruta, out var e) || e.M != mAntes || e.S != sAntes) return false;
            if (e.M == m && e.S == s) return true;
            e.M = m;
            e.S = s;
            _dirty = true;
            return true;
        }
    }
}
