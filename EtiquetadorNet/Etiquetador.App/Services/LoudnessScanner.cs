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
            using var reader = new AudioFileReader(path);   // decodifica a float -1..1
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

    public void Save()
    {
        lock (_lock)
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

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_file, Encoding.UTF8));
            if (d != null) _map = new Dictionary<string, Entry>(d, StringComparer.OrdinalIgnoreCase);
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
