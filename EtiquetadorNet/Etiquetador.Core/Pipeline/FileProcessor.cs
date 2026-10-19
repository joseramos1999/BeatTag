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
        $"{Credenciales()}{AiModel}|" +
        $"{CleanOnly}";

    /// <summary>
    /// Resumen de las credenciales en uso. Antes solo se anotaba SI había clave, no CUÁL, así que
    /// cambiar un token de Discogs o unas credenciales de Spotify -normalmente porque las anteriores
    /// no funcionaban- dejaba intacta la caché: se seguían viendo los resultados obtenidos con las
    /// viejas hasta reanalizar a la fuerza.
    ///
    /// Se guarda un hash recortado, nunca la clave: este texto acaba escrito en la caché de análisis.
    /// </summary>
    private string Credenciales()
    {
        var material = $"{SpotifyId}|{SpotifySecret}|{DiscogsToken}|{AcoustIdKey}";
        if (material.Length == 3) return "sin-claves";   // solo los separadores: no hay ninguna
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(bytes, 0, 6);
    }

    /// <summary>
    /// La firma tal y como se escribia antes de distinguir QUE credenciales se usan. Solo sirve
    /// para reconocer lo que ya hay guardado y ponerle la firma nueva: sin esto, el cambio de
    /// formato invalidaria la cache entera de golpe y habria que reanalizar toda la biblioteca
    /// (en la del autor, 12.428 canciones y horas de consultas) para acabar en el mismo sitio.
    /// </summary>
    public string SignatureLegacy() =>
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

    /// <summary>
    /// Analiza un archivo. El resultado dice además cuántas peticiones a los catálogos se quedaron
    /// sin respuesta mientras tanto (<see cref="ProcessResult.FallosDeRed"/>): un «no encontrada» con
    /// fallos de red no es un dato, y AnalysisCache no lo guarda (contrato C8a de ApiClient). Se
    /// mide por flujo asíncrono, así que vale también con varias canciones a la vez.
    /// </summary>
    public async Task<ProcessResult> ProcessAsync(string filePath, bool isAcapella, ProcessOptions o, CancellationToken ct = default)
    {
        using var medicion = new MedicionFallos();
        var r = await ProcesarAsync(filePath, isAcapella, o, ct).ConfigureAwait(false);
        r.FallosDeRed = medicion.Fallos;
        if (r.IncompletoPorLaRed)
            _log?.Detail($"    -> {medicion.Fallos} petición(es) sin respuesta: «no encontrada» no se guarda en la caché y se volverá a preguntar");
        return r;
    }

    private async Task<ProcessResult> ProcesarAsync(string filePath, bool isAcapella, ProcessOptions o, CancellationToken ct)
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

        // Las palabras del nombre del artista no cuentan como descriptor: «Born Dirty», «James Hype» o
        // «Clean Bandit» no hacen del archivo una edición, y con isEdit activo Deezer deja de castigar
        // la diferencia de duración, así que entraban coincidencias de otra versión sin penalización.
        // Solo las palabras que suelen ser parte de un nombre: en los nombres invertidos del pool esta
        // mitad es el título, y su «Remix» sí cuenta.
        var isEdit = TextUtils.Rx(Descriptors.DescRe, IC).IsMatch(Descriptors.SinArtistas(rawForOtros, new[] { fnArtist }, soloDebiles: true));
        var kwUsed = Descriptors.CleanKeywords($"{fnArtist} {qTitle}");

        // Lo que dice el nombre de la letra («Clean», «Dirty»…). Deezer lo usa para elegir entre la
        // versión explícita y la editada de un mismo tema, que los pools publican las dos (C14).
        var letraArchivo = Analysis.ExplicitDetector.DelNombre(new Track { FilePath = filePath, Title = tagTitle });

        // Traza por canción: es la que permite entender después POR QUÉ salió lo que salió.
        _log?.Detail($"· {fileName}");
        _log?.Detail($"    nombre -> artista='{fnArtist}' titulo='{qTitle}'" + (manual ? $"  [MANUAL fuente={(o.SearchSource.Length > 0 ? o.SearchSource : "cualquiera")}]" : ""));
        if (tagArtist.Length > 0 || tagTitle.Length > 0)
            _log?.Detail($"    tags   -> artista='{tagArtist}' titulo='{tagTitle}'");
        _log?.Detail($"    audio  -> {localDur}s · edit={isEdit}");

        // Carpeta de mashups: se salta TODO lo que hay dentro, se llame como se llame el archivo.
        // Va antes que la comprobación por nombre porque no depende de cómo esté escrito el archivo.
        // Igual que abajo, una búsqueda manual manda: si el usuario pide identificar ESTE tema, se
        // busca aunque esté ahí guardado.
        if (!manual && Matching.IsMashupFolder(Path.GetDirectoryName(filePath)))
        {
            _log?.Detail("    -> SALTADA (está en una carpeta de mashups)");
            return new ProcessResult { FilePath = filePath, Old = fileName, New = fileName, Source = "Mezcla", Skip = true, Kw = kwUsed, DurLocal = localDur };
        }

        // En búsqueda manual no se descarta como mezcla: el usuario pide expresamente identificar ESTE tema.
        if (!manual && Matching.IsSkipMix(@base, fnTitle, fnArtist))
        {
            _log?.Detail("    -> SALTADA (se considera mezcla/mashup)");
            return new ProcessResult { FilePath = filePath, Old = fileName, New = fileName, Source = "Mezcla", Skip = true, Kw = kwUsed, DurLocal = localDur };
        }

        // Nombre ambiguo: la misma "x" une colaboradores en "Nicky Jam x J. Balvin - X (EQUIS)" y
        // canciones distintas en "Ella Me Levanto x Gulevando". Ninguna expresión regular resuelve
        // eso, pero la IA local sí juzga si son dos temas o uno con varios artistas.
        //
        // Se pregunta ANTES de buscar en el catálogo: si es un mashup no existe como lanzamiento, y
        // seguir adelante solo puede acabar en una identificación equivocada. Son pocos archivos
        // (41 en una tirada de 12.428), así que el coste es despreciable.
        if (!manual && o.Ai && Matching.LooksAmbiguousMix(@base))
        {
            Step("IA local: ¿es una mezcla?", 0.1);
            var juicio = await _ai.ParseAsync(@base, tagArtist, tagTitle, o.AiModel, ct).ConfigureAwait(false);
            if (juicio is { IsMashup: true })
            {
                _log?.Detail("    -> SALTADA (la IA local la considera una mezcla de varios temas)");
                return new ProcessResult { FilePath = filePath, Old = fileName, New = fileName, Source = "Mezcla", Skip = true, Kw = kwUsed, DurLocal = localDur };
            }
            if (juicio != null) _log?.Detail("    IA local: no es una mezcla, se sigue buscando");
        }

        var cleanOnly = o.CleanOnly;
        ProviderResult? sp = null, it = null, dz = null, mb = null, dc = null, primary = null, sec = null;
        ProviderResult? acHit = null;
        string variant = "", secSrc = "";
        // Propuesta de la IA local que ningun catalogo confirmo: no se escribe, se sugiere.
        string sugA = "", sugT = "", sugV = "";

        // Los descriptores también se leen de lo tecleado: buscar "Tema (Live)" pide la versión en directo.
        var descSrc = manual ? $"{rawForOtros} {o.SearchTitle}" : rawForOtros;
        var wantRemix = Regex.IsMatch(descSrc, @"(?i)\b(remix|rmx|bootleg|flip|mashup|vip)\b");
        var wantLive = Regex.IsMatch(descSrc, @"(?i)\b(live|en\s+vivo|en\s+directo|unplugged|ac[uú]stic[oa])\b");
        // ¿El archivo ES una instrumental o una acapella? Hay que decírselo a los catálogos: el
        // título con el que se busca (qTitle) ya llega sin «(Instrumental)» ni «(Acapella)», y sin
        // esto el catálogo castiga esas versiones como «no pedidas». Visto en data.txt: «Enur -
        // Calabria 2007 (Acapella)», «George Michael - Faith (Instrumental)», «The Weeknd - Popular
        // (A Cappella)»… cogían la vocal (y el nombre conservaba el descriptor de rebote), y si el
        // catálogo solo tenía la acapella salía con confianza negativa. Las ediciones «Acapella
        // In/Out/Intro» llevan la canción entera y no cuentan (ver ReglasCatalogo.EsHerramienta).
        var wantTool = isAcapella || DescriptorDeHerramienta(Descriptors.SinArtistas(descSrc, new[] { fnArtist }, soloDebiles: true), filePath);

        // Quién firma la versión. Se calcula ANTES de buscar para poder puntuar mejor: si el catálogo
        // devuelve justo esa versión ("... (Yaniss Remix)"), es la pista correcta y no una coincidencia
        // a medias. Para un DJ, además, el remixer no puede perderse al limpiar el nombre.
        // Se mira el nombre ORIGINAL (FileNameParser ya le quita el editor), pero SIN el record pool:
        // "DJTOOLSVIP", "Latin Box"… distribuyen, no firman la versión.
        var nameForRemix = TextUtils.Rx(Descriptors.PoolRe, IC).Replace(Path.GetFileNameWithoutExtension(fileName), " ");
        var remix = RemixParser.Parse(nameForRemix);
        if (!remix.HasRemixer) remix = RemixParser.Parse(rawForOtros);
        // Para BUSCAR, en cambio, la primera firma del nombre: es la versión publicada, la que puede
        // tener el catálogo; la última suele ser el editor del pool, que no la tiene. Con la última,
        // Deezer dejaba de reconocer «Hello (Sidney Samson Remix) (Cesar Vilo Remix)» como la versión
        // de Sidney Samson y la empataba con cualquier otro remix. Ver RemixParser.PrimeraFirma.
        var remixBusqueda = RemixParser.PrimeraFirma(nameForRemix);
        if (!remixBusqueda.HasRemixer) remixBusqueda = RemixParser.PrimeraFirma(rawForOtros);
        if (manual && o.SearchTitle.Length > 0)
        {
            var manualRemix = RemixParser.Parse(o.SearchTitle);
            if (manualRemix.HasRemixer) remix = manualRemix;
            var manualBusqueda = RemixParser.PrimeraFirma(o.SearchTitle);
            if (manualBusqueda.HasRemixer) remixBusqueda = manualBusqueda;
        }
        if (remix.HasRemixer) _log?.Detail($"    remix  -> '{remix.Remixer}' ({remix.Kind})"
            + (remixBusqueda.HasRemixer && remixBusqueda.Remixer != remix.Remixer ? $" · se busca la versión de '{remixBusqueda.Remixer}'" : ""));
        var remixerBusqueda = remixBusqueda.Remixer ?? "";

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
                dz = await _dz.SearchAsync(fnArtist, qTitle, wantRemix, wantLive, localDur, isEdit, ct, remixerBusqueda, verbatim: manual, wantTool: wantTool, letra: letraArchivo).ConfigureAwait(false); if (dz != null) variant = "nombre";
                if (dz == null && fnArtist.Length > 0) { dz = await _dz.SearchAsync(fnTitle, fnArtist, wantRemix, wantLive, localDur, isEdit, ct, remixerBusqueda, verbatim: manual, wantTool: wantTool, letra: letraArchivo).ConfigureAwait(false); if (dz != null) variant = "invertido"; }
                if (dz == null && tagTitle.Length > 0 && Nk2(tagArtist, tagTitle) != Nk2(fnArtist, qTitle)) { dz = await _dz.SearchAsync(tagArtist, tagTitle, wantRemix, wantLive, localDur, isEdit, ct, remixerBusqueda, verbatim: manual, wantTool: wantTool, letra: letraArchivo).ConfigureAwait(false); if (dz != null) variant = "tag"; }
                if (dz == null && firstArtist.Length > 0 && firstTitle.Length > 0 && Nk2(firstArtist, firstTitle) != Nk2(fnArtist, qTitle)) { dz = await _dz.SearchAsync(firstArtist, firstTitle, wantRemix, wantLive, localDur, isEdit, ct, remixerBusqueda, verbatim: manual, wantTool: wantTool, letra: letraArchivo).ConfigureAwait(false); if (dz != null) variant = "principal"; }
                if (dz == null && firstTitleArtist.Length > 0 && firstArtist.Length > 0 && Nk2(firstTitleArtist, firstArtist) != Nk2(fnTitle, fnArtist)) { dz = await _dz.SearchAsync(firstTitleArtist, firstArtist, wantRemix, wantLive, localDur, isEdit, ct, remixerBusqueda, verbatim: manual, wantTool: wantTool, letra: letraArchivo).ConfigureAwait(false); if (dz != null) variant = "principal"; }
                if (dz == null && firstTitle.Length > 0) { dz = await _dz.SearchAsync("", firstTitle, wantRemix, wantLive, localDur, isEdit, ct, remixerBusqueda, verbatim: manual, wantTool: wantTool, letra: letraArchivo).ConfigureAwait(false); if (dz != null) variant = "titulo"; }
            }
            if (o.Itunes && dz == null && !onlyDz && !onlySp)
            {
                Step("iTunes", 0.3);
                it = await _it.SearchAsync(fnArtist, qTitle, localDur, isEdit, ct, verbatim: manual, wantRemix: wantRemix, wantTool: wantTool).ConfigureAwait(false); if (it != null) variant = "nombre";
                if (it == null && fnArtist.Length > 0) { it = await _it.SearchAsync(fnTitle, fnArtist, localDur, isEdit, ct, verbatim: manual, wantRemix: wantRemix, wantTool: wantTool).ConfigureAwait(false); if (it != null) variant = "invertido"; }
                if (it == null && tagTitle.Length > 0 && Nk2(tagArtist, tagTitle) != Nk2(fnArtist, qTitle)) { it = await _it.SearchAsync(tagArtist, tagTitle, localDur, isEdit, ct, verbatim: manual, wantRemix: wantRemix, wantTool: wantTool).ConfigureAwait(false); if (it != null) variant = "tag"; }
                if (it == null && firstArtist.Length > 0 && firstTitle.Length > 0 && Nk2(firstArtist, firstTitle) != Nk2(fnArtist, qTitle)) { it = await _it.SearchAsync(firstArtist, firstTitle, localDur, isEdit, ct, verbatim: manual, wantRemix: wantRemix, wantTool: wantTool).ConfigureAwait(false); if (it != null) variant = "principal"; }
                if (it == null && firstTitleArtist.Length > 0 && firstArtist.Length > 0 && Nk2(firstTitleArtist, firstArtist) != Nk2(fnTitle, fnArtist)) { it = await _it.SearchAsync(firstTitleArtist, firstArtist, localDur, isEdit, ct, verbatim: manual, wantRemix: wantRemix, wantTool: wantTool).ConfigureAwait(false); if (it != null) variant = "principal"; }
                if (it == null && firstTitle.Length > 0) { it = await _it.SearchAsync("", firstTitle, localDur, isEdit, ct, verbatim: manual, wantRemix: wantRemix, wantTool: wantTool).ConfigureAwait(false); if (it != null) variant = "titulo"; }
            }
            if (o.Spotify && o.SpotifyId.Length > 0 && o.SpotifySecret.Length > 0
                && ((dz == null && it == null && !forced) || onlySp))
            {
                Step("Spotify", 0.42);
                sp = await _sp.SearchAsync(fnArtist, qTitle, o.SpotifyId, o.SpotifySecret, wantRemix, wantLive, localDur, isEdit, ct, wantTool).ConfigureAwait(false); if (sp != null) variant = "nombre";
                if (sp == null && fnArtist.Length > 0) { sp = await _sp.SearchAsync(fnTitle, fnArtist, o.SpotifyId, o.SpotifySecret, wantRemix, wantLive, localDur, isEdit, ct, wantTool).ConfigureAwait(false); if (sp != null) variant = "invertido"; }
                if (sp == null && tagTitle.Length > 0) { sp = await _sp.SearchAsync(tagArtist, tagTitle, o.SpotifyId, o.SpotifySecret, wantRemix, wantLive, localDur, isEdit, ct, wantTool).ConfigureAwait(false); if (sp != null) variant = "tag"; }
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
            // Con coincidencia en el catálogo principal, Discogs ya no se consulta aquí: solo rellena
            // los huecos que dejen las demás fuentes (ver más abajo, junto al género).

            // IA local de rescate: la propuesta se RE-VERIFICA contra Deezer/iTunes; nunca se escribe sin confirmar.
            if (primary == null && o.Ai && !forced)
            {
                Step("IA local", 0.7);
                var ai = await _ai.ParseAsync(@base, tagArtist, tagTitle, o.AiModel, ct).ConfigureAwait(false);
                if (ai != null && ai.Title.Length > 0 && ai.Confidence >= 0.5 && !ai.IsMashup)
                {
                    var aiA = ai.Artist; var aiT = ai.Title;
                    if (o.Deezer) { dz = await _dz.SearchAsync(aiA, aiT, wantRemix, wantLive, localDur, isEdit, ct, remixerBusqueda, verbatim: manual, wantTool: wantTool, letra: letraArchivo).ConfigureAwait(false); if (dz == null && aiA.Length > 0) dz = await _dz.SearchAsync(aiT, aiA, wantRemix, wantLive, localDur, isEdit, ct, remixerBusqueda, verbatim: manual, wantTool: wantTool, letra: letraArchivo).ConfigureAwait(false); }
                    if (dz == null && o.Itunes) { it = await _it.SearchAsync(aiA, aiT, localDur, isEdit, ct, verbatim: manual, wantRemix: wantRemix, wantTool: wantTool).ConfigureAwait(false); if (it == null && aiA.Length > 0) it = await _it.SearchAsync(aiT, aiA, localDur, isEdit, ct, verbatim: manual, wantRemix: wantRemix, wantTool: wantTool).ConfigureAwait(false); }
                    primary = dz ?? it;
                    if (primary != null) variant = "ia";
                    _log?.Log($"        · IA local propuso: {aiA} - {aiT} (conf {ai.Confidence}) -> {(primary != null ? "VERIFICADO en " + (dz != null ? "Deezer" : "iTunes") : "no verificado en catalogo")}", LogKind.Dim, true);

                    // Sin confirmar no se escribe nada, pero la propuesta tampoco se tira: si se
                    // limita a reordenar lo que ya había en el nombre, viaja a "No encontradas"
                    // como sugerencia para que decida el usuario. Lo que la IA se inventa se
                    // descarta aquí, porque eso es lo que una persona no puede cazar de un vistazo.
                    // Los tags cuentan como material de origen igual que el nombre: muchos archivos
                    // llegan con el nombre cortado a 43 caracteres y el título entero dentro del
                    // tag. Completar eso NO es inventar, y mirando solo el nombre se perdían 174
                    // sugerencias buenas de una tirada real (de 56 invenciones a 228 descartes).
                    if (primary == null && Matching.SoloReordena($"{fileName} {tagArtist} {tagTitle}", aiA, aiT))
                    {
                        sugA = aiA; sugT = aiT; sugV = ai.Version;
                        _log?.Log("        · guardada como sugerencia para 'No encontradas'", LogKind.Dim, true);
                    }
                }
                else if (ai is { IsMashup: true })
                {
                    // Antes esto solo se anotaba en el registro y el archivo seguía adelante hasta
                    // acabar en "No encontradas". Si la IA dice que son varios temas, lo que
                    // corresponde es apartarlo como mezcla, igual que hacen los demás detectores.
                    _log?.Detail("    -> SALTADA (la IA local la considera una mezcla de varios temas)");
                    return new ProcessResult { FilePath = filePath, Old = fileName, New = fileName, Source = "Mezcla", Skip = true, Kw = kwUsed, DurLocal = localDur };
                }
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
                        if (o.Deezer) dz = await _dz.SearchAsync(ac.Artist, ac.Title, false, false, localDur, isEdit, ct, wantTool: wantTool, letra: letraArchivo).ConfigureAwait(false);
                        if (dz == null && o.Itunes) it = await _it.SearchAsync(ac.Artist, ac.Title, localDur, isEdit, ct, verbatim: manual, wantRemix: wantRemix, wantTool: wantTool).ConfigureAwait(false);
                        primary = dz ?? it;
                        if (primary == null) acHit = ac;
                    }
                }
            }
        }

        if (variant.Length == 0) { if (mb != null) variant = "mb"; else if (dc != null) variant = "discogs"; }

        // Lo que de verdad gana: el catálogo principal (Deezer, iTunes o Spotify, también cuando lo
        // propuso la IA o la huella y ahí se verificó) y, si no hubo, una fuente de rescate. La
        // confianza y el «por qué» se calculan sobre ESTE resultado. Antes solo se calculaban para el
        // principal, y lo que sacaba MusicBrainz salía sin confianza, sin motivo y marcado para
        // aplicar. Visto en una biblioteca real: «Gayle x Eric Prydz - abcdepjanoo (DJs from M» →
        // «Eric Prydz - Eric Prydz», «Bellaqueo 4 Life - Ñengo Flow» → otra canción, «Kartashow -
        // Золото (…)» → «KARTASHOW, - (…)». 46 líneas de los registros, todas con la confianza vacía.
        var winner = primary ?? mb ?? acHit;
        var esAcoustId = primary == null && mb == null && acHit != null;
        var soloRescate = primary == null && winner != null;

        // Estrategia POR CAMPO: rellena huecos (album/año/carátula) con una fuente secundaria.
        if (primary != null && !cleanOnly && (primary.Album.Length == 0 || primary.Year.Length == 0 || primary.CoverUrl.Length == 0))
        {
            if (o.Itunes && it == null) { sec = await _it.SearchAsync(primary.Artist, primary.Title, localDur, isEdit, ct, verbatim: manual, wantRemix: wantRemix, wantTool: wantTool).ConfigureAwait(false); if (sec != null) secSrc = "AppleMusic"; }
            if (sec == null && o.Deezer && dz == null) { sec = await _dz.SearchAsync(primary.Artist, primary.Title, false, false, localDur, isEdit, ct).ConfigureAwait(false); if (sec != null) secSrc = "Deezer"; }
        }

        var genreOnly = dc != null && !(sp != null || it != null || dz != null || mb != null || acHit != null) && !cleanOnly;
        var srcLabel = sp != null ? "Spotify" : it != null ? "AppleMusic" : dz != null ? "Deezer" : mb != null ? "MusicBrainz" : acHit != null ? "AcoustID" : genreOnly ? "Discogs(gén)" : cleanOnly ? "Limpieza" : "-";

        var title = sp != null ? sp.Title : it != null ? it.Title : dz != null ? dz.Title : mb != null ? mb.Title : acHit != null ? acHit.Title : (qTitle.Length > 0 ? qTitle : fnTitle);
        var artist = sp != null ? sp.Artist : it != null ? it.Artist : dz != null ? dz.Artist : mb != null ? mb.Artist : acHit != null ? acHit.Artist : fnArtist;

        string album = Pick(sp?.Album, it?.Album, dz?.Album, mb?.Album, sec?.Album);
        string year = Pick(sp?.Year, it?.Year, dz?.Year, mb?.Year, sec?.Year);

        // Género: mandan las fuentes que identificaron la canción (Spotify, Deezer, iTunes) y Discogs
        // solo rellena si ninguna lo da. Antes Discogs iba el primero y mandaba sobre todas, aunque
        // el disco que devolvía fuera de otro artista. Visto en una biblioteca real, con la canción
        // bien identificada por Deezer (confianza 15-20): «Arcangel - Bali» → Gamelan, «Mad Mind -
        // Bien Duro» → Medieval, «COQE, JC Reyes - Multiplicando $$$» → Educational.
        string genre = "";
        if (sp != null && sp.ArtistId.Length > 0) { var g = await _sp.ArtistGenreAsync(sp.ArtistId, o.SpotifyId, o.SpotifySecret, ct).ConfigureAwait(false); if (g.Length > 0) genre = g; }
        if (genre.Length == 0 && dz != null && dz.AlbumId.Length > 0) { var g = await _dz.AlbumGenreAsync(dz.AlbumId, ct).ConfigureAwait(false); if (g.Length > 0) genre = g; }
        if (genre.Length == 0 && it != null && it.Genre.Length > 0) genre = it.Genre;
        if (genre.Length == 0 && sec != null && sec.Genre.Length > 0) genre = sec.Genre;
        if (genre.Length == 0 && sec != null && sec.AlbumId.Length > 0) { var g = await _dz.AlbumGenreAsync(sec.AlbumId, ct).ConfigureAwait(false); if (g.Length > 0) genre = g; }

        // Discogs, por la misma razón, solo se consulta si queda algún hueco. MEDIDO en una tirada:
        // 207 de las 578 peticiones fueron a Discogs, a unos 0,9 s cada una, solo para el género.
        if (dc == null && o.Discogs && !cleanOnly && winner != null && (genre.Length == 0 || album.Length == 0 || year.Length == 0))
        {
            Step("Discogs", 0.9);
            dc = await _dc.SearchAsync(winner.Artist, winner.Title, AppInfo.UserAgent, o.DiscogsToken, ct).ConfigureAwait(false);
        }
        // Un resultado SoloArtista (C4a) sale de OTRO disco del artista: su género vale para rellenar
        // el hueco si nadie más lo da, pero su álbum y su año no son los de esta canción.
        if (genre.Length == 0 && dc != null && dc.Genre.Length > 0) genre = dc.Genre;
        if (album.Length == 0 && dc is { SoloArtista: false }) album = dc.Album;
        if (year.Length == 0 && dc is { SoloArtista: false }) year = dc.Year;
        genre = GenreNormalizer.Canonical(genre);   // unifica sinónimos/grafías y da formato

        // El BPM del catálogo se decide más abajo, cuando ya se sabe qué versión es el archivo.
        var bpmCatalogo = dz is { Bpm: > 0 } ? dz.Bpm
                        : (secSrc == "Deezer" && sec is { Bpm: > 0 }) ? sec!.Bpm : 0;
        // Título de la pista que da ese BPM: las versiones que ya nombra son las de esa pista.
        var tituloDelBpm = dz is { Bpm: > 0 } ? dz.Title : bpmCatalogo > 0 ? sec!.Title : "";
        var cover = Pick(sp?.CoverUrl, it?.CoverUrl, dz?.CoverUrl, sec?.CoverUrl);

        title = Descriptors.CleanDbTitle(title, @base);
        title = TextUtils.UnScream(title);

        // feat/ft del nombre original (no descriptor DJ): se conserva en el título si no está ya
        var ft = FeatDelNombre(@base);
        if (ft.Length > 0 && !TextUtils.Rx(@"(?i)\bfeat|\bft\b").IsMatch(title) && !TextUtils.Nk(title).Contains(TextUtils.Nk(ft)) && !TextUtils.Nk(artist).Contains(TextUtils.Nk(ft)))
            title = $"{title} feat. {ft}";

        var titleTrail = new List<string>();
        var mtr = TextUtils.Rx(@"(?i)\s+(remix|extended(?:\s+mix)?|club\s+mix|radio\s+edit|version)\s*$").Match(title);
        // También el guion que quedara delante: «El Motorcito - Remix» no puede acabar en «El Motorcito -».
        if (mtr.Success) { titleTrail.Add(TextUtils.TitleCase(mtr.Groups[1].Value.Trim())); title = title.Substring(0, mtr.Index).Trim().TrimEnd('-', '–', '—').Trim(); }

        // Las palabras del artista no son descriptores: las del catálogo se descartan enteras; las del
        // nombre de archivo, solo las que suelen ser parte de un nombre, porque en los nombres
        // invertidos del pool esa mitad es el título con su versión («Si Tu Te Vas Remix - Mora»).
        var otros = Descriptors.ExtractOtros(Descriptors.CompleteTruncated(rawForOtros), title,
            winner != null ? new[] { artist } : null, fnArtist).ToList();
        foreach (var tr in titleTrail)
            if (!otros.Any(x => TextUtils.Nk(x) == TextUtils.Nk(tr))) otros.Add(tr);

        // Si el nombre del archivo no daba autor, se intenta con el título del catálogo.
        if (!remix.HasRemixer && winner != null)
        {
            var fromDb = RemixParser.Parse(winner.Title);
            if (fromDb.HasRemixer) remix = fromDb;
        }

        // Si el autor elegido ya lo trae el catálogo en su título, el que firma ESTE archivo es otro:
        // el siguiente del nombre. «RITMO (Bad Boys For Life) (Steve Aoki Remix) (Yaniss X Greg
        // Lassierra Remix)» es la versión de Yaniss X Greg Lassierra sobre el remix de Steve Aoki.
        var tituloCat = winner?.Title ?? "";
        if (remix.HasRemixer && ContieneAlAutor(tituloCat, remix.Remixer))
        {
            var siguiente = RemixParser.Candidates(nameForRemix).Concat(RemixParser.Candidates(rawForOtros))
                .FirstOrDefault(r => r.HasRemixer && !ContieneAlAutor(tituloCat, r.Remixer) && !EsElPropioTitulo(r.Remixer, title, qTitle));
            if (siguiente.HasRemixer)
            {
                _log?.Detail($"    remix  -> '{remix.Remixer}' ya viene en el catálogo; firma el archivo '{siguiente.Remixer}'");
                remix = siguiente;
            }
        }

        // El "autor" no puede ser el propio título de la canción: en nombres como
        // "Stereo Love (Stereo Love Extended)" lo que precede al descriptor es el título repetido,
        // y colarlo daba cosas como "Stereo Love (Stereo Love Extended Extended)".
        var esElTitulo = remix.HasRemixer && EsElPropioTitulo(remix.Remixer, title, qTitle);
        if (esElTitulo) _log?.Detail($"    remix  -> '{remix.Remixer}' descartado (es el propio título)");

        // Cada firma del nombre sustituye a su descriptor pelado («Remix» → «Tiesto Remix»), en el
        // orden del nombre y sin perder ninguna: «CHEVERE (premium remix) (Davidmusic Slam Acapella)»
        // lleva dos. Las que el catálogo ya nombra en su título («Get Ready For This (Rio & Le Jean
        // '92 Remix)») no se repiten, y las que son el propio título se descartan.
        var firmas = RemixParser.ConAutor(nameForRemix).ToList();
        if (remix.HasRemixer && !firmas.Contains(remix)) firmas.Add(remix);
        foreach (var f in firmas)
        {
            if (EsElPropioTitulo(f.Remixer, title, qTitle) || ContieneAlAutor(title, f.Remixer)) continue;
            var etiqueta = TextUtils.Nk(f.Label);
            if (otros.Any(x => TextUtils.Nk(x) == etiqueta)) continue;
            var kindKey = TextUtils.Nk(f.Kind);
            var idx = otros.FindIndex(x => TextUtils.Nk(x) == kindKey);
            if (idx >= 0) otros[idx] = f.Label;
            else otros.Add(f.Label);
        }
        // Para el tag y la columna Remixer, solo la persona: «Alex Torres», no «Alex Torres Intro».
        var remixerFinal = remix.HasRemixer && !esElTitulo ? RemixParser.Autor(remix.Remixer) : "";
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

        // BPM: ya se sabe qué versiones lleva el archivo. Solo cuentan las que no nombra el título de
        // la pista del catálogo: en otros también está lo que se le quitó a ese título («Macarena
        // Bayside Boys Remix» → «(Remix)») y lo que el archivo repite («Scooteroni (RMX)» con el
        // catálogo «Scooteroni (RMX)»), y eso es la misma versión, no otra a otro tempo.
        var versionesNoDelCatalogo = otros.Where(v => !NombraLaVersion(tituloDelBpm, v)).ToList();
        var bpm = ElegirBpm(fileName, versionesNoDelCatalogo, bpmCatalogo, out var porQueBpm);
        if (porQueBpm.Length > 0) _log?.Detail($"    bpm    -> {porQueBpm}");
        var bpmEsDelCatalogo = bpm.Length > 0 && BpmDelNombre(Path.GetFileNameWithoutExtension(fileName), out _) == 0;

        // ISRC y letra del catálogo (C14): solo de Deezer, y solo si es Deezer quien identificó la
        // canción (no si solo rellenó el álbum como fuente secundaria).
        var dzGana = dz != null && ReferenceEquals(primary, dz);
        var isrc = "";
        if (dzGana)
        {
            isrc = IsrcDelArchivo(dz!.Isrc, dz.Title, dz.Explicit, otros, letraArchivo, out var porQueIsrc);
            if (porQueIsrc.Length > 0) _log?.Detail($"    isrc   -> {porQueIsrc}");
        }

        var mainArtist = artist.Trim(); if (mainArtist.Length == 0) mainArtist = fnArtist;
        // «ft» detrás de una coma empieza el nombre del siguiente intérprete, no es un invitado: Deezer
        // da «Kid Yugi, Ill Santo, FT Kings» (FT Kings es un grupo) y salía «…, feat. Kings».
        mainArtist = TextUtils.Rx(@"(?i)(?<![,\s])\s+ft\.?\s+").Replace(mainArtist, " feat. ");
        mainArtist = TextUtils.Rx(@"(?i)\s+featuring\s+").Replace(mainArtist, " feat. ");
        mainArtist = TextUtils.Rx(@"\s{2,}").Replace(mainArtist.Replace('_', ' '), " ").Trim();
        // El tag lleva las grafías y los alias del nombre (Cruzzi → Cruz Cafuné), pero no el paso de
        // MAYÚSCULAS a tipo título: en el catálogo suele ser la grafía oficial («MC YOSHI», «SOLTO
        // (FR)»), y el tag Artista la conservaba antes de llevar la del nombre.
        var artistaTag = _artistExc.NormalizeArtists(mainArtist, desgritar: false);
        mainArtist = _artistExc.NormalizeArtists(mainArtist);
        title = Feat.RemoveRedundantFeat(title, mainArtist).Trim();

        var newBase = ComponerNombre(mainArtist, title, otros);
        if (newBase.Length == 0) newBase = TextUtils.Sanitize(TextUtils.AsciiSinPerderLetras(@base));

        // El título era «A x B» y el catálogo solo devuelve A: es un mashup que no se reconoció de
        // antemano, y renombrarlo lo haría pasar por el original. En búsqueda manual manda el usuario.
        // Se mira la mitad del nombre que lleva el título, que en el formato invertido de los pools
        // es la primera.
        var mitadTitulo = Matching.MitadConElTitulo(fnArtist, fnTitle, winner?.Title ?? title);
        if (!manual && Matching.PierdeUnaCancion(mitadTitulo, newBase))
        {
            _log?.Detail($"    -> SALTADA (parece un mashup: '{newBase}' pierde una de las canciones del nombre)");
            return new ProcessResult { FilePath = filePath, Old = fileName, New = fileName, Source = "Mezcla", Skip = true, Kw = kwUsed, DurLocal = localDur };
        }

        var titleTag = title;
        if (otros.Count > 0) titleTag = $"{title} (" + string.Join(", ", otros) + ")";

        Step("componiendo", 0.95);

        // Confianza mostrada = puntuación de la fuente que gana + coherencia con los tags ya embebidos.
        var scoreStr = "";
        var why = "";
        var revisar = new List<string>();
        if (winner != null)
        {
            // AcoustID no puntúa el título ni el artista, solo el parecido del audio: se le da un valor
            // fijo, en la franja de las coincidencias aproximadas. MusicBrainz trae la suya.
            var puntuacionFuente = esAcoustId ? PuntuacionAcoustId : winner.Score;
            var tagAdj = Matching.TagCoherence(tagArtist, tagTitle, winner.Artist, winner.Title);
            var finalScore = Math.Round(puntuacionFuente + tagAdj, 1);

            // Si la coincidencia la elegiste TÚ (de la lista, por enlace o por huella), no tiene
            // sentido marcarla como dudosa: la validaste viéndola. Antes salía con confianza 0 y
            // se auto-desmarcaba, que es justo lo contrario de lo que quieres. No aplica a lo que
            // sale de una fuente de rescate: eso no es lo que eligió el usuario.
            if (!soloRescate && manual && o.SearchSource.Length > 0 && finalScore < 10)
            {
                _log?.Detail($"    confianza-> {finalScore} -> 10 (coincidencia elegida por el usuario en {o.SearchSource})");
                finalScore = 10;
            }
            else if (!soloRescate && manual && finalScore < 4)
            {
                // Términos dictados a mano: tampoco debería quedar por debajo del umbral de revisión.
                _log?.Detail($"    confianza-> {finalScore} -> 4 (búsqueda dictada por el usuario)");
                finalScore = 4;
            }
            // Todo el renglón en cultura invariante: mezclar coma y punto decimal en la misma línea
            // (la fuente con la del sistema, el total invariante) hace que al analizar los registros
            // se lean mal los números.
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            scoreStr = finalScore.ToString(inv);
            _log?.Detail($"    confianza-> {puntuacionFuente.ToString(inv)} (fuente) "
                       + $"{(tagAdj >= 0 ? "+" : "")}{tagAdj.ToString(inv)} (tags) = {scoreStr}"
                       + (finalScore < 2.0 ? "  [BAJA]" : ""));

            // El mismo razonamiento que va al registro, guardado para enseñarlo en la tabla: por qué
            // ganó esta coincidencia y cómo la corrigen los tags que el archivo ya traía. Es lo que
            // convierte una propuesta dudosa en una decisión de un segundo.
            var partes = new List<string>();
            if (winner.Why.Length > 0) partes.Add(winner.Why);
            else if (winner.Dur > 0 && localDur > 0)
            {
                // Fuentes que no detallan su puntuación (iTunes, Spotify). Al menos se dice lo que
                // sí se puede afirmar de este archivo: si la duración cuadra con la del catálogo,
                // que es la comprobación que más veces cambia una decisión.
                var dd = Math.Abs(winner.Dur - localDur);
                partes.Add(dd <= 2 ? $"la duración cuadra (Δ{dd}s)" : $"la duración difiere en {dd}s");
            }
            else if (esAcoustId) partes.Add("la huella acústica coincide");
            if (tagAdj != 0)
                partes.Add(tagAdj > 0
                    ? $"los tags del archivo concuerdan +{tagAdj.ToString(inv)}"
                    : $"los tags del archivo NO concuerdan {tagAdj.ToString(inv)}");
            if (manual) partes.Add(o.SearchSource.Length > 0 ? $"coincidencia elegida por ti en {o.SearchSource}" : "búsqueda dictada por ti");
            why = string.Join(" · ", partes);

            // Lo que solo encontró una fuente de rescate no se aplica de oficio: MusicBrainz y AcoustID
            // se consultan precisamente cuando los catálogos principales no dan con la canción.
            if (soloRescate)
                revisar.Add(MotivoSoloRescate(esAcoustId ? "AcoustID" : "MusicBrainz"));
        }

        // Comprobación final del nombre propuesto: las huellas que deja una regla que ha fallado
        // (guion colgando, título vacío, artista repetido…) se ven mirando solo el resultado.
        revisar.AddRange(NombrePropuesto.Anomalias(newBase, mainArtist, title, fileName, bpm));
        var motivoRevisar = string.Join(" · ", revisar);
        if (motivoRevisar.Length > 0)
        {
            _log?.Detail($"    revisar-> {motivoRevisar}");
            why = why.Length > 0 ? $"revisar: {motivoRevisar} · {why}" : $"revisar: {motivoRevisar}";
        }
        _log?.Detail($"    -> {(winner != null || genreOnly || cleanOnly ? "OK" : "SIN RESULTADO")} · fuente={srcLabel} · nuevo='{newBase + ext}'");

        // Línea estructurada (una por canción) pensada para analizar la tirada entera después:
        // se puede filtrar con  findstr DATA  y abrir como CSV con separador |.
        var durMatch = winner?.Dur ?? 0;
        var durDelta = (localDur > 0 && durMatch > 0) ? Math.Abs(durMatch - localDur) : -1;
        _log?.Detail(string.Join("|", new[]
        {
            "DATA",
            Csv(fileName),
            primary != null ? "OK" : (cleanOnly ? "LIMPIEZA" : mb != null ? "OK-MB" : acHit != null ? "ACOUSTID" : genreOnly ? "SOLO-GENERO" : "SIN-RESULTADO"),
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
            // El tag lleva la misma grafía que el nombre de archivo (excepciones, alias, «feat.»),
            // pero con sus acentos. Visto en una biblioteca real: 8 temas de Cruz Cafuné pasaban a
            // llamarse «Cruz Cafune - …» y su tag Artista se quedaba en «Cruzzi», así que rekordbox,
            // que ordena por el tag, partía al mismo artista en dos. Las mayúsculas del catálogo se
            // respetan (ver artistaTag).
            Artist = artistaTag,
            Album = album,
            Year = year,
            Genre = genre,
            CoverUrl = cover,
            Bpm = bpm,
            BpmCatalogo = bpmEsDelCatalogo ? bpm : "",
            Found = sp != null || it != null || dz != null || mb != null || acHit != null,
            GenreOnly = genreOnly,
            Source = srcLabel,
            SpDiag = o.Spotify ? _sp.SpDiag : "off",
            Kw = kwUsed,
            CleanOnly = cleanOnly,
            Skip = false,
            Variant = variant,
            Score = scoreStr,
            Remixer = remixerFinal,
            // El tag sale de su propio campo: el Remixer que guardó en la caché una versión anterior
            // es texto en bruto y no debe escribirse (ver ProcessResult.RemixerTag).
            RemixerTag = remixerFinal,
            RemixKind = remix.Kind,
            Why = why,
            Revisar = motivoRevisar,
            Reglas = ProcessResult.ReglasActuales,
            AiArtist = sugA,
            AiTitle = sugT,
            AiVersion = sugV,
            DurLocal = localDur,
            DurMatch = winner != null ? winner.Dur.ToString() : "",
            Isrc = isrc,
            IsrcDe = isrc.Length > 0 ? ProcessResult.ClaveIsrc(artistaTag, titleTag) : "",
            Explicit = dzGana ? dz!.Explicit : Analysis.Explicitness.Unknown,
        };
    }

    /// <summary>
    /// El ISRC que se propone para el archivo, o "" (C14). El ISRC identifica UNA grabación, así que
    /// el del catálogo solo vale si el archivo es esa misma:
    ///   · sin versiones que el título del catálogo no nombre: una extended, un remix, una intro de DJ
    ///     o una acapella tienen su propio código, o ninguno (las ediciones de los pools);
    ///   · con la misma letra: si el nombre dice «Clean» o «Dirty», el catálogo tiene que confirmar esa
    ///     versión, porque la editada y la explícita llevan códigos distintos.
    /// Escribir el de la original en una edición haría que cualquier programa que agrupe por ISRC las
    /// diera por la misma grabación.
    /// </summary>
    /// <param name="versiones">Las versiones que lleva el archivo (los paréntesis del nombre propuesto).</param>
    /// <param name="motivo">Por qué no se propone, para el registro ("" si se propone o no había ISRC).</param>
    public static string IsrcDelArchivo(string isrcCatalogo, string tituloCatalogo, Analysis.Explicitness letraCatalogo,
                                        IEnumerable<string> versiones, Analysis.Explicitness letraArchivo, out string motivo)
    {
        motivo = "";
        if (string.IsNullOrEmpty(isrcCatalogo)) return "";

        var otra = versiones.FirstOrDefault(v => !NombraLaVersion(tituloCatalogo, v) && !EsMarcaDeLetra(v));
        if (otra != null)
        {
            motivo = $"no se propone el del catálogo ({isrcCatalogo}): el archivo es una versión «{otra}», que no es esa grabación";
            return "";
        }
        if (letraArchivo != Analysis.Explicitness.Unknown && letraCatalogo != letraArchivo)
        {
            motivo = $"no se propone el del catálogo ({isrcCatalogo}): el archivo es la versión "
                   + $"{(letraArchivo == Analysis.Explicitness.Clean ? "clean" : "explícita")} y el catálogo no lo confirma";
            return "";
        }
        return isrcCatalogo;
    }

    /// <summary>
    /// «Clean», «Dirty», «Explicit», «Clean Version»…: solo dicen cómo es la letra. Esas no cuentan
    /// como otra versión para el ISRC: lo decide la comparación de la letra con la del catálogo.
    /// </summary>
    private static bool EsMarcaDeLetra(string version)
        => TextUtils.Rx(@"(?i)^\s*(clean|dirty|explicit|expl[ií]cita|limpia|sucia|censored|censurada|uncensored)(\s+version|\s+versi[oó]n)?\s*$")
               .IsMatch(version ?? "");

    /// <summary>
    /// Puntuación que se da a lo que solo identificó AcoustID. Su propia nota (0,5-1) mide el parecido
    /// del audio, no si el título y el artista que devuelve son los buenos, así que no es comparable
    /// con la de los catálogos. 5 es la franja de las coincidencias aproximadas de Deezer: por encima
    /// del umbral de revisión solo con la huella, y que los tags la suban o la bajen.
    /// </summary>
    public const double PuntuacionAcoustId = 5.0;

    /// <summary>Motivo para revisar lo que solo identificó una fuente de rescate («MusicBrainz», «AcoustID»).</summary>
    public static string MotivoSoloRescate(string fuente) => $"identificada solo por {fuente}, una fuente de rescate";

    /// <summary>
    /// ¿El archivo es una instrumental o una acapella? Lo dicen los descriptores del nombre
    /// («(Instrumental)», «(A Cappella)», «(Acappella)») o la carpeta («Acapellas», «Instrumentales»),
    /// igual que mira la carpeta <see cref="Analysis.Identificacion.EsAcapella"/>. Las ediciones
    /// «Acapella In/Out/Intro» no cuentan: llevan la canción entera.
    /// </summary>
    public static bool DescriptorDeHerramienta(string? descriptores, string? filePath)
    {
        if (ReglasCatalogo.EsHerramienta(descriptores)) return true;
        var carpeta = string.IsNullOrEmpty(filePath) ? "" : Path.GetDirectoryName(filePath) ?? "";
        return carpeta.Length > 0 && ReglasCatalogo.EsHerramienta(carpeta);
    }

    /// <summary>
    /// El invitado que el nombre de archivo declara con «feat.», «ft.» o «featuring», ya limpio, o ""
    /// si no hay ninguno.
    ///
    /// Con límite de palabra: sin él, el «ft» final de Drift, Swift o Daft se tomaba por un invitado.
    /// Visto en una biblioteca real: «Teriyaki Boyz - Tokyo Drift (Sam Collins Remix)» acababa en
    /// «… feat. Sam Collins (Remix)» (invitado inventado y remixer perdido), «ZAYN, Taylor Swift - I
    /// Dont Wanna Live Forever (Fifty Shades Darker)» en «… feat. I Dont Wanna Forever» y «DJ Allan x
    /// Daft Punk - One More Time» en «… One More Time feat. Punk». Dentro de un paréntesis, tampoco
    /// cuando lo que sigue nombra una versión («(ft. Sam Collins Remix)»): ahí lo que hay es quién la
    /// firma, y eso lo resuelve RemixParser. Un invitado de verdad en el paréntesis sí vale («(Original
    /// 2022 Remaster Ft. Daddy Yankee)»).
    /// </summary>
    public static string FeatDelNombre(string? nombre)
    {
        var s = nombre ?? "";
        foreach (Match m in TextUtils.Rx(@"(?i)\b(?:feat\.?|ft\.?|featuring)\s+(.+?)(?=\s*[-(\[]|$)").Matches(s))
        {
            if (DentroDeParentesis(s, m.Index) && TextUtils.Rx(Descriptors.DescRe, IC).IsMatch(m.Groups[1].Value)) continue;
            var ft = m.Groups[1].Value;
            ft = TextUtils.Rx(Descriptors.DescRe, IC).Replace(ft, " ");
            ft = TextUtils.Rx(@"\b\d{1,3}\s*bpm\b", IC).Replace(ft, " ");
            ft = TextUtils.Rx(@"\b\d{2,3}\b").Replace(ft, " ");
            ft = TextUtils.Rx(@"\s+").Replace(TextUtils.Rx(@"[()\[\]]").Replace(ft, " "), " ").Trim().Trim(',').Trim();
            if (ft.Length > 0) return ft;
        }
        return "";
    }

    /// <summary>¿La posición cae dentro de un paréntesis o corchete abierto?</summary>
    private static bool DentroDeParentesis(string s, int pos)
    {
        var nivel = 0;
        for (int i = 0; i < pos && i < s.Length; i++)
        {
            if (s[i] is '(' or '[') nivel++;
            else if (s[i] is ')' or ']' && nivel > 0) nivel--;
        }
        return nivel > 0;
    }

    /// <summary>
    /// ¿El título contiene al autor como palabras enteras y seguidas? «Get Ready For This (Rio &amp; Le
    /// Jean '92 Remix)» contiene a «Rio &amp; Le Jean»; «Animals» no contiene a «Ani».
    /// </summary>
    private static bool ContieneAlAutor(string? titulo, string? autor)
    {
        var t = PalabrasNk(titulo);
        var a = PalabrasNk(autor);
        if (a.Count == 0 || a.Count > t.Count) return false;
        for (int i = 0; i + a.Count <= t.Count; i++)
            if (t.Skip(i).Take(a.Count).SequenceEqual(a)) return true;
        return false;
    }

    /// <summary>
    /// ¿El título del catálogo nombra ya esta versión? Por palabras enteras, y «RMX» cuenta como
    /// «Remix»: «Scooteroni (RMX)» nombra la versión «Remix».
    /// </summary>
    private static bool NombraLaVersion(string? titulo, string? version)
        => ContieneAlAutor(ComoRemix(titulo), ComoRemix(version));

    private static string ComoRemix(string? s) => TextUtils.Rx(@"(?i)\brmx\b").Replace(s ?? "", "remix");

    private static List<string> PalabrasNk(string? s)
        => TextUtils.Rx(@"[^\p{L}\p{N}]+").Split(s ?? "").Select(TextUtils.Nk).Where(w => w.Length > 0).ToList();

    /// <summary>
    /// El «autor» que RemixParser ha sacado es en realidad el título de la canción: «Stereo Love
    /// (Stereo Love Extended)», «Look At Me Now Remix».
    ///
    /// El título del catálogo se compara sin sus paréntesis ni su «feat.», que es donde puede ir
    /// legítimamente el autor de la versión. El del nombre de archivo, solo por igualdad o por el
    /// principio: el título repetido va delante y el editor del pool va detrás. Visto en una
    /// biblioteca real con el formato de doble espacio: «Duki x Pickle - Givenchy  Alex Cortes Open
    /// Show» se leía como título «Givenchy Alex Cortes», que CONTIENE al editor, y «Alex Cortes» se
    /// descartaba por ser «el propio título» (igual con Alex Torres, Alberto Rodrigo, Nico Guerra…).
    /// </summary>
    public static bool EsElPropioTitulo(string? remixer, string? tituloCatalogo, string? tituloNombre)
    {
        var nRx = TextUtils.Nk(remixer);
        if (nRx.Length == 0) return false;
        var a = PalabrasNk(remixer);

        // Por palabras enteras: «Animals» no contiene ni empieza por un remixer llamado «Ani».
        var nucleo = TextUtils.Rx(@"\s*[\(\[][^\)\]]*[\)\]]").Replace(tituloCatalogo ?? "", " ");
        nucleo = TextUtils.Rx(@"(?i)\s+(?:feat\.?|ft\.?|featuring)\s+.*$").Replace(nucleo, "");
        var nTi = TextUtils.Nk(nucleo);
        if (nTi.Length > 3 && nRx.Contains(nTi)) return true;
        // Con títulos de tres letras o menos («AM», «47», «ALO») no vale buscar el texto dentro del
        // autor, pero sí las palabras enteras: el autor es el título o empieza por él. Antes no se
        // miraba nada, y «Nio Garcia - AM Remix» daba el remixer «Am», «Anuel AA - 47 Remix …» el
        // «47» y «Katteyes … ALO  Open Show   Extended» el nombre «ALO (Alo Open Show Extended)».
        if (ContieneAlAutor(nucleo, remixer) || EmpiezaPor(a, PalabrasNk(nucleo))) return true;

        var nQt = TextUtils.Nk(tituloNombre);
        if (nQt.Length > 3 && nRx.Contains(nQt)) return true;
        var q = PalabrasNk(tituloNombre);
        // El título del nombre empieza por el autor (el título repetido va delante y el editor del
        // pool detrás), o el autor empieza por el título.
        return EmpiezaPor(q, a) || EmpiezaPor(a, q);
    }

    /// <summary>¿La lista de palabras <paramref name="todo"/> empieza por <paramref name="principio"/> (no vacía)?</summary>
    private static bool EmpiezaPor(IReadOnlyList<string> todo, IReadOnlyList<string> principio)
        => principio.Count > 0 && principio.Count <= todo.Count && todo.Take(principio.Count).SequenceEqual(principio);

    /// <summary>
    /// Nombre de archivo (sin extensión) a partir de artista, título y versiones. Cada parte pasa a
    /// ASCII por separado y se queda en Unicode si al pasarla perdería letras: ver
    /// <see cref="TextUtils.AsciiSinPerderLetras"/>.
    /// </summary>
    public static string ComponerNombre(string? artista, string? titulo, IReadOnlyCollection<string>? versiones)
    {
        var a = TextUtils.AsciiSinPerderLetras(artista).Trim();
        var t = TextUtils.AsciiSinPerderLetras(titulo).Trim();
        var v = versiones is { Count: > 0 } ? TextUtils.AsciiSinPerderLetras(string.Join(", ", versiones)).Trim() : "";
        var s = a.Length > 0 && t.Length > 0 ? $"{a} - {t}" : (t.Length > 0 ? t : a);
        if (v.Length > 0) s = $"{s} ({v})";
        return TextUtils.Sanitize(s);
    }

    /// <summary>
    /// El BPM que declara el nombre de archivo («155bpm», «126 Bpm», «126.01bpm»). Devuelve 0 si no
    /// declara ninguno, y marca <paramref name="cambiaDeTempo"/> si declara dos distintos o un rango
    /// («118-128 Bpm», «100 150 BPM»): es una transición y no tiene un BPM único.
    /// </summary>
    public static int BpmDelNombre(string? nombre, out bool cambiaDeTempo)
    {
        cambiaDeTempo = false;
        var s = nombre ?? "";
        if (TextUtils.Rx(@"\b\d{2,3}\s*(?:-|–|/|\s|\ba\b|\bto\b)\s*\d{2,3}\s*bpm\b", IC).IsMatch(s))
        {
            cambiaDeTempo = true;
            return 0;
        }
        var valores = TextUtils.Rx(@"(?<![\d.,])(\d{2,3})(?:[.,]\d{1,2})?\s*bpm\b", IC).Matches(s)
            .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Where(v => v is >= 50 and <= 220)
            .Distinct()
            .ToList();
        if (valores.Count > 1) { cambiaDeTempo = true; return 0; }
        return valores.Count == 1 ? valores[0] : 0;
    }

    /// <summary>
    /// El BPM que se propone: el que declara el nombre manda; el del catálogo, solo si el archivo es
    /// esa misma versión.
    ///
    /// Deezer da el BPM de la pista que encontró, que para un remix o una edición de DJ suele ser el
    /// del tema original. MEDIDO en los registros: de 39 archivos que declaraban el BPM en el nombre y
    /// recibían otro del catálogo, solo 13 coincidían, 9 eran el doble o la mitad y 17 iban a otro
    /// tempo. «Ahora Me Llama (ADRUKID Remix) - … - 155bpm» recibía 135, «How Deep Is Your Love (VD
    /// Soundsystem Remix 126Bpm)» 122 y «Rompe (Bayne Open Show 88bpm)» 176.
    ///
    /// <paramref name="versiones"/> son las versiones del archivo que el título del catálogo NO trae:
    /// si alguna puede cambiar el tempo (remix, bootleg, open show, edit, transición…), el BPM del
    /// catálogo no es el de este archivo. Las que lo conservan (extended, intro, acapella, clean…) no
    /// lo impiden.
    /// </summary>
    public static string ElegirBpm(string? nombreArchivo, IEnumerable<string>? versiones, int bpmCatalogo, out string motivo)
    {
        motivo = "";
        var delNombre = BpmDelNombre(Path.GetFileNameWithoutExtension(nombreArchivo ?? ""), out var cambiaDeTempo);
        if (cambiaDeTempo)
        {
            motivo = "el nombre declara un cambio de tempo: no se propone BPM";
            return "";
        }
        if (delNombre > 0)
        {
            if (bpmCatalogo > 0 && bpmCatalogo != delNombre)
                motivo = $"manda el del nombre ({delNombre}); el catálogo daba {bpmCatalogo}";
            return delNombre.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (bpmCatalogo <= 0) return "";

        var otraVersion = (versiones ?? Enumerable.Empty<string>()).FirstOrDefault(PuedeCambiarElTempo);
        if (otraVersion != null)
        {
            motivo = $"no se propone el del catálogo ({bpmCatalogo}): el archivo es una versión «{otraVersion}» que puede ir a otro tempo";
            return "";
        }
        return bpmCatalogo.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Versiones que conservan el tempo del tema: alargarlo, añadirle una intro o quedarse con la voz
    /// no lo cambia. Todo lo demás (remix, bootleg, open show, edit, transición, directo…) puede.
    /// </summary>
    private static bool PuedeCambiarElTempo(string version)
    {
        var k = TextUtils.Nk(version);
        if (k.Length == 0) return false;
        if (TextUtils.Rx(@"(intro|outro|acapella|aca(in|out)?$|acp$|acap$|instrumental|extended|starter)").IsMatch(k)) return false;
        return k is not ("clean" or "dirty" or "radioedit" or "originalmix" or "originalversion"
                         or "clubmix" or "quickhit" or "quickedit" or "shortedit" or "version"
                         or "remaster" or "remastered" or "hype" or "loop" or "percapella");
    }

    private static string Nk2(string a, string b) => TextUtils.Nk(a + b);

    private static string Pick(params string?[] vals)
    {
        foreach (var v in vals) if (!string.IsNullOrEmpty(v)) return v!;
        return "";
    }
}
