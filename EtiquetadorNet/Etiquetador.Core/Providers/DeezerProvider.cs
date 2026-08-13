using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Etiquetador.Core.Providers;

/// <summary>
/// Deezer: fuente principal (sin clave) y única con BPM. Port de Get-Deezer/Get-DeezerGenre.
/// La selección/scoring (SelectBest) es pura y testeable; SearchAsync añade la red + enriquecido.
/// </summary>
public sealed class DeezerProvider
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    internal const string BadRe = @"(?i)\b(sped\s*up|speed\s*up|slowed|nightcore|reverb|8d\s*audio|karaoke|tribute|made\s+famous|cover\s+version|remaster(?:ed)?|anniversary|aniversario)\b";
    internal const string LiveRe = @"(?i)\b(live|en\s+vivo|en\s+directo|directo|unplugged)\b";
    internal const string RemixRe = @"(?i)\b(remix|rmx|bootleg|mashup|flip)\b";
    internal const string VerRe = @"(?i)\b(?:radio|main|album|single)\s+version\b";

    private readonly ApiClient _api;

    /// <summary>Log opcional: deja en el archivo por qué se eligió (o se rechazó) cada candidato.</summary>
    public Logger? Log { get; set; }

    public DeezerProvider(ApiClient api) => _api = api;

    public async Task<ProviderResult?> SearchAsync(string artist, string title, bool wantRemix, bool wantLive,
        int localDur = 0, bool isEdit = false, CancellationToken ct = default, string expectedRemixer = "", bool verbatim = false)
    {
        var kw = verbatim ? Descriptors.BuildKwVerbatim(artist, title) : Descriptors.BuildKw(artist, title);
        if (kw.Length == 0) kw = Descriptors.CleanKeywords(title);
        if (kw.Length == 0) { Log?.Detail("      dz: query vacía tras limpiar -> no se busca"); return null; }

        Log?.Detail($"      dz: query='{kw}' (de '{artist}' / '{title}') remix={wantRemix} live={wantLive} dur={localDur}s");
        var r = await _api.GetAsync($"https://api.deezer.com/search?q={TextUtils.UrlEnc(kw)}&limit=8", null, 350, ct).ConfigureAwait(false);
        var data = J.A(J.P(r, "data"));
        if (data == null || data.Count == 0) { Log?.Detail("      dz: 0 resultados"); return null; }

        var pick = SelectBest(data, artist, title, wantRemix, wantLive, localDur, isEdit, out var bestSc,
            Log == null ? null : m => Log.Detail("      dz: " + m), expectedRemixer);
        if (pick == null) { Log?.Detail($"      dz: {data.Count} resultados, ninguno aceptado"); return null; }

        // Enriquecido: BPM, año y contribuidores completos vía track/{id}
        string bpm = "", year = "";
        string artistFull = J.S(J.P(pick, "artist", "name"));
        var tk = await _api.GetAsync("https://api.deezer.com/track/" + J.S(J.P(pick, "id")), null, 350, ct).ConfigureAwait(false);
        if (tk != null)
        {
            var b = J.D(J.P(tk, "bpm"));
            if (b > 0) bpm = ((int)Math.Round(b)).ToString();
            var rd = J.S(J.P(tk, "release_date"));
            var my = Regex.Match(rd, @"^(\d{4})");
            if (my.Success) year = my.Groups[1].Value;
            var contribs = J.A(J.P(tk, "contributors"));
            if (contribs is { Count: > 0 })
            {
                var seen = new HashSet<string>();
                var nm = new List<string>();
                foreach (var c in contribs)
                {
                    var cn = J.S(J.P(c, "name"));
                    if (cn.Length > 0 && seen.Add(cn.ToLowerInvariant())) nm.Add(cn);
                }
                if (nm.Count >= 1) artistFull = string.Join(", ", nm);
            }
        }

        return new ProviderResult
        {
            Title = J.S(J.P(pick, "title")),
            Artist = artistFull,
            Album = J.S(J.P(pick, "album", "title")),
            Year = year,
            Bpm = bpm.Length > 0 ? int.Parse(bpm) : 0,
            CoverUrl = J.S(J.P(pick, "album", "cover_big")),
            AlbumId = J.S(J.P(pick, "album", "id")),
            Score = bestSc < -900 ? 0 : Math.Round(bestSc, 1),
            Dur = J.I(J.P(pick, "duration")),
        };
    }

    /// <summary>Género del álbum (Get-DeezerGenre).</summary>
    public async Task<string> AlbumGenreAsync(string albumId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(albumId)) return "";
        var al = await _api.GetAsync("https://api.deezer.com/album/" + albumId, null, 350, ct).ConfigureAwait(false);
        var data = J.A(J.P(J.P(al, "genres"), "data"));
        if (data is { Count: > 0 })
        {
            var names = new List<string>();
            foreach (var g in data) { var s = J.S(J.P(g, "name")); if (s.Length > 0) names.Add(s); }
            var picked = GenrePicker.Pick(null, names);
            return picked.Length > 0 ? picked : (names.Count > 0 ? names[0] : "");
        }
        return "";
    }

    /// <summary>Selección + scoring pura sobre los items de Deezer (data). Devuelve el item elegido o null.</summary>
    public static JsonNode? SelectBest(JsonArray data, string artist, string title, bool wantRemix, bool wantLive,
        int localDur, bool isEdit, out double bestSc, Action<string>? trace = null, string expectedRemixer = "")
    {
        var nRemixer = TextUtils.Nk(expectedRemixer);
        var na = TextUtils.Nk(artist);
        var nt = TextUtils.Nk(Descriptors.CleanKeywords(title));
        JsonNode? pick = null;
        bestSc = -999.0;

        foreach (var x in data)
        {
            var an = TextUtils.Nk(J.S(J.P(x, "artist", "name")));
            var tn = TextUtils.Nk(J.S(J.P(x, "title")));
            if (an.Length > 0 && tn.StartsWith(an) && nt.Length > 0 && !nt.StartsWith(an)) continue;
            var aok = Matching.ArtistMatch(na, an);
            // Que quede constancia: aceptar por alias es una decisión que conviene poder revisar en el log.
            if (aok && an != na && ArtistAliases.Current.SameArtist(na, an))
                trace?.Invoke($"alias: '{J.S(J.P(x, "artist", "name"))}' se considera el mismo artista");
            var tok2 = nt.Length > 0 && (tn.Contains(nt) || nt.Contains(tn));
            if (!(aok && tok2))
            {
                trace?.Invoke($"descartado '{J.S(J.P(x, "artist", "name"))} - {J.S(J.P(x, "title"))}' "
                            + $"({(aok ? "" : "artista≠")}{(tok2 ? "" : " titulo≠")})".TrimEnd());
                continue;
            }

            var rt = J.S(J.P(x, "title"));
            double sc = 0.0;
            var why = new List<string>();

            // La versión que pedíamos: el catálogo trae el mismo título MÁS el descriptor (y a veces el
            // remixer) que el archivo ya nombraba. Antes esto valía 0 (solo puntuaba el título idéntico),
            // así que remixes bien identificados se quedaban por debajo del umbral de revisión.
            var esLaVersionPedida = false;
            if (tn == nt) { sc += 10; why.Add("titulo exacto +10"); }
            else if (nt.Length > 0 && tn.Contains(nt))
            {
                if (nRemixer.Length > 2 && tn.Contains(nRemixer))
                {
                    sc += 9; esLaVersionPedida = true; why.Add($"version de '{expectedRemixer}' +9");
                }
                else if (wantRemix && Regex.IsMatch(rt, RemixRe, IC))
                {
                    sc += 6; esLaVersionPedida = true; why.Add("remix pedido +6");
                }
                else if (wantLive && Regex.IsMatch(rt, LiveRe, IC))
                {
                    sc += 6; esLaVersionPedida = true; why.Add("live pedido +6");
                }
            }

            if (Regex.IsMatch(rt, BadRe, IC)) { sc -= 9; why.Add("basura -9"); }
            if (!wantLive && Regex.IsMatch(rt, LiveRe, IC)) { sc -= 9; why.Add("live no pedido -9"); }
            if (!wantRemix && Regex.IsMatch(rt, RemixRe, IC)) { sc -= 6; why.Add("remix no pedido -6"); }
            if (!wantRemix && !wantLive && Regex.IsMatch(rt, VerRe, IC)) { sc -= 2; why.Add("otra version -2"); }
            // Si lo que sobra es justo la versión pedida, no se castiga por ser más largo.
            var lenPen = esLaVersionPedida ? 0 : Math.Max(0, tn.Length - nt.Length) * 0.03;
            if (lenPen > 0) { sc -= lenPen; why.Add($"largo -{lenPen:0.##}"); }
            var dur = J.I(J.P(x, "duration"));
            if (localDur > 0 && dur > 0)
            {
                var dd = Math.Abs(dur - localDur);
                if (dd <= 2) { sc += 5; why.Add($"dur ={dd}s +5"); }
                else if (dd <= 5) { sc += 2; why.Add($"dur ~{dd}s +2"); }
                else if (!isEdit)
                {
                    if (dd > 20) { sc -= 5; why.Add($"dur Δ{dd}s -5"); }
                    else if (dd > 10) { sc -= 2; why.Add($"dur Δ{dd}s -2"); }
                    else why.Add($"dur Δ{dd}s");
                }
                else why.Add($"dur Δ{dd}s (edit)");
            }
            else why.Add("sin duracion");
            trace?.Invoke($"candidato '{J.S(J.P(x, "artist", "name"))} - {rt}' = {sc:0.##}  [{string.Join(", ", why)}]");
            if (sc > bestSc) { bestSc = sc; pick = x; }
        }
        if (pick != null && bestSc <= -8)
        {
            trace?.Invoke($"RECHAZADO: el mejor solo llega a {bestSc:0.##} (<= -8) -> mejor no tocar");
            pick = null;   // solo versiones penalizadas -> mejor no tocar
        }

        // Respaldo sin artista: el nombre del archivo no traía artista (muy típico del material de
        // pool: "Bye bye (YANISS Remix).mp3"). OJO: antes se elegía aquí SIN asignar puntuación, así
        // que todas estas canciones acababan con confianza 0 y se marcaban como dudosas aunque el
        // match fuera correcto. Ahora se puntúan, y se prefiere la versión concreta si el catálogo
        // la tiene (título + el remixer que nombra el archivo).
        if (pick == null && na.Length == 0 && nt.Length > 0 && J.WordCount(title) >= 2)
        {
            var best = double.NegativeInfinity;
            foreach (var x in data)
            {
                var tnx = TextUtils.Nk(J.S(J.P(x, "title")));
                var rt = J.S(J.P(x, "title"));
                var exacto = tnx == nt;
                var conRemixer = nRemixer.Length > 2 && tnx.Contains(nt) && tnx.Contains(nRemixer);
                if (!exacto && !conRemixer) continue;
                if (Regex.IsMatch(rt, BadRe, IC)) continue;

                var why = new List<string>();
                double sc;
                if (conRemixer) { sc = 9; why.Add($"titulo + version de '{expectedRemixer}' +9"); }
                else { sc = 5; why.Add("titulo exacto (sin artista en el nombre) +5"); }
                if (!wantLive && Regex.IsMatch(rt, LiveRe, IC)) { sc -= 9; why.Add("live no pedido -9"); }
                if (!wantRemix && Regex.IsMatch(rt, RemixRe, IC)) { sc -= 6; why.Add("remix no pedido -6"); }
                var dur2 = J.I(J.P(x, "duration"));
                if (localDur > 0 && dur2 > 0)
                {
                    var dd2 = Math.Abs(dur2 - localDur);
                    if (dd2 <= 2) { sc += 5; why.Add($"dur ={dd2}s +5"); }
                    else if (dd2 <= 5) { sc += 2; why.Add($"dur ~{dd2}s +2"); }
                    else if (!isEdit && !conRemixer)
                    {
                        if (dd2 > 20) { sc -= 5; why.Add($"dur Δ{dd2}s -5"); }
                        else if (dd2 > 10) { sc -= 2; why.Add($"dur Δ{dd2}s -2"); }
                    }
                    else why.Add($"dur Δ{dd2}s (version)");
                }
                trace?.Invoke($"respaldo '{J.S(J.P(x, "artist", "name"))} - {rt}' = {sc:0.##}  [{string.Join(", ", why)}]");
                if (sc > best) { best = sc; pick = x; }
            }
            if (pick != null) bestSc = best;
        }

        // Último recurso FUZZY (typos)
        if (pick == null && na.Length > 0 && nt.Length > 0)
        {
            JsonNode? bestFz = null;
            double bestFzJw = 0.0;
            foreach (var x in data)
            {
                var an = TextUtils.Nk(J.S(J.P(x, "artist", "name")));
                var tn = TextUtils.Nk(J.S(J.P(x, "title")));
                if (an.Length == 0 || tn.Length == 0) continue;
                var rt = J.S(J.P(x, "title"));
                if (Regex.IsMatch(rt, BadRe, IC)) continue;
                if (!wantLive && Regex.IsMatch(rt, LiveRe, IC)) continue;
                var aStrict = Matching.ArtistMatch(na, an);
                var tStrict = tn.Contains(nt) || nt.Contains(tn);
                var aFuzzy = na.Length >= 5 && an.Length >= 5 && Matching.JaroWinkler(na, an) >= 0.90;
                var tFuzzy = nt.Length >= 5 && tn.Length >= 5 && Matching.JaroWinkler(nt, tn) >= 0.90;
                var dur = J.I(J.P(x, "duration"));
                var durOk = localDur > 0 && dur > 0 && Math.Abs(dur - localDur) <= 4;
                if ((aFuzzy && tStrict) || (aStrict && tFuzzy && durOk && !isEdit)) { pick = x; bestSc = 5; break; }
                if (aStrict && !tStrict && nt.Length >= 6 && tn.Length >= 6)
                {
                    var jw = Matching.JaroWinkler(nt, tn);
                    if (jw >= 0.88 && jw > bestFzJw) { bestFzJw = jw; bestFz = x; }
                }
            }
            if (pick == null && bestFz != null) { pick = bestFz; bestSc = 4; }
        }
        return pick;
    }
}
