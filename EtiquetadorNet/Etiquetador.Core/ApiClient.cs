using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Etiquetador.Core;

/// <summary>
/// Cliente HTTP con caché por URL en disco + limitador por servidor + reintento en los fallos pasajeros.
/// Port de Invoke-Api / Cache-Path / Cache-Store / Infer-Throttle del .ps1.
/// La caché es compatible con la del .ps1 (misma carpeta, mismo hash MD5 de la URL).
///
/// Distingue tres cosas que antes acababan todas en un «null»:
///   · el servidor respondió y no hay resultados (una búsqueda vacía, un 404): es una respuesta;
///   · no se pudo preguntar (sin conexión, tiempo agotado, 5xx, 429, cuota de Deezer): eso NO dice
///     nada de la canción, y se cuenta en <see cref="FallosDeRed"/> para que quien orquesta lo sepa;
///   · el usuario canceló: solo entonces sale una OperationCanceledException.
/// </summary>
public sealed class ApiClient : IDisposable
{
    /// <summary>Intentos de una GET ante un fallo pasajero (el original más un reintento).</summary>
    private const int IntentosGet = 2;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly AppPaths _paths;
    private int _cacheHits, _cacheMiss;
    private long _fallosDeRed;
    private int _podaLanzada;

    /// <summary>Log opcional: cada petición queda trazada en el archivo (no en la UI).</summary>
    public Logger? Log { get; set; }

    public bool CacheOn { get; set; } = true;
    public int CacheTtlDays { get; set; } = 30;
    public int CacheHits => Volatile.Read(ref _cacheHits);
    public int CacheMiss => Volatile.Read(ref _cacheMiss);
    public string LastApiError { get; set; } = "";

    /// <summary>Reparte las peticiones a cada servidor según su límite publicado (ver <see cref="LimitePara"/>).</summary>
    public LimitadorPeticiones Limitador { get; }

    /// <summary>
    /// Borrar lo caducado de la caché una vez por sesión, en segundo plano, la primera vez que se
    /// toca. Se puede apagar para las pruebas que miden la caché.
    /// </summary>
    public bool PodaAutomatica { get; set; } = true;

    /// <summary>
    /// Peticiones que se quedaron SIN respuesta válida desde que se creó el cliente: sin conexión,
    /// tiempo de espera agotado, error 5xx, 429 o la «cuota agotada» de Deezer, ya contado el
    /// reintento. Una búsqueda que responde bien sin resultados, o un 404, NO cuenta: eso sí es
    /// una respuesta.
    ///
    /// Existe para no confundir «no está en el catálogo» con «no se pudo preguntar». Visto en los
    /// registros reales: 98 respuestas «musicbrainz.org HTTP 503» y 23 «se abandona tras 2
    /// intentos», todas en canciones que acabaron guardadas como no encontradas. Quien procesa una
    /// canción lee el contador antes y después (o usa <see cref="MedirFallos"/>, que además
    /// funciona con varias canciones en paralelo) y, si subió, sabe que el resultado está incompleto.
    /// </summary>
    public long FallosDeRed => Interlocked.Read(ref _fallosDeRed);

    /// <summary>El HttpClient de este cliente, para lo que tiene que seguir enlaces a mano (enlaces cortos).</summary>
    internal HttpClient Http => _http;

    public ApiClient(AppPaths paths, HttpClient? http = null, LimitadorPeticiones? limitador = null)
    {
        _paths = paths;
        Limitador = limitador ?? new LimitadorPeticiones();
        if (http != null) { _http = http; _ownsHttp = false; }
        else
        {
            _http = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            })
            { Timeout = TimeSpan.FromSeconds(30) };
            _ownsHttp = true;
        }
    }

    /// <summary>
    /// Empieza a contar los fallos de red de un tramo concreto -típicamente, el análisis de UNA
    /// canción-: <c>using var m = api.MedirFallos(); … if (m.HuboFallos) …</c>.
    ///
    /// Va por flujo asíncrono (AsyncLocal), no por reloj: con varias canciones en paralelo, cada una
    /// ve solo los fallos de sus propias peticiones, cosa que comparar <see cref="FallosDeRed"/>
    /// antes y después no garantiza.
    /// </summary>
    public MedicionFallos MedirFallos() => new();

    private void AnotarFalloDeRed()
    {
        Interlocked.Increment(ref _fallosDeRed);
        MedicionFallos.Anotar();
    }

    // ------------------------------------------------------------------ Caché en disco

    /// <summary>Ruta de caché para una URL: MD5 hex, subcarpeta = 2 primeros chars, archivo = hex.json.</summary>
    public string? CachePath(string url)
    {
        try
        {
            var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
            return Path.Combine(_paths.CacheDir, hash.Substring(0, 2), hash + ".json");
        }
        catch { return null; }
    }

    /// <summary>
    /// Guarda una respuesta en la caché. Se escribe en un temporal y se mueve encima: antes se
    /// escribía directamente, y cerrar la aplicación a mitad dejaba una entrada truncada que se
    /// servía durante un mes como «sin resultados».
    /// </summary>
    public void CacheStore(string cachePath, string text)
    {
        ProgramarPoda();
        string? tmp = null;
        try
        {
            var d = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
            // Nombre único: dos peticiones a la misma URL en paralelo no se pisan el temporal.
            tmp = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp, text ?? "", Encoding.UTF8);
            File.Move(tmp, cachePath, overwrite: true);
            tmp = null;
        }
        catch { /* best-effort */ }
        finally { if (tmp != null) BorrarSinFallar(tmp); }
    }

    /// <summary>Lee la caché cruda para una URL/clave (o null si no hay o caducó). Para AcoustID (cachea por huella).</summary>
    public string? ReadCache(string url) => TryReadCache(CacheOn ? CachePath(url) : null, null);

    /// <summary>
    /// Cuándo se descargó lo que hay en caché para esta URL, o null si no hay nada. Sirve para
    /// decir en pantalla de qué día es una lista que se está enseñando.
    /// </summary>
    public DateTime? FechaEnCache(string url)
    {
        var cp = CachePath(url);
        try { return cp != null && File.Exists(cp) ? File.GetLastWriteTime(cp) : null; }
        catch { return null; }
    }

    // Devuelve el texto crudo desde caché (o null); incrementa CacheHits si sirve.
    private string? TryReadCache(string? cp, TimeSpan? maxAge)
    {
        if (!CacheOn || cp is null) return null;
        ProgramarPoda();
        try
        {
            if (!File.Exists(cp)) return null;
            var edad = DateTime.Now - File.GetLastWriteTime(cp);
            if (edad.TotalDays > CacheTtlDays) return null;
            // Una antigüedad máxima propia acorta la general, nunca la alarga: una lista «diaria»
            // no puede servirse con la caducidad de una búsqueda.
            if (maxAge is { } max && edad > max) return null;
            var raw = File.ReadAllText(cp, Encoding.UTF8);
            Interlocked.Increment(ref _cacheHits);
            return raw;
        }
        catch { /* cae a red */ }
        return null;
    }

    /// <summary>
    /// Borra de la caché de red lo caducado (más antiguo que <see cref="CacheTtlDays"/>) y los
    /// temporales huérfanos de escrituras interrumpidas.
    ///
    /// MEDIDO en la carpeta real: 69.123 archivos y 314 MB, de los que unos 56.000 y ~254 MB
    /// tenían más de 30 días. Nada los leía ya -la caché solo sirve lo de los últimos 30 días-,
    /// pero tampoco nada los borraba: solo «Vaciar caché», que se lleva también lo útil.
    /// </summary>
    public (int Archivos, long Bytes) PodarCache(CancellationToken ct = default)
    {
        var dir = _paths.CacheDir;
        if (!Directory.Exists(dir)) return (0, 0);
        var caduca = DateTime.Now.AddDays(-CacheTtlDays);
        var temporalHuerfano = DateTime.Now.AddHours(-1);
        int n = 0;
        long bytes = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var esTmp = f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            if (!esTmp && !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var fi = new FileInfo(f);
                if (fi.LastWriteTime >= (esTmp ? temporalHuerfano : caduca)) continue;
                var largo = fi.Length;
                fi.Delete();
                n++;
                bytes += largo;
            }
            catch { /* en uso o sin permiso: se intentará en otra sesión */ }
        }
        return (n, bytes);
    }

    /// <summary>
    /// Lanza la poda en segundo plano la primera vez que se toca la caché, y como mucho una vez al
    /// día (se apunta en «.ultima-poda»). Así no hace falta que nadie se acuerde de llamarla ni se
    /// retrasa el arranque: recorrer 70.000 archivos lleva unos segundos.
    /// </summary>
    private void ProgramarPoda()
    {
        if (!PodaAutomatica || Interlocked.Exchange(ref _podaLanzada, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                var marca = Path.Combine(_paths.CacheDir, ".ultima-poda");
                if (File.Exists(marca) && DateTime.Now - File.GetLastWriteTime(marca) < TimeSpan.FromDays(1)) return;
                var (n, bytes) = PodarCache();
                if (Directory.Exists(_paths.CacheDir)) File.WriteAllText(marca, DateTime.Now.ToString("O"));
                if (n > 0) Log?.Detail($"Caché de red: {n} respuestas caducadas borradas ({bytes / (1024.0 * 1024.0):0.#} MB).");
            }
            catch { /* best-effort: la poda nunca debe molestar */ }
        });
    }

    private static void BorrarSinFallar(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }

    /// <summary>
    /// ¿Se puede usar este texto como respuesta? Una entrada que no se puede leer cuenta como
    /// ausente -se borra y se vuelve a preguntar- en vez de servirse durante un mes como «sin
    /// resultados».
    /// </summary>
    private static bool EsLegible(string texto, bool json)
        => json ? Parse(texto) != null : !string.IsNullOrWhiteSpace(texto);

    // ------------------------------------------------------------------ Limitador

    /// <summary>
    /// El límite de peticiones de cada servidor, según lo que PUBLICA cada servicio, con algo de
    /// margen. Sustituye a la espera fija de antes, que se pagaba ENTERA antes de cada petición
    /// aunque hiciera rato de la anterior: MEDIDO en una tirada real (195 canciones en 7 min 15 s),
    /// 316 s de los 435 eran espera pura.
    ///
    ///   · Deezer: 50 peticiones cada 5 s -> 45 (un 10 % de margen).
    ///   · MusicBrainz: 1 por segundo de media -> 1 cada 1,1 s.
    ///   · Discogs: 60 por minuto con token y 25 sin él -> 55 y 22.
    ///   · iTunes: «unas 20 por minuto». Es el más estricto y el que se rompe en silencio: al
    ///     pasarse responde 403, que se leía como «no está en el catálogo».
    ///   · AcoustID: 3 por segundo -> 3 cada 1,1 s.
    ///   · Spotify no publica cifra (ventana móvil de 30 s): se mantiene el ritmo de siempre, una
    ///     cada 150 ms, con el que no hay un solo 429 en los registros.
    ///   · kworb.net (chart de Spotify) no publica límite: una por segundo, por cortesía.
    /// Cualquier otro servidor usa <paramref name="throttleMs"/> como separación mínima (0 = sin
    /// límite; -1 = 200 ms).
    /// </summary>
    public static (string Servidor, LimiteServidor Limite) LimitePara(string url, int throttleMs = -1, bool conToken = false)
    {
        string host;
        try { host = new Uri(url).Host.ToLowerInvariant(); }
        catch { host = ""; }

        if (Es(host, "api.deezer.com")) return (host, new LimiteServidor(45, TimeSpan.FromSeconds(5)));
        if (Es(host, "musicbrainz.org")) return (host, LimiteServidor.Espaciado(1100));
        if (Es(host, "api.discogs.com"))
            return conToken ? (host + "#token", new LimiteServidor(55, TimeSpan.FromMinutes(1)))
                            : (host, new LimiteServidor(22, TimeSpan.FromMinutes(1)));
        if (Es(host, "itunes.apple.com")) return (host, new LimiteServidor(20, TimeSpan.FromMinutes(1)));
        if (Es(host, "api.acoustid.org")) return (host, new LimiteServidor(3, TimeSpan.FromMilliseconds(1100)));
        if (Es(host, "api.spotify.com")) return (host, LimiteServidor.Espaciado(150));
        if (Es(host, "kworb.net")) return (host, LimiteServidor.Espaciado(1000));
        return (host, LimiteServidor.Espaciado(throttleMs >= 0 ? throttleMs : 200));

        static bool Es(string h, string dominio) => h == dominio || h.EndsWith("." + dominio, StringComparison.Ordinal);
    }

    private Task TurnoAsync(string url, IDictionary<string, string>? headers, int throttleMs, CancellationToken ct)
    {
        var conToken = false;
        if (headers != null)
            foreach (var kv in headers)
                if (kv.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    && kv.Value.StartsWith("Discogs", StringComparison.OrdinalIgnoreCase)) conToken = true;
        var (servidor, limite) = LimitePara(url, throttleMs, conToken);
        return Limitador.EsperarTurnoAsync(servidor, limite, ct);
    }

    // ------------------------------------------------------------------ GET

    /// <summary>
    /// Igual que <see cref="GetAsync"/> pero devolviendo el cuerpo TAL CUAL, sin interpretarlo como
    /// JSON. Hace falta para las fuentes que publican HTML, como el chart de Spotify: así se
    /// aprovechan la caché, el limitador por dominio y los reintentos que ya hay aquí, en vez de
    /// abrir un camino aparte que no los tenga.
    /// </summary>
    public async Task<string?> GetTextAsync(string url, IDictionary<string, string>? headers = null,
        int throttleMs = -1, CancellationToken ct = default, bool useCache = true, TimeSpan? maxAge = null)
        => (await PedirAsync(url, headers, throttleMs, ct, useCache, maxAge, json: false).ConfigureAwait(false)).Texto;

    /// <summary>
    /// GET con caché + limitador + reintento. Devuelve el JSON parseado o null.
    /// <paramref name="throttleMs"/> solo cuenta para servidores sin límite conocido (ver
    /// <see cref="LimitePara"/>). <paramref name="maxAge"/> acorta la caducidad de la caché para
    /// lo que cambia a diario.
    /// </summary>
    public async Task<JsonNode?> GetAsync(string url, IDictionary<string, string>? headers = null,
        int throttleMs = -1, CancellationToken ct = default, bool useCache = true, TimeSpan? maxAge = null)
        => (await PedirAsync(url, headers, throttleMs, ct, useCache, maxAge, json: true).ConfigureAwait(false)).Json();

    /// <summary>
    /// GET que devuelve también CÓMO fue: el código HTTP, si vino de caché y si fue un fallo de red.
    /// Para quien necesita decidir por su propia respuesta y no por <see cref="LastApiError"/>,
    /// que comparten todos los servidores y no se limpia tras un acierto.
    /// </summary>
    public Task<RespuestaApi> GetRespuestaAsync(string url, IDictionary<string, string>? headers = null,
        int throttleMs = -1, CancellationToken ct = default, bool useCache = true, TimeSpan? maxAge = null)
        => PedirAsync(url, headers, throttleMs, ct, useCache, maxAge, json: true);

    private async Task<RespuestaApi> PedirAsync(string url, IDictionary<string, string>? headers, int throttleMs,
        CancellationToken ct, bool useCache, TimeSpan? maxAge, bool json)
    {
        var cp = (CacheOn && useCache) ? CachePath(url) : null;
        var cached = TryReadCache(cp, maxAge);
        if (cached != null)
        {
            if (EsLegible(cached, json))
            {
                Log?.Detail($"    GET {Short(url)} · caché");
                return new RespuestaApi(cached, 200, DeCache: true, FalloDeRed: false);
            }
            Log?.Detail($"    GET {Short(url)} · la caché no se puede leer, se descarta");
            BorrarSinFallar(cp!);
        }

        var sw = Stopwatch.StartNew();
        for (int intento = 1; ; intento++)
        {
            await TurnoAsync(url, headers, throttleMs, ct).ConfigureAwait(false);
            string motivo;
            int codigo;
            TimeSpan espera;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (headers != null)
                    foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var tx = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                codigo = (int)resp.StatusCode;
                if (resp.IsSuccessStatusCode)
                {
                    if (!EsErrorPasajeroDeDeezer(url, tx, out var codDeezer))
                    {
                        Interlocked.Increment(ref _cacheMiss);
                        if (cp != null && EsLegible(tx, json)) CacheStore(cp, tx);
                        Log?.Detail($"    GET {Short(url)} · HTTP {codigo} · {tx.Length} B · {sw.ElapsedMilliseconds} ms");
                        return new RespuestaApi(tx, codigo, DeCache: false, FalloDeRed: false);
                    }
                    // Deezer avisa de la cuota con un 200 y un JSON de error. Antes se guardaba en
                    // caché como si fuera la respuesta: esa búsqueda daba «0 resultados» un mes.
                    motivo = $"Deezer: cuota agotada (código {codDeezer})";
                    LastApiError = motivo;
                    espera = TimeSpan.FromSeconds(5);
                }
                else
                {
                    LastApiError = ($"HTTP {codigo} {tx}").Trim();
                    if (!EsPasajero(codigo))
                    {
                        // 4xx: el servidor ha contestado («no existe», «sin permiso»). Es una
                        // respuesta, no un fallo de red, y reintentar daría lo mismo.
                        Log?.Detail($"    GET {Short(url)} · HTTP {codigo} · {Trunc(tx, 200)}");
                        return new RespuestaApi(null, codigo, DeCache: false, FalloDeRed: false);
                    }
                    motivo = $"HTTP {codigo}";
                    espera = TimeSpan.FromSeconds(codigo is 429 or 503 ? RetryAfterSeconds(resp.Headers.RetryAfter) : 2);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Un tiempo de espera agotado llega como TaskCanceledException AUNQUE NADIE HAYA
                // CANCELADO. Antes se relanzaba tal cual, y un solo servidor colgado 30 s detenía el
                // análisis entero con «Análisis cancelado por el usuario». Es un fallo de red más.
                motivo = ex is OperationCanceledException
                    ? $"tiempo de espera agotado ({_http.Timeout.TotalSeconds:0} s)"
                    : $"FALLO: {ex.GetType().Name}: {ex.Message}";
                LastApiError = ex is OperationCanceledException ? motivo : ex.Message;
                codigo = 0;
                espera = TimeSpan.FromSeconds(2);
            }

            if (intento >= IntentosGet)
            {
                Log?.Detail($"    GET {Short(url)} · {motivo}, se abandona tras {intento} intentos");
                AnotarFalloDeRed();
                return new RespuestaApi(null, codigo, DeCache: false, FalloDeRed: true);
            }
            Log?.Detail($"    GET {Short(url)} · {motivo}, reintento en {espera.TotalSeconds:0}s");
            await Limitador.EsperarAsync(espera, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Códigos que hablan del servidor y no de la petición: merece la pena volver a intentarlo.</summary>
    private static bool EsPasajero(int codigo) => codigo is 408 or 429 || codigo >= 500;

    /// <summary>
    /// Deezer responde «cuota agotada» (código 4) o «servicio ocupado» (700) con un HTTP 200 y un
    /// JSON de error. Para el resto del programa eso tiene que ser un fallo de red, no una búsqueda
    /// sin resultados.
    /// </summary>
    internal static bool EsErrorPasajeroDeDeezer(string url, string cuerpo, out int codigo)
    {
        codigo = 0;
        if (!url.Contains("api.deezer.com", StringComparison.OrdinalIgnoreCase)) return false;
        var t = cuerpo.TrimStart();
        if (t.Length > 4000 || !t.StartsWith("{\"error\"", StringComparison.Ordinal)) return false;
        try { int.TryParse(JsonNode.Parse(t)?["error"]?["code"]?.ToString(), out codigo); }
        catch { return false; }
        return codigo is 4 or 700;
    }

    /// <summary>Acorta la URL para el log: host + ruta + query recortada (sin claves largas).</summary>
    private static string Short(string url)
    {
        try
        {
            var u = new Uri(url);
            var q = u.Query;
            // No se registran claves de API aunque viajen en la query.
            q = System.Text.RegularExpressions.Regex.Replace(q, @"(?i)([?&](?:client|api|token|key)[^=]*=)[^&]+", "$1***");
            return u.Host + u.AbsolutePath + Trunc(q, 120);
        }
        catch { return Trunc(url, 120); }
    }

    private static string Trunc(string? s, int n)
    {
        s ??= "";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    // ------------------------------------------------------------------ POST y descargas

    /// <summary>POST application/x-www-form-urlencoded. Devuelve el cuerpo crudo o null. Sin caché.</summary>
    public Task<string?> PostFormAsync(string url, string body, IDictionary<string, string>? headers = null,
        int throttleMs = 0, CancellationToken ct = default)
        => PostAsync(url, () => new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"), headers, throttleMs, ct);

    /// <summary>POST application/json. Devuelve el cuerpo crudo o null; en error LastApiError = "HTTP {code} {body}".</summary>
    public Task<string?> PostJsonAsync(string url, string json, IDictionary<string, string>? headers = null,
        int throttleMs = 0, CancellationToken ct = default)
        => PostAsync(url, () => new StringContent(json, Encoding.UTF8, "application/json"), headers, throttleMs, ct);

    private async Task<string?> PostAsync(string url, Func<HttpContent> contenido, IDictionary<string, string>? headers,
        int throttleMs, CancellationToken ct)
    {
        await TurnoAsync(url, headers, throttleMs, ct).ConfigureAwait(false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = contenido() };
            if (headers != null) foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var tx = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LastApiError = ($"HTTP {(int)resp.StatusCode} {tx}").Trim();
                if (EsPasajero((int)resp.StatusCode)) AnotarFalloDeRed();
                return null;
            }
            return tx;
        }
        // Solo es cancelar si lo pidió el usuario: un tiempo agotado es un fallo de red (ver GET).
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastApiError = ex is OperationCanceledException ? $"tiempo de espera agotado ({_http.Timeout.TotalSeconds:0} s)" : ex.Message;
            Log?.Detail($"    POST {Short(url)} · {LastApiError}");
            AnotarFalloDeRed();
            return null;
        }
    }

    /// <summary>POST form que devuelve JSON parseado (para el token de Spotify).</summary>
    public async Task<JsonNode?> PostFormJsonAsync(string url, string body, IDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        var raw = await PostFormAsync(url, body, headers, 0, ct).ConfigureAwait(false);
        return raw == null ? null : Parse(raw);
    }

    /// <summary>Descarga cruda de bytes (para carátulas).</summary>
    public async Task<byte[]?> GetBytesAsync(string url, CancellationToken ct = default)
    {
        try { return await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastApiError = ex is OperationCanceledException ? $"tiempo de espera agotado ({_http.Timeout.TotalSeconds:0} s)" : ex.Message;
            AnotarFalloDeRed();
            return null;
        }
    }

    private static int RetryAfterSeconds(RetryConditionHeaderValue? ra)
    {
        int s = 1;
        if (ra?.Delta is TimeSpan d) s = (int)d.TotalSeconds;
        if (s < 1) s = 1;
        if (s > 4) s = 4;
        return s;
    }

    private static JsonNode? Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try { return JsonNode.Parse(raw); } catch { return null; }
    }

    internal static JsonNode? ParseJson(string? raw) => Parse(raw);

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}

/// <summary>
/// Cómo terminó una GET. <see cref="Texto"/> es null si no hubo respuesta útil; entonces
/// <see cref="FalloDeRed"/> dice si fue porque no se pudo preguntar (y no porque no exista).
/// </summary>
/// <param name="Texto">El cuerpo de la respuesta, o null.</param>
/// <param name="Codigo">Código HTTP (200 si vino de caché; 0 si ni siquiera hubo respuesta).</param>
/// <param name="DeCache">Se sirvió de la caché en disco.</param>
/// <param name="FalloDeRed">Sin conexión, tiempo agotado, 5xx, 429 o cuota: la ausencia de datos no significa nada.</param>
public sealed record RespuestaApi(string? Texto, int Codigo, bool DeCache, bool FalloDeRed)
{
    /// <summary>El cuerpo interpretado como JSON (null si no hay cuerpo o no es JSON).</summary>
    public JsonNode? Json() => ApiClient.ParseJson(Texto);
}

/// <summary>
/// Fallos de red de un tramo concreto del programa (ver <see cref="ApiClient.MedirFallos"/>).
/// Las mediciones se pueden anidar: un fallo cuenta en la de dentro y en todas las de fuera.
/// </summary>
public sealed class MedicionFallos : IDisposable
{
    private static readonly AsyncLocal<MedicionFallos?> Actual = new();
    private readonly MedicionFallos? _exterior;
    private int _fallos;

    internal MedicionFallos()
    {
        _exterior = Actual.Value;
        Actual.Value = this;
    }

    /// <summary>Peticiones que se quedaron sin respuesta válida dentro de este tramo.</summary>
    public int Fallos => Volatile.Read(ref _fallos);

    /// <summary>Hubo al menos un fallo: lo que se concluya de este tramo puede estar incompleto.</summary>
    public bool HuboFallos => Fallos > 0;

    internal static void Anotar()
    {
        for (var m = Actual.Value; m != null; m = m._exterior) Interlocked.Increment(ref m._fallos);
    }

    public void Dispose()
    {
        if (ReferenceEquals(Actual.Value, this)) Actual.Value = _exterior;
    }
}

/// <summary>
/// Límite de un servidor: como mucho <see cref="Peticiones"/> cada <see cref="Ventana"/>. Con una
/// sola petición por ventana es una separación mínima entre dos seguidas.
/// </summary>
public readonly record struct LimiteServidor(int Peticiones, TimeSpan Ventana)
{
    public static LimiteServidor Espaciado(int ms) => new(1, TimeSpan.FromMilliseconds(Math.Max(0, ms)));
    public bool SinLimite => Peticiones <= 0 || Ventana <= TimeSpan.Zero;
}

/// <summary>
/// Limitador de peticiones por servidor, con ventana deslizante: recuerda cuándo EMPEZÓ cada una
/// de las últimas N peticiones y solo hace esperar lo que falte para que la más antigua salga de
/// la ventana. Si ya ha pasado ese tiempo -lo normal, porque entre dos llamadas a Deezer suele
/// haber otra a Discogs-, no se espera nada.
///
/// Es seguro con varias canciones en paralelo: cada servidor tiene su turno (un SemaphoreSlim),
/// así que el ritmo se respeta aunque los avisos lleguen a la vez. Cancelar mientras se espera
/// sale enseguida y libera el turno.
/// </summary>
public sealed class LimitadorPeticiones
{
    private sealed class Servidor
    {
        public readonly SemaphoreSlim Turno = new(1, 1);
        public readonly Queue<TimeSpan> Inicios = new();
    }

    private static readonly Stopwatch Reloj = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<string, Servidor> _servidores = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<TimeSpan> _ahora;
    private readonly Func<TimeSpan, CancellationToken, Task> _esperar;

    /// <param name="ahora">Reloj monótono; por defecto, el de la máquina. Las pruebas pasan uno simulado.</param>
    /// <param name="esperar">Cómo se espera; por defecto, Task.Delay.</param>
    public LimitadorPeticiones(Func<TimeSpan>? ahora = null, Func<TimeSpan, CancellationToken, Task>? esperar = null)
    {
        _ahora = ahora ?? (() => Reloj.Elapsed);
        _esperar = esperar ?? ((t, ct) => Task.Delay(t, ct));
    }

    /// <summary>Espera con el reloj de este limitador (los reintentos también pasan por aquí).</summary>
    public Task EsperarAsync(TimeSpan t, CancellationToken ct)
        => t > TimeSpan.Zero ? _esperar(t, ct) : Task.CompletedTask;

    /// <summary>
    /// Espera el turno para empezar una petición a <paramref name="servidor"/> y lo anota.
    /// Devuelve cuánto hubo que esperar (cero si no hizo falta).
    /// </summary>
    public async Task<TimeSpan> EsperarTurnoAsync(string servidor, LimiteServidor limite, CancellationToken ct = default)
    {
        if (limite.SinLimite) return TimeSpan.Zero;
        var s = _servidores.GetOrAdd(servidor ?? "", _ => new Servidor());
        await s.Turno.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ahora = _ahora();
            // Lo que ya salió de la ventana no cuenta; de lo que queda, solo importan las N últimas.
            while (s.Inicios.Count > 0 && ahora - s.Inicios.Peek() >= limite.Ventana) s.Inicios.Dequeue();
            while (s.Inicios.Count > limite.Peticiones) s.Inicios.Dequeue();

            var esperado = TimeSpan.Zero;
            if (s.Inicios.Count >= limite.Peticiones)
            {
                var libre = s.Inicios.Peek() + limite.Ventana;
                esperado = libre - ahora;
                if (esperado > TimeSpan.Zero) await _esperar(esperado, ct).ConfigureAwait(false);
                else esperado = TimeSpan.Zero;
                s.Inicios.Dequeue();
                var tras = _ahora();
                ahora = tras > libre ? tras : libre;
            }
            s.Inicios.Enqueue(ahora);
            return esperado;
        }
        finally { s.Turno.Release(); }
    }
}
