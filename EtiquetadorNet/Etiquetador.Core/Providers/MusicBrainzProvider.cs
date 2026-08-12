using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Etiquetador.Core.Providers;

/// <summary>MusicBrainz (sin clave, requiere User-Agent). Port de Get-MB/_MBPick. PickBest es puro.</summary>
public sealed class MusicBrainzProvider
{
    private readonly ApiClient _api;
    public MusicBrainzProvider(ApiClient api) => _api = api;

    public async Task<ProviderResult?> SearchAsync(string artist, string title, string ua, CancellationToken ct = default)
    {
        var ctitle = Descriptors.CleanKeywords(title);
        if (ctitle.Length == 0) ctitle = title;
        var ca = Regex.Split(artist ?? "", ",")[0].Trim();
        ca = Regex.Replace(ca, @"(?i)(feat\.?|ft\.?|featuring).*$", "").Trim();
        if (ca.Length == 0) ca = artist ?? "";

        var headers = new Dictionary<string, string> { ["User-Agent"] = ua };
        var q = $"recording:%22{TextUtils.UrlEnc(ctitle)}%22%20AND%20artist:%22{TextUtils.UrlEnc(ca)}%22";
        var r = await _api.GetAsync($"https://musicbrainz.org/ws/2/recording?query={q}&fmt=json&limit=5", headers, 1100, ct).ConfigureAwait(false);
        var res = PickBest(r, ca);
        if (res != null) return res;

        var kw = Descriptors.BuildKw(artist, title);
        if (kw.Length > 0)
        {
            var r2 = await _api.GetAsync($"https://musicbrainz.org/ws/2/recording?query={TextUtils.UrlEnc(kw)}&fmt=json&limit=6&dismax=true", headers, 1100, ct).ConfigureAwait(false);
            res = PickBest(r2, ca);
            if (res != null) return res;
        }
        return null;
    }

    /// <summary>Elige la mejor grabación (score >= 85, artista coincidente). Puro.</summary>
    public static ProviderResult? PickBest(JsonNode? r, string artist)
    {
        var recs = J.A(J.P(r, "recordings"));
        if (recs == null || recs.Count == 0) return null;
        JsonNode? best = null;
        int bestScore = int.MinValue;
        foreach (var rec in recs) { var s = J.I(J.P(rec, "score")); if (s > bestScore) { bestScore = s; best = rec; } }
        if (best == null || bestScore < 85) return null;

        var creditArr = J.A(J.P(best, "artist-credit"));
        var credit = "";
        if (creditArr != null)
        {
            var names = new List<string>();
            foreach (var c in creditArr) { var n = J.S(J.P(c, "name")); if (n.Length > 0) names.Add(n); }
            credit = string.Join(", ", names);
        }
        if (!string.IsNullOrEmpty(artist))
        {
            var nkc = TextUtils.Nk(credit);
            var nka = TextUtils.Nk(artist);
            if (!nkc.Contains(nka) && !nka.Contains(nkc)) return null;
        }

        string album = "", year = "";
        var releases = J.A(J.P(best, "releases"));
        if (releases is { Count: > 0 })
        {
            // Sort-Object -Property date | Select -First 1
            JsonNode? rel = null;
            string bestDate = null!;
            foreach (var x in releases)
            {
                var d = J.S(J.P(x, "date"));
                if (rel == null || string.CompareOrdinal(d, bestDate) < 0) { rel = x; bestDate = d; }
            }
            album = J.S(J.P(rel, "title"));
            var my = Regex.Match(J.S(J.P(rel, "date")), @"^(\d{4})");
            if (my.Success) year = my.Groups[1].Value;
        }
        // MusicBrainz trae su PROPIA puntuación (0-100) y aquí solo se aceptan las de 85 para arriba,
        // que ya son coincidencias sólidas. Antes no se trasladaba y se quedaba en 0, así que TODAS
        // acababan marcadas como "baja confianza" sin motivo. Se lleva 85-100 a nuestra escala 2,5-10.
        var score = Math.Round((bestScore - 80) / 2.0, 1);
        return new ProviderResult
        {
            Title = J.S(J.P(best, "title")), Artist = credit, Album = album, Year = year,
            Score = score,
        };
    }
}
