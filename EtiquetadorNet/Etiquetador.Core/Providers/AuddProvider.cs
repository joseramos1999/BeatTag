using System.Net.Http;
using System.Text.Json.Nodes;
using Etiquetador.Core.Analysis;

namespace Etiquetador.Core.Providers;

/// <summary>
/// Respuesta de un servicio de identificación por audio.
///
/// Distinguir «no se reconoció» de «el servicio ya no atiende» es la razón de que esto no sea
/// simplemente un <see cref="Identificado"/> anulable. Sin esa distinción, agotar la cuota se
/// vería en pantalla como una biblioteca entera sin identificar, y esa es exactamente la
/// conclusión equivocada: parecería que el servicio no conoce la música cuando lo que pasa es que
/// ha dejado de responder.
/// </summary>
/// <param name="Match">Lo que resultó ser el audio, o null si no se reconoció.</param>
/// <param name="Error">Explicación del fallo, vacía si todo fue bien.</param>
/// <param name="Detener">El fallo afecta a TODAS las canciones: no tiene sentido seguir pidiendo.</param>
public sealed record RespuestaId(Identificado? Match, string Error = "", bool Detener = false);

/// <summary>
/// AudD: identifica una grabación a partir de un FRAGMENTO, igual que hace Shazam al escuchar por
/// el micrófono.
///
/// Por qué esto y no AcoustID, que ya está integrado y es gratis: son dos técnicas distintas, y la
/// diferencia importa justo en una biblioteca de DJ. AcoustID (Chromaprint) compara la huella de
/// la grabación ENTERA, así que solo reconoce copias del mismo máster de punta a punta; una
/// edición de pool con otra intro y otro final ya no le casa. Un servicio del tipo de Shazam busca
/// marcas espectrales dentro de unos segundos cualesquiera, de modo que reconoce el tema aunque el
/// archivo sea una edición. Medido sobre esta biblioteca, AcoustID identificaba 61 canciones.
///
/// Shazam en sí no tiene API pública -es de Apple, y solo se abre a través de sus propios
/// frameworks-; lo que circula son relés no oficiales de su protocolo interno. Sobre eso no se
/// construye una función de la aplicación, así que se usa AudD, que hace lo mismo y sí es una API
/// documentada con precio público.
/// </summary>
public sealed class AuddProvider
{
    public const string Endpoint = "https://api.audd.io/";

    /// <summary>
    /// Errores que hablan de la CUENTA, no de la canción: token inválido, sin token, cuota agotada.
    /// Con cualquiera de ellos hay que parar: seguir pidiendo son miles de peticiones que van a
    /// fallar igual.
    /// </summary>
    private static readonly HashSet<int> ErroresDeCuenta = new() { 900, 901, 903 };

    private readonly HttpClient _http;
    private readonly Logger? _log;

    public AuddProvider(HttpClient http, Logger? log = null)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Manda un fragmento de audio y devuelve lo que resultó ser. El archivo se envía tal cual,
    /// sin conservarse en ningún sitio: quien llama lo borra al terminar.
    /// </summary>
    public async Task<RespuestaId> IdentificarAsync(string audioPath, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new RespuestaId(null, "falta la clave de AudD (pestaña Ajustes)", Detener: true);

        try
        {
            using var form = new MultipartFormDataContent();
            // El token va en el cuerpo, nunca en la URL: una URL acaba en registros y en cachés.
            form.Add(new StringContent(token), "api_token");

            await using var fs = File.OpenRead(audioPath);
            var archivo = new StreamContent(fs);
            archivo.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            form.Add(archivo, "file", "fragmento.wav");

            using var resp = await _http.PostAsync(Endpoint, form, ct).ConfigureAwait(false);
            var cuerpo = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                // 401/402/429 son de la cuenta o del ritmo: en los tres casos hay que parar.
                var codigo = (int)resp.StatusCode;
                var detener = codigo is 401 or 402 or 403 or 429;
                return new RespuestaId(null, $"el servicio respondió {codigo}", detener);
            }

            return Interpretar(cuerpo);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            _log?.Detail($"      identificación: fallo de red · {e.Message}");
            return new RespuestaId(null, "no se pudo contactar con el servicio: " + e.Message);
        }
    }

    /// <summary>
    /// Interpreta la respuesta. Separado de la red a propósito: es donde están las decisiones, y
    /// así se prueba con respuestas de ejemplo sin gastar una sola petición.
    /// </summary>
    public static RespuestaId Interpretar(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new RespuestaId(null, "el servicio devolvió una respuesta vacía");

        JsonNode? raiz;
        try { raiz = JsonNode.Parse(json); }
        catch { return new RespuestaId(null, "el servicio devolvió una respuesta ilegible"); }

        var estado = J.S(J.P(raiz, "status"));

        if (estado == "error")
        {
            var err = J.P(raiz, "error");
            var codigo = J.I(J.P(err, "error_code"));
            var mensaje = J.S(J.P(err, "error_message"));
            if (mensaje.Length == 0) mensaje = $"error {codigo}";
            return new RespuestaId(null, mensaje, ErroresDeCuenta.Contains(codigo));
        }

        // "success" con result nulo es la respuesta normal a un audio que no está en su catálogo.
        var r = J.P(raiz, "result");
        if (r is null) return new RespuestaId(null);

        var titulo = J.S(J.P(r, "title"));
        var artista = J.S(J.P(r, "artist"));
        if (titulo.Length == 0 && artista.Length == 0) return new RespuestaId(null);

        return new RespuestaId(new Identificado(artista, titulo, J.S(J.P(r, "album")), "AudD"));
    }
}
