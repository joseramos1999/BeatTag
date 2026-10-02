using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Etiquetador.Core.Providers;

/// <summary>Una lista leída de Spotify o Deezer, o el motivo por el que no se pudo.</summary>
public sealed record PlaylistLeida(
    string Nombre, PlataformaLista Plataforma, IReadOnlyList<ChartTrack> Pistas, bool Recortada, string Error)
{
    public bool Ok => Error.Length == 0;

    public static PlaylistLeida Fallo(PlataformaLista p, string error) => new("", p, Array.Empty<ChartTrack>(), false, error);
}

/// <summary>
/// Lee playlists (y álbumes) de Spotify y Deezer a partir del enlace que pega el usuario.
///
/// MEDIDO antes de construirlo, con las credenciales de Spotify del propio usuario (octubre 2026):
///   · La API de Spotify ya NO da el contenido de ninguna lista con credenciales de aplicación:
///     «/playlists/{id}/tracks» responde 403 y el nuevo «/playlists/{id}/items» 401 («hace falta un
///     usuario autenticado»). La ficha de la lista llega, pero sin canciones. Las editoriales ni eso
///     (404 desde noviembre de 2024).
///   · La página «embed» -la que Spotify da para incrustar una lista en una web- sí trae las
///     canciones, con su ID y su duración, para cualquier lista pública, editorial incluida. Pero
///     solo las 100 primeras: tres listas de usuario más largas dieron exactamente 100.
///   · Deezer sirve cualquier lista pública por su API, sin clave y entera: una de 456 canciones se
///     leyó en 5 páginas de 100.
///
/// Nada se cachea: una lista cambia, y la gracia es ver lo que tiene hoy.
/// </summary>
public sealed class PlaylistProvider
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Lo que da la página embed de Spotify como mucho.</summary>
    public const int LimiteSpotify = 100;

    private readonly ApiClient _api;
    private readonly HttpClient _http;

    public PlaylistProvider(ApiClient api, HttpClient http) { _api = api; _http = http; }

    public async Task<PlaylistLeida> LeerAsync(string texto, CancellationToken ct = default)
    {
        var enlace = PlaylistLinkParser.Parse(texto);
        if (enlace == null && PlaylistLinkParser.EsCorto(texto))
            enlace = await ResolverCortoAsync(texto.Trim(), ct).ConfigureAwait(false);

        if (enlace is not { } l)
            return PlaylistLeida.Fallo(PlataformaLista.Spotify, PareceEnlace(texto)
                ? "Enlace no reconocido. Pega el enlace de una playlist (o un álbum) de Spotify o Deezer: el de «Compartir → Copiar enlace»."
                : "Eso no parece un enlace.");

        return l.Plataforma == PlataformaLista.Deezer
            ? await DeezerAsync(l, ct).ConfigureAwait(false)
            : await SpotifyAsync(l, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- Deezer

    private async Task<PlaylistLeida> DeezerAsync(PlaylistLink l, CancellationToken ct)
    {
        var ficha = await _api.GetAsync($"https://api.deezer.com/{l.Tipo}/{l.Id}", null, 0, ct, useCache: false).ConfigureAwait(false);
        if (ficha == null) return PlaylistLeida.Fallo(PlataformaLista.Deezer, "Deezer no responde. ¿Hay conexión a internet?");
        if (ficha["error"] != null)
            return PlaylistLeida.Fallo(PlataformaLista.Deezer, l.EsAlbum ? "Ese álbum no existe en Deezer." : "Esa lista no existe en Deezer o es privada.");

        var nombre = ficha["title"]?.ToString() ?? "";
        var pistas = new List<ChartTrack>();
        string? url = $"https://api.deezer.com/{l.Tipo}/{l.Id}/tracks?limit=100&index=0";
        for (var pagina = 0; url != null && pagina < 100; pagina++)   // 10.000 canciones: el máximo de Deezer
        {
            ct.ThrowIfCancellationRequested();
            var p = await _api.GetAsync(url, null, 0, ct, useCache: false).ConfigureAwait(false);
            if (p == null) break;
            pistas.AddRange(PistasDeezer(p, pistas.Count));
            url = p["next"]?.ToString();
        }
        return new PlaylistLeida(nombre, PlataformaLista.Deezer, pistas, false, "");
    }

    /// <summary>Las canciones de una página de «/playlist/{id}/tracks» de Deezer, numeradas desde <paramref name="desde"/>+1.</summary>
    public static List<ChartTrack> PistasDeezer(JsonNode pagina, int desde = 0)
    {
        var lista = new List<ChartTrack>();
        foreach (var t in pagina["data"]?.AsArray() ?? new JsonArray())
        {
            if (t == null) continue;
            var titulo = t["title"]?.ToString() ?? "";
            if (titulo.Length == 0) continue;
            int.TryParse(t["duration"]?.ToString(), out var dur);
            lista.Add(new ChartTrack(desde + lista.Count + 1, t["artist"]?["name"]?.ToString() ?? "", titulo, dur,
                                     t["link"]?.ToString() ?? ""));
        }
        return lista;
    }

    // ---------------------------------------------------------------- Spotify

    private async Task<PlaylistLeida> SpotifyAsync(PlaylistLink l, CancellationToken ct)
    {
        var html = await _api.GetTextAsync($"https://open.spotify.com/embed/{l.Tipo}/{l.Id}",
            new Dictionary<string, string> { ["User-Agent"] = "Mozilla/5.0" }, 0, ct, useCache: false).ConfigureAwait(false);
        if (html == null) return PlaylistLeida.Fallo(PlataformaLista.Spotify, "Spotify no responde. ¿Hay conexión a internet?");

        var (nombre, pistas) = LeerEmbedSpotify(html);
        if (pistas.Count == 0)
            return PlaylistLeida.Fallo(PlataformaLista.Spotify, l.EsAlbum
                ? "Spotify no ha devuelto ese álbum. Comprueba el enlace."
                : "Spotify no ha devuelto la lista. ¿Es privada? Solo se pueden leer las listas públicas.");

        return new PlaylistLeida(nombre, PlataformaLista.Spotify, pistas, !l.EsAlbum && pistas.Count >= LimiteSpotify, "");
    }

    /// <summary>
    /// Nombre y canciones de la página embed de Spotify. Vienen en el JSON de «__NEXT_DATA__»:
    /// título, artistas separados por comas en «subtitle», duración en milisegundos y la URI de la
    /// pista, de la que sale su ID.
    /// </summary>
    public static (string Nombre, List<ChartTrack> Pistas) LeerEmbedSpotify(string html)
    {
        var lista = new List<ChartTrack>();
        var m = Regex.Match(html ?? "", "<script id=\"__NEXT_DATA__\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline);
        if (!m.Success) return ("", lista);

        JsonNode? entidad;
        try { entidad = JsonNode.Parse(m.Groups[1].Value)?["props"]?["pageProps"]?["state"]?["data"]?["entity"]; }
        catch { return ("", lista); }
        if (entidad == null) return ("", lista);

        foreach (var t in entidad["trackList"]?.AsArray() ?? new JsonArray())
        {
            if (t == null) continue;
            var titulo = t["title"]?.ToString() ?? "";
            if (titulo.Length == 0) continue;
            long.TryParse(t["duration"]?.ToString(), out var ms);
            var uri = t["uri"]?.ToString() ?? "";
            var id = uri.StartsWith("spotify:track:", StringComparison.Ordinal) ? uri["spotify:track:".Length..] : "";
            lista.Add(new ChartTrack(lista.Count + 1, t["subtitle"]?.ToString() ?? "", titulo, (int)Math.Round(ms / 1000.0),
                                     id.Length > 0 ? $"https://open.spotify.com/track/{id}" : "") { SpotifyId = id });
        }
        var nombre = entidad["name"]?.ToString() ?? entidad["title"]?.ToString() ?? "";
        return (nombre, lista);
    }

    // ---------------------------------------------------------------- Enlaces cortos

    /// <summary>
    /// Sigue un enlace corto («link.deezer.com/s/…», «spotify.link/…») hasta la lista a la que
    /// apunta. Primero por la dirección final tras las redirecciones; si la página redirige por
    /// script, buscando el enlace dentro de ella.
    /// </summary>
    private async Task<PlaylistLink?> ResolverCortoAsync(string url, CancellationToken ct)
    {
        try
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (PlaylistLinkParser.Parse(resp.RequestMessage?.RequestUri?.ToString()) is { } final) return final;

            var cuerpo = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var m = Regex.Match(cuerpo, @"(?:deezer\.com/(?:[a-z]{2}/)?(?:playlist|album)/\d+|open\.spotify\.com/(?:[a-z\-]+/)*?(?:playlist|album)/[A-Za-z0-9]{22})", IC);
            return m.Success ? PlaylistLinkParser.Parse(m.Value) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static bool PareceEnlace(string? s)
    {
        var t = (s ?? "").Trim();
        return t.StartsWith("http", StringComparison.OrdinalIgnoreCase) || t.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)
            || t.Contains(".com/", StringComparison.OrdinalIgnoreCase) || t.Contains(".link/", StringComparison.OrdinalIgnoreCase);
    }
}
