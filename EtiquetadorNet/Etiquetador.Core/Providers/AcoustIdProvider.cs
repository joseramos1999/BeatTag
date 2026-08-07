using System.Globalization;
using System.Text.Json.Nodes;

namespace Etiquetador.Core.Providers;

/// <summary>AcoustID: identifica por huella acústica cuando el nombre no sirve. Port de Get-AcoustID.</summary>
public sealed class AcoustIdProvider
{
    private readonly ApiClient _api;
    public AcoustIdProvider(ApiClient api) => _api = api;

    public async Task<ProviderResult?> LookupAsync(double dur, string fp, string key, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fp) || string.IsNullOrEmpty(key) || dur <= 0) return null;

        var cacheKey = "acoustid|" + fp;
        var raw = _api.ReadCache(cacheKey);
        if (raw == null)
        {
            var body = $"client={TextUtils.UrlEnc(key)}&duration={(int)Math.Round(dur)}&fingerprint={TextUtils.UrlEnc(fp)}&meta=recordings";
            raw = await _api.PostFormAsync("https://api.acoustid.org/v2/lookup", body, null, 350, ct).ConfigureAwait(false);
            if (raw == null) return null;
            var cp = _api.CachePath(cacheKey);
            if (cp != null) _api.CacheStore(cp, raw);
        }

        JsonNode? r;
        try { r = JsonNode.Parse(raw); } catch { return null; }
        if (J.S(J.P(r, "status")) != "ok") return null;
        var results = J.A(J.P(r, "results"));
        if (results == null || results.Count == 0) return null;

        JsonNode? best = null;
        double bestScore = double.MinValue;
        foreach (var x in results) { var s = J.D(J.P(x, "score")); if (s > bestScore) { bestScore = s; best = x; } }
        if (best == null || bestScore < 0.5) return null;
        var recs = J.A(J.P(best, "recordings"));
        if (recs == null || recs.Count == 0) return null;

        JsonNode? rec = null;
        foreach (var x in recs)
        {
            var t = J.S(J.P(x, "title"));
            var arts = J.A(J.P(x, "artists"));
            if (t.Length > 0 && arts is { Count: > 0 }) { rec = x; break; }
        }
        if (rec == null) return null;

        var names = new List<string>();
        foreach (var a in J.A(J.P(rec, "artists"))!) { var n = J.S(J.P(a, "name")); if (n.Length > 0) names.Add(n); }
        return new ProviderResult
        {
            Artist = string.Join(", ", names),
            Title = J.S(J.P(rec, "title")),
            Score = Math.Round(bestScore, 3),
        };
    }
}
