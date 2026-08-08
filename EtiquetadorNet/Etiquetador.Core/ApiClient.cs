using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Etiquetador.Core;

/// <summary>
/// Cliente HTTP con caché por URL en disco + throttle por dominio + backoff en 429/503.
/// Port de Invoke-Api / Cache-Path / Cache-Store / Infer-Throttle del .ps1.
/// La caché es compatible con la del .ps1 (misma carpeta, mismo hash MD5 de la URL).
/// </summary>
public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly AppPaths _paths;

    /// <summary>Log opcional: cada petición queda trazada en el archivo (no en la UI).</summary>
    public Logger? Log { get; set; }

    public bool CacheOn { get; set; } = true;
    public int CacheTtlDays { get; set; } = 30;
    public int CacheHits { get; private set; }
    public int CacheMiss { get; private set; }
    public string LastApiError { get; set; } = "";

    public ApiClient(AppPaths paths, HttpClient? http = null)
    {
        _paths = paths;
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

    public void CacheStore(string cachePath, string text)
    {
        try
        {
            var d = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
            File.WriteAllText(cachePath, text ?? "", Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }

    public static int InferThrottle(string url)
    {
        if (url.Contains("musicbrainz")) return 1100;
        if (url.Contains("discogs")) return 1000;
        if (url.Contains("deezer")) return 350;
        if (url.Contains("spotify")) return 150;
        return 200;
    }

    /// <summary>Lee la caché cruda para una URL/clave (o null si no hay o caducó). Para AcoustID (cachea por huella).</summary>
    public string? ReadCache(string url) => TryReadCache(CacheOn ? CachePath(url) : null);

    // Devuelve el texto crudo desde caché (o null); incrementa CacheHits si sirve.
    private string? TryReadCache(string? cp)
    {
        if (!CacheOn || cp is null) return null;
        try
        {
            if (File.Exists(cp) && (DateTime.Now - File.GetLastWriteTime(cp)).TotalDays <= CacheTtlDays)
            {
                var raw = File.ReadAllText(cp, Encoding.UTF8);
                CacheHits++;
                return raw;
            }
        }
        catch { /* cae a red */ }
        return null;
    }

    /// <summary>GET con caché+throttle+backoff. Devuelve el JSON parseado o null.</summary>
    public async Task<JsonNode?> GetAsync(string url, IDictionary<string, string>? headers = null,
        int throttleMs = -1, CancellationToken ct = default, bool useCache = true)
    {
        var cp = (CacheOn && useCache) ? CachePath(url) : null;
        var cached = TryReadCache(cp);
        if (cached != null) { Log?.Detail($"    GET {Short(url)} · caché"); return Parse(cached); }

        var th = throttleMs >= 0 ? throttleMs : InferThrottle(url);
        if (th > 0) await Task.Delay(th, ct).ConfigureAwait(false);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 4; i++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (headers != null)
                    foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var tx = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    int code = (int)resp.StatusCode;
                    LastApiError = ($"HTTP {code} {tx}").Trim();
                    if (code == 429 || code == 503)
                    {
                        if (i >= 1) { Log?.Detail($"    GET {Short(url)} · HTTP {code}, se abandona tras 2 intentos"); return null; }
                        int ra = RetryAfterSeconds(resp.Headers.RetryAfter);
                        Log?.Detail($"    GET {Short(url)} · HTTP {code}, reintento en {ra}s");
                        await Task.Delay(ra * 1000, ct).ConfigureAwait(false);
                        continue;
                    }
                    Log?.Detail($"    GET {Short(url)} · HTTP {code} · {Trunc(tx, 200)}");
                    return null;
                }
                CacheMiss++;
                if (CacheOn && cp != null) CacheStore(cp, tx);
                Log?.Detail($"    GET {Short(url)} · HTTP 200 · {tx.Length} B · {sw.ElapsedMilliseconds} ms");
                return Parse(tx);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LastApiError = ex.Message;
                Log?.Detail($"    GET {Short(url)} · FALLO: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
        return null;
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

    /// <summary>POST application/x-www-form-urlencoded. Devuelve el cuerpo crudo o null. Sin caché.</summary>
    public async Task<string?> PostFormAsync(string url, string body, IDictionary<string, string>? headers = null,
        int throttleMs = 0, CancellationToken ct = default)
    {
        if (throttleMs > 0) await Task.Delay(throttleMs, ct).ConfigureAwait(false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
            };
            if (headers != null) foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var tx = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { LastApiError = ($"HTTP {(int)resp.StatusCode} {tx}").Trim(); return null; }
            return tx;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LastApiError = ex.Message; return null; }
    }

    /// <summary>POST application/json. Devuelve el cuerpo crudo o null; en error LastApiError = "HTTP {code} {body}".</summary>
    public async Task<string?> PostJsonAsync(string url, string json, IDictionary<string, string>? headers = null,
        int throttleMs = 0, CancellationToken ct = default)
    {
        if (throttleMs > 0) await Task.Delay(throttleMs, ct).ConfigureAwait(false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            if (headers != null) foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var tx = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { LastApiError = ($"HTTP {(int)resp.StatusCode} {tx}").Trim(); return null; }
            return tx;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LastApiError = ex.Message; return null; }
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
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LastApiError = ex.Message; return null; }
    }

    private static int RetryAfterSeconds(RetryConditionHeaderValue? ra)
    {
        int s = 1;
        if (ra?.Delta is TimeSpan d) s = (int)d.TotalSeconds;
        if (s < 1) s = 1;
        if (s > 4) s = 4;
        return s;
    }

    private static JsonNode? Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try { return JsonNode.Parse(raw); } catch { return null; }
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
