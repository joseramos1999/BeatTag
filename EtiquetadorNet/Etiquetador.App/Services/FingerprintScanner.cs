using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Etiquetador.Core;

namespace Etiquetador.App.Services;

/// <summary>
/// Calcula y guarda la huella acústica de cada archivo, para poder detectar duplicados por el
/// AUDIO en lugar de por el nombre o los tags.
///
/// Calcularlas es caro (fpcalc tarda alrededor de un segundo por canción, y una biblioteca grande
/// son horas), así que se cachean por fecha y tamaño igual que las mediciones de volumen: solo se
/// recalcula lo que ha cambiado. La primera pasada es lenta; las siguientes, inmediatas.
///
/// Se usa "fpcalc -raw", que da la huella como enteros ya descomprimidos. La forma comprimida en
/// base64 obligaría a implementar el descompresor de Chromaprint sin ganar nada a cambio.
/// </summary>
public sealed class FingerprintScanner
{
    private sealed class Entry
    {
        public long M { get; set; }        // fecha de modificación
        public long S { get; set; }        // tamaño
        public double D { get; set; }      // duración en segundos
        public int[] F { get; set; } = Array.Empty<int>();
    }

    /// <summary>
    /// Segundos de audio que se analizan de cada canción. Suficiente para identificar sin
    /// ambigüedad, mantiene acotados el cálculo y el archivo de caché, y hace comparables entre sí
    /// temas de duraciones muy distintas.
    /// </summary>
    private const int SegundosAnalizados = 120;

    /// <summary>Tiempo maximo por archivo. Pasado esto, fpcalc esta colgado y se mata.</summary>
    private const int TiempoLimiteMs = 60_000;

    private readonly string _cacheFile;
    private readonly string _fpcalc;
    private readonly Logger? _log;
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private int _sucio;

    public FingerprintScanner(string cacheFile, string fpcalcPath, Logger? log = null)
    {
        _cacheFile = cacheFile;
        _fpcalc = fpcalcPath;
        _log = log;
        Cargar();
    }

    public bool FpcalcDisponible => File.Exists(_fpcalc);

    /// <summary>Huella guardada de un archivo, o null si no está o el archivo ha cambiado.</summary>
    public int[]? Get(string path)
    {
        if (!_cache.TryGetValue(path, out var e)) return null;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.LastWriteTimeUtc.Ticks != e.M || fi.Length != e.S) return null;
            return e.F.Length > 0 ? e.F : null;
        }
        catch { return null; }
    }

    /// <summary>Cuántas huellas hay guardadas y siguen siendo válidas para esta lista.</summary>
    public int Contar(IEnumerable<string> paths) => paths.Count(p => Get(p) != null);

    /// <summary>
    /// Calcula las huellas que falten. Informa del avance y se puede cancelar; lo ya calculado
    /// queda guardado, de modo que cancelar no tira por la borda el trabajo hecho.
    /// </summary>
    public async Task ScanAsync(IReadOnlyList<string> paths, bool force,
                                IProgress<(int Hechas, int Total, string Archivo)>? progreso,
                                CancellationToken ct = default)
    {
        if (!FpcalcDisponible) { _log?.Err("Huellas: falta fpcalc, no se puede calcular nada."); return; }

        var pendientes = force ? paths.ToList() : paths.Where(p => Get(p) == null).ToList();
        if (pendientes.Count == 0) { progreso?.Report((paths.Count, paths.Count, "")); return; }

        _log?.Head($"Huellas: {pendientes.Count} por calcular de {paths.Count} (el resto ya estaban).");
        var hechas = 0;
        var crono = Stopwatch.StartNew();

        // fpcalc es un proceso externo que se pasa el tiempo leyendo disco y descodificando, así
        // que varios a la vez rinden mucho mejor. Se deja un núcleo libre para que la interfaz
        // siga respondiendo durante las horas que dura la primera pasada.
        var hilos = Math.Max(1, Environment.ProcessorCount - 1);
        await Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(pendientes,
                    new ParallelOptions { MaxDegreeOfParallelism = hilos, CancellationToken = ct },
                    ruta =>
                    {
                        var e = Calcular(ruta, ct);
                        if (e != null) { _cache[ruta] = e; Interlocked.Exchange(ref _sucio, 1); }
                        var n = Interlocked.Increment(ref hechas);
                        if (n % 25 == 0 || n == pendientes.Count)
                            progreso?.Report((n, pendientes.Count, Path.GetFileName(ruta)));
                    });
            }
            catch (OperationCanceledException) { /* lo ya calculado se guarda igualmente */ }
        }, ct).ConfigureAwait(false);

        Save();
        _log?.Sum($"Huellas: {hechas} calculadas en {TextUtils.FormatEta(crono.Elapsed.TotalSeconds)}"
                + (ct.IsCancellationRequested ? " (cancelado; lo hecho queda guardado)" : "") + ".");
    }

    private Entry? Calcular(string path, CancellationToken ct)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;

            var psi = new ProcessStartInfo(_fpcalc)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-raw");
            psi.ArgumentList.Add("-json");
            psi.ArgumentList.Add("-length");
            psi.ArgumentList.Add(SegundosAnalizados.ToString());
            psi.ArgumentList.Add(path);

            using var p = Process.Start(psi);
            if (p == null) return null;

            // Vaciar los DOS flujos A LA VEZ: si el de errores se llena mientras se espera al de
            // salida, fpcalc se bloquea al escribir y aquí no se vuelve nunca. Leerlos en fila,
            // como se hacía antes, tenía además el efecto de que el límite de tiempo de abajo no
            // llegaba a evaluarse jamás: la espera ya había ocurrido dentro de la lectura.
            var salidaTask = p.StandardOutput.ReadToEndAsync(ct);
            var errorTask = p.StandardError.ReadToEndAsync(ct);

            if (!p.WaitForExit(TiempoLimiteMs))
            {
                // Colgado de verdad: no queda otra que matarlo, o el análisis entero se para aquí.
                try { p.Kill(entireProcessTree: true); } catch { }
                _log?.Detail($"      huella: fpcalc no respondió en {TiempoLimiteMs / 1000}s · {Path.GetFileName(path)}");
                return null;
            }
            p.WaitForExit();   // deja que los lectores acaben de vaciar los búferes

            var salida = salidaTask.GetAwaiter().GetResult();
            errorTask.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(salida)) return null;

            using var doc = JsonDocument.Parse(salida);
            var raiz = doc.RootElement;
            if (!raiz.TryGetProperty("fingerprint", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;

            var lista = new int[arr.GetArrayLength()];
            var i = 0;
            foreach (var v in arr.EnumerateArray())
                lista[i++] = unchecked((int)v.GetUInt32());   // fpcalc los da sin signo

            var dur = raiz.TryGetProperty("duration", out var d) ? d.GetDouble() : 0;
            return new Entry { M = fi.LastWriteTimeUtc.Ticks, S = fi.Length, D = dur, F = lista };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            _log?.Detail($"      huella: fallo en {Path.GetFileName(path)} · {e.Message}");
            return null;
        }
    }

    private void Cargar()
    {
        try
        {
            if (!File.Exists(_cacheFile)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_cacheFile));
            if (d == null) return;
            foreach (var kv in d) _cache[kv.Key] = kv.Value;
        }
        catch { /* caché ilegible: se recalcula sola */ }
    }

    public void Save()
    {
        if (Interlocked.Exchange(ref _sucio, 0) == 0) return;
        try
        {
            var dir = Path.GetDirectoryName(_cacheFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _cacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cache));
            File.Move(tmp, _cacheFile, true);
        }
        catch (Exception e) { _log?.Detail("No se pudo guardar la caché de huellas: " + e.Message); }
    }

    public void Clear()
    {
        _cache.Clear();
        try { if (File.Exists(_cacheFile)) File.Delete(_cacheFile); } catch { }
    }
}
