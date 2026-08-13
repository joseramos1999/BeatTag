using System.Text;
using System.Text.Json.Nodes;
using Etiquetador.Core.Ai;

namespace Etiquetador.Core.Providers;

/// <summary>Prueba de credenciales y servicios (port de btnTest). Devuelve un informe multilínea.</summary>
public sealed class ConnectionTester
{
    private readonly ApiClient _api;
    private readonly SpotifyProvider _sp;
    private readonly OllamaClient _ai;

    public ConnectionTester(ApiClient api, SpotifyProvider sp, OllamaClient ai) { _api = api; _sp = sp; _ai = ai; }

    public async Task<string> RunAsync(AppConfig cfg, CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        // Deezer (sin clave)
        try { var r = await _api.GetAsync("https://api.deezer.com/search?q=bad%20bunny&limit=1", null, 0, ct, useCache: false); sb.AppendLine(J.A(J.P(r, "data")) is { Count: > 0 } ? "Deezer: OK" : "Deezer: responde pero sin datos"); }
        catch { sb.AppendLine("Deezer: ERROR de conexión"); }

        // Apple Music / iTunes (sin clave)
        try { var r = await _api.GetAsync("https://itunes.apple.com/search?term=bad+bunny&limit=1&entity=song", null, 0, ct, useCache: false); sb.AppendLine(r != null ? "Apple Music/iTunes: OK" : "Apple Music/iTunes: responde raro"); }
        catch { sb.AppendLine("Apple Music/iTunes: ERROR de conexión"); }

        // MusicBrainz (requiere User-Agent)
        try { var r = await _api.GetAsync("https://musicbrainz.org/ws/2/recording?query=gasolina&fmt=json&limit=1", new Dictionary<string, string> { ["User-Agent"] = AppInfo.MusicBrainzUserAgent }, 0, ct, useCache: false); sb.AppendLine(r != null ? "MusicBrainz: OK" : "MusicBrainz: responde sin datos"); }
        catch { sb.AppendLine("MusicBrainz: ERROR o límite (429)"); }

        // Discogs (token opcional)
        var tk = cfg.DiscogsToken.Trim();
        try
        {
            var h = new Dictionary<string, string> { ["User-Agent"] = "Tagger/1.0" };
            if (tk.Length > 0) h["Authorization"] = "Discogs token=" + tk;
            var r = await _api.GetAsync("https://api.discogs.com/database/search?q=test&per_page=1", h, 0, ct, useCache: false);
            sb.AppendLine(r != null ? (tk.Length > 0 ? "Discogs: OK (con token)" : "Discogs: OK (sin token)") : "Discogs: sin respuesta");
        }
        catch { sb.AppendLine("Discogs: ERROR o límite de peticiones"); }

        // Spotify (credenciales)
        var id = cfg.SpotifyId.Trim(); var sec = cfg.SpotifySecret.Trim();
        if (id.Length > 0 && sec.Length > 0)
        {
            var tok = await _sp.GetTokenAsync(id, sec, ct);
            if (tok != null)
            {
                var r = await _api.GetAsync($"https://api.spotify.com/v1/search?type=track&limit=1&q={TextUtils.UrlEnc("Bad Bunny Titi")}", new Dictionary<string, string> { ["Authorization"] = "Bearer " + tok }, 0, ct, useCache: false);
                var items = J.A(J.P(J.P(r, "tracks"), "items"));
                sb.AppendLine(items is { Count: > 0 } ? "Spotify: OK (token + búsqueda)" : "Spotify: token OK pero la búsqueda falló. " + _api.LastApiError);
            }
            else sb.AppendLine("Spotify: NO se obtuvo token. " + _api.LastApiError);
        }
        else sb.AppendLine("Spotify: sin credenciales");

        // AcoustID (clave)
        var ak = cfg.AcoustIdKey.Trim();
        if (ak.Length > 0)
        {
            var r = await _api.GetAsync($"https://api.acoustid.org/v2/lookup?format=json&client={TextUtils.UrlEnc(ak)}&duration=120&fingerprint=AQAAAA", null, 0, ct, useCache: false);
            var err = _api.LastApiError;
            if (r != null && J.S(J.P(r, "status")) == "ok") sb.AppendLine("AcoustID: OK (clave aceptada)");
            else if (err.Contains("invalid api key", StringComparison.OrdinalIgnoreCase)) sb.AppendLine("AcoustID: clave RECHAZADA");
            else if (err.Contains("invalid fingerprint", StringComparison.OrdinalIgnoreCase)) sb.AppendLine("AcoustID: OK (clave aceptada; huella de prueba inválida, esperado)");
            else sb.AppendLine("AcoustID: respuesta inesperada. " + err);
        }
        else sb.AppendLine("AcoustID: sin clave");

        // IA local (Ollama): no hay clave que validar, solo disponibilidad del servicio y del modelo.
        var modelos = await _ai.ListModelsAsync(ct);
        if (modelos == null)
            sb.AppendLine($"IA local: Ollama no responde en {_ai.Host}. Instálalo desde https://ollama.com y déjalo en marcha.");
        else if (modelos.Count == 0)
            sb.AppendLine($"IA local: Ollama responde, pero no hay ningún modelo descargado (ejecuta: ollama pull {OllamaClient.DefaultModel}).");
        else
        {
            var eleg = cfg.AiModel.Trim();
            if (eleg.Length == 0)
                sb.AppendLine($"IA local: OK — {modelos.Count} modelo(s) disponible(s). Sin modelo seleccionado; se usará «{modelos[0]}».");
            else if (modelos.Any(m => m == eleg || m.StartsWith(eleg + ":", StringComparison.OrdinalIgnoreCase)))
                sb.AppendLine($"IA local: OK — modelo {eleg}");
            else
                sb.AppendLine($"IA local: el modelo «{eleg}» no está descargado. Disponibles: {string.Join(", ", modelos)}");
        }

        return sb.ToString().TrimEnd();
    }
}
