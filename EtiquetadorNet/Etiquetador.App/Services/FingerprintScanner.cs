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

    /// <summary>1 cuando ya no hay que leer el disco: la caché está leída, o se borró con Clear.</summary>
    private int _cargada;

    /// <summary>
    /// La lectura en curso (o ya hecha), COMPARTIDA por todos los que la necesiten a la vez. Antes
    /// se marcaba como leída ANTES de leer, así que una segunda llamada concurrente encontraba la
    /// caché vacía y daba por «sin calcular» canciones que sí lo estaban.
    /// </summary>
    private Task? _carga;
    private readonly object _cargaLock = new();

    /// <summary>
    /// El archivo está dañado y no se ha podido apartar. No se guarda encima: pisarlo con lo poco
    /// calculado en esta sesión tiraría horas de análisis.
    /// </summary>
    private volatile bool _noSobrescribir;

    /// <summary>Ver <see cref="AvisoCarga"/>.</summary>
    private volatile string _avisoCarga = "";

    public FingerprintScanner(string cacheFile, string fpcalcPath, Logger? log = null)
    {
        _cacheFile = cacheFile;
        _fpcalc = fpcalcPath;
        _log = log;
    }

    public bool FpcalcDisponible => File.Exists(_fpcalc);

    /// <summary>Ya se ha leído la caché del disco (o no hace falta). Sirve a las pruebas y al estado.</summary>
    public bool EstaCargada => Volatile.Read(ref _cargada) == 1;

    /// <summary>
    /// Por qué no se pueden usar ahora las huellas guardadas, o "" si se puede. Redactado para el
    /// usuario. Mientras no esté vacío no se calcula ni se guarda nada, y Duplicados lo enseña en
    /// lugar de «Sin calcular… las 15000 canciones», que sería falso: las huellas están en el disco,
    /// solo que no se han podido leer.
    /// </summary>
    public string AvisoCarga => _avisoCarga;

    /// <summary>
    /// Lee la caché del disco en segundo plano, una sola vez. Quien llegue mientras se está leyendo
    /// espera a esa misma lectura en vez de empezar otra o de ver la caché a medias.
    ///
    /// Si la última lectura no pudo abrir el archivo, se vuelve a intentar. Antes la caché quedaba
    /// dada por leída y vacía para toda la sesión por un bloqueo pasajero (un antivirus, una copia
    /// de seguridad que lo tenía abierto): Duplicados decía que no había nada calculado y «Analizar
    /// audio» calculaba durante horas para luego negarse a guardar.
    /// </summary>
    public Task CargarAsync()
    {
        if (EstaCargada) return Task.CompletedTask;
        lock (_cargaLock)
        {
            if (_carga is { IsCompleted: true } && !EstaCargada) _carga = null;
            return _carga ??= Task.Run(LeerDelDiscoAsync);
        }
    }

    /// <summary>
    /// Asegura que la caché está leída, esperando a la lectura compartida si está en marcha. Bloquea
    /// la primera vez, así que solo se usa desde hilos de fondo.
    ///
    /// A diferencia de <see cref="CargarAsync"/>, no reintenta una lectura fallida: se llama una vez
    /// por canción, y con el archivo aún bloqueado serían 15.000 intentos y 15.000 líneas en el
    /// registro. Reintenta quien empieza una operación (contar, comparar o calcular).
    /// </summary>
    private void Cargar()
    {
        if (EstaCargada) return;
        Task carga;
        lock (_cargaLock) carga = _carga ??= Task.Run(LeerDelDiscoAsync);
        carga.GetAwaiter().GetResult();
    }

    /// <summary>Huella guardada de un archivo, o null si no está o el archivo ha cambiado.</summary>
    /// <remarks>
    /// La primera llamada espera a que se lea la caché (unos 1,3-3,5 s con 15.000 huellas). Por eso
    /// no se llama nunca desde el hilo de la interfaz: Duplicados cuenta y compara en segundo plano.
    /// </remarks>
    public int[]? Get(string path)
    {
        Cargar();
        if (!_cache.TryGetValue(path, out var e)) return null;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.LastWriteTimeUtc.Ticks != e.M || fi.Length != e.S) return null;
            return e.F.Length > 0 ? e.F : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Cuántas huellas hay guardadas y siguen siendo válidas para esta lista. Mira la fecha y el
    /// tamaño de cada archivo, así que con una biblioteca grande en un disco externo tarda: se
    /// llama siempre en segundo plano.
    /// </summary>
    public int Contar(IEnumerable<string> paths) => paths.Count(p => Get(p) != null);

    /// <summary>
    /// Calcula las huellas que falten. Informa del avance y se puede cancelar; lo ya calculado
    /// queda guardado, de modo que cancelar no tira por la borda el trabajo hecho.
    /// </summary>
    /// <returns>"" si se ha podido trabajar; si no, por qué no se ha calculado nada, para el usuario.</returns>
    public async Task<string> ScanAsync(IReadOnlyList<string> paths, bool force,
                                        IProgress<(int Hechas, int Total, string Archivo)>? progreso,
                                        CancellationToken ct = default)
    {
        if (!FpcalcDisponible)
        {
            _log?.Err("Huellas: falta fpcalc, no se puede calcular nada.");
            return "No está disponible el analizador de audio (fpcalc).";
        }

        // La caché se lee SIEMPRE antes de calcular, también con «force»: si no, el guardado del
        // final escribiría solo lo calculado ahora y se perderían las huellas de las demás.
        //
        // Y el filtro de pendientes va fuera del hilo de quien llama: son la lectura de la caché y
        // una consulta de fecha y tamaño por canción. Hecho antes del primer await, como estaba,
        // congelaba la ventana al pulsar «Analizar audio».
        await CargarAsync().ConfigureAwait(false);

        // Si las huellas guardadas no se han podido leer, no se calcula nada: se tardarían horas en
        // rehacer lo que ya está en el disco y al final no se podría guardar sin pisarlo.
        if (AvisoCarga.Length > 0)
        {
            _log?.Err("Huellas: no se calcula nada. " + AvisoCarga);
            return AvisoCarga;
        }

        List<string> pendientes;
        try
        {
            pendientes = await Task.Run(
                () => force ? paths.ToList() : paths.Where(p => Get(p) == null).ToList(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return ""; }   // nada calculado aún
        if (pendientes.Count == 0) { progreso?.Report((paths.Count, paths.Count, "")); return ""; }

        _log?.Head($"Huellas: {pendientes.Count} por calcular de {paths.Count} (el resto ya estaban).");
        var hechas = 0;
        var crono = Stopwatch.StartNew();

        // fpcalc es un proceso externo que se pasa el tiempo leyendo disco y descodificando, así
        // que varios a la vez rinden mucho mejor. Se deja un núcleo libre para que la interfaz
        // siga respondiendo durante las horas que dura la primera pasada.
        //
        // Al cancelar, cada fpcalc en marcha se mata en el acto (Fingerprint.EjecutarConLimiteAsync).
        // Antes se dejaba de vaciar su salida y se esperaba su minuto de plazo con WaitForExit, que
        // no mira el token: la barra seguía ocupada hasta un minuto después de pulsar Cancelar y el
        // registro se llenaba de falsos «no respondió».
        var hilos = Math.Max(1, Environment.ProcessorCount - 1);
        try
        {
            await Parallel.ForEachAsync(pendientes,
                new ParallelOptions { MaxDegreeOfParallelism = hilos, CancellationToken = ct },
                async (ruta, tk) =>
                {
                    var e = await CalcularAsync(ruta, tk).ConfigureAwait(false);
                    if (e != null) { _cache[ruta] = e; Interlocked.Exchange(ref _sucio, 1); }
                    var n = Interlocked.Increment(ref hechas);
                    if (n % 25 == 0 || n == pendientes.Count)
                        progreso?.Report((n, pendientes.Count, Path.GetFileName(ruta)));
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* lo ya calculado se guarda igualmente */ }

        await Task.Run(Save, CancellationToken.None).ConfigureAwait(false);
        _log?.Sum($"Huellas: {hechas} calculadas en {TextUtils.FormatEta(crono.Elapsed.TotalSeconds)}"
                + (ct.IsCancellationRequested ? " (cancelado; lo hecho queda guardado)" : "") + ".");
        return "";
    }

    private async Task<Entry?> CalcularAsync(string path, CancellationToken ct)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;

            var psi = new ProcessStartInfo(_fpcalc);
            psi.ArgumentList.Add("-raw");
            psi.ArgumentList.Add("-json");
            psi.ArgumentList.Add("-length");
            psi.ArgumentList.Add(SegundosAnalizados.ToString());
            psi.ArgumentList.Add(path);

            // El mismo ejecutor que usa Enriquecer: vacía las dos salidas sin token (si dejaran de
            // leerse, fpcalc se quedaría bloqueado al escribir sus ~10 KB de JSON) y mata el árbol
            // de procesos al vencer el plazo o al cancelar.
            var r = await Fingerprint.EjecutarConLimiteAsync(psi, TimeSpan.FromMilliseconds(TiempoLimiteMs), ct)
                                     .ConfigureAwait(false);
            if (r == null) return null;
            if (r.Colgado)
            {
                _log?.Detail($"      huella: fpcalc no respondió en {TiempoLimiteMs / 1000}s · {Path.GetFileName(path)}");
                return null;
            }

            var salida = r.Salida;
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            _log?.Detail($"      huella: fallo en {Path.GetFileName(path)} · {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Lee la caché del disco la primera vez que alguien la necesita, no al arrancar, y SIEMPRE en
    /// segundo plano.
    ///
    /// MEDIDO: el archivo de huellas de una biblioteca de 15.000 canciones ocupa 153 MB y tardaba
    /// 1.333 de los 1.588 ms que costaba montar el motor, con la ventana todavía sin aparecer. Las
    /// huellas solo hacen falta en los duplicados por audio, así que quien no entra ahí no paga nada.
    ///
    /// Visto en los registros reales: esa lectura acabó en el hilo de la interfaz al abrir
    /// Duplicados («15241 leídas de la caché en 1341 / 1387 / 1393 / 1775 / 1890 / 3453 ms»), también
    /// con el criterio por nombre y en sesiones que no tocaron las huellas. Además pasaba por un
    /// texto de unos 320 MB (ReadAllText) antes de interpretarlo; ahora se lee del archivo a trozos.
    /// </summary>
    private async Task LeerDelDiscoAsync()
    {
        if (!File.Exists(_cacheFile)) { DarPorLeida(); return; }
        var reloj = Stopwatch.StartNew();

        Dictionary<string, Entry>? d;
        try
        {
            var fs = new FileStream(_cacheFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                                    bufferSize: 1 << 16, useAsync: true);
            await using (fs.ConfigureAwait(false))
                d = await JsonSerializer.DeserializeAsync<Dictionary<string, Entry>>(fs).ConfigureAwait(false);
        }
        catch (JsonException e)
        {
            // Dañada (un corte a media escritura de una versión antigua, por ejemplo): se aparta con
            // otro nombre en vez de dejar que el siguiente guardado la pise sin decir nada. SOLO ante
            // un JSON que no se entiende: un fallo al leer no dice nada del contenido.
            var apartado = ArchivoDatos.Apartar(_cacheFile);
            if (apartado.Length > 0)
            {
                _log?.Err($"La caché de huellas no se pudo leer ({e.Message}); se ha apartado como {Path.GetFileName(apartado)} y se volverán a calcular.");
                DarPorLeida();
                return;
            }
            _noSobrescribir = true;
            _log?.Err($"La caché de huellas no se pudo leer ({e.Message}) ni apartar; no se guardará encima.");
            DarPorLeida($"El archivo de huellas guardadas está dañado y no se ha podido apartar. Para no "
                      + $"perder nada no se calcula ni se guarda; muévelo o bórralo a mano y vuelve a intentarlo: {_cacheFile}");
            return;
        }
        catch (Exception e)
        {
            // No se ha podido abrir o leer (otro programa lo tiene abierto, sin permiso, un disco que
            // no responde). No hay motivo para creerlo dañado: ni se aparta ni se da por leída, y la
            // próxima operación lo vuelve a intentar (CargarAsync).
            _avisoCarga = $"No se han podido leer las huellas ya calculadas ({e.Message}). Si otro programa "
                        + "tiene abierto el archivo, ciérralo y vuelve a intentarlo. Mientras tanto no se calcula nada, "
                        + "para no rehacer ni pisar lo que ya está guardado.";
            _log?.Err($"No se pudo leer la caché de huellas ({e.Message}); se volverá a intentar en la próxima operación.");
            return;
        }

        if (d != null)
        {
            foreach (var kv in d) _cache.TryAdd(kv.Key, kv.Value);   // lo calculado entretanto manda
            _log?.Detail($"Huellas de audio: {d.Count} leídas de la caché en {reloj.ElapsedMilliseconds} ms.");
        }
        DarPorLeida();
    }

    /// <summary>La caché ya no hay que leerla; <paramref name="aviso"/> dice si se puede usar.</summary>
    private void DarPorLeida(string aviso = "")
    {
        _avisoCarga = aviso;
        Volatile.Write(ref _cargada, 1);
    }

    /// <summary>
    /// Guarda la caché de forma atómica (temporal + intercambio) y escribiendo directamente al
    /// archivo, sin montar antes un texto de cientos de megas en memoria.
    /// </summary>
    public void Save()
    {
        if (Interlocked.Exchange(ref _sucio, 0) == 0) return;
        // Sin haber leído lo del disco, lo que hay en memoria no es la caché entera: guardarlo la pisaría.
        if (_noSobrescribir || !EstaCargada)
        {
            _log?.Err("Huellas: la caché del disco no se pudo leer, así que lo calculado en esta sesión no se guarda encima.");
            return;
        }
        var tmp = _cacheFile + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(_cacheFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 16))
                JsonSerializer.Serialize(fs, _cache);
            File.Move(tmp, _cacheFile, true);
        }
        catch (Exception e)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            Interlocked.Exchange(ref _sucio, 1);   // sigue pendiente: el próximo guardado lo reintenta
            _log?.Detail("No se pudo guardar la caché de huellas: " + e.Message);
        }
    }

    public void Clear()
    {
        // Dada por leída: si no, la primera consulta después volvería a cargar lo que se borra aquí.
        Interlocked.Exchange(ref _cargada, 1);
        _cache.Clear();
        try { if (File.Exists(_cacheFile)) File.Delete(_cacheFile); } catch { }
        // Sin archivo ya no hay nada que no se pudiera leer ni que se pudiera pisar.
        if (!File.Exists(_cacheFile)) { _avisoCarga = ""; _noSobrescribir = false; }
    }

    // --- Seguir al archivo cuando lo cambia la propia aplicación (hallazgos A2 y X4) ---
    //
    // La huella se valida por ruta, fecha y tamaño, y la aplicación cambia las tres cosas sin tocar
    // el audio: renombra (Enriquecer, Asistente, Editor) y escribe etiquetas o el volumen. MEDIDO
    // en una biblioteca real (solo lectura): de 15.241 huellas guardadas solo seguían valiendo
    // 8.205; 1.070 habían cambiado de fecha o tamaño (111 con el mismo tamaño, lo típico de un
    // ajuste de volumen). Cada una son ~1 s de fpcalc al volver a Duplicados o a Comprobar audio.

    /// <summary>
    /// La huella acompaña al archivo que la aplicación acaba de renombrar o mover. Se llama DESPUÉS
    /// de moverlo. Un renombrado no cambia ni la fecha ni el tamaño, así que la entrada sigue
    /// valiendo (o no) exactamente igual que antes. Devuelve si había algo que mover.
    ///
    /// Si la caché aún no se había leído en esta sesión se lee ahora (MEDIDO: ~1,3 s con 153 MB):
    /// es mucho menos que recalcular aunque sea un puñado de huellas.
    /// </summary>
    public bool Mover(string origen, string destino)
    {
        if (string.IsNullOrWhiteSpace(origen) || string.IsNullOrWhiteSpace(destino)) return false;
        if (string.Equals(origen, destino, StringComparison.Ordinal)) return false;
        Cargar();
        if (!_cache.TryRemove(origen, out var e)) return false;
        _cache[destino] = e;
        Interlocked.Exchange(ref _sucio, 1);
        return true;
    }

    /// <summary>
    /// La aplicación acaba de escribir en el archivo algo que no cambia la huella (etiquetas, o el
    /// volumen: Chromaprint trabaja con el croma, no con el nivel). Si la entrada valía JUSTO antes
    /// de escribir (fecha y tamaño <paramref name="mAntes"/> y <paramref name="sAntes"/>), se le
    /// ponen la fecha y el tamaño de ahora. Si ya no valía, se deja como estaba: resucitarla daría
    /// por buena la huella de un audio que cambió por otro lado. Devuelve si la puso al día.
    /// </summary>
    public bool Retocar(string ruta, long mAntes, long sAntes)
    {
        if (string.IsNullOrWhiteSpace(ruta)) return false;
        Cargar();
        if (!_cache.TryGetValue(ruta, out var e) || e.M != mAntes || e.S != sAntes) return false;
        try
        {
            var fi = new FileInfo(ruta);
            if (!fi.Exists) return false;
            if (fi.LastWriteTimeUtc.Ticks == e.M && fi.Length == e.S) return true;   // no se llegó a escribir: nada que guardar
            // Se cambia en la misma entrada para no perder ningún campo de la huella.
            e.M = fi.LastWriteTimeUtc.Ticks;
            e.S = fi.Length;
            Interlocked.Exchange(ref _sucio, 1);
            return true;
        }
        catch { return false; }
    }
}
