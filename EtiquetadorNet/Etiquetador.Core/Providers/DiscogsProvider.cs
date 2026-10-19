using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Etiquetador.Core.Providers;

/// <summary>
/// Discogs (token opcional): aporta género (y a veces álbum/año). Port de Get-Discogs/_DiscogsQuery.
///
/// Todo lo que devuelve sale de un disco cuyo ARTISTA casa con el del archivo, nunca de un
/// recopilatorio «Various» ni de una búsqueda por el título suelto. Antes, si nadie casaba, se
/// tomaba el primer resultado que hubiera, y el respaldo buscaba solo por el título. Vistos en una
/// biblioteca real, con temas bien identificados por Deezer: «Arcangel - Bali» acabó con género
/// «Gamelan» (la búsqueda «Bali» devolvía «Eberhard Schoener - Bali-Agúng»), «Mad Mind - Bien
/// Duro» con «Medieval» (el único resultado era «Various - Medieval &amp; Renaissance») y «COQE,
/// JC Reyes - Multiplicando $$$» con «Educational». De paso desaparece la búsqueda por el título
/// suelto (una petición de ~0,9 s menos por canción sin resultado) y no se pregunta nada si el
/// archivo no tiene artista.
/// </summary>
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
        // El ritmo lo pone ApiClient: 60 peticiones por minuto con token y 25 sin él.
        var r = await _api.GetAsync($"https://api.discogs.com/database/search?q={TextUtils.UrlEnc(kw)}&per_page={per}",
            h, ct: ct).ConfigureAwait(false);
        var results = J.A(J.P(r, "results"));
        return results is { Count: > 0 } ? results : null;
    }

    public async Task<ProviderResult?> SearchAsync(string artist, string title, string ua, string token, CancellationToken ct = default)
    {
        // Sin artista no hay forma de saber de quién es un disco: no se pregunta.
        if (TextUtils.Nk(artist).Length == 0) return null;

        var kw = Descriptors.CleanKeywords($"{artist} {title}");
        if (kw.Length > 0)
        {
            var res = await QueryAsync(kw, ua, token, 10, ct).ConfigureAwait(false);
            if (res != null && ElegirDelArtista(res, artist) is { } r) return r;
        }

        // Respaldo: solo el artista principal. Da el género del ARTISTA, no el de esta canción, y
        // así se marca (SoloArtista) para que se use únicamente si no hay otra fuente.
        var principal = Descriptors.CleanKeywords(Regex.Split(artist, @"(?i)\s*(?:,| x |&| feat\.?| ft\.?)\s*")[0]);
        if (principal.Length >= 3 && !string.Equals(principal, kw, StringComparison.OrdinalIgnoreCase))
        {
            var res2 = await QueryAsync(principal, ua, token, 5, ct).ConfigureAwait(false);
            if (res2 != null) return GeneroDelArtista(res2, principal);
        }
        return null;
    }

    /// <summary>
    /// Género, álbum y año del primer resultado cuyo artista casa con <paramref name="artist"/>, o
    /// null si ninguno casa. Puro.
    /// </summary>
    public static ProviderResult? ElegirDelArtista(JsonArray resultados, string artist)
    {
        var delArtista = DelArtista(resultados, artist).ToList();
        if (delArtista.Count == 0) return null;
        var (pick, disco) = delArtista[0];
        // Si ese no trae género, el de otro disco suyo de la misma búsqueda vale más que nada.
        var genero = GenrePicker.Pick(pick);
        if (genero.Length == 0)
            genero = delArtista.Select(d => GenrePicker.Pick(d.Item)).FirstOrDefault(g => g.Length > 0) ?? "";
        return new ProviderResult { Genre = genero, Album = disco, Year = J.S(J.P(pick, "year")) };
    }

    /// <summary>
    /// Género de algún disco del artista, marcado como <see cref="ProviderResult.SoloArtista"/>,
    /// o null. Sin álbum ni año: no son de esta canción. Puro.
    /// </summary>
    public static ProviderResult? GeneroDelArtista(JsonArray resultados, string artist)
    {
        var g = DelArtista(resultados, artist).Select(d => GenrePicker.Pick(d.Item)).FirstOrDefault(x => x.Length > 0) ?? "";
        return g.Length == 0 ? null
            : new ProviderResult { Genre = g, SoloArtista = true, Why = "género de otro disco del mismo artista en Discogs" };
    }

    /// <summary>
    /// Los resultados «Artista - Disco» cuyo artista casa, en orden. Fuera los «Various» y los que
    /// no dicen de quién son.
    /// </summary>
    private static IEnumerable<(JsonNode Item, string Disco)> DelArtista(JsonArray resultados, string artist)
    {
        var delArchivo = Interpretes(artist);
        if (delArchivo.Count == 0) yield break;
        foreach (var it in resultados)
        {
            if (it == null) continue;
            var m = Regex.Match(J.S(J.P(it, "title")), @"^(.*?) - (.+)$");
            if (!m.Success) continue;
            var a = m.Groups[1].Value;
            if (Regex.IsMatch(a, @"^\s*Various\b", IC)) continue;
            if (Interpretes(a).Any(d => delArchivo.Any(f => MismoInterprete(f, d))))
                yield return (it, m.Groups[2].Value);
        }
    }

    // Separadores de un crédito con varios intérpretes: «Anuel AA, Ñengo Flow, Sinfonico»,
    // «Wisin & Yandel», «Bad Bunny x Jhay Cortez», «Daddy Yankee feat. Snow».
    private const string SeparadorInterpretes =
        @"\s*(?:,|&|\+|/|;|\bx\b|\bvs\.?(?=\s)|\bfeat\.?(?=\s)|\bft\.?(?=\s)|\bfeaturing\b|\band\b|\by\b|\be\b)\s*";

    /// <summary>Los intérpretes de un crédito, cada uno normalizado (Nk); sin los vacíos.</summary>
    internal static List<string> Interpretes(string? credito)
        => Regex.Split(credito ?? "", SeparadorInterpretes, IC)
                .Select(TextUtils.Nk)
                .Where(s => s.Length >= 2)
                .Distinct()
                .ToList();

    /// <summary>
    /// ¿Son el mismo intérprete? Comparando UNO a UNO, nunca el crédito entero.
    ///
    /// Visto en revisión (C4a): con el crédito concatenado («Anuel AA, Ñengo Flow, Sinfonico» →
    /// «anuelaanengoflowsinfonico») casaba por subcadena cualquier artista de Discogs de tres letras
    /// o más contenido ahí («Flow», «Sin»), y ArtistMatch añadía un StartsWith sin longitud mínima:
    /// «Arca» casaba con «Arcangel». Ahora tienen que ser el mismo nombre, el mismo con el número
    /// con que Discogs distingue a los homónimos («Mad Mind (2)»), o un alias conocido.
    /// </summary>
    internal static bool MismoInterprete(string delArchivo, string deDiscogs)
    {
        if (delArchivo.Length == 0 || deDiscogs.Length == 0) return false;
        // «The Weeknd» y «Weeknd» son el mismo: el artículo no cuenta.
        var f = SinArticulo(delArchivo);
        var d = SinArticulo(deDiscogs);
        if (f == d) return true;
        if (d.StartsWith(f, StringComparison.Ordinal) && d[f.Length..].All(char.IsAsciiDigit)) return true;
        return ArtistAliases.Current.SameArtist(delArchivo, deDiscogs);

        static string SinArticulo(string s) => s.Length > 5 && s.StartsWith("the", StringComparison.Ordinal) ? s[3..] : s;
    }
}
