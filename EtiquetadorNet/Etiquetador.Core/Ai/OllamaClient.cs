using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etiquetador.Core.Providers;

namespace Etiquetador.Core.Ai;

/// <summary>Propuesta de la IA para un nombre de archivo sucio (solo PROPONE; se verifica luego contra Deezer/iTunes).</summary>
public sealed record AiParse(string Artist, string Title, string Version, bool IsMashup, double Confidence);

/// <summary>
/// Si la IA local se puede usar con el modelo elegido en Ajustes y, si no, por qué.
/// </summary>
/// <param name="Modelo">El modelo que se usará; vacío si la IA no está disponible.</param>
/// <param name="Disponible">Ollama responde y el modelo está descargado.</param>
/// <param name="Motivo">Por qué no está disponible, para enseñárselo al usuario. Vacío si lo está.</param>
/// <param name="Instalados">Los modelos descargados en Ollama (vacío si no responde), para ofrecer otro.</param>
public sealed record EstadoModelo(string Modelo, bool Disponible, string Motivo, IReadOnlyList<string> Instalados);

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

    /// <summary>
    /// Por qué se desactivó la IA (<see cref="AiBlocked"/>), en palabras para el usuario. Vacío si no
    /// está desactivada.
    ///
    /// Antes el motivo solo iba al archivo de log: Enriquecer analizaba quince mil canciones sin IA
    /// porque el modelo elegido no estaba descargado, y en la interfaz no lo decía nadie.
    /// </summary>
    public string MotivoBloqueo { get; private set; } = "";

    public int AiCalls { get; private set; }
    private string? _autoModel;
    private bool _probed;

    /// <summary>
    /// Cuántas veces seguidas puede agotarse el tiempo de espera antes de desactivar la IA. Una
    /// primera carga lenta del modelo agota los 180 s y la siguiente petición ya responde: desactivar
    /// la IA a la primera dejaba el resto de la tirada sin ella por un fallo pasajero.
    /// </summary>
    public const int TiemposAgotadosParaDesactivar = 2;
    private int _tiemposAgotadosSeguidos;

    /// <summary>
    /// Cuántos fallos seguidos de <see cref="PedirJsonAsync"/> que no dependen de la canción (no hay
    /// conexión, tiempo agotado, el modelo no está) bastan para dejar de preguntar un rato.
    /// </summary>
    public const int FallosSeguidosParaPausar = 3;

    /// <summary>
    /// Cuánto se deja de preguntar tras <see cref="FallosSeguidosParaPausar"/> fallos seguidos. Sin
    /// esa pausa, proponer fichas (hasta 5000 canciones) repetía el mismo fallo canción a canción, y
    /// con un cortafuegos que descarta los paquetes cada una esperaba los 180 s enteros.
    /// </summary>
    public TimeSpan PausaTrasFallos { get; set; } = TimeSpan.FromSeconds(30);
    private int _fallosSeguidos;
    private string _ultimoFallo = "";
    private DateTime _pausadoHasta = DateTime.MinValue;

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
        MotivoBloqueo = "";
        _probed = false;
        _autoModel = null;
        _tiemposAgotadosSeguidos = 0;
        _fallosSeguidos = 0;
        _pausadoHasta = DateTime.MinValue;
    }

    /// <summary>Desactiva la IA para el resto de la tirada, con el motivo para el usuario y para el log.</summary>
    private void Bloquear(string motivo)
    {
        AiBlocked = true;
        MotivoBloqueo = motivo;
        _log?.Log($"        · IA local DESACTIVADA: {motivo}", LogKind.Err, fileOnly: true);
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
                Bloquear(ms == null ? MotivoSinOllama() : MotivoSinModelos());
                return null;
            }
            if (string.IsNullOrWhiteSpace(model))
            {
                _autoModel = Automatico(ms);
                _log?.Log($"        · IA local: se usará el modelo «{_autoModel}».", LogKind.Dim, fileOnly: true);
            }
            // El modelo elegido en Ajustes puede no estar descargado: el desplegable ofrece también
            // los sugeridos. Se sabe ya, sin esperar al 404 de la primera canción.
            else if (!EstaInstalado(model, ms))
            {
                Bloquear(MotivoNoDescargado(model.Trim(), ms));
                return null;
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
                if ((int)resp.StatusCode == 404) Bloquear(MotivoNoDescargado(model, Array.Empty<string>()));
                else _log?.Log($"        · IA local: error HTTP {(int)resp.StatusCode} · {Trim(det)}", LogKind.Err, fileOnly: true);
                return null;
            }
            raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _tiemposAgotadosSeguidos = 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            // Sin cancelar nosotros, es el tiempo de espera del cliente. Ollama respondió al sondeo, así
            // que suele ser la primera carga del modelo en memoria: la siguiente petición ya responde.
            // Solo si se repite se da por perdido.
            if (++_tiemposAgotadosSeguidos >= TiemposAgotadosParaDesactivar)
                Bloquear($"La IA local no ha respondido a tiempo ({_http.Timeout.TotalSeconds:0} s) {_tiemposAgotadosSeguidos} veces "
                       + $"seguidas con el modelo «{model}». Puede que el equipo no tenga memoria suficiente para él: "
                       + "prueba con un modelo más pequeño en Ajustes.");
            else
                _log?.Log($"        · IA local: no respondió a tiempo ({_http.Timeout.TotalSeconds:0} s); se vuelve a intentar con la siguiente.",
                          LogKind.Err, fileOnly: true);
            return null;
        }
        catch (Exception e)
        {
            // Ollama no está arrancado (o se cayó): desactivar para el resto de la tirada.
            Bloquear($"No se pudo contactar con Ollama en {Host} ({e.Message}). Arráncalo o instálalo en Ajustes.");
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

    /// <summary>
    /// El modelo «automático» entre los instalados: el recomendado si está, y si no el primero.
    ///
    /// Antes era siempre el primero de la lista, y eso tenía una trampa: al descargar un segundo
    /// modelo para probarlo, Ollama lo pone delante y el análisis de Enriquecer cambiaba de modelo
    /// sin que nadie lo hubiera elegido.
    /// </summary>
    public static string Automatico(IReadOnlyList<string> instalados)
    {
        if (instalados.Count == 0) return "";
        return instalados.FirstOrDefault(m => string.Equals(m, DefaultModel, StringComparison.OrdinalIgnoreCase)
                                              || m.StartsWith(DefaultModel + ":", StringComparison.OrdinalIgnoreCase))
               ?? instalados[0];
    }

    /// <summary>
    /// El modelo que se usará: el elegido en Ajustes o, si no hay ninguno, el automático.
    /// Devuelve "" si la IA no se puede usar, que es la forma de saber que no está; el motivo lo da
    /// <see cref="ComprobarModeloAsync"/>.
    /// </summary>
    public async Task<string> ModeloEfectivoAsync(string elegido, CancellationToken ct = default)
        => (await ComprobarModeloAsync(elegido, ct).ConfigureAwait(false)).Modelo;

    /// <summary>
    /// Si la IA se puede usar con el modelo elegido en Ajustes (vacío = automático) y, si no, por qué.
    ///
    /// Que Ollama responda no basta: el desplegable de Ajustes ofrece también modelos sugeridos sin
    /// descargar, y esa elección se guarda. Antes se daba por disponible cualquier modelo elegido,
    /// y el Asistente decía «IA local: llama3.1:8b» para luego acabar «Renombrar con IA» con 200
    /// descartes por «la IA no respondió», sin explicar por qué.
    /// </summary>
    public async Task<EstadoModelo> ComprobarModeloAsync(string elegido, CancellationToken ct = default)
    {
        var instalados = await ListModelsAsync(ct).ConfigureAwait(false);
        if (instalados == null) return new EstadoModelo("", false, MotivoSinOllama(), Array.Empty<string>());
        if (instalados.Count == 0) return new EstadoModelo("", false, MotivoSinModelos(), instalados);
        if (string.IsNullOrWhiteSpace(elegido)) return new EstadoModelo(Automatico(instalados), true, "", instalados);

        var modelo = elegido.Trim();
        return EstaInstalado(modelo, instalados)
            ? new EstadoModelo(modelo, true, "", instalados)
            : new EstadoModelo("", false, MotivoNoDescargado(modelo, instalados), instalados);
    }

    /// <summary>
    /// El modelo está entre los instalados. Sin distinguir «:latest»: Ollama lista «llama3.2:latest»
    /// aunque se descargara como «llama3.2», y los dos nombres son el mismo modelo.
    /// </summary>
    public static bool EstaInstalado(string modelo, IEnumerable<string> instalados)
    {
        static string SinLatest(string m)
        {
            m = m.Trim();
            return m.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? m[..^":latest".Length] : m;
        }
        var buscado = SinLatest(modelo);
        return buscado.Length > 0 && instalados.Any(x => string.Equals(SinLatest(x), buscado, StringComparison.OrdinalIgnoreCase));
    }

    private string MotivoSinOllama()
        => $"Ollama no responde en {Host}. Arráncalo o instálalo en Ajustes.";

    private static string MotivoSinModelos()
        => "Ollama no tiene ningún modelo descargado. Descarga uno en Ajustes.";

    private static string MotivoNoDescargado(string modelo, IReadOnlyCollection<string> instalados)
        => $"El modelo «{modelo}» no está descargado en Ollama. Descárgalo en Ajustes"
         + (instalados.Count > 0 ? $" o elige uno de los instalados: {string.Join(", ", instalados)}." : ".");

    /// <summary>
    /// Pregunta al modelo y devuelve su respuesta como JSON, o el motivo por el que no hay respuesta.
    ///
    /// Es la puerta de entrada de las herramientas del Asistente IA. No interpreta nada: lo que diga
    /// el modelo se valida después, en cada herramienta, contra datos reales. Con un modelo pequeño
    /// eso no es opcional (ver las mediciones en <see cref="Etiquetador.Core.Ai.BusquedaIa"/>).
    ///
    /// Con <paramref name="claveCache"/> la respuesta se guarda y se reutiliza: proponer la ficha de
    /// una canción ya consultada no vuelve a costar un segundo por canción.
    /// </summary>
    public async Task<(JsonNode? Json, string Error)> PedirJsonAsync(string sistema, string peticion, string modelo,
                                                                   int maxTokens = 512, string? claveCache = null,
                                                                   CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelo)) return (null, "No hay ningún modelo de IA disponible.");

        var clave = claveCache == null ? null : $"ai-ollama:asistente:v1:{modelo}|{claveCache}";
        if (clave != null && _api.ReadCache(clave) is { Length: > 0 } guardada)
        {
            try { return (JsonNode.Parse(guardada), ""); } catch { /* se vuelve a preguntar */ }
        }

        // Tras varios fallos seguidos que no dependen de la canción, no se vuelve a preguntar hasta
        // que pase la pausa: se devuelve el mismo motivo, sin esperar a que falle otra vez.
        if (DateTime.UtcNow < _pausadoHasta) return (null, _ultimoFallo);

        var body = JsonSerializer.Serialize(new
        {
            model = modelo,
            system = sistema,
            prompt = peticion,
            stream = false,
            format = "json",
            options = new { temperature = 0.0, num_predict = maxTokens },
        });

        string raw;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Host}/api/generate")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return Fallo((int)resp.StatusCode == 404
                    ? $"El modelo «{modelo}» no está descargado. Descárgalo en Ajustes."
                    : $"Ollama respondió {(int)resp.StatusCode}. {Trim(raw)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return Fallo($"La IA local no ha respondido a tiempo ({_http.Timeout.TotalSeconds:0} s).");
        }
        catch (Exception e) { return Fallo($"No se pudo contactar con Ollama en {Host}. {e.Message}"); }

        // Ollama ha respondido: lo que venga a partir de aquí depende de la canción, no de la conexión.
        _fallosSeguidos = 0;

        // Un JSON ilegible es una mala respuesta del modelo para ESTA petición, no un problema de
        // conexión. Antes caía en el mismo catch y se informaba como «No se pudo contactar con Ollama».
        try
        {
            var txt = J.S(J.P(JsonNode.Parse(raw), "response"));
            var m = Regex.Match(txt, @"\{.*\}", RegexOptions.Singleline);
            if (!m.Success) return (null, "La IA no devolvió una respuesta legible.");
            var json = JsonNode.Parse(m.Value);
            AiCalls++;

            if (clave != null && _api.CachePath(clave) is { } cp) _api.CacheStore(cp, m.Value);
            return (json, "");
        }
        catch (Exception) { return (null, "La IA no devolvió una respuesta legible."); }
    }

    /// <summary>
    /// Anota un fallo de <see cref="PedirJsonAsync"/> que no depende de la canción y, si ya van
    /// <see cref="FallosSeguidosParaPausar"/> seguidos, deja de preguntar durante <see cref="PausaTrasFallos"/>.
    /// </summary>
    private (JsonNode? Json, string Error) Fallo(string motivo)
    {
        _ultimoFallo = motivo;
        // El contador no vuelve a cero al pausar: si al acabar la pausa el primer intento también
        // falla, se vuelve a pausar en vez de esperar otros tres fallos.
        if (++_fallosSeguidos >= FallosSeguidosParaPausar)
        {
            _pausadoHasta = DateTime.UtcNow + PausaTrasFallos;
            _log?.Log($"        · IA local: {FallosSeguidosParaPausar} fallos seguidos ({motivo}); "
                    + $"no se vuelve a preguntar en {PausaTrasFallos.TotalSeconds:0} s.", LogKind.Err, fileOnly: true);
        }
        return (null, motivo);
    }

    private static string Trim(string s) => s.Length <= 140 ? s : s[..140];

    // Los modelos pequeños necesitan instrucciones cortas y ejemplos. Los tres ejemplos cubren los tres
    // fallos reales observados: ruido de pool, ausencia de separador artista/título, y mashup.
    private const string SystemPrompt =
        "Eres un experto en musica (reggaeton, latino, electronica, flamenco, sevillanas) que limpia nombres de " +
        "archivo de DJ. Recibes un nombre sucio, con erratas, etiquetas de record pool, nombres de editor, BPM y " +
        "tonalidad, y deduces la CANCION ORIGINAL real.\n" +
        "Responde SOLO con un objeto JSON con estas claves exactas: \"artist\" (string), \"title\" (string), " +
        "\"version\" (string), \"is_mashup\" (boolean), \"confidence\" (numero 0.0 a 1.0).\n" +
        "\n" +
        "REGLAS IMPORTANTES:\n" +
        "1. \"title\" es SOLO el titulo original, LIMPIO. Todo lo que sea descriptor de edicion (Intro, Extended, " +
        "Hype Intro, Break, Acapella, Remix, Dirty, Clean, Starter) va en \"version\", NUNCA dentro de \"title\". " +
        "El titulo tiene que poder buscarse tal cual en un catalogo de musica.\n" +
        "2. \"artist\" es el INTERPRETE de la cancion, no quien hizo la edicion. Los nombres de DJ o editor que " +
        "acompanan a un descriptor (por ejemplo 'X Hype Intro', 'Y Extended', 'Intro Z') NO son el artista: " +
        "descartalos. Si el interprete real aparece en otra parte del nombre, usa ese.\n" +
        "3. Descarta BPM, tonalidad y etiquetas de record pool.\n" +
        "4. No inventes: si no reconoces una cancion real, devuelve artist y title vacios y confidence 0.\n" +
        // La regla del mashup va tan explicada a propósito. Con la versión corta ("true si combina
        // dos canciones") el modelo acertaba 3 de 7 sobre casos reales, confundiendo una lista de
        // colaboradores con una mezcla. Diciéndole DÓNDE mirar -el título, no los artistas- y con
        // ejemplos de las dos formas, sube a 6 de 7.
        "5. \"is_mashup\": decide asi, y SOLO asi. Mira el TITULO (lo que va DESPUES del guion). Si el " +
        "titulo contiene DOS NOMBRES DE CANCION distintos unidos por \"x\", \"vs\" o \"&\", es un mashup: " +
        "true. Si el titulo es UNA sola cancion, es false, DA IGUAL cuantos artistas haya antes del " +
        "guion. Varios artistas separados por \"x\" ANTES del guion son colaboradores de una misma " +
        "cancion, nunca un mashup.\n" +
        "Ejemplos de esta regla:\n" +
        "\"Tokischa x July Queen x Liss Doll RD - Bandidaje\" -> titulo \"Bandidaje\", UNA cancion -> false\n" +
        "\"Daddy Yankee x SHAQI - Ella Me Levanto x Gulevando\" -> titulo \"Ella Me Levanto x Gulevando\", DOS canciones -> true\n" +
        "\"Quevedo x Panic - Columbia x Bohemian\" -> titulo \"Columbia x Bohemian\", DOS canciones -> true\n" +
        "\"Delincuente - Tokischa x Anuel AA x Nengo Flow\" -> titulo \"Delincuente\", UNA cancion -> false\n" +
        "\n" +
        "Ejemplos:\n" +
        "Nombre: Bichota Karol G Dj Masa Intro Ronca Break 95 Bpm - - bpm - DJTOOLSVIP.mp3\n" +
        "{\"artist\":\"Karol G\",\"title\":\"Bichota\",\"version\":\"Intro Break\",\"is_mashup\":false,\"confidence\":0.9}\n" +
        "Nombre: Bad Bunny x Jhay Cortez x Pablo Chill-E - Resentia (IVAN RF Hype Intro).mp3\n" +
        "{\"artist\":\"Bad Bunny, Jhay Cortez, Pablo Chill-E\",\"title\":\"Resentia\",\"version\":\"Hype Intro\",\"is_mashup\":false,\"confidence\":0.9}\n" +
        "Nombre: TORA - Alex Selas Hype Intro (Dirty) - Rvfv.mp3\n" +
        "{\"artist\":\"Rvfv\",\"title\":\"Tora\",\"version\":\"Hype Intro Dirty\",\"is_mashup\":false,\"confidence\":0.8}\n" +
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
