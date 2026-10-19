using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Etiquetador.Core.Providers;

/// <summary>MusicBrainz (sin clave, requiere User-Agent). Port de Get-MB/_MBPick. PickBest es puro.</summary>
public sealed class MusicBrainzProvider
{
    /// <summary>Puntuación mínima de MusicBrainz en la consulta estricta (título Y artista).</summary>
    public const int ScoreMinimo = 85;

    private readonly ApiClient _api;
    public MusicBrainzProvider(ApiClient api) => _api = api;

    public async Task<ProviderResult?> SearchAsync(string artist, string title, string ua, CancellationToken ct = default)
    {
        var ctitle = Descriptors.CleanKeywords(title);
        if (ctitle.Length == 0) ctitle = title;
        var ca = Regex.Split(artist ?? "", ",")[0].Trim();
        ca = Regex.Replace(ca, @"(?i)(feat\.?|ft\.?|featuring).*$", "").Trim();
        if (ca.Length == 0) ca = artist ?? "";
        // Sin artista no hay forma de comprobar de quién es lo que devuelva: no se pregunta.
        if (TextUtils.NkClave(ca).Length == 0) return null;

        // El ritmo (1 petición por segundo, lo que publica MusicBrainz) lo pone ApiClient.
        var headers = new Dictionary<string, string> { ["User-Agent"] = ua };
        var q = $"recording:%22{TextUtils.UrlEnc(ctitle)}%22%20AND%20artist:%22{TextUtils.UrlEnc(ca)}%22";
        var r = await _api.GetAsync($"https://musicbrainz.org/ws/2/recording?query={q}&fmt=json&limit=5", headers, ct: ct).ConfigureAwait(false);
        var res = PickBest(r, ca, ctitle);
        if (res != null) return res;

        var kw = Descriptors.BuildKw(artist, title);
        if (kw.Length > 0)
        {
            var r2 = await _api.GetAsync($"https://musicbrainz.org/ws/2/recording?query={TextUtils.UrlEnc(kw)}&fmt=json&limit=6&dismax=true", headers, ct: ct).ConfigureAwait(false);
            res = PickBest(r2, ca, ctitle, consultaLibre: true);
            if (res != null) return res;
        }
        return null;
    }

    /// <summary>
    /// Elige la grabación cuyo artista Y título casan con los del archivo. Puro.
    ///
    /// Antes se quedaba con la de mayor puntuación y solo miraba el artista. Pero la puntuación de
    /// MusicBrainz es RELATIVA -la primera siempre saca 100-, así que el corte de 85 no filtraba
    /// nada, y un tema distinto del mismo artista pasaba sin más. Vistos en una biblioteca real, con
    /// la casilla de aplicar marcada: «Bellaqueo 4 Life - Ñengo Flow» -> «Yomo con Ñengo», «Gayle x
    /// Eric Prydz - abcdepjanoo» -> «Eric Prydz - Eric Prydz», «Garmiani - Watch out for fogo» ->
    /// «Julimar Santos - Fogo». Ahora:
    ///   · el título tiene que casar (<see cref="ReglasCatalogo.TituloVerificado"/>);
    ///   · sin crédito de artista no se acepta (antes, un crédito vacío pasaba la comprobación);
    ///   · el corte de 85 solo vale en la consulta estricta; en la libre (dismax) la puntuación no
    ///     dice nada y se ignora, así que puede ganar una que no sea la primera si es la que casa.
    /// </summary>
    /// <param name="artist">Artista del archivo (el principal).</param>
    /// <param name="title">Título del archivo.</param>
    /// <param name="consultaLibre">La respuesta viene de la consulta dismax, no de la estricta.</param>
    public static ProviderResult? PickBest(JsonNode? r, string artist, string title, bool consultaLibre = false)
    {
        var recs = J.A(J.P(r, "recordings"));
        if (recs == null || recs.Count == 0) return null;
        var nka = TextUtils.NkClave(artist);
        if (nka.Length == 0) return null;

        // De mayor a menor puntuación, y a igualdad, en el orden en que vienen.
        var ordenadas = recs.Select((rec, i) => (rec, i, score: J.I(J.P(rec, "score"))))
                            .Where(t => t.rec != null)
                            .OrderByDescending(t => t.score).ThenBy(t => t.i);
        foreach (var (best, _, bestScore) in ordenadas)
        {
            if (!consultaLibre && bestScore < ScoreMinimo) break;

            var credit = Credito(best);
            var nkc = TextUtils.NkClave(credit);
            if (nkc.Length == 0) continue;
            if (!nkc.Contains(nka) && !nka.Contains(nkc)) continue;
            if (!ReglasCatalogo.TituloVerificado(title, J.S(J.P(best, "title")), out var exacto)) continue;

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
            // En la consulta estricta, MusicBrainz trae su PROPIA puntuación (0-100) y solo se aceptan
            // las de 85 para arriba: se lleva 85-100 a nuestra escala 2,5-10. Antes no se trasladaba
            // y TODAS acababan marcadas como "baja confianza" sin motivo.
            // En la libre esa puntuación no significa nada (la primera siempre es 100): la confianza
            // la da lo que se ha comprobado aquí, como la del respaldo aproximado de Deezer.
            var score = consultaLibre ? (exacto ? 5.0 : 3.0) : Math.Round((bestScore - 80) / 2.0, 1);
            return new ProviderResult
            {
                Title = J.S(J.P(best, "title")), Artist = credit, Album = album, Year = year,
                Score = score,
                Why = consultaLibre
                    ? (exacto ? "MusicBrainz (consulta libre): mismo título y artista" : "MusicBrainz (consulta libre): título parecido, mismo artista")
                    : $"MusicBrainz: puntuación {bestScore}, mismo título y artista",
            };
        }
        return null;
    }

    private static string Credito(JsonNode? rec)
    {
        var creditArr = J.A(J.P(rec, "artist-credit"));
        if (creditArr == null) return "";
        var names = new List<string>();
        foreach (var c in creditArr) { var n = J.S(J.P(c, "name")); if (n.Length > 0) names.Add(n); }
        return string.Join(", ", names);
    }
}
