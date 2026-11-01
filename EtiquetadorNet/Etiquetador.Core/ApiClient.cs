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
    private Task? _poda;

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
    /// La poda automática ya lanzada, o null si aún no se ha tocado la caché. Existe para que las
    /// pruebas esperen a que TERMINE en vez de mirar el disco durante un plazo fijo: la poda corre en
    /// un hilo del pool y empieza cuando queda uno libre. MEDIDO en octubre de 2026 con la batería
    /// completa y otra compilación a la vez: llegó a empezar 0,3 s después de pedirla, aunque luego
    /// solo tarde unos milisegundos, y la prueba que le daba 5 s falló alguna vez en Windows.
    /// </summary>
    internal Task? PodaEnCurso => Volatile.Read(ref _poda);

    /// <summary>
    /// Peticiones que se quedaron SIN respuesta válida desde que se creó el cliente: sin conexión,
    /// tiempo de espera agotado, error 5xx, 429, la «cuota agotada» de Deezer, el 403 con que iTunes
    /// avisa de su límite, un 200 que no es JSON cuando se pedía JSON (una página de mantenimiento) o
    /// una petición que no se hizo porque el servidor estaba en pausa (<see cref="Cortacircuitos"/>),
    /// ya contado el reintento. Una búsqueda que responde bien sin resultados, un 404 (también de una
    /// carátula) o un 401/403 de clave mala NO cuentan: eso sí es una respuesta.
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
        // Con el reloj del limitador: las pruebas que lo simulan simulan también las pausas.
        Cortacircuitos = new CortacircuitosRed(() => Limitador.Ahora);
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
        Volatile.Write(ref _poda, Task.Run(() =>
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
        }));
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

    /// <summary>
    /// Espera a partir de la cual se deja constancia en el registro. Visto en revisión (C9): con
    /// iTunes a 20 por minuto, una racha de canciones que Deezer no tiene hace esperar hasta ~60 s
    /// por petición, y el registro no decía nada; parecía que la aplicación se había colgado.
    /// </summary>
    public static readonly TimeSpan EsperaQueSeAnota = TimeSpan.FromSeconds(3);

    private async Task TurnoAsync(string url, IDictionary<string, string>? headers, int throttleMs, CancellationToken ct)
    {
        var conToken = false;
        if (headers != null)
            foreach (var kv in headers)
                if (kv.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    && kv.Value.StartsWith("Discogs", StringComparison.OrdinalIgnoreCase)) conToken = true;
        var (servidor, limite) = LimitePara(url, throttleMs, conToken);
        var esperado = await Limitador.EsperarTurnoAsync(servidor, limite, ct).ConfigureAwait(false);
        if (esperado >= EsperaQueSeAnota)
            Log?.Detail($"    espera de {esperado.TotalSeconds:0} s por el límite de peticiones de {servidor.Split('#')[0]} " +
                        $"({limite.Peticiones} cada {limite.Ventana.TotalSeconds:0.#} s)");
    }

    // ------------------------------------------------------------------ Cortacircuitos

    /// <summary>Deja de preguntar un rato a un servidor que no responde (ver <see cref="CortacircuitosRed"/>).</summary>
    public CortacircuitosRed Cortacircuitos { get; }

    private static string Servidor(string url)
    {
        try { return new Uri(url).Host.ToLowerInvariant(); }
        catch { return ""; }
    }

    /// <summary>
    /// Si el servidor está en pausa por el cortacircuitos, cuenta la petición como fallo de red
    /// SIN hacerla y devuelve true. Así un servidor muerto cuesta un instante por petición y no los
    /// 62 s de dos tiempos agotados más la espera.
    /// </summary>
    private bool EnPausa(string url, string metodo)
    {
        var servidor = Servidor(url);
        if (Cortacircuitos.PuedePasar(servidor, out var falta)) return false;
        LastApiError = $"{servidor} no responde; no se le pregunta durante {Math.Ceiling(falta.TotalSeconds):0} s más";
        Log?.Detail($"    {metodo} {Short(url)} · {LastApiError}");
        AnotarFalloDeRed();
        return true;
    }

    /// <summary>Anota cómo acabó una petición en el cortacircuitos (y avisa si con ella se abre).</summary>
    private void AnotarEnCortacircuitos(string url, bool fallo)
    {
        var servidor = Servidor(url);
        if (!fallo) { Cortacircuitos.Exito(servidor); return; }
        if (Cortacircuitos.Fallo(servidor) is { } pausa)
            Log?.Log($"{servidor} no responde ({CortacircuitosRed.FallosParaAbrir} peticiones seguidas sin respuesta): " +
                     $"se deja de preguntarle durante {pausa.TotalSeconds:0} s. Lo que se analice mientras tanto no se guarda como «no encontrada».",
                     LogKind.Err);
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
            // Se interpreta UNA vez y el nodo viaja en la respuesta: antes se leía aquí para ver si
            // era legible y otra vez en Json(), unos 100.000 análisis de JSON de más en un
            // reanálisis de 15-20k canciones servido desde la caché.
            JsonNode? nodo = null;
            var legible = json ? (nodo = Parse(cached)) != null : !string.IsNullOrWhiteSpace(cached);
            // C10: las versiones anteriores guardaban la «cuota agotada» de Deezer ({"error":{"code":4}}
            // con HTTP 200) como si fuera la respuesta. Es JSON legible, y se servía hasta 30 días
            // como «0 resultados»; se borra y se vuelve a preguntar.
            var cuota = legible && EsErrorPasajeroDeDeezer(url, cached, out _);
            if (legible && !cuota)
            {
                Log?.Detail($"    GET {Short(url)} · caché");
                return new RespuestaApi(cached, 200, DeCache: true, FalloDeRed: false) { Nodo = nodo };
            }
            Log?.Detail(cuota
                ? $"    GET {Short(url)} · la caché guarda un aviso de cuota de Deezer, se descarta"
                : $"    GET {Short(url)} · la caché no se puede leer, se descarta");
            BorrarSinFallar(cp!);
        }

        if (EnPausa(url, "GET")) return new RespuestaApi(null, 0, DeCache: false, FalloDeRed: true);

        var sw = Stopwatch.StartNew();
        for (int intento = 1; ; intento++)
        {
            await TurnoAsync(url, headers, throttleMs, ct).ConfigureAwait(false);
            string motivo;
            var noContesta = true;   // para el cortacircuitos: un límite de peticiones SÍ es una respuesta
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
                    JsonNode? nodo = null;
                    var legible = json ? (nodo = Parse(tx)) != null : true;
                    if (!legible)
                    {
                        // C8a: un 200 que no es JSON cuando se pedía JSON es una página de
                        // mantenimiento o de Cloudflare, no una búsqueda vacía. Antes salía como
                        // «0 resultados» sin contar como fallo, y la canción se guardaba como no
                        // encontrada.
                        motivo = $"HTTP {codigo} sin JSON ({Trunc(tx, 60)})";
                        LastApiError = motivo;
                        espera = TimeSpan.FromSeconds(2);
                    }
                    else if (!EsErrorPasajeroDeDeezer(url, tx, out var codDeezer))
                    {
                        Interlocked.Increment(ref _cacheMiss);
                        if (cp != null && EsLegible(tx, json: false)) CacheStore(cp, tx);   // el JSON ya se ha comprobado arriba
                        Log?.Detail($"    GET {Short(url)} · HTTP {codigo} · {tx.Length} B · {sw.ElapsedMilliseconds} ms");
                        AnotarEnCortacircuitos(url, fallo: false);
                        return new RespuestaApi(tx, codigo, DeCache: false, FalloDeRed: false) { Nodo = nodo };
                    }
                    else
                    {
                        // Deezer avisa de la cuota con un 200 y un JSON de error. Antes se guardaba en
                        // caché como si fuera la respuesta: esa búsqueda daba «0 resultados» un mes.
                        motivo = $"Deezer: cuota agotada (código {codDeezer})";
                        LastApiError = motivo;
                        espera = TimeSpan.FromSeconds(5);
                        noContesta = false;
                    }
                }
                else
                {
                    LastApiError = ($"HTTP {codigo} {tx}").Trim();
                    if (!EsPasajero(url, codigo))
                    {
                        // 4xx: el servidor ha contestado («no existe», «sin permiso»). Es una
                        // respuesta, no un fallo de red, y reintentar daría lo mismo.
                        Log?.Detail($"    GET {Short(url)} · HTTP {codigo} · {Trunc(tx, 200)}");
                        AnotarEnCortacircuitos(url, fallo: false);
                        return new RespuestaApi(null, codigo, DeCache: false, FalloDeRed: false);
                    }
                    motivo = codigo == 403 ? $"HTTP 403 (límite de peticiones de {Servidor(url)})" : $"HTTP {codigo}";
                    espera = TimeSpan.FromSeconds(codigo is 429 or 503 ? RetryAfterSeconds(resp.Headers.RetryAfter)
                                                 : codigo == 403 ? 5 : 2);
                    noContesta = NoContesta(codigo);
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
                AnotarEnCortacircuitos(url, fallo: noContesta);
                return new RespuestaApi(null, codigo, DeCache: false, FalloDeRed: true);
            }
            Log?.Detail($"    GET {Short(url)} · {motivo}, reintento en {espera.TotalSeconds:0}s");
            await Limitador.EsperarAsync(espera, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Para el cortacircuitos: ¿el servidor NO ha contestado? Sin respuesta (0), 408 o 5xx. Un 429,
    /// el 403 de iTunes o la cuota de Deezer sí son respuestas -«más despacio»-: de eso se encargan
    /// el limitador y cada proveedor (Spotify lleva su propio bloqueo por 429), no esta pausa.
    /// </summary>
    private static bool NoContesta(int codigo) => codigo is 0 or 408 || codigo >= 500;

    /// <summary>
    /// Códigos que hablan del servidor y no de la petición: merece la pena volver a intentarlo, y si
    /// no responde, es un fallo de red.
    ///
    /// El 403 de iTunes también (C8a): es como Apple avisa de que se ha pasado del límite de
    /// peticiones (ver <see cref="LimitePara"/>), y se leía como «no está en el catálogo». En los
    /// demás servidores un 403 es «sin permiso» (una clave mala): una respuesta, no un fallo.
    /// </summary>
    private static bool EsPasajero(string url, int codigo)
    {
        if (codigo is 408 or 429 || codigo >= 500) return true;
        if (codigo != 403) return false;
        var servidor = Servidor(url);
        return servidor == "itunes.apple.com" || servidor.EndsWith(".itunes.apple.com", StringComparison.Ordinal);
    }

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
        if (EnPausa(url, "POST")) return null;
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
                var pasajero = EsPasajero(url, (int)resp.StatusCode);
                if (pasajero) AnotarFalloDeRed();
                AnotarEnCortacircuitos(url, fallo: pasajero && NoContesta((int)resp.StatusCode));
                return null;
            }
            AnotarEnCortacircuitos(url, fallo: false);
            return tx;
        }
        // Solo es cancelar si lo pidió el usuario: un tiempo agotado es un fallo de red (ver GET).
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastApiError = ex is OperationCanceledException ? $"tiempo de espera agotado ({_http.Timeout.TotalSeconds:0} s)" : ex.Message;
            Log?.Detail($"    POST {Short(url)} · {LastApiError}");
            AnotarFalloDeRed();
            AnotarEnCortacircuitos(url, fallo: true);
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

    /// <summary>
    /// Descarga cruda de bytes (para carátulas). Null si no hay; un 404 (la carátula ya no existe)
    /// es una respuesta y NO cuenta como fallo de red, como documenta <see cref="FallosDeRed"/>.
    /// Antes GetByteArrayAsync lanzaba con cualquier código de error y todo se contaba como fallo.
    /// </summary>
    public async Task<byte[]?> GetBytesAsync(string url, CancellationToken ct = default)
    {
        if (EnPausa(url, "GET")) return null;
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var codigo = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode)
            {
                LastApiError = $"HTTP {codigo}";
                var pasajero = EsPasajero(url, codigo);
                if (pasajero) AnotarFalloDeRed();
                AnotarEnCortacircuitos(url, fallo: pasajero && NoContesta(codigo));
                return null;
            }
            var datos = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            AnotarEnCortacircuitos(url, fallo: false);
            return datos;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastApiError = ex is OperationCanceledException ? $"tiempo de espera agotado ({_http.Timeout.TotalSeconds:0} s)" : ex.Message;
            AnotarFalloDeRed();
            AnotarEnCortacircuitos(url, fallo: true);
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
/// <param name="FalloDeRed">
/// Sin conexión, tiempo agotado, 5xx, 429, cuota de Deezer, 403 de iTunes, un 200 que no es JSON, o
/// el servidor en pausa por el cortacircuitos: la ausencia de datos no significa nada.
/// </param>
public sealed record RespuestaApi(string? Texto, int Codigo, bool DeCache, bool FalloDeRed)
{
    /// <summary>El JSON ya interpretado por ApiClient, para no leerlo dos veces (null si no lo hizo).</summary>
    internal JsonNode? Nodo { get; init; }

    /// <summary>El cuerpo interpretado como JSON (null si no hay cuerpo o no es JSON).</summary>
    public JsonNode? Json() => Nodo ?? ApiClient.ParseJson(Texto);
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

    /// <summary>La hora del reloj de este limitador (monótono; simulado en las pruebas).</summary>
    public TimeSpan Ahora => _ahora();

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

/// <summary>
/// Cortacircuitos por servidor: tras <see cref="FallosParaAbrir"/> peticiones SEGUIDAS sin
/// respuesta a un mismo servidor, se deja de preguntarle durante una pausa (30 s la primera vez,
/// que se dobla en cada recaída hasta 5 min). Cualquier respuesta, también un 404, lo cierra.
///
/// POR QUÉ. Desde C7 un tiempo agotado ya no aborta el análisis: cuenta como fallo y se sigue. Pero
/// un servidor que acepta la conexión y no contesta cuesta entonces 30 s + 2 s + 30 s POR PETICIÓN,
/// y un Discogs colgado convertía una tirada de 15.000 canciones en días, sin ningún aviso. Con la
/// pausa, mientras no responde cada petición falla al instante (y cuenta como fallo de red: lo que
/// se analice no se guarda como «no encontrada»), y pasada la pausa se vuelve a probar con una.
/// </summary>
public sealed class CortacircuitosRed
{
    /// <summary>Peticiones seguidas sin respuesta que abren el circuito.</summary>
    public const int FallosParaAbrir = 3;

    /// <summary>Primera pausa; se dobla en cada recaída.</summary>
    public static readonly TimeSpan PausaInicial = TimeSpan.FromSeconds(30);

    /// <summary>Pausa máxima: un servidor caído un buen rato se sigue probando cada 5 min.</summary>
    public static readonly TimeSpan PausaMaxima = TimeSpan.FromMinutes(5);

    private sealed class Estado
    {
        public int Seguidos;
        public TimeSpan Pausa;
        public TimeSpan AbiertoHasta;
    }

    private readonly ConcurrentDictionary<string, Estado> _estados = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<TimeSpan> _ahora;

    /// <param name="ahora">Reloj monótono (el del limitador; simulado en las pruebas).</param>
    public CortacircuitosRed(Func<TimeSpan> ahora) => _ahora = ahora;

    /// <summary>¿Se puede preguntar ya a este servidor? Si no, cuánto falta.</summary>
    public bool PuedePasar(string servidor, out TimeSpan falta)
    {
        falta = TimeSpan.Zero;
        if (!_estados.TryGetValue(servidor ?? "", out var e)) return true;
        lock (e)
        {
            var ahora = _ahora();
            if (ahora >= e.AbiertoHasta) return true;
            falta = e.AbiertoHasta - ahora;
            return false;
        }
    }

    /// <summary>El servidor respondió: se olvidan los fallos y la pausa.</summary>
    public void Exito(string servidor)
    {
        if (!_estados.TryGetValue(servidor ?? "", out var e)) return;
        lock (e) { e.Seguidos = 0; e.Pausa = TimeSpan.Zero; e.AbiertoHasta = TimeSpan.Zero; }
    }

    /// <summary>
    /// Una petición más sin respuesta. Devuelve la pausa si con ella se abre el circuito (para
    /// avisar una sola vez), o null.
    /// </summary>
    public TimeSpan? Fallo(string servidor)
    {
        var e = _estados.GetOrAdd(servidor ?? "", _ => new Estado());
        lock (e)
        {
            e.Seguidos++;
            if (e.Seguidos < FallosParaAbrir) return null;
            e.Pausa = e.Pausa == TimeSpan.Zero ? PausaInicial
                    : (e.Pausa + e.Pausa < PausaMaxima ? e.Pausa + e.Pausa : PausaMaxima);
            e.AbiertoHasta = _ahora() + e.Pausa;
            return e.Pausa;
        }
    }
}
