using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etiquetador.Core.Providers;

namespace Etiquetador.Core.Ai;

/// <summary>Propuesta de la IA para un nombre de archivo sucio (solo PROPONE; se verifica luego contra Deezer/iTunes).</summary>
public sealed record AiParse(string Artist, string Title, string Version, bool IsMashup, double Confidence);

/// <summary>
/// IA local a través de Ollama (http://localhost:11434). No requiere clave ni conexión a internet.
///
/// Su cometido NO es identificar audio —de eso se encarga la huella acústica— sino LIMPIAR nombres de
/// archivo contaminados (etiquetas de pool, BPM, tonalidad, nombres de editor) y convertirlos en una
/// consulta aprovechable. La propuesta se re-verifica siempre contra el catálogo antes de escribir nada,
/// de modo que una invención del modelo nunca llega a los archivos.
///
/// Si Ollama no está instalado o no responde, la función se desactiva sin entorpecer el análisis.
/// </summary>
public sealed class OllamaClient
{
    public const string DefaultHost = "http://localhost:11434";
    public const string DefaultModel = "llama3.2";

    private readonly ApiClient _api;
    private readonly Logger? _log;
    private readonly HttpClient _http;

    /// <summary>Se pone a true cuando Ollama no está disponible: evita reintentar en cada archivo.</summary>
    public bool AiBlocked { get; private set; }
    public int AiCalls { get; private set; }
    private string? _autoModel;
    private bool _probed;

    public string Host { get; set; } = DefaultHost;

    public OllamaClient(ApiClient api, Logger? log = null, HttpClient? http = null)
    {
        _api = api;
        _log = log;
        // Un modelo local puede tardar bastante en la primera llamada (carga el modelo en memoria),
        // así que necesita mucho más margen que el cliente compartido de las APIs web.
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
    }

    /// <summary>Modelos instalados en Ollama. null si Ollama no está disponible.</summary>
    public async Task<List<string>?> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            // Sondeo corto: si no hay nadie escuchando, no merece la pena esperar.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var resp = await _http.GetAsync($"{Host}/api/tags", cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var arr = J.A(J.P(JsonNode.Parse(raw), "models"));
            if (arr == null) return null;
            var outp = new List<string>();
            foreach (var m in arr)
            {
                var n = J.S(J.P(m, "name"));
                if (n.Length > 0) outp.Add(n);
            }
            return outp;
        }
        catch { return null; }
    }

    /// <summary>true si Ollama responde y tiene al menos un modelo instalado.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => await ListModelsAsync(ct).ConfigureAwait(false) is { Count: > 0 };

    /// <summary>true si Ollama responde, tenga modelos o no.</summary>
    public async Task<bool> IsRunningAsync(CancellationToken ct = default)
        => await ListModelsAsync(ct).ConfigureAwait(false) != null;

    /// <summary>
    /// Espera a que Ollama responda, hasta el tope indicado. Recién arrancado tarda unos segundos
    /// en aceptar peticiones, así que preguntarle una sola vez daría un "no está" equivocado.
    /// </summary>
    public async Task<bool> WaitUntilRunningAsync(TimeSpan tope, CancellationToken ct = default)
    {
        var hasta = DateTime.UtcNow + tope;
        while (true)
        {
            if (await IsRunningAsync(ct).ConfigureAwait(false)) return true;
            if (DateTime.UtcNow >= hasta) return false;
            await Task.Delay(700, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Vuelve a habilitar la IA tras haberse desactivado sola. Necesario cuando Ollama no estaba
    /// en marcha al empezar y se arranca después: si no, seguiría descartada el resto de la tirada.
    /// </summary>
    public void Reset()
    {
        AiBlocked = false;
        _probed = false;
        _autoModel = null;
    }

    /// <summary>
    /// Descarga un modelo. Son varios GB, así que informa del avance: Ollama responde con una línea
    /// JSON por cada actualización de estado. Devuelve el error, o "" si fue bien.
    /// </summary>
    public async Task<string> PullModelAsync(string model, IProgress<(string Estado, double Fraccion)>? progreso,
                                             CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model)) return "No se ha indicado ningún modelo.";
        try
        {
            var body = JsonSerializer.Serialize(new { model, stream = true });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Host}/api/pull")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };

            // Sin esto se esperaría a tener el cuerpo entero: hay que leerlo según llega para ver el avance.
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var det = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return $"Ollama respondió {(int)resp.StatusCode}. {Trim(det)}";
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var sr = new StreamReader(stream);
            string? linea;
            var ultimo = "";
            while ((linea = await sr.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                if (linea.Length == 0) continue;
                JsonNode? j;
                try { j = JsonNode.Parse(linea); } catch { continue; }

                var err = J.S(J.P(j, "error"));
                if (err.Length > 0) return err;

                var estado = J.S(J.P(j, "status"));
                if (estado.Length > 0) ultimo = estado;

                double total = J.D(J.P(j, "total")), hecho = J.D(J.P(j, "completed"));
                progreso?.Report((ultimo, total > 0 ? Math.Clamp(hecho / total, 0, 1) : 0));
            }
            return "";   // la secuencia terminó sin error
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return "Descarga cancelada."; }
        catch (Exception e) { return $"No se pudo contactar con Ollama en {Host}. {e.Message}"; }
    }

    public async Task<AiParse?> ParseAsync(string name, string tagA, string tagT, string model, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(name) || AiBlocked) return null;

        // Antes de la primera petición se confirma que Ollama está ahí, con un sondeo de 3 s. Sin esto, un
        // cortafuegos que descarte los paquetes en silencio dejaría la primera canción esperando el timeout
        // largo de la generación (180 s). Se hace una sola vez por tirada.
        if (!_probed)
        {
            _probed = true;
            var ms = await ListModelsAsync(ct).ConfigureAwait(false);
            if (ms is not { Count: > 0 })
            {
                AiBlocked = true;
                _log?.Log($"        · IA local DESACTIVADA: Ollama no responde en {Host} o no tiene modelos instalados.",
                          LogKind.Err, fileOnly: true);
                return null;
            }
            if (string.IsNullOrWhiteSpace(model))
            {
                _autoModel = ms[0];
                _log?.Log($"        · IA local: se usará el modelo «{_autoModel}».", LogKind.Dim, fileOnly: true);
            }
        }
        if (string.IsNullOrWhiteSpace(model)) model = _autoModel ?? DefaultModel;

        var cacheKey = $"ai-ollama:v1:{model}|{name}|{tagA}|{tagT}";
        var cached = _api.ReadCache(cacheKey);
        if (cached != null)
        {
            if (cached.Length == 0) return null;
            try { return FromNode(JsonNode.Parse(cached)); } catch { /* sigue a la IA */ }
        }

        var body = JsonSerializer.Serialize(new
        {
            model,
            system = SystemPrompt,
            prompt = UserPrompt(name, tagA, tagT),
            stream = false,
            format = "json",           // Ollama garantiza JSON válido; evita rescatarlo con expresiones regulares.
            options = new { temperature = 0.0, num_predict = 256 },
        });

        string raw;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Host}/api/generate")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var det = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                // 404 = el modelo no está descargado: es un fallo de configuración, no algo transitorio.
                if ((int)resp.StatusCode == 404)
                {
                    AiBlocked = true;
                    _log?.Log($"        · IA local DESACTIVADA: el modelo '{model}' no está descargado en Ollama "
                            + $"(ejecuta: ollama pull {model})", LogKind.Err, fileOnly: true);
                }
                else _log?.Log($"        · IA local: error HTTP {(int)resp.StatusCode} · {Trim(det)}", LogKind.Err, fileOnly: true);
                return null;
            }
            raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            // Ollama no está arrancado (o se cayó): desactivar para el resto de la tirada.
            AiBlocked = true;
            _log?.Log($"        · IA local DESACTIVADA: no se pudo contactar con Ollama en {Host} ({e.Message})",
                      LogKind.Err, fileOnly: true);
            return null;
        }

        AiParse? outp;
        try
        {
            var txt = J.S(J.P(JsonNode.Parse(raw), "response"));
            if (txt.Length == 0) return null;
            // Con format=json la respuesta ya es un objeto, pero algún modelo pequeño puede añadir texto.
            var m = Regex.Match(txt, @"\{.*\}", RegexOptions.Singleline);
            if (!m.Success) return null;
            outp = FromNode(JsonNode.Parse(m.Value));
        }
        catch (Exception e)
        {
            _log?.Log($"        · IA local: respuesta ilegible ({e.Message})", LogKind.No, fileOnly: true);
            return null;
        }

        AiCalls++;

        // Solo se cachea una propuesta CON título (los vacíos se reintentan la próxima vez).
        if (outp != null && outp.Title.Length > 0)
        {
            var cp = _api.CachePath(cacheKey);
            if (cp != null)
                _api.CacheStore(cp, JsonSerializer.Serialize(new
                {
                    artist = outp.Artist,
                    title = outp.Title,
                    version = outp.Version,
                    is_mashup = outp.IsMashup,
                    confidence = outp.Confidence,
                }));
        }
        return outp;
    }

    private static string Trim(string s) => s.Length <= 140 ? s : s[..140];

    // Los modelos pequeños necesitan instrucciones cortas y ejemplos. Los tres ejemplos cubren los tres
    // fallos reales observados: ruido de pool, ausencia de separador artista/título, y mashup.
    private const string SystemPrompt =
        "Eres un experto en musica (reggaeton, latino, electronica, flamenco, sevillanas) que limpia nombres de " +
        "archivo de DJ. Recibes un nombre sucio, con erratas, etiquetas de record pool, nombres de editor, BPM y " +
        "tonalidad, y deduces la CANCION ORIGINAL real.\n" +
        "Responde SOLO con un objeto JSON con estas claves exactas: \"artist\" (string), \"title\" (string), " +
        "\"version\" (string: descriptor tipo Extended/Intro/Remix/Acapella, o \"\"), \"is_mashup\" (boolean: true " +
        "si combina 2 o mas canciones distintas), \"confidence\" (numero 0.0 a 1.0).\n" +
        "Reglas: descarta BPM, tonalidad y nombres de pool o editor. No inventes: si no reconoces una cancion real, " +
        "devuelve artist y title vacios y confidence 0.\n" +
        "Ejemplos:\n" +
        "Nombre: Bichota Karol G Dj Masa Intro Ronca Break 95 Bpm - - bpm - DJTOOLSVIP.mp3\n" +
        "{\"artist\":\"Karol G\",\"title\":\"Bichota\",\"version\":\"Intro\",\"is_mashup\":false,\"confidence\":0.9}\n" +
        "Nombre: CALLE BOTICA Con la primavera (Version 2017).mp3\n" +
        "{\"artist\":\"Calle Botica\",\"title\":\"Con la primavera\",\"version\":\"\",\"is_mashup\":false,\"confidence\":0.8}\n" +
        "Nombre: THE-FINAL-CONTDOWN-X-FEEL-GOOD-_GUERZON-AT-MASHUP_-FINALLY.mp3\n" +
        "{\"artist\":\"\",\"title\":\"\",\"version\":\"\",\"is_mashup\":true,\"confidence\":0.0}";

    private static string UserPrompt(string name, string tagA, string tagT)
    {
        var s = "Nombre de archivo: " + name;
        if (tagA.Length > 0 || tagT.Length > 0) s += $"\nTags actuales: artista=\"{tagA}\" titulo=\"{tagT}\"";
        return s;
    }

    private static AiParse FromNode(JsonNode? j) => new(
        J.S(J.P(j, "artist")).Trim(),
        J.S(J.P(j, "title")).Trim(),
        J.S(J.P(j, "version")).Trim(),
        J.P(j, "is_mashup") is JsonValue v && v.TryGetValue<bool>(out var b) && b,
        J.D(J.P(j, "confidence")));
}
