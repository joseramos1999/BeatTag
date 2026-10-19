using System.Text.RegularExpressions;

namespace Etiquetador.Core.Providers;

/// <summary>
/// Convierte un enlace de canción (Deezer/Spotify/Apple Music) en la coincidencia concreta a la que
/// apunta. Es la forma más exacta de identificar una pista: el usuario pega el enlace y no hay que
/// adivinar nada. Deezer e iTunes no piden clave; Spotify usa la del usuario.
/// </summary>
public sealed class LinkResolver
{
    /// <summary>Dónde buscar el enlace de la canción en la página de un enlace corto que redirige por script.</summary>
    private const string PatronCancion =
        @"(?:deezer\.com/(?:[a-z]{2}/)?track/\d+|open\.spotify\.com/(?:[a-z\-]+/)*?track/[A-Za-z0-9]{22})";

    private readonly ApiClient _api;
    private readonly SpotifyProvider _sp;
    private readonly HttpClient _http;

    /// <param name="http">Para seguir los enlaces cortos; por defecto, el del propio <paramref name="api"/>.</param>
    public LinkResolver(ApiClient api, SpotifyProvider sp, HttpClient? http = null) { _api = api; _sp = sp; _http = http ?? api.Http; }

    /// <summary>Resultado de resolver: la coincidencia, o el motivo por el que no se pudo.</summary>
    public sealed record Result(Candidate? Candidate, string Error);

    public async Task<Result> ResolveAsync(string url, string spotifyId = "", string spotifySecret = "",
        CancellationToken ct = default)
    {
        var link = TrackLinkParser.Parse(url);

        // El botón «Compartir» del móvil da un enlace corto (link.deezer.com/s/…, spotify.link/…)
        // que no dice qué canción es hasta seguirlo. Antes daba «Enlace no reconocido», justo con
        // el enlace más exacto que puede dar el usuario. Se sigue igual que en las listas.
        if (link == null && EnlaceCorto.Es(url))
        {
            link = await EnlaceCorto.SeguirAsync(_http, url, TrackLinkParser.Parse, PatronCancion, ct).ConfigureAwait(false);
            if (link == null)
                return new Result(null, "Ese enlace corto no lleva a una canción de Deezer ni de Spotify. Si es de una lista, ábrelo en la pestaña Playlists.");
        }

        if (link == null)
        {
            return new Result(null, TrackLinkParser.LooksLikeUrl(url)
                ? "Enlace no reconocido. Pega el enlace de una CANCIÓN de Deezer, Spotify o Apple Music (en Apple Music debe incluir «?i=»)."
                : "Eso no parece un enlace.");
        }

        var l = link.Value;
        try
        {
            return l.Source switch
            {
                "Deezer" => await DeezerAsync(l.Id, ct).ConfigureAwait(false),
                "iTunes" => await ItunesAsync(l.Id, ct).ConfigureAwait(false),
                "Spotify" => await SpotifyAsync(l.Id, spotifyId, spotifySecret, ct).ConfigureAwait(false),
                _ => new Result(null, "Fuente no soportada."),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { return new Result(null, "No se pudo leer el enlace: " + e.Message); }
    }

    private async Task<Result> DeezerAsync(string id, CancellationToken ct)
    {
        var r = await _api.GetAsync("https://api.deezer.com/track/" + id, ct: ct).ConfigureAwait(false);
        var title = J.S(J.P(r, "title"));
        if (title.Length == 0) return new Result(null, "Deezer no devolvió esa canción.");
        var year = Regex.Match(J.S(J.P(r, "release_date")), @"^(\d{4})");
        return new Result(new Candidate("Deezer",
            J.S(J.P(r, "artist", "name")), title,
            J.S(J.P(r, "album", "title")),
            year.Success ? year.Groups[1].Value : "",
            J.I(J.P(r, "duration"))), "");
    }

    private async Task<Result> ItunesAsync(string id, CancellationToken ct)
    {
        var r = await _api.GetAsync($"https://itunes.apple.com/lookup?id={id}&entity=song", ct: ct).ConfigureAwait(false);
        var first = (J.A(J.P(r, "results")) ?? new()).FirstOrDefault();
        var title = J.S(J.P(first, "trackName"));
        if (title.Length == 0) return new Result(null, "Apple Music no devolvió esa canción.");
        var year = Regex.Match(J.S(J.P(first, "releaseDate")), @"^(\d{4})");
        return new Result(new Candidate("iTunes",
            J.S(J.P(first, "artistName")), title,
            J.S(J.P(first, "collectionName")),
            year.Success ? year.Groups[1].Value : "",
            J.I(J.P(first, "trackTimeMillis")) / 1000), "");
    }

    private async Task<Result> SpotifyAsync(string id, string clientId, string secret, CancellationToken ct)
    {
        if (clientId.Length == 0 || secret.Length == 0)
            return new Result(null, "Para los enlaces de Spotify hace falta tu clave de Spotify (pestaña Ajustes).");

        var tok = await _sp.GetTokenAsync(clientId, secret, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(tok)) return new Result(null, "No se pudo autenticar con Spotify. Revisa tus claves en Ajustes.");

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + tok,
            ["User-Agent"] = AppInfo.UserAgent,
        };
        var r = await _api.GetAsync($"https://api.spotify.com/v1/tracks/{id}", headers, ct: ct).ConfigureAwait(false);
        var title = J.S(J.P(r, "name"));
        if (title.Length == 0) return new Result(null, "Spotify no devolvió esa canción.");

        var artists = J.A(J.P(r, "artists")) ?? new();
        var artist = artists.Count > 0 ? J.S(J.P(artists[0], "name")) : "";
        var year = Regex.Match(J.S(J.P(r, "album", "release_date")), @"^(\d{4})");
        return new Result(new Candidate("Spotify",
            artist, title,
            J.S(J.P(r, "album", "name")),
            year.Success ? year.Groups[1].Value : "",
            J.I(J.P(r, "duration_ms")) / 1000), "");
    }
}
