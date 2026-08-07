using System.Text.RegularExpressions;

namespace Etiquetador.Core.Providers;

/// <summary>Una coincidencia del catálogo, tal cual la devuelve la fuente (sin elegir por el usuario).</summary>
public sealed record Candidate(string Source, string Artist, string Title, string Album, string Year, int Dur)
{
    /// <summary>Duración en m:ss (vacío si la fuente no la da).</summary>
    public string DurText => Dur > 0 ? $"{Dur / 60}:{Dur % 60:00}" : "";

    /// <summary>Texto de una línea para listarlo.</summary>
    public string Display
    {
        get
        {
            var s = $"{Artist} — {Title}";
            var extra = new List<string>();
            if (Album.Length > 0) extra.Add(Album);
            if (Year.Length > 0) extra.Add(Year);
            if (DurText.Length > 0) extra.Add(DurText);
            if (extra.Count > 0) s += "   (" + string.Join(" · ", extra) + ")";
            return s;
        }
    }
}

/// <summary>
/// Devuelve VARIAS coincidencias del catálogo para que el usuario elija a mano, en vez de quedarse
/// con la que el scoring considera mejor. Se usa desde "Reanalizar…" para las pistas difíciles.
/// </summary>
public sealed class CandidateFinder
{
    private readonly ApiClient _api;
    public CandidateFinder(ApiClient api) => _api = api;

    public async Task<IReadOnlyList<Candidate>> FindAsync(string artist, string title, bool deezer, bool itunes,
        CancellationToken ct = default)
    {
        var kw = Descriptors.BuildKw(artist, title);
        if (kw.Length == 0) kw = Descriptors.CleanKeywords(title);
        if (kw.Length == 0) kw = $"{artist} {title}".Trim();
        if (kw.Length == 0) return Array.Empty<Candidate>();

        var list = new List<Candidate>();

        if (deezer)
        {
            var r = await _api.GetAsync($"https://api.deezer.com/search?q={TextUtils.UrlEnc(kw)}&limit=12", null, 350, ct).ConfigureAwait(false);
            foreach (var x in J.A(J.P(r, "data")) ?? new())
            {
                var t = J.S(J.P(x, "title"));
                if (t.Length == 0) continue;
                list.Add(new Candidate("Deezer",
                    J.S(J.P(x, "artist", "name")), t,
                    J.S(J.P(x, "album", "title")), "", J.I(J.P(x, "duration"))));
            }
        }

        if (itunes)
        {
            var r = await _api.GetAsync($"https://itunes.apple.com/search?term={TextUtils.UrlEnc(kw)}&entity=song&limit=12", null, 200, ct).ConfigureAwait(false);
            foreach (var x in J.A(J.P(r, "results")) ?? new())
            {
                var t = J.S(J.P(x, "trackName"));
                if (t.Length == 0) continue;
                var my = Regex.Match(J.S(J.P(x, "releaseDate")), @"^(\d{4})");
                list.Add(new Candidate("iTunes",
                    J.S(J.P(x, "artistName")), t,
                    J.S(J.P(x, "collectionName")), my.Success ? my.Groups[1].Value : "",
                    J.I(J.P(x, "trackTimeMillis")) / 1000));
            }
        }

        return Dedup(list);
    }

    /// <summary>Quita repetidos (mismo artista+título), conservando el primero que llegó.</summary>
    public static List<Candidate> Dedup(IEnumerable<Candidate> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outp = new List<Candidate>();
        foreach (var c in items)
        {
            var k = TextUtils.Nk(c.Artist) + "|" + TextUtils.Nk(c.Title);
            if (seen.Add(k)) outp.Add(c);
        }
        return outp;
    }
}
