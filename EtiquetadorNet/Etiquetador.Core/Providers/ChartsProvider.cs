using System.Text.RegularExpressions;

namespace Etiquetador.Core.Providers;

/// <summary>Un país con su lista oficial de éxitos en Deezer.</summary>
public sealed record ChartCountry(string Name, long PlaylistId)
{
    public override string ToString() => Name;
}

/// <summary>Una canción del chart, con su posición.</summary>
public sealed record ChartTrack(int Position, string Artist, string Title, int Dur, string Link)
{
    public string DurText => Dur > 0 ? $"{Dur / 60}:{Dur % 60:00}" : "";
}

/// <summary>
/// Listas de éxitos por país. Se usa Deezer porque sus charts son públicos y no piden clave:
/// Spotify cerró el acceso a sus playlists editoriales (incluido el Top 50) en noviembre de 2024.
/// Los países se leen en vivo del usuario editorial de Deezer, así que la lista no se queda vieja.
/// </summary>
public sealed class ChartsProvider
{
    /// <summary>Usuario editorial de Deezer, dueño de todas las listas "Top &lt;país&gt;".</summary>
    private const long EditorialUser = 637006841;

    // Listas que NO son de país (segmentadas o temáticas): fuera del selector.
    private static readonly Regex NoEsPais =
        new(@"(?i)\b(mujeres|women|femmes|mulheres|songcatcher)\b|20\d\d");

    private readonly ApiClient _api;
    public ChartsProvider(ApiClient api) => _api = api;

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
