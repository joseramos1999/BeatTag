using System.Text.RegularExpressions;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Providers;

namespace Etiquetador.Core.Pipeline;

/// <summary>Opciones de una tirada (equivale a los $src/flags que recibía Process-File).</summary>
public sealed class ProcessOptions
{
    public bool Deezer { get; set; } = true;
    public bool Itunes { get; set; }
    public bool Spotify { get; set; }
    public bool MusicBrainz { get; set; }
    public bool Discogs { get; set; }
    public bool AcoustId { get; set; }
    public bool Ai { get; set; }

    public string SpotifyId { get; set; } = "";
    public string SpotifySecret { get; set; } = "";
    public string DiscogsToken { get; set; } = "";
    public string AcoustIdKey { get; set; } = "";
    /// <summary>Modelo de Ollama a usar (p. ej. "llama3.2"). La IA local no necesita clave.</summary>
    public string AiModel { get; set; } = "";

    public bool CleanOnly { get; set; }
    public bool ForceSkipMix { get; set; }

    /// <summary>
    /// Búsqueda manual: si vienen informados, sustituyen al artista/título deducidos del nombre de
    /// archivo (el usuario dicta qué buscar desde "Reanalizar con búsqueda…"). Deliberadamente NO
    /// entran en Signature(): el resultado se guarda en la caché como la propuesta buena del archivo.
    /// </summary>
    public string SearchArtist { get; set; } = "";
    public string SearchTitle { get; set; } = "";

    /// <summary>
    /// Fuente elegida a mano ("Deezer" / "iTunes"): cuando el usuario escoge una coincidencia
    /// concreta, se busca SOLO en esa fuente para no acabar resolviéndolo otra distinta.
    /// </summary>
    public string SearchSource { get; set; } = "";

    /// <summary>Huella de las opciones que afectan al RESULTADO del análisis (para validar la caché).</summary>
    public string Signature() =>
        $"{Deezer}{Itunes}{Spotify}{MusicBrainz}{Discogs}{AcoustId}{Ai}|" +
        $"{SpotifyId.Length > 0}{SpotifySecret.Length > 0}{DiscogsToken.Length > 0}{AcoustIdKey.Length > 0}{AiModel}|" +
        $"{CleanOnly}";
}

/// <summary>
/// Orquestador: parsea el nombre, consulta las fuentes en orden de prioridad (Deezer→iTunes→Spotify,
/// luego MB/Discogs, IA de rescate verificada y AcoustID), aplica la estrategia de metadatos POR CAMPO
/// y compone el nombre de archivo y el tag de título. Port de Process-File.
/// </summary>
public sealed class FileProcessor
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private readonly DeezerProvider _dz;
    private readonly ItunesProvider _it;
    private readonly SpotifyProvider _sp;
    private readonly MusicBrainzProvider _mb;
    private readonly DiscogsProvider _dc;
    private readonly AcoustIdProvider _ac;
    private readonly OllamaClient _ai;
    private readonly Fingerprint _fp;
    private readonly HttpClient _http;
    private readonly ArtistExceptions _artistExc;
    private readonly Logger? _log;

    public FileProcessor(DeezerProvider dz, ItunesProvider it, SpotifyProvider sp, MusicBrainzProvider mb,
        DiscogsProvider dc, AcoustIdProvider ac, OllamaClient ai, Fingerprint fp, HttpClient http,
        ArtistExceptions artistExc, Logger? log = null)
    {
        _dz = dz; _it = it; _sp = sp; _mb = mb; _dc = dc; _ac = ac; _ai = ai; _fp = fp; _http = http;
        _artistExc = artistExc; _log = log;
    }

    /// <summary>
    /// Progreso DENTRO de una canción: (fase, 0..1). Permite a la UI mostrar una segunda barra
    /// con el avance del tema en curso. Opcional: si es null no se reporta nada.
    /// </summary>
    public IProgress<(string Phase, double Fraction)>? StepProgress { get; set; }

    private void Step(string phase, double fraction) => StepProgress?.Report((phase, fraction));

    /// <summary>Escapa un campo de la línea DATA (el separador es |).</summary>
    private static string Csv(string? s) => (s ?? "").Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ').Trim();

    /// <summary>Resumen de una coincidencia para el log ("-" si esa fuente no devolvió nada).</summary>
    private static string Desc(ProviderResult? r)
        => r == null ? "-" : $"'{r.Artist} - {r.Title}' ({r.Score}, {r.Dur}s)";

    public async Task<ProcessResult> ProcessAsync(string filePath, bool isAcapella, ProcessOptions o, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(filePath);
        var ext = Path.GetExtension(filePath);
        Step("leyendo tags", 0.05);

        if (o.ForceSkipMix)
            return new ProcessResult { FilePath = filePath, Old = fileName, New = fileName, Source = "Mezcla", Skip = true };

        var pr = FileNameParser.Parse(fileName);
        string @base = pr.Base, rawForOtros = pr.RawForOtros, fnArtist = pr.FnArtist, fnTitle = pr.FnTitle, qTitle = pr.QTitle;

        // Búsqueda manual: lo que teclea el usuario manda sobre lo deducido del nombre de archivo.
        var manual = o.SearchArtist.Length > 0 || o.SearchTitle.Length > 0;
        if (manual)
        {
            if (o.SearchArtist.Length > 0) fnArtist = o.SearchArtist.Trim();
            if (o.SearchTitle.Length > 0) { qTitle = o.SearchTitle.Trim(); fnTitle = qTitle; }
        }

        // Tags embebidos + duración local
        string tagTitle = "", tagArtist = "";
        int localDur = 0;
        try
        {
            using var tf = TagLib.File.Create(filePath);
            tagTitle = (tf.Tag.Title ?? "").Trim();
            tagArtist = (tf.Tag.JoinedPerformers ?? "").Trim();
            if (tagArtist.Length == 0) tagArtist = (tf.Tag.JoinedAlbumArtists ?? "").Trim();
            try { localDur = (int)Math.Round(tf.Properties.Duration.TotalSeconds); } catch { }
        }
        catch { }

        var isEdit = Regex.IsMatch(rawForOtros, Descriptors.DescRe, IC);
        var kwUsed = Descriptors.CleanKeywords($"{fnArtist} {qTitle}");

        // Traza por canción: es la que permite entender después POR QUÉ salió lo que salió.
        _log?.Detail($"· {fileName}");
        _log?.Detail($"    nombre -> artista='{fnArtist}' titulo='{qTitle}'" + (manual ? $"  [MANUAL fuente={(o.SearchSource.Length > 0 ? o.SearchSource : "cualquiera")}]" : ""));
        if (tagArtist.Length > 0 || tagTitle.Length > 0)
            _log?.Detail($"    tags   -> artista='{tagArtist}' titulo='{tagTitle}'");
        _log?.Detail($"    audio  -> {localDur}s · edit={isEdit}");

        // En búsqueda manual no se descarta como mezcla: el usuario pide expresamente identificar ESTE tema.
        if (!manual && Matching.IsSkipMix(@base, fnTitle, fnArtist))
        {
            _log?.Detail("    -> SALTADA (se considera mezcla/mashup)");
            return new ProcessResult { FilePath = filePath, Old = fileName, New = fileName, Source = "Mezcla", Skip = true, Kw = kwUsed, DurLocal = localDur };
        }

        var cleanOnly = o.CleanOnly;
        ProviderResult? sp = null, it = null, dz = null, mb = null, dc = null, primary = null, sec = null;
        ProviderResult? acHit = null;
        string variant = "", secSrc = "";

        // Los descriptores también se leen de lo tecleado: buscar "Tema (Live)" pide la versión en directo.
        var descSrc = manual ? $"{rawForOtros} {o.SearchTitle}" : rawForOtros;
        var wantRemix = Regex.IsMatch(descSrc, @"(?i)\b(remix|rmx|bootleg|flip|mashup|vip)\b");
        var wantLive = Regex.IsMatch(descSrc, @"(?i)\b(live|en\s+vivo|en\s+directo|unplugged|ac[uú]stic[oa])\b");

        // Quién firma la versión. Se calcula ANTES de buscar para poder puntuar mejor: si el catálogo
        // devuelve justo esa versión ("... (Yaniss Remix)"), es la pista correcta y no una coincidencia
        // a medias. Para un DJ, además, el remixer no puede perderse al limpiar el nombre.
        // Se mira el nombre ORIGINAL (FileNameParser ya le quita el editor), pero SIN el record pool:
        // "DJTOOLSVIP", "Latin Box"… distribuyen, no firman la versión.
        var nameForRemix = Regex.Replace(Path.GetFileNameWithoutExtension(fileName), Descriptors.PoolRe, " ", IC);
        var remix = RemixParser.Parse(nameForRemix);
        if (!remix.HasRemixer) remix = RemixParser.Parse(rawForOtros);
        if (manual && o.SearchTitle.Length > 0)
        {
            var manualRemix = RemixParser.Parse(o.SearchTitle);
            if (manualRemix.HasRemixer) remix = manualRemix;
        }
        if (remix.HasRemixer) _log?.Detail($"    remix  -> '{remix.Remixer}' ({remix.Kind})");

        const string splitArt = @"(?i)\s*(?:,| x | vs\.?| feat\.?| ft\.?)\s*";
        var firstArtist = fnArtist.Length > 0 ? Regex.Split(fnArtist, splitArt)[0].Trim() : "";
        var firstTitleArtist = fnTitle.Length > 0 ? Regex.Split(fnTitle, splitArt)[0].Trim() : "";
        var firstTitle = qTitle.Length > 0 ? Regex.Split(qTitle, splitArt)[0].Trim() : "";

        // Fuente forzada por el usuario al elegir una coincidencia concreta.
        var onlyDz = manual && o.SearchSource == "Deezer";
        var onlyIt = manual && o.SearchSource == "iTunes";
        var onlySp = manual && o.SearchSource == "Spotify";
        var forced = onlyDz || onlyIt || onlySp;   // si el usuario eligió fuente, nada la sustituye por detrás

        if (!cleanOnly)
        {
            // ORDEN: Deezer -> iTunes -> Spotify (último, cuota limitada)
            if (o.Deezer && !onlyIt && !onlySp)
            {
                Step("Deezer", 0.15);
                dz = await _dz.SearchAsync(fnArtist, qTitle, wantRemix, wantLive, localDur, isEdit, ct, remix.Remixer, verbatim: manual).ConfigureAwait(false); if (dz != null) variant = "nombre";
                if (dz == null && fnArtist.Length > 0) { dz = await _dz.SearchAsync(fnTitle, fnArtist, wantRemix, wantLive, localDur, isEdit, ct, remix.Remixer, verbatim: manual).ConfigureAwait(false); if (dz != null) variant = "invertido"; }
                if (dz == null && tagTitle.Length > 0 && Nk2(tagArtist, tagTitle) != Nk2(fnArtist, qTitle)) { dz = await _dz.SearchAsync(tagArtist, tagTitle, wantRemix, wantLive, localDur, isEdit, ct, remix.Remixer, verbatim: manual).ConfigureAwait(false); if (dz != null) variant = "tag"; }
                if (dz == null && firstArtist.Length > 0 && firstTitle.Length > 0 && Nk2(firstArtist, firstTitle) != Nk2(fnArtist, qTitle)) { dz = await _dz.SearchAsync(firstArtist, firstTitle, wantRemix, wantLive, localDur, isEdit, ct, remix.Remixer, verbatim: manual).ConfigureAwait(false); if (dz != null) variant = "principal"; }
                if (dz == null && firstTitleArtist.Length > 0 && firstArtist.Length > 0 && Nk2(firstTitleArtist, firstArtist) != Nk2(fnTitle, fnArtist)) { dz = await _dz.SearchAsync(firstTitleArtist, firstArtist, wantRemix, wantLive, localDur, isEdit, ct, remix.Remixer, verbatim: manual).ConfigureAwait(false); if (dz != null) variant = "principal"; }
                if (dz == null && firstTitle.Length > 0) { dz = await _dz.SearchAsync("", firstTitle, wantRemix, wantLive, localDur, isEdit, ct, remix.Remixer, verbatim: manual).ConfigureAwait(false); if (dz != null) variant = "titulo"; }
            }
            if (o.Itunes && dz == null && !onlyDz && !onlySp)
            {
                Step("iTunes", 0.3);
                it = await _it.SearchAsync(fnArtist, qTitle, localDur, isEdit, ct, verbatim: manual).ConfigureAwait(false); if (it != null) variant = "nombre";
                if (it == null && fnArtist.Length > 0) { it = await _it.SearchAsync(fnTitle, fnArtist, localDur, isEdit, ct, verbatim: manual).ConfigureAwait(false); if (it != null) variant = "invertido"; }
                if (it == null && tagTitle.Length > 0 && Nk2(tagArtist, tagTitle) != Nk2(fnArtist, qTitle)) { it = await _it.SearchAsync(tagArtist, tagTitle, localDur, isEdit, ct, verbatim: manual).ConfigureAwait(false); if (it != null) variant = "tag"; }
                if (it == null && firstArtist.Length > 0 && firstTitle.Length > 0 && Nk2(firstArtist, firstTitle) != Nk2(fnArtist, qTitle)) { it = await _it.SearchAsync(firstArtist, firstTitle, localDur, isEdit, ct, verbatim: manual).ConfigureAwait(false); if (it != null) variant = "principal"; }
                if (it == null && firstTitleArtist.Length > 0 && firstArtist.Length > 0 && Nk2(firstTitleArtist, firstArtist) != Nk2(fnTitle, fnArtist)) { it = await _it.SearchAsync(firstTitleArtist, firstArtist, localDur, isEdit, ct, verbatim: manual).ConfigureAwait(false); if (it != null) variant = "principal"; }
                if (it == null && firstTitle.Length > 0) { it = await _it.SearchAsync("", firstTitle, localDur, isEdit, ct, verbatim: manual).ConfigureAwait(false); if (it != null) variant = "titulo"; }
            }
            if (o.Spotify && o.SpotifyId.Length > 0 && o.SpotifySecret.Length > 0
                && ((dz == null && it == null && !forced) || onlySp))
            {
                Step("Spotify", 0.42);
                sp = await _sp.SearchAsync(fnArtist, qTitle, o.SpotifyId, o.SpotifySecret, wantRemix, wantLive, localDur, isEdit, ct).ConfigureAwait(false); if (sp != null) variant = "nombre";
                if (sp == null && fnArtist.Length > 0) { sp = await _sp.SearchAsync(fnTitle, fnArtist, o.SpotifyId, o.SpotifySecret, wantRemix, wantLive, localDur, isEdit, ct).ConfigureAwait(false); if (sp != null) variant = "invertido"; }
                if (sp == null && tagTitle.Length > 0) { sp = await _sp.SearchAsync(tagArtist, tagTitle, o.SpotifyId, o.SpotifySecret, wantRemix, wantLive, localDur, isEdit, ct).ConfigureAwait(false); if (sp != null) variant = "tag"; }
            }
            var spDiag = o.Spotify ? _sp.SpDiag : "off";
            primary = dz ?? it ?? sp;
            _log?.Detail($"    fuentes-> Deezer={Desc(dz)} · iTunes={Desc(it)} · Spotify={Desc(sp)}"
                       + (variant.Length > 0 ? $" (via {variant})" : ""));

            if (primary == null)
            {
                Step("MusicBrainz/Discogs", 0.55);
                if (o.MusicBrainz && fnArtist.Length > 0) mb = await _mb.SearchAsync(fnArtist, qTitle, AppInfo.MusicBrainzUserAgent, ct).ConfigureAwait(false);
                if (o.Discogs) dc = await _dc.SearchAsync(fnArtist, qTitle, AppInfo.UserAgent, o.DiscogsToken, ct).ConfigureAwait(false);
                if (mb == null && dc == null && tagTitle.Length > 0)
                {
                    var q2 = Regex.Replace(tagTitle, @"\([^)]*\)", "").Trim(); if (q2.Length == 0) q2 = tagTitle;
                    var aa = tagArtist.Length > 0 ? tagArtist : fnArtist;
                    if (Nk2(aa, q2) != Nk2(fnArtist, qTitle))
                    {
                        if (o.MusicBrainz && aa.Length > 0) mb = await _mb.SearchAsync(aa, q2, AppInfo.MusicBrainzUserAgent, ct).ConfigureAwait(false);
                        if (o.Discogs) dc = await _dc.SearchAsync(aa, q2, AppInfo.UserAgent, o.DiscogsToken, ct).ConfigureAwait(false);
                    }
                }
            }
            else if (o.Discogs) dc = await _dc.SearchAsync(primary.Artist, primary.Title, AppInfo.UserAgent, o.DiscogsToken, ct).ConfigureAwait(false);

            // IA local de rescate: la propuesta se RE-VERIFICA contra Deezer/iTunes; nunca se escribe sin confirmar.
            if (primary == null && o.Ai && !forced)
            {
                Step("IA local", 0.7);
                var ai = await _ai.ParseAsync(@base, tagArtist, tagTitle, o.AiModel, ct).ConfigureAwait(false);
                if (ai != null && ai.Title.Length > 0 && ai.Confidence >= 0.5 && !ai.IsMashup)
                {
                    var aiA = ai.Artist; var aiT = ai.Title;
                    if (o.Deezer) { dz = await _dz.SearchAsync(aiA, aiT, wantRemix, wantLive, localDur, isEdit, ct, remix.Remixer, verbatim: manual).ConfigureAwait(false); if (dz == null && aiA.Length > 0) dz = await _dz.SearchAsync(aiT, aiA, wantRemix, wantLive, localDur, isEdit, ct, remix.Remixer, verbatim: manual).ConfigureAwait(false); }
                    if (dz == null && o.Itunes) { it = await _it.SearchAsync(aiA, aiT, localDur, isEdit, ct, verbatim: manual).ConfigureAwait(false); if (it == null && aiA.Length > 0) it = await _it.SearchAsync(aiT, aiA, localDur, isEdit, ct, verbatim: manual).ConfigureAwait(false); }
                    primary = dz ?? it;
                    if (primary != null) variant = "ia";
                    if (primary != null && o.Discogs && dc == null) dc = await _dc.SearchAsync(primary.Artist, primary.Title, AppInfo.UserAgent, o.DiscogsToken, ct).ConfigureAwait(false);
                    _log?.Log($"        · IA local propuso: {aiA} - {aiT} (conf {ai.Confidence}) -> {(primary != null ? "VERIFICADO en " + (dz != null ? "Deezer" : "iTunes") : "no verificado en catalogo")}", LogKind.Dim, true);
                }
                else if (ai != null && ai.IsMashup) _log?.Log("        · IA local: lo considera un mashup -> no se etiqueta", LogKind.Dim, true);
                else if (ai != null && ai.Title.Length == 0) _log?.Log("        · IA local: no supo identificar la cancion", LogKind.Dim, true);
            }

            // AcoustID (último recurso): identificar por audio
            if (primary == null && o.AcoustId && o.AcoustIdKey.Length > 0 && !forced)
            {
                Step("huella acústica", 0.82);
                var fpr = await _fp.GetAsync(filePath, _http, ct).ConfigureAwait(false);
                if (fpr is FingerprintResult f)
                {
                    var ac = await _ac.LookupAsync(f.Duration, f.Fingerprint, o.AcoustIdKey, ct).ConfigureAwait(false);
                    if (ac == null || ac.Title.Length == 0)
                        // Lo normal en ediciones de DJ y mezclas: AcoustID solo conoce lanzamientos comerciales.
                        _log?.Detail("      huella: calculada, pero AcoustID no tiene esta grabación");
                    if (ac != null && ac.Title.Length > 0)
                    {
                        variant = "acoustid";
                        _log?.Detail($"      huella: AcoustID -> {ac.Artist} - {ac.Title}");
                        if (o.Deezer) dz = await _dz.SearchAsync(ac.Artist, ac.Title, false, false, localDur, isEdit, ct).ConfigureAwait(false);
                        if (dz == null && o.Itunes) it = await _it.SearchAsync(ac.Artist, ac.Title, localDur, isEdit, ct, verbatim: manual).ConfigureAwait(false);
                        if (o.Discogs && dc == null) dc = await _dc.SearchAsync(ac.Artist, ac.Title, AppInfo.UserAgent, o.DiscogsToken, ct).ConfigureAwait(false);
                        primary = dz ?? it;
                        if (primary == null) acHit = ac;
                    }
                }
            }
        }

        if (variant.Length == 0) { if (mb != null) variant = "mb"; else if (dc != null) variant = "discogs"; }

        // Estrategia POR CAMPO: rellena huecos (album/año/carátula) con una fuente secundaria.
        if (primary != null && !cleanOnly && (primary.Album.Length == 0 || primary.Year.Length == 0 || primary.CoverUrl.Length == 0))
        {
            if (o.Itunes && it == null) { sec = await _it.SearchAsync(primary.Artist, primary.Title, localDur, isEdit, ct, verbatim: manual).ConfigureAwait(false); if (sec != null) secSrc = "AppleMusic"; }
            if (sec == null && o.Deezer && dz == null) { sec = await _dz.SearchAsync(primary.Artist, primary.Title, false, false, localDur, isEdit, ct).ConfigureAwait(false); if (sec != null) secSrc = "Deezer"; }
        }

        var genreOnly = dc != null && !(sp != null || it != null || dz != null || mb != null || acHit != null) && !cleanOnly;
        var srcLabel = sp != null ? "Spotify" : it != null ? "AppleMusic" : dz != null ? "Deezer" : mb != null ? "MusicBrainz" : acHit != null ? "AcoustID" : genreOnly ? "Discogs(gén)" : cleanOnly ? "Limpieza" : "-";

        var title = sp != null ? sp.Title : it != null ? it.Title : dz != null ? dz.Title : mb != null ? mb.Title : acHit != null ? acHit.Title : (qTitle.Length > 0 ? qTitle : fnTitle);
        var artist = sp != null ? sp.Artist : it != null ? it.Artist : dz != null ? dz.Artist : mb != null ? mb.Artist : acHit != null ? acHit.Artist : fnArtist;

        string album = Pick(sp?.Album, it?.Album, dz?.Album, mb?.Album, sec?.Album, dc?.Album);
        string year = Pick(sp?.Year, it?.Year, dz?.Year, mb?.Year, sec?.Year, dc?.Year);

        string genre = "";
        if (dc != null && dc.Genre.Length > 0) genre = dc.Genre;
        if (genre.Length == 0 && sp != null && sp.ArtistId.Length > 0) { var g = await _sp.ArtistGenreAsync(sp.ArtistId, o.SpotifyId, o.SpotifySecret, ct).ConfigureAwait(false); if (g.Length > 0) genre = g; }
        if (genre.Length == 0 && dz != null && dz.AlbumId.Length > 0) { var g = await _dz.AlbumGenreAsync(dz.AlbumId, ct).ConfigureAwait(false); if (g.Length > 0) genre = g; }
        if (genre.Length == 0 && it != null && it.Genre.Length > 0) genre = it.Genre;
        if (genre.Length == 0 && sec != null && sec.Genre.Length > 0) genre = sec.Genre;
        if (genre.Length == 0 && sec != null && sec.AlbumId.Length > 0) { var g = await _dz.AlbumGenreAsync(sec.AlbumId, ct).ConfigureAwait(false); if (g.Length > 0) genre = g; }
        genre = GenreNormalizer.Canonical(genre);   // unifica sinónimos/grafías y da formato

        var bpm = dz is { Bpm: > 0 } ? dz.Bpm.ToString()
                : (secSrc == "Deezer" && sec is { Bpm: > 0 }) ? sec!.Bpm.ToString() : "";
        var cover = Pick(sp?.CoverUrl, it?.CoverUrl, dz?.CoverUrl, sec?.CoverUrl);

        title = Descriptors.CleanDbTitle(title, @base);
        title = TextUtils.UnScream(title);

        // feat/ft del nombre original (no descriptor DJ): se conserva en el título si no está ya
        var featM = Regex.Match(@base, @"(?i)(?:feat\.?|ft\.?|featuring)\s+(.+?)(?=\s*[-(\[]|$)");
        if (featM.Success)
        {
            var ft = featM.Groups[1].Value;
            ft = Regex.Replace(ft, Descriptors.DescRe, " ", IC);
            ft = Regex.Replace(ft, @"\b\d{1,3}\s*bpm\b", " ", IC);
            ft = Regex.Replace(ft, @"\b\d{2,3}\b", " ");
            ft = Regex.Replace(Regex.Replace(ft, @"[()\[\]]", " "), @"\s+", " ").Trim().Trim(',').Trim();
            if (ft.Length > 0 && !Regex.IsMatch(title, @"(?i)\bfeat|\bft\b") && !TextUtils.Nk(title).Contains(TextUtils.Nk(ft)) && !TextUtils.Nk(artist).Contains(TextUtils.Nk(ft)))
                title = $"{title} feat. {ft}";
        }

        var titleTrail = new List<string>();
        var mtr = Regex.Match(title, @"(?i)\s+(remix|extended(?:\s+mix)?|club\s+mix|radio\s+edit|version)\s*$");
        if (mtr.Success) { titleTrail.Add(TextUtils.TitleCase(mtr.Groups[1].Value.Trim())); title = title.Substring(0, mtr.Index).Trim(); }

        var otros = Descriptors.ExtractOtros(Descriptors.CompleteTruncated(rawForOtros), title).ToList();
        foreach (var tr in titleTrail)
            if (!otros.Any(x => TextUtils.Nk(x) == TextUtils.Nk(tr))) otros.Add(tr);

        // Si el nombre del archivo no daba autor, se intenta con el título del catálogo.
        if (!remix.HasRemixer && primary != null)
        {
            var fromDb = RemixParser.Parse(primary.Title);
            if (fromDb.HasRemixer) remix = fromDb;
        }

        // El "autor" no puede ser el propio título de la canción: en nombres como
        // "Stereo Love (Stereo Love Extended)" lo que precede al descriptor es el título repetido,
        // y colarlo daba cosas como "Stereo Love (Stereo Love Extended Extended)".
        var nRx = TextUtils.Nk(remix.Remixer);
        var nTi = TextUtils.Nk(title);
        var nQt = TextUtils.Nk(qTitle);
        var esElTitulo = nRx.Length > 0 && (
               (nTi.Length > 3 && (nTi.Contains(nRx) || nRx.Contains(nTi)))
            || (nQt.Length > 3 && (nQt.Contains(nRx) || nRx.Contains(nQt))));

        if (remix.HasRemixer && !esElTitulo)
        {
            // Sustituye el descriptor pelado ("Remix") por el completo ("Tiesto Remix").
            var kindKey = TextUtils.Nk(remix.Kind);
            var idx = otros.FindIndex(x => TextUtils.Nk(x) == kindKey);
            if (idx >= 0) otros[idx] = remix.Label;
            else otros.Add(remix.Label);
        }
        else if (esElTitulo) _log?.Detail($"    remix  -> '{remix.Remixer}' descartado (es el propio título)");
        if (isAcapella && !TextUtils.Nk(title).Contains("acapella") && !otros.Any(x => TextUtils.Nk(x).Contains("acapella")))
            otros.Add("Acapella");

        // Descriptores redundantes: si uno ya está contenido en otro se queda el más completo
        // ("Extended" sobra si está "Cesar Vilo Extended"). Evita "(Extended, X Extended)".
        if (otros.Count > 1)
        {
            var keys = otros.Select(TextUtils.Nk).ToList();
            for (int a = otros.Count - 1; a >= 0; a--)
            {
                if (keys[a].Length == 0) { otros.RemoveAt(a); keys.RemoveAt(a); continue; }
                for (int b = 0; b < otros.Count; b++)
                {
                    if (a == b) continue;
                    if (keys[b].Length > keys[a].Length && keys[b].Contains(keys[a]))
                    { otros.RemoveAt(a); keys.RemoveAt(a); break; }
                }
            }
        }

        var mainArtist = artist.Trim(); if (mainArtist.Length == 0) mainArtist = fnArtist;
        mainArtist = Regex.Replace(mainArtist, @"(?i)\s+ft\.?\s+", " feat. ");
        mainArtist = Regex.Replace(mainArtist, @"(?i)\s+featuring\s+", " feat. ");
        mainArtist = Regex.Replace(Regex.Replace(mainArtist, "_", " "), @"\s{2,}", " ").Trim();
        mainArtist = _artistExc.NormalizeArtists(mainArtist);
        title = Feat.RemoveRedundantFeat(title, mainArtist).Trim();

        var newBase = mainArtist.Length > 0 ? $"{mainArtist} - {title}" : title;
        if (otros.Count > 0) newBase = $"{newBase} (" + string.Join(", ", otros) + ")";
        newBase = TextUtils.Sanitize(TextUtils.ToAscii(newBase));
        if (newBase.Length == 0) newBase = TextUtils.Sanitize(TextUtils.ToAscii(@base));

        var titleTag = title;
        if (otros.Count > 0) titleTag = $"{title} (" + string.Join(", ", otros) + ")";

        Step("componiendo", 0.95);

        // Confianza mostrada = puntuación del proveedor + coherencia con los tags ya embebidos.
        var scoreStr = "";
        if (primary != null)
        {
            var tagAdj = Matching.TagCoherence(tagArtist, tagTitle, primary.Artist, primary.Title);
            var finalScore = Math.Round(primary.Score + tagAdj, 1);

            // Si la coincidencia la elegiste TÚ (de la lista, por enlace o por huella), no tiene
            // sentido marcarla como dudosa: la validaste viéndola. Antes salía con confianza 0 y
            // se auto-desmarcaba, que es justo lo contrario de lo que quieres.
            if (manual && o.SearchSource.Length > 0 && finalScore < 10)
            {
                _log?.Detail($"    confianza-> {finalScore} -> 10 (coincidencia elegida por el usuario en {o.SearchSource})");
                finalScore = 10;
            }
            else if (manual && finalScore < 4)
            {
                // Términos dictados a mano: tampoco debería quedar por debajo del umbral de revisión.
                _log?.Detail($"    confianza-> {finalScore} -> 4 (búsqueda dictada por el usuario)");
                finalScore = 4;
            }
            scoreStr = finalScore.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _log?.Detail($"    confianza-> {primary.Score} (fuente) {(tagAdj >= 0 ? "+" : "")}{tagAdj} (tags) = {scoreStr}"
                       + (finalScore < 2.0 ? "  [BAJA]" : ""));
        }
        _log?.Detail($"    -> {(primary != null || acHit != null || genreOnly || cleanOnly ? "OK" : "SIN RESULTADO")} · fuente={srcLabel} · nuevo='{newBase + ext}'");

        // Línea estructurada (una por canción) pensada para analizar la tirada entera después:
        // se puede filtrar con  findstr DATA  y abrir como CSV con separador |.
        var durMatch = primary?.Dur ?? 0;
        var durDelta = (localDur > 0 && durMatch > 0) ? Math.Abs(durMatch - localDur) : -1;
        _log?.Detail(string.Join("|", new[]
        {
            "DATA",
            Csv(fileName),
            primary != null ? "OK" : (cleanOnly ? "LIMPIEZA" : genreOnly ? "SOLO-GENERO" : acHit != null ? "ACOUSTID" : "SIN-RESULTADO"),
            Csv(srcLabel),
            Csv(variant),
            scoreStr,
            localDur.ToString(),
            durMatch.ToString(),
            durDelta.ToString(),
            Csv(kwUsed),
            Csv(artist), Csv(title),
            Csv(remix.Remixer), Csv(remix.Kind),
            Csv(genre), Csv(bpm), Csv(year),
            Csv(newBase + ext),
        }));

        return new ProcessResult
        {
            FilePath = filePath,
            Old = fileName,
            New = newBase + ext,
            Title = titleTag,
            Artist = artist,
            Album = album,
            Year = year,
            Genre = genre,
            CoverUrl = cover,
            Bpm = bpm,
            Found = sp != null || it != null || dz != null || mb != null || acHit != null,
            GenreOnly = genreOnly,
            Source = srcLabel,
            SpDiag = o.Spotify ? _sp.SpDiag : "off",
            Kw = kwUsed,
            CleanOnly = cleanOnly,
            Skip = false,
            Variant = variant,
            Score = scoreStr,
            Remixer = remix.Remixer,
            RemixKind = remix.Kind,
            DurLocal = localDur,
            DurMatch = primary != null ? primary.Dur.ToString() : "",
        };
    }

    private static string Nk2(string a, string b) => TextUtils.Nk(a + b);

    private static string Pick(params string?[] vals)
    {
        foreach (var v in vals) if (!string.IsNullOrEmpty(v)) return v!;
        return "";
    }
}
