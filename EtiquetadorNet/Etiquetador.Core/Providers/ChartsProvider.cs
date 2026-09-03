using System.Text.RegularExpressions;

namespace Etiquetador.Core.Providers;

/// <summary>De dónde salen las listas de éxitos.</summary>
public enum ChartSource
{
    /// <summary>El chart diario de Spotify, el que de verdad marca lo que suena.</summary>
    Spotify,

    /// <summary>Deezer. Público y sin clave, pero su reparto de oyentes no es el del mercado.</summary>
    Deezer,
}

/// <summary>
/// Un país del selector. Lleva las dos formas de identificarlo porque cada fuente usa la suya:
/// Deezer numera sus listas y Spotify va por código de país.
/// </summary>
public sealed record ChartCountry(string Name, long PlaylistId, string Code = "")
{
    public override string ToString() => Name;
}

/// <summary>Una canción del chart, con su posición.</summary>
public sealed record ChartTrack(int Position, string Artist, string Title, int Dur, string Link)
{
    /// <summary>ID de la pista en Spotify, si el chart viene de ahi. Permite pedirle la ficha exacta.</summary>
    public string SpotifyId { get; init; } = "";

    public string DurText => Dur > 0 ? $"{Dur / 60}:{Dur % 60:00}" : "";
}

/// <summary>
/// Listas de éxitos por país, de dos fuentes.
///
/// SPOTIFY es la que marca lo que de verdad suena, pero su API devuelve 404 en todas las listas
/// editoriales -el Top 50 incluido- desde noviembre de 2024, y 403 en todo `browse`. Comprobado con
/// las credenciales del propio usuario. Se lee del chart que publica kworb.net, que SÍ es el de
/// Spotify y trae el ID de pista de Spotify en cada fila.
///
/// DEEZER se conserva como alternativa: es pública, sin clave y no depende de terceros. Sirve de
/// red de seguridad si el otro camino se cae.
/// </summary>
public sealed class ChartsProvider
{
    /// <summary>Usuario editorial de Deezer, dueño de todas las listas "Top &lt;país&gt;".</summary>
    private const long EditorialUser = 637006841;

    // Listas que NO son de país (segmentadas o temáticas): fuera del selector.
    private static readonly Regex NoEsPais =
        new(@"(?i)\b(mujeres|women|femmes|mulheres|songcatcher)\b|20\d\d");

    /// <summary>Índice de países del chart de Spotify.</summary>
    private const string SpotifyIndice = "https://kworb.net/spotify/";

    private readonly ApiClient _api;
    public ChartsProvider(ApiClient api) => _api = api;

    /// <summary>Países disponibles en la fuente indicada, ordenados alfabéticamente.</summary>
    public Task<IReadOnlyList<ChartCountry>> GetCountriesAsync(ChartSource fuente, CancellationToken ct = default)
        => fuente == ChartSource.Spotify ? SpotifyCountriesAsync(ct) : GetCountriesAsync(ct);

    /// <summary>Canciones del chart de un país en la fuente indicada, en orden de posición.</summary>
    public Task<IReadOnlyList<ChartTrack>> GetChartAsync(ChartSource fuente, ChartCountry pais, int limit = 50,
        CancellationToken ct = default)
        => fuente == ChartSource.Spotify
            ? SpotifyChartAsync(pais.Code, limit, ct)
            : GetChartAsync(pais.PlaylistId, limit, ct);

    // ---------- Spotify (vía kworb) ----------

    private async Task<IReadOnlyList<ChartCountry>> SpotifyCountriesAsync(CancellationToken ct)
    {
        var html = await _api.GetTextAsync(SpotifyIndice, null, 300, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(html)) return Array.Empty<ChartCountry>();

        // PlaylistId no aplica aquí: Spotify va por código de país, no por número de lista.
        return SpotifyChartsParser.ParseCountries(html)
                                  .Select(p => new ChartCountry(p.Name, 0, p.Code))
                                  .ToList();
    }

    private async Task<IReadOnlyList<ChartTrack>> SpotifyChartAsync(string code, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return Array.Empty<ChartTrack>();
        var html = await _api.GetTextAsync($"https://kworb.net/spotify/country/{code}_daily.html", null, 300, ct)
                             .ConfigureAwait(false);
        if (string.IsNullOrEmpty(html)) return Array.Empty<ChartTrack>();
        return SpotifyChartsParser.ParseTracks(html, limit);
    }

    // ---------- Deezer ----------

    /// <summary>Países disponibles, ordenados alfabéticamente.</summary>
    public async Task<IReadOnlyList<ChartCountry>> GetCountriesAsync(CancellationToken ct = default)
    {
        var salida = new List<ChartCountry>();
        var r = await _api.GetAsync($"https://api.deezer.com/user/{EditorialUser}/playlists?limit=200", null, 300, ct)
                          .ConfigureAwait(false);
        foreach (var x in J.A(J.P(r, "data")) ?? new())
        {
            var titulo = J.S(J.P(x, "title"));
            if (!titulo.StartsWith("Top ", StringComparison.OrdinalIgnoreCase)) continue;
            if (NoEsPais.IsMatch(titulo)) continue;

            var pais = titulo.Substring(4).Trim();
            if (pais.Length == 0) continue;
            var id = J.L(J.P(x, "id"));
            if (id > 0) salida.Add(new ChartCountry(pais, id));
        }
        return salida
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Canciones de la lista de un país, en orden de posición.</summary>
    public async Task<IReadOnlyList<ChartTrack>> GetChartAsync(long playlistId, int limit = 50,
        CancellationToken ct = default)
    {
        var salida = new List<ChartTrack>();
        var r = await _api.GetAsync($"https://api.deezer.com/playlist/{playlistId}/tracks?limit={limit}", null, 300, ct)
                          .ConfigureAwait(false);
        var pos = 0;
        foreach (var x in J.A(J.P(r, "data")) ?? new())
        {
            var titulo = J.S(J.P(x, "title"));
            if (titulo.Length == 0) continue;
            pos++;
            salida.Add(new ChartTrack(pos,
                J.S(J.P(x, "artist", "name")), titulo,
                J.I(J.P(x, "duration")), J.S(J.P(x, "link"))));
        }
        return salida;
    }
}
