using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Etiquetador.Core.Providers;

/// <summary>Apple Music / iTunes (sin clave). Port de Get-iTunes. SelectBest es puro y testeable.</summary>
public sealed class ItunesProvider
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    internal const string WantVerRe = @"(?i)\b(live|vivo|directo|unplugged|sinf|remaster|karaoke|deluxe)\b";
    internal const string BadRe = @"(?i)\b(live|en vivo|en directo|vivo|unplugged|sinfonico|karaoke|tribute|made famous|remaster|remastered|deluxe|mixed|sped up|slowed)\b";

    private readonly ApiClient _api;
    public ItunesProvider(ApiClient api) => _api = api;

    public async Task<ProviderResult?> SearchAsync(string artist, string title, int localDur = 0, bool isEdit = false, CancellationToken ct = default, bool verbatim = false)
    {
        var kw = verbatim ? Descriptors.BuildKwVerbatim(artist, title) : Descriptors.BuildKw(artist, title);
        if (kw.Length == 0) kw = Descriptors.CleanKeywords(title);
        if (kw.Length == 0) return null;

        var r = await _api.GetAsync($"https://itunes.apple.com/search?term={TextUtils.UrlEnc(kw)}&entity=song&limit=8", null, 200, ct).ConfigureAwait(false);
        var results = J.A(J.P(r, "results"));
        if (results == null || results.Count == 0) return null;

        var pick = SelectBest(results, artist, title, localDur, isEdit, out var bestSc);
        if (pick == null) return null;

        var rd = J.S(J.P(pick, "releaseDate"));
        var my = Regex.Match(rd, @"^(\d{4})");
        var cov = J.S(J.P(pick, "artworkUrl100"));
        if (cov.Length > 0) cov = cov.Replace("100x100bb", "600x600bb");
        return new ProviderResult
        {
            Title = J.S(J.P(pick, "trackName")),
            Artist = J.S(J.P(pick, "artistName")),
            Album = J.S(J.P(pick, "collectionName")),
            Year = my.Success ? my.Groups[1].Value : "",
            Genre = J.S(J.P(pick, "primaryGenreName")),
            CoverUrl = cov,
            Score = bestSc < -900 ? 0 : bestSc,
            Dur = J.I(J.P(pick, "trackTimeMillis")) / 1000,
        };
    }

    public static JsonNode? SelectBest(JsonArray results, string artist, string title, int localDur, bool isEdit, out int bestSc)
    {
        var na = TextUtils.Nk(artist);
        var nt = TextUtils.Nk(Descriptors.CleanKeywords(title));
        JsonNode? pick = null;
        bestSc = -999;
        var wantVer = Regex.IsMatch(title, WantVerRe, IC);

        foreach (var x in results)
        {
            var anx = TextUtils.Nk(J.S(J.P(x, "artistName")));
            var rn = TextUtils.Nk(J.S(J.P(x, "trackName")));
            if (anx.Length > 0 && rn.StartsWith(anx) && nt.Length > 0 && !nt.StartsWith(anx)) continue;
            var artistOk = Matching.ArtistMatch(na, anx);
            var titleOk = nt.Length > 0 && (rn.Contains(nt) || nt.Contains(rn));
            if (!(artistOk && titleOk)) continue;

            int sc = 0;
            if (rn == nt) sc += 10;
            if (!wantVer && (Regex.IsMatch(J.S(J.P(x, "trackName")), BadRe, IC) || Regex.IsMatch(J.S(J.P(x, "collectionName")), BadRe, IC))) sc -= 8;
            var ms = J.I(J.P(x, "trackTimeMillis"));
            if (localDur > 0 && ms > 0)
            {
                var cd = ms / 1000;
                var dd = Math.Abs(cd - localDur);
                if (dd <= 2) sc += 5; else if (dd <= 5) sc += 2;
                else if (!isEdit) { if (dd > 20) sc -= 5; else if (dd > 10) sc -= 2; }
            }
            if (sc > bestSc) { bestSc = sc; pick = x; }
        }

        // Respaldo sin artista: título exacto (>=2 palabras)
        if (pick == null && na.Length == 0 && nt.Length > 0 && J.WordCount(title) >= 2)
            foreach (var x in results)
                if (TextUtils.Nk(J.S(J.P(x, "trackName"))) == nt) { pick = x; break; }

        // Último recurso FUZZY (typos)
        if (pick == null && na.Length > 0 && nt.Length > 0)
        {
            JsonNode? bestFz = null;
            double bestFzJw = 0.0;
            foreach (var x in results)
            {
                var anx = TextUtils.Nk(J.S(J.P(x, "artistName")));
                var rn = TextUtils.Nk(J.S(J.P(x, "trackName")));
                if (anx.Length == 0 || rn.Length == 0) continue;
                if (!wantVer && (Regex.IsMatch(J.S(J.P(x, "trackName")), BadRe, IC) || Regex.IsMatch(J.S(J.P(x, "collectionName")), BadRe, IC))) continue;
                var aStrict = Matching.ArtistMatch(na, anx);
                var tStrict = rn.Contains(nt) || nt.Contains(rn);
                var aFuzzy = na.Length >= 5 && anx.Length >= 5 && Matching.JaroWinkler(na, anx) >= 0.90;
                var tFuzzy = nt.Length >= 5 && rn.Length >= 5 && Matching.JaroWinkler(nt, rn) >= 0.90;
                var ms = J.I(J.P(x, "trackTimeMillis"));
                var cd = ms > 0 ? ms / 1000 : 0;
                var durOk = localDur > 0 && cd > 0 && Math.Abs(cd - localDur) <= 4;
                if ((aFuzzy && tStrict) || (aStrict && tFuzzy && durOk && !isEdit)) { pick = x; bestSc = 5; break; }
                if (aStrict && !tStrict && nt.Length >= 6 && rn.Length >= 6)
                {
                    var jw = Matching.JaroWinkler(nt, rn);
                    if (jw >= 0.88 && jw > bestFzJw) { bestFzJw = jw; bestFz = x; }
                }
            }
            if (pick == null && bestFz != null) { pick = bestFz; bestSc = 4; }
        }
        return pick;
    }
}
