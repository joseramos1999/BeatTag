using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Etiquetador.Core.Providers;

/// <summary>Discogs (token opcional): aporta género (y a veces álbum/año). Port de Get-Discogs/_DiscogsQuery.</summary>
public sealed class DiscogsProvider
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private readonly ApiClient _api;
    public DiscogsProvider(ApiClient api) => _api = api;

    private async Task<JsonArray?> QueryAsync(string kw, string ua, string token, int per, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(kw)) return null;
        var h = new Dictionary<string, string> { ["User-Agent"] = ua };
        if (!string.IsNullOrEmpty(token)) h["Authorization"] = "Discogs token=" + token;
        var r = await _api.GetAsync($"https://api.discogs.com/database/search?q={TextUtils.UrlEnc(kw)}&per_page={per}",
            h, string.IsNullOrEmpty(token) ? 2200 : 900, ct).ConfigureAwait(false);
        var results = J.A(J.P(r, "results"));
        return results is { Count: > 0 } ? results : null;
    }

    public async Task<ProviderResult?> SearchAsync(string artist, string title, string ua, string token, CancellationToken ct = default)
    {
        var na = TextUtils.Nk(artist);
        var kw = Descriptors.CleanKeywords($"{artist} {title}");
        if (kw.Length == 0) kw = Descriptors.CleanKeywords(title);

        var res = await QueryAsync(kw, ua, token, 10, ct).ConfigureAwait(false);
        if (res != null)
        {
            JsonNode? pick = null;
            if (na.Length > 0)
                foreach (var it in res)
                {
                    var t = J.S(J.P(it, "title"));
                    if (Regex.IsMatch(t, @"^\s*Various", IC)) continue;
                    var a = t.Split(new[] { " - " }, StringSplitOptions.None)[0];
                    var nka = TextUtils.Nk(a);
                    if (nka.Contains(na) || na.Contains(nka)) { pick = it; break; }
                }
            if (pick == null)
                foreach (var it in res)
                    if (!Regex.IsMatch(J.S(J.P(it, "title")), @"^\s*Various", IC)) { pick = it; break; }
            pick ??= res[0];

            string album = "", year = "";
            var pt = J.S(J.P(pick, "title"));
            var m = Regex.Match(pt, @"^(.*?) - (.+)$");
            if (m.Success && na.Length > 0)
            {
                var nkpa = TextUtils.Nk(m.Groups[1].Value);
                if (nkpa.Contains(na) || na.Contains(nkpa)) album = m.Groups[2].Value;
            }
            var y = J.S(J.P(pick, "year"));
            if (y.Length > 0) year = y;
            return new ProviderResult { Genre = GenrePicker.Pick(pick), Album = album, Year = year };
        }

        // Respaldo: primer segmento de artista y de título por separado, solo para género
        var cands = new List<string>();
        foreach (var seg in new[] { artist, title })
        {
            var c = Regex.Split(seg ?? "", @"(?i)\s*(?:,| x |&| feat\.?| ft\.?)\s*")[0];
            c = Descriptors.CleanKeywords(c);
            if (c.Length >= 3) cands.Add(c);
        }
        foreach (var c in cands)
        {
            var res2 = await QueryAsync(c, ua, token, 5, ct).ConfigureAwait(false);
            if (res2 != null)
            {
                JsonNode? p = null;
                foreach (var it in res2) if (!Regex.IsMatch(J.S(J.P(it, "title")), @"^\s*Various", IC)) { p = it; break; }
                p ??= res2[0];
                var gg = GenrePicker.Pick(p);
                if (gg.Length > 0) return new ProviderResult { Genre = gg };
            }
        }
        return null;
    }
}
