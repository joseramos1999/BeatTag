using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etiquetador.Core.Analysis;

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

    /// <param name="wantTool">
    /// El archivo ES una versión instrumental o acapella (lo dice su nombre). Sin esto, esas
    /// versiones del catálogo se tratan como no pedidas. Si el título que llega aún la nombra, se
    /// deduce solo.
    /// </param>
    /// <param name="letra">
    /// Lo que dice el NOMBRE del archivo de su letra («Clean», «Dirty»…; ver
    /// <see cref="ExplicitDetector.DelNombre"/>). Sirve para elegir entre las dos versiones que
    /// publican los catálogos (ver <see cref="CoherenciaDeLetra"/>).
    /// </param>
    public async Task<ProviderResult?> SearchAsync(string artist, string title, bool wantRemix, bool wantLive,
        int localDur = 0, bool isEdit = false, CancellationToken ct = default, string expectedRemixer = "", bool verbatim = false,
        bool wantTool = false, Explicitness letra = Explicitness.Unknown)
    {
        var kw = verbatim ? Descriptors.BuildKwVerbatim(artist, title) : Descriptors.BuildKw(artist, title);
        if (kw.Length == 0) kw = Descriptors.CleanKeywords(title);
        if (kw.Length == 0) { Log?.Detail("      dz: query vacía tras limpiar -> no se busca"); return null; }

        Log?.Detail($"      dz: query='{kw}' (de '{artist}' / '{title}') remix={wantRemix} live={wantLive} dur={localDur}s");
        // El ritmo lo pone el limitador por servidor de ApiClient (50 peticiones cada 5 s en Deezer).
        var r = await _api.GetAsync($"https://api.deezer.com/search?q={TextUtils.UrlEnc(kw)}&limit=8", ct: ct).ConfigureAwait(false);
        var data = J.A(J.P(r, "data"));
        if (data == null || data.Count == 0) { Log?.Detail("      dz: 0 resultados"); return null; }

        var pick = SelectBest(data, artist, title, wantRemix, wantLive, localDur, isEdit, out var bestSc, out var bestWhy,
            Log == null ? null : m => Log.Detail("      dz: " + m), expectedRemixer, wantTool, letra);
        if (pick == null) { Log?.Detail($"      dz: {data.Count} resultados, ninguno aceptado"); return null; }

        // Enriquecido: BPM, año y contribuidores completos vía track/{id}
        string bpm = "", year = "";
        string artistFull = J.S(J.P(pick, "artist", "name"));
        var tk = await _api.GetAsync("https://api.deezer.com/track/" + J.S(J.P(pick, "id")), ct: ct).ConfigureAwait(false);
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

        // ISRC y letra (C14): vienen en las respuestas que ya se han pedido, así que no cuestan nada.
        // Manda track/{id}, que es la ficha completa; si no llegó, la pista de la búsqueda también los trae.
        var isrc = NormalizarIsrc(J.S(J.P(tk, "isrc")));
        if (isrc.Length == 0) isrc = NormalizarIsrc(J.S(J.P(pick, "isrc")));
        var letraCatalogo = LetraDe(tk);
        if (letraCatalogo == Explicitness.Unknown) letraCatalogo = LetraDe(pick);
        if (isrc.Length > 0 || letraCatalogo != Explicitness.Unknown)
            Log?.Detail($"      dz: isrc={(isrc.Length > 0 ? isrc : "-")} letra={letraCatalogo}");

        return new ProviderResult
        {
            Isrc = isrc,
            Explicit = letraCatalogo,
            Title = J.S(J.P(pick, "title")),
            Artist = artistFull,
            Album = J.S(J.P(pick, "album", "title")),
            Year = year,
            Bpm = bpm.Length > 0 ? int.Parse(bpm) : 0,
            CoverUrl = J.S(J.P(pick, "album", "cover_big")),
            AlbumId = J.S(J.P(pick, "album", "id")),
            Score = bestSc < -900 ? 0 : Math.Round(bestSc, 1),
            Why = bestWhy,
            Dur = J.I(J.P(pick, "duration")),
        };
    }

    /// <summary>Género del álbum (Get-DeezerGenre).</summary>
    public async Task<string> AlbumGenreAsync(string albumId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(albumId)) return "";
        var al = await _api.GetAsync("https://api.deezer.com/album/" + albumId, ct: ct).ConfigureAwait(false);
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

    /// <summary>
    /// Misma selección para quien solo quiere la puntuación. Existe para no obligar a declarar un
    /// descarte en las decenas de pruebas de scoring, a las que el motivo no les aporta nada.
    /// </summary>
    public static JsonNode? SelectBest(JsonArray data, string artist, string title, bool wantRemix, bool wantLive,
        int localDur, bool isEdit, out double bestSc, Action<string>? trace = null, string expectedRemixer = "", bool wantTool = false,
        Explicitness letra = Explicitness.Unknown)
        => SelectBest(data, artist, title, wantRemix, wantLive, localDur, isEdit, out bestSc, out _, trace, expectedRemixer, wantTool, letra);

    /// <summary>Selección + scoring pura sobre los items de Deezer (data). Devuelve el item elegido o null.</summary>
    /// <param name="letra">Lo que dice el nombre del archivo de su letra; ver <see cref="CoherenciaDeLetra"/>.</param>
    public static JsonNode? SelectBest(JsonArray data, string artist, string title, bool wantRemix, bool wantLive,
        int localDur, bool isEdit, out double bestSc, out string bestWhy, Action<string>? trace = null, string expectedRemixer = "",
        bool wantTool = false, Explicitness letra = Explicitness.Unknown)
    {
        var nRemixer = TextUtils.Nk(expectedRemixer);
        var na = TextUtils.Nk(artist);
        var nt = TextUtils.Nk(Descriptors.CleanKeywords(title));
        // Instrumental y acapella son OTRO archivo para un DJ, no la canción. Visto en una
        // biblioteca real: «Black Eyed Peas - Shut Up» (vocal) perdía contra «Shut Up
        // (Instrumental)» por 12,64 a 8 -la regla del título sin sufijo le daba +8 y la duración
        // cuadraba-, y se renombraba como la instrumental con confianza 17,6, sin revisión.
        var pideHerramienta = wantTool || ReglasCatalogo.EsHerramienta(title);
        JsonNode? pick = null;
        bestSc = -999.0;
        bestWhy = "";
        // El desempate por la letra del candidato elegido (ver CoherenciaDeLetra): decide la elección
        // pero no entra en la confianza que se devuelve.
        var bestLetra = 0.0;
        // Motivo por el que gana el candidato elegido. Antes solo iba a la traza y se perdia: es
        // justo lo que hace falta para decidir sobre una propuesta dudosa sin abrir el registro.
        var razones = new List<string>();

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
                else if (pideHerramienta && ReglasCatalogo.EsHerramienta(rt))
                {
                    sc += 6; esLaVersionPedida = true; why.Add("instrumental/acapella pedida +6");
                }
            }

            var tituloPuntuo = sc > 0.0;

            // Estas descartan la pista a propósito: el catálogo trae una versión que NO se pedía.
            var esVersionNoPedida = false;
            var esBasura = Regex.IsMatch(rt, BadRe, IC);
            if (esBasura) { sc -= 9; esVersionNoPedida = true; why.Add("basura -9"); }
            if (!pideHerramienta && !esBasura && ReglasCatalogo.EsHerramienta(rt))
            {
                sc -= 9; esVersionNoPedida = true; why.Add("instrumental/acapella no pedida -9");
            }
            if (!wantLive && Regex.IsMatch(rt, LiveRe, IC)) { sc -= 9; esVersionNoPedida = true; why.Add("live no pedido -9"); }
            if (!wantRemix && Regex.IsMatch(rt, RemixRe, IC)) { sc -= 6; esVersionNoPedida = true; why.Add("remix no pedido -6"); }
            if (!wantRemix && !wantLive && Regex.IsMatch(rt, VerRe, IC)) { sc -= 2; why.Add("otra version -2"); }

            // El título no puntuó por ninguna vía, pero uno contiene al otro: comparten el núcleo.
            // Es el caso corriente en una biblioteca de DJ ("Groovejet" frente a "Groovejet (If This
            // Ain't Love)"), y valía CERO, de modo que la coincidencia correcta se quedaba por debajo
            // del umbral de revisión pese a ser la buena.
            //
            // Se puntúa en proporción a cuánto del título más largo explica el más corto, y se queda
            // por debajo de lo que vale una versión reconocida (+6): es un indicio, no una certeza.
            //
            // NO se aplica si lo que sobra es justo una versión no pedida: ahí el texto de más es
            // precisamente el motivo del descarte, y premiarlo rescataría por encima del umbral la
            // pista que se acaba de rechazar (un remix ajeno con la duración parecida).
            if (!tituloPuntuo && !esVersionNoPedida && nt.Length > 0 && tn.Length > 0)
            {
                // El catálogo añade a menudo un sufijo entre paréntesis que no forma parte de la
                // identidad del tema: "(feat. X)", "(Radio Edit)", el subtítulo. Comparando sin él,
                // muchos de estos títulos resultan ser exactamente el mismo. Medido sobre una tirada
                // real: 36 de 139 coincidencias dudosas sin crédito eran justo este caso.
                var nucleoCat = TextUtils.Nk(Regex.Replace(rt, @"\s*[\(\[].*$", ""));
                if (nucleoCat.Length > 0 && nucleoCat == nt)
                {
                    sc += 8; why.Add("titulo exacto sin el sufijo +8");
                }
                else
                {
                    var comun = tn.Contains(nt) || nt.Contains(tn);
                    var frac = (double)Math.Min(tn.Length, nt.Length) / Math.Max(tn.Length, nt.Length);

                    // Los record pool cortan el nombre del archivo por la mitad, así que su título
                    // es el PRINCIPIO del real ("Bu" de "Bum Bum Tam Tam"). Un prefijo es mucho
                    // menos casual que una coincidencia en cualquier punto, y para llegar aquí el
                    // artista YA ha casado, así que se admite con bastante menos parecido. Otros 58
                    // de aquellas 139 eran de este tipo.
                    var esPrefijo = tn.StartsWith(nt) || nt.StartsWith(tn);
                    var minimo = esPrefijo ? 0.15 : 0.34;

                    if (comun && frac >= minimo && Math.Min(tn.Length, nt.Length) >= 2)
                    {
                        var p = Math.Round(1.5 + 4.0 * frac, 1);
                        sc += p; why.Add($"{(esPrefijo ? "principio" : "nucleo")} del titulo +{p:0.##}");
                    }
                }
            }
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
            var letraP = PuntuarLetra(letra, x, why);
            trace?.Invoke($"candidato '{J.S(J.P(x, "artist", "name"))} - {rt}' = {sc:0.##}  [{string.Join(", ", why)}]");
            if (sc + letraP > bestSc + bestLetra) { bestSc = sc; bestLetra = letraP; pick = x; razones = why; }
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
            var bestOrden = double.NegativeInfinity;   // con el desempate por la letra
            foreach (var x in data)
            {
                var tnx = TextUtils.Nk(J.S(J.P(x, "title")));
                var rt = J.S(J.P(x, "title"));
                var exacto = tnx == nt;
                var conRemixer = nRemixer.Length > 2 && tnx.Contains(nt) && tnx.Contains(nRemixer);
                if (!exacto && !conRemixer) continue;
                if (Regex.IsMatch(rt, BadRe, IC)) continue;
                if (!pideHerramienta && ReglasCatalogo.EsHerramienta(rt)) continue;

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
                var letraP = PuntuarLetra(letra, x, why);
                trace?.Invoke($"respaldo '{J.S(J.P(x, "artist", "name"))} - {rt}' = {sc:0.##}  [{string.Join(", ", why)}]");
                if (sc + letraP > bestOrden) { bestOrden = sc + letraP; best = sc; pick = x; razones = why; }
            }
            if (pick != null) bestSc = best;
        }

        // Último recurso FUZZY (typos). Las reglas, umbrales incluidos, son las comunes con iTunes
        // (ReglasCatalogo.RespaldoAproximado). Aquí solo se decide qué es una versión no pedida, con
        // los mismos criterios que la vía normal: antes este respaldo solo miraba BadRe y LiveRe, y
        // recuperaba con confianza 5 el remix que la vía normal acababa de RECHAZAR.
        if (pick == null && na.Length > 0 && nt.Length > 0)
        {
            var candidatos = data.Where(x => x != null).Select(x =>
            {
                var rt = J.S(J.P(x, "title"));
                var tn = TextUtils.Nk(rt);
                var noPedida = Regex.IsMatch(rt, BadRe, IC)
                            || (!wantLive && Regex.IsMatch(rt, LiveRe, IC))
                            || (!wantRemix && Regex.IsMatch(rt, RemixRe, IC) && !(nRemixer.Length > 2 && tn.Contains(nRemixer)))
                            || (!pideHerramienta && ReglasCatalogo.EsHerramienta(rt));
                return new ReglasCatalogo.CandidatoAproximado(x!, J.S(J.P(x, "artist", "name")), rt,
                                                              J.I(J.P(x, "duration")), noPedida);
            });
            var fz = ReglasCatalogo.RespaldoAproximado(candidatos, na, nt, localDur, isEdit, out var fzSc, out var fzWhy);
            if (fz != null)
            {
                pick = fz; bestSc = fzSc;
                razones = new List<string> { fzWhy };
                trace?.Invoke($"respaldo aproximado '{J.S(J.P(fz, "artist", "name"))} - {J.S(J.P(fz, "title"))}' = {fzSc:0.##}  [{fzWhy}]");
            }
        }
        bestWhy = pick == null ? "" : string.Join(", ", razones);
        return pick;
    }

    // ---- ISRC y letra del catálogo (C14) ----

    /// <summary>
    /// Lo que pesa, al ELEGIR, que la letra del candidato cuadre con la del archivo. Es un desempate:
    /// no pasa por encima del título ni de la duración, pero decide entre dos pistas que por lo demás
    /// puntúan igual, que es justo el caso de un tema publicado en versión explícita y en versión
    /// editada (mismo título, misma duración). Antes ganaba la primera de la lista, y con un archivo
    /// «(Clean)» del pool se proponían el álbum, el año y el ISRC de la explícita.
    ///
    /// No se suma a la confianza que se devuelve (visto en revisión): un archivo «(Clean)» del que
    /// Deezer solo tiene la explícita, que es lo habitual, perdía un punto de confianza y podía pasar a
    /// revisión por algo que no dice nada de si la coincidencia es buena.
    /// </summary>
    internal const double PuntosLetra = 1.0;

    /// <summary>
    /// +1 si el candidato es la misma versión que dice el nombre del archivo («Dirty» y pista
    /// explícita, «Clean» y pista editada), −1 si es la contraria, 0 si alguno de los dos no lo dice.
    /// Una pista «no explícita» no cuenta como clean: es la marca por defecto de muchas distribuidoras.
    /// </summary>
    public static double CoherenciaDeLetra(Explicitness archivo, JsonNode? candidato)
    {
        if (archivo == Explicitness.Unknown) return 0;
        var cat = LetraDe(candidato);
        if (cat == Explicitness.Unknown) return 0;
        return cat == archivo ? PuntosLetra : -PuntosLetra;
    }

    private static double PuntuarLetra(Explicitness archivo, JsonNode? candidato, List<string> why)
    {
        var p = CoherenciaDeLetra(archivo, candidato);
        if (p > 0) why.Add("letra como la del archivo (desempate)");
        else if (p < 0) why.Add("letra contraria a la del archivo (desempate)");
        return p;
    }

    /// <summary>
    /// La letra de una pista de Deezer. «explicit_lyrics» verdadero, o «explicit_content_lyrics» 1
    /// (explícita) o 4 (en parte explícita): explícita. 3 («editada»): la versión clean. El resto (0 «no
    /// explícita», 2 y 5 «desconocida», 6 y 7 «sin indicación»): sin dato. Una respuesta guardada por
    /// una versión anterior se lee igual: estos campos ya venían en ella.
    /// </summary>
    public static Explicitness LetraDe(JsonNode? pista)
    {
        if (pista == null) return Explicitness.Unknown;
        if (J.P(pista, "explicit_lyrics") is JsonValue v && v.TryGetValue<bool>(out var explicita) && explicita)
            return Explicitness.Explicit;
        if (J.P(pista, "explicit_content_lyrics") is not JsonValue) return Explicitness.Unknown;
        return J.I(J.P(pista, "explicit_content_lyrics")) switch
        {
            1 or 4 => Explicitness.Explicit,
            3 => Explicitness.Clean,
            _ => Explicitness.Unknown,
        };
    }

    /// <summary>
    /// El ISRC en su forma canónica de 12 caracteres (país, registrante, año y número: «USUYG1017706»),
    /// o "" si lo que llega no lo es. Se aceptan guiones y espacios («US-UYG-10-17706»), que algunas
    /// fuentes intercalan; cualquier otra cosa se descarta en vez de escribirla en el archivo.
    /// </summary>
    public static string NormalizarIsrc(string? isrc)
    {
        var s = (isrc ?? "").Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
        return IsrcRe.IsMatch(s) ? s : "";
    }

    private static readonly Regex IsrcRe = new(@"^[A-Z]{2}[A-Z0-9]{3}\d{7}$", RegexOptions.CultureInvariant);
}
