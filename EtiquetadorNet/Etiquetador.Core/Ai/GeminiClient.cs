using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etiquetador.Core.Providers;

namespace Etiquetador.Core.Ai;

/// <summary>Propuesta de la IA para un nombre de archivo sucio (solo PROPONE; se verifica luego contra Deezer/iTunes).</summary>
public sealed record AiParse(string Artist, string Title, string Version, bool IsMashup, double Confidence);

/// <summary>
/// IA de rescate (Google Gemini Flash, capa gratuita). Port de Resolve-AiModel/Get-AiParse.
/// Autorresuelve el modelo desde la propia clave (ListModels), throttlea, y se corta por cuota (429 repetido).
/// </summary>
public sealed class GeminiClient
{
    private const string Base = "https://generativelanguage.googleapis.com/v1beta";
    private const int MinGapMs = 5000;

    private readonly ApiClient _api;
    private readonly Logger? _log;

    public string AiModel { get; private set; } = "gemini-2.5-flash-lite";
    public bool AiModelResolved { get; private set; }
    public bool AiBlocked { get; private set; }
    public int AiCalls { get; private set; }
    private int _ai429run;
    private DateTime _aiLast = DateTime.MinValue;

    public GeminiClient(ApiClient api, Logger? log = null) { _api = api; _log = log; }

    private static Dictionary<string, string> Key(string apiKey) => new() { ["x-goog-api-key"] = apiKey };

    /// <summary>Descubre un modelo válido de ESA clave (evita 404 por adivinar el ID). Preferencia flash-lite > flash.</summary>
    public async Task<string?> ResolveModelAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(apiKey)) return null;
        if (AiModelResolved) return AiModel;
        var r = await _api.GetAsync($"{Base}/models?pageSize=200", Key(apiKey), 0, ct, useCache: false).ConfigureAwait(false);
        var models = J.A(J.P(r, "models"));
        if (models == null) return null;

        var cand = new List<string>();
        foreach (var m in models)
        {
            var methods = J.A(J.P(m, "supportedGenerationMethods"));
            var supports = methods != null && methods.Any(x => J.S(x) == "generateContent");
            if (!supports) continue;
            var id = Regex.Replace(J.S(J.P(m, "name")), @"^models/", "");
            if (Regex.IsMatch(id, "(?i)embedding|aqa|vision|imagen|image|tts|audio|native|thinking|learnlm")) continue;
            cand.Add(id);
        }
        if (cand.Count == 0) return null;

        string? pick =
            cand.FirstOrDefault(x => Regex.IsMatch(x, "(?i)flash-lite") && !Regex.IsMatch(x, "(?i)preview|exp|latest"))
            ?? cand.FirstOrDefault(x => Regex.IsMatch(x, "(?i)flash") && !Regex.IsMatch(x, "(?i)lite|preview|exp|latest"))
            ?? cand.FirstOrDefault(x => Regex.IsMatch(x, "(?i)flash"))
            ?? cand[0];

        AiModel = pick;
        AiModelResolved = true;
        return pick;
    }

    public async Task<AiParse?> ParseAsync(string name, string tagA, string tagT, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(name) || AiBlocked) return null;
        if (!AiModelResolved) await ResolveModelAsync(apiKey, ct).ConfigureAwait(false);

        var cacheKey = "ai-gemini:v2:" + name + "|" + tagA + "|" + tagT;
        var cached = _api.ReadCache(cacheKey);
        if (cached != null)
        {
            if (cached.Length == 0) return null;
            try { return FromNode(JsonNode.Parse(cached)); } catch { /* sigue a red */ }
        }

        var sys = "Eres un experto en musica (reggaeton, latino, electronica, flamenco) que normaliza nombres de archivo de DJ. " +
                  "Te doy un nombre de archivo sucio (con erratas, tags de pool, editores, BPM y adornos) y debes deducir la CANCION " +
                  "ORIGINAL real, corrigiendo las erratas. Responde SOLO con un objeto JSON (sin razonamiento, sin texto alrededor, sin " +
                  "markdown) con estas claves exactas: \"artist\" (string), \"title\" (string), \"version\" (string: descriptor tipo " +
                  "Extended/Intro/Remix/Acapella o \"\"), \"is_mashup\" (boolean: true si combina 2 o mas canciones distintas), " +
                  "\"confidence\" (numero 0.0 a 1.0). Si no puedes identificar una cancion real, devuelve artist y title vacios y confidence 0.";
        var usr = "Nombre de archivo: " + name;
        if (tagA.Length > 0 || tagT.Length > 0) usr += $"\nTags actuales: artista=\"{tagA}\" titulo=\"{tagT}\"";

        var body = JsonSerializer.Serialize(new
        {
            systemInstruction = new { parts = new[] { new { text = sys } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = usr } } } },
            generationConfig = new { maxOutputTokens = 2048, temperature = 0 },
        });

        // Throttle: hueco mínimo entre llamadas para no reventar el límite por minuto de la capa gratuita.
        var since = (DateTime.Now - _aiLast).TotalMilliseconds;
        if (since < MinGapMs) await Task.Delay((int)(MinGapMs - since), ct).ConfigureAwait(false);

        var url = $"{Base}/models/{AiModel}:generateContent";
        AiParse? outp = null;
        for (int att = 0; att < 3; att++)
        {
            _aiLast = DateTime.Now;
            var raw = await _api.PostJsonAsync(url, body, Key(apiKey), 0, ct).ConfigureAwait(false);
            if (raw != null)
            {
                var txt = ExtractText(raw);
                var m = Regex.Match(txt, @"\{.*\}", RegexOptions.Singleline);
                if (!m.Success) return null;
                try { outp = FromNode(JsonNode.Parse(m.Value)); } catch { return null; }
                AiCalls++; _ai429run = 0;
                break;
            }

            // error: LastApiError = "HTTP {code} {body}"
            var err = _api.LastApiError;
            var code = 0;
            var cm = Regex.Match(err, @"HTTP\s+(\d+)");
            if (cm.Success) code = int.Parse(cm.Groups[1].Value);

            if (code == 429)
            {
                int ra = 6;
                var rm = Regex.Match(err, "\"retryDelay\"\\s*:\\s*\"?(\\d+)s");
                if (rm.Success) ra = int.Parse(rm.Groups[1].Value);
                if (ra < 4) ra = 4; if (ra > 30) ra = 30;
                if (att < 2) { await Task.Delay(ra * 1000, ct).ConfigureAwait(false); continue; }
                _ai429run++;
                _log?.Log("        · IA: límite de peticiones (429); se omite este archivo", LogKind.No, fileOnly: true);
                if (_ai429run >= 5) { AiBlocked = true; _log?.Log("        · IA DESACTIVADA: cuota gratuita de Gemini agotada (429 repetido).", LogKind.Err, fileOnly: true); }
                return null;
            }
            _log?.Log($"        · IA error HTTP {code}: {err[..Math.Min(140, err.Length)]}", LogKind.Err, fileOnly: true);
            if (code is 400 or 401 or 403) { AiBlocked = true; _log?.Log("        · IA DESACTIVADA para el resto de la tirada (clave/servicio rechazado).", LogKind.Err, fileOnly: true); }
            return null;
        }

        // Solo se cachea una propuesta CON título (los vacíos se reintentan la próxima vez).
        // Mismo formato de claves (minúsculas) que la respuesta de la IA para releerlo con FromNode.
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

    private static string ExtractText(string raw)
    {
        try
        {
            var r = JsonNode.Parse(raw);
            var cands = J.A(J.P(r, "candidates"));
            if (cands is not { Count: > 0 }) return "";
            var parts = J.A(J.P(J.P(cands[0], "content"), "parts"));
            if (parts == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var p in parts) sb.Append(J.S(J.P(p, "text")));
            return sb.ToString();
        }
        catch { return ""; }
    }

    private static AiParse FromNode(JsonNode? j) => new(
        J.S(J.P(j, "artist")).Trim(),
        J.S(J.P(j, "title")).Trim(),
        J.S(J.P(j, "version")).Trim(),
        J.P(j, "is_mashup") is JsonValue v && v.TryGetValue<bool>(out var b) && b,
        J.D(J.P(j, "confidence")));
}
