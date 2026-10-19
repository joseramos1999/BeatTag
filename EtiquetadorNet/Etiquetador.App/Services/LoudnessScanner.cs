using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
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
    private sealed class Entry
    {
        public long M { get; set; }
        public long S { get; set; }
        public double Lufs { get; set; }
        public double Peak { get; set; }
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
                return new LoudnessResult(e.Lufs, e.Peak, 0);
        return null;
    }

    /// <summary>
    /// Actualiza la medida tras haber cambiado el volumen del archivo, sin volver a analizarlo:
    /// una ganancia conocida desplaza el nivel y el pico en la misma cantidad.
    /// </summary>
    public void Update(string path, double lufs, double peak)
    {
        if (!Stat(path, out var m, out var s)) return;
        lock (_lock)
        {
            _map[path] = new Entry { M = m, S = s, Lufs = lufs, Peak = peak };
            _dirty = true;
        }
    }

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
            var buf = new float[fmt.SampleRate * fmt.Channels / 4];   // ~250 ms
            int n;
            while ((n = reader.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                meter.Add(buf, n);
            }

            var r = meter.GetResult();
            if (!r.Ok) return r;   // silencio: se devuelve pero no se cachea

            if (Stat(path, out var m, out var s))
            {
                lock (_lock)
                {
                    _map[path] = new Entry { M = m, S = s, Lufs = r.Lufs, Peak = r.PeakDb };
                    _dirty = true;
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

    /// <summary>Mide muchos archivos repartiendo el trabajo entre núcleos.</summary>
    public async Task MeasureManyAsync(IReadOnlyList<string> paths, bool force,
        IProgress<(int done, int total, string file)>? progress, CancellationToken ct = default)
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
                Measure(p, force, ct);
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
