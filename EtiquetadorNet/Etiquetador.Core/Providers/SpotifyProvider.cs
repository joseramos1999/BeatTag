using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Etiquetador.Core.Providers;

/// <summary>
/// Spotify (client-credentials, gratis). Port de Get-SpotifyToken/_SpItems/Get-Spotify/Get-SpArtistGenre.
/// Mantiene el token cacheado y un cortacircuitos: si 429ea repetido, se autodesactiva (SpBlocked).
/// </summary>
public sealed class SpotifyProvider
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private readonly ApiClient _api;
    private readonly Logger? _log;
    private string? _token;
    private DateTime _tokenExp = DateTime.MinValue;
    private readonly Dictionary<string, string> _genreCache = new();

    public int Sp429 { get; private set; }
    public bool SpBlocked { get; private set; }
    public string SpDiag { get; private set; } = "";

    public SpotifyProvider(ApiClient api, Logger? log = null) { _api = api; _log = log; }

    private static Dictionary<string, string> Auth(string tok) => new()
    {
        ["Authorization"] = "Bearer " + tok,
        ["User-Agent"] = AppInfo.UserAgent,
    };

    public async Task<string?> GetTokenAsync(string id, string secret, CancellationToken ct = default)
    {
        if (_token != null && DateTime.Now < _tokenExp) return _token;
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret)) return null;
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{id}:{secret}"));
        var headers = new Dictionary<string, string> { ["Authorization"] = "Basic " + basic, ["User-Agent"] = AppInfo.UserAgent };
        var r = await _api.PostFormJsonAsync("https://accounts.spotify.com/api/token", "grant_type=client_credentials", headers, ct).ConfigureAwait(false);
        var at = J.S(J.P(r, "access_token"));
        if (at.Length == 0) { _log?.Log("Spotify token: " + _api.LastApiError); return null; }
        _token = at;
        _tokenExp = DateTime.Now.AddSeconds(J.I(J.P(r, "expires_in")) - 60);
        return _token;
    }

    private async Task<JsonArray?> ItemsAsync(string q, string tok, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(q)) return null;
        var r = await _api.GetAsync($"https://api.spotify.com/v1/search?type=track&limit=10&q={TextUtils.UrlEnc(q)}", Auth(tok), 200, ct).ConfigureAwait(false);
        var items = J.A(J.P(J.P(r, "tracks"), "items"));
        return items is { Count: > 0 } ? items : null;
    }

    public async Task<ProviderResult?> SearchAsync(string artist, string title, string id, string secret,
        bool wantRemix, bool wantLive, int localDur = 0, bool isEdit = false, CancellationToken ct = default)
    {
        if (SpBlocked) { SpDiag = "blocked"; return null; }
        var tok = await GetTokenAsync(id, secret, ct).ConfigureAwait(false);
        if (tok == null) { SpDiag = "notoken"; return null; }

        var ct2 = Descriptors.CleanKeywords(title);
        var ca = Descriptors.CleanKeywords(artist);
        var qs = new List<string>();
        if (ca.Length > 0 && ct2.Length > 0) { qs.Add($"track:{ct2} artist:{ca}"); qs.Add($"{ca} {ct2}"); }
        if (ct2.Length > 0) qs.Add(ct2);

        JsonArray? items = null;
        foreach (var q in qs) { items = await ItemsAsync(q, tok, ct).ConfigureAwait(false); if (items != null) break; }
        if (items == null)
        {
            SpDiag = ("0items " + _api.LastApiError).Trim();
            if (Regex.IsMatch(_api.LastApiError, "429")) { Sp429++; if (Sp429 >= 8) { SpBlocked = true; _log?.Log("Spotify: límite de peticiones (429) repetido -> se desactiva Spotify el resto de la ejecución.", LogKind.Err); } }
            else Sp429 = 0;
            return null;
        }
        Sp429 = 0;

        var pick = SelectBest(items, artist, title, wantRemix, wantLive, localDur, isEdit, out var bestSc);
        if (pick == null) { SpDiag = "nomatch/" + items.Count; return null; }
        SpDiag = "ok";

        var arts = JoinArtists(pick);
        var relDate = J.S(J.P(J.P(pick, "album"), "release_date"));
        var my = Regex.Match(relDate, @"^(\d{4})");
        var images = J.A(J.P(J.P(pick, "album"), "images"));
        var cover = images is { Count: > 0 } ? J.S(J.P(images[0], "url")) : "";
        var artistsArr = J.A(J.P(pick, "artists"));
        var aid = artistsArr is { Count: > 0 } ? J.S(J.P(artistsArr[0], "id")) : "";
        var durMs = J.I(J.P(pick, "duration_ms"));
        return new ProviderResult
        {
            Title = J.S(J.P(pick, "name")),
            Artist = arts,
            Album = J.S(J.P(J.P(pick, "album"), "name")),
            Year = my.Success ? my.Groups[1].Value : "",
            ArtistId = aid,
            CoverUrl = cover,
            Score = bestSc < -900 ? 0 : Math.Round(bestSc, 1),
            Dur = durMs > 0 ? durMs / 1000 : 0,
        };
    }

    public async Task<string> ArtistGenreAsync(string aid, string id, string secret, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(aid)) return "";
        if (_genreCache.TryGetValue(aid, out var cached)) return cached;
        var tok = await GetTokenAsync(id, secret, ct).ConfigureAwait(false);
        if (tok == null) return "";
        var r = await _api.GetAsync($"https://api.spotify.com/v1/artists/{aid}", Auth(tok), 120, ct).ConfigureAwait(false);
        var genres = J.A(J.P(r, "genres"));
        var g = "";
        if (genres is { Count: > 0 })
        {
            g = J.S(genres[0]);
            foreach (var x in genres) { var s = J.S(x); if (Regex.IsMatch(s, "(?i)reggaeton|dembow|trap|latin|urban|salsa|bachata|merengue|cumbia|afro")) { g = s; break; } }
            g = TextUtils.TitleCase(g);
        }
        _genreCache[aid] = g;
        return g;
    }

    private static string JoinArtists(JsonNode? track)
    {
        var arr = J.A(J.P(track, "artists"));
        if (arr == null) return "";
        var names = new List<string>();
        foreach (var a in arr) { var n = J.S(J.P(a, "name")); if (n.Length > 0) names.Add(n); }
        return string.Join(", ", names);
    }

    /// <summary>Selección/scoring pura sobre los items de Spotify (sin fuzzy, como el .ps1).</summary>
    public static JsonNode? SelectBest(JsonArray items, string artist, string title, bool wantRemix, bool wantLive,
        int localDur, bool isEdit, out double bestSc)
    {
        var na = TextUtils.Nk(artist);
        var nt = TextUtils.Nk(Descriptors.CleanKeywords(title));
        JsonNode? pick = null;
        bestSc = -999.0;

        foreach (var x in items)
        {
            var arts = JoinArtists(x);
            var artistsArr = J.A(J.P(x, "artists"));
            var a0 = artistsArr is { Count: > 0 } ? TextUtils.Nk(J.S(J.P(artistsArr[0], "name"))) : "";
            var artistOk = na.Length > 0 && (TextUtils.Nk(arts).Contains(na) || (a0.Length > 0 && na.Contains(a0)));
            var rn = TextUtils.Nk(J.S(J.P(x, "name")));
            var titleOk = nt.Length > 0 && (rn.Contains(nt) || nt.Contains(rn));
            if (!(artistOk && titleOk)) continue;

            var rt = J.S(J.P(x, "name"));
            double sc = 0.0;
            if (rn == nt) sc += 10;
            if (Regex.IsMatch(rt, DeezerProvider.BadRe, IC)) sc -= 9;
            if (!wantLive && Regex.IsMatch(rt, DeezerProvider.LiveRe, IC)) sc -= 9;
            if (!wantRemix && Regex.IsMatch(rt, DeezerProvider.RemixRe, IC)) sc -= 6;
            if (!wantRemix && !wantLive && Regex.IsMatch(rt, DeezerProvider.VerRe, IC)) sc -= 2;
            sc -= Math.Max(0, rn.Length - nt.Length) * 0.03;
            var durMs = J.I(J.P(x, "duration_ms"));
            if (localDur > 0 && durMs > 0)
            {
                var cd = durMs / 1000;
                var dd = Math.Abs(cd - localDur);
                if (dd <= 2) sc += 5; else if (dd <= 5) sc += 2;
                else if (!isEdit) { if (dd > 20) sc -= 5; else if (dd > 10) sc -= 2; }
            }
            if (sc > bestSc) { bestSc = sc; pick = x; }
        }
        if (pick != null && bestSc <= -8) pick = null;

        // Respaldo sin artista (título >=2 palabras)
        if (pick == null && na.Length == 0 && nt.Length > 0 && J.WordCount(title) >= 2)
        {
            foreach (var x in items)
                if (TextUtils.Nk(J.S(J.P(x, "name"))) == nt)
                {
                    var rt = J.S(J.P(x, "name"));
                    if (!Regex.IsMatch(rt, DeezerProvider.BadRe, IC) && (wantLive || !Regex.IsMatch(rt, DeezerProvider.LiveRe, IC)) && (wantRemix || !Regex.IsMatch(rt, DeezerProvider.RemixRe, IC)))
                    { pick = x; break; }
                }
            if (pick == null)
                foreach (var x in items)
                    if (TextUtils.Nk(J.S(J.P(x, "name"))) == nt) { pick = x; break; }
        }
        return pick;
    }
}
