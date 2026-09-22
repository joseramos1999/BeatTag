using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using Etiquetador.Core.Dj;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Pipeline;
using Etiquetador.Core.Providers;

namespace Etiquetador.App.Services;

/// <summary>
/// Cablea toda la capa Core una sola vez y la comparte entre pestañas: rutas, config, API/caché,
/// proveedores, procesador, escritura y deshacer. Es el "backend" de la app.
/// </summary>
public sealed class AppEngine
{
    public AppPaths Paths { get; }
    public AppConfig Config { get; private set; }
    public Logger Logger { get; } = new();
    public ApiClient Api { get; }
    public HttpClient Http { get; } = new();

    public DeezerProvider Deezer { get; }
    public ItunesProvider Itunes { get; }
    public SpotifyProvider Spotify { get; }
    public MusicBrainzProvider MusicBrainz { get; }
    public DiscogsProvider Discogs { get; }
    public AcoustIdProvider AcoustId { get; }
    public OllamaClient Ai { get; }
    public Fingerprint Fingerprint { get; }
    public CoverFetcher Covers { get; }
    public ArtistExceptions ArtistExc { get; }

    public FileProcessor Processor { get; }
    public ApplyEngine Apply { get; }
    public UndoEngine Undo { get; }
    public ConnectionTester Tester { get; }

    /// <summary>Biblioteca compartida por todas las pestañas (carpetas + tracks escaneados).</summary>
    public LibraryStore Library { get; }

    /// <summary>Caché persistente del resultado del análisis (por archivo + firma de opciones).</summary>
    public AnalysisCache Analysis { get; }
    public IgnoreList Ignored { get; }

    /// <summary>Casillas "Aplicar" que el usuario ha tocado: sobreviven a cerrar la aplicacion.</summary>
    public ApplyMarks Marks { get; }

    /// <summary>Canciones ya aplicadas: se omiten en "Analizar" (pero no en "Reanalizar todo").</summary>
    public IgnoreList Applied { get; }
    public CandidateFinder Candidates { get; }

    /// <summary>Medida de sonoridad (EBU R128) con cache propia. Solo mide, no toca archivos.</summary>
    public LoudnessScanner Loudness { get; }
    public FingerprintScanner Fingerprints { get; }

    /// <summary>Identificación por audio al estilo de Shazam. De pago: solo se usa si el usuario lo activa.</summary>
    public AuddProvider Audd { get; }
    public IdentificationScanner Identificacion { get; }

    /// <summary>
    /// Avisos de «Comprobar audio» que el usuario ha revisado y descartado por ser falsos. Lista
    /// PROPIA, aparte de las descartadas de Enriquecer: dar por bueno un aviso de esta pestaña no
    /// puede tener el efecto lateral de sacar la canción del análisis de las demás.
    /// </summary>
    public IgnoreList AudioAceptadas { get; }
    public ChartsProvider Charts { get; }
    public LinkResolver Links { get; }

    /// <summary>Fichas de DJ: energía, momento, ambiente… Se guardan en local, no en el archivo.</summary>
    public AlmacenFichas Fichas { get; }

    /// <summary>Colecciones inteligentes guardadas.</summary>
    public AlmacenColecciones Colecciones { get; }

    /// <summary>Estado de cada canción de la bandeja de entrada.</summary>
    public AlmacenBandeja Bandeja { get; }

    /// <summary>
    /// Alguna ficha ha cambiado. Las fichas se editan desde dos páginas (Ficha DJ y Bandeja) y se
    /// leen desde una tercera (Colecciones): sin este aviso, una colección seguiría enseñando lo de
    /// antes de guardar.
    /// </summary>
    public event Action? FichasCambiadas;
    public void AvisarFichasCambiadas() => FichasCambiadas?.Invoke();

    /// <summary>Reproductor de vista previa (uno a la vez), compartido entre pestañas.</summary>
    public AudioPreview Preview { get; } = new();

    /// <summary>Petición de "editar esta canción" desde otras pestañas (lo atiende el shell).</summary>
    public event Action<string>? EditRequested;
    public void RequestEdit(string filePath) => EditRequested?.Invoke(filePath);

    /// <summary>
    /// Instancia en uso. La app tiene un único motor compartido; esto permite que detalles de la
    /// interfaz (como recordar el ancho de las columnas) lleguen a la config sin cablearla por
    /// todos los ViewModels. No usar para lógica de negocio: ahí se pasa el motor por constructor.
    /// </summary>
    public static AppEngine? Current { get; private set; }

    /// <summary>
    /// En qué va el arranque. Lo escucha la pantalla de carga para decir qué se está haciendo, en
    /// vez de dejar el escritorio vacío mientras se leen las cachés.
    /// </summary>
    public static Action<string>? Progreso;

    private readonly System.Diagnostics.Stopwatch _arranque = System.Diagnostics.Stopwatch.StartNew();
    private long _faseDesde;
    private string _faseActual = "";

    /// <summary>Cierra la fase anterior en el log (con su duración) y anuncia la siguiente.</summary>
    private void Fase(string texto)
    {
        var ahora = _arranque.ElapsedMilliseconds;
        if (_faseActual.Length > 0) Logger.Detail($"Arranque · {_faseActual}: {ahora - _faseDesde} ms");
        _faseActual = texto;
        _faseDesde = ahora;
        Progreso?.Invoke(texto);
    }

    public AppEngine()
    {
        Current = this;
        Paths = new AppPaths();
        Paths.EnsureDirectories();

        // Log de la sesión a archivo (además del panel en Ajustes).
        Logger.LogFile = Path.Combine(Paths.LogsDir, $"beattag_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        Logger.SessionHeader(AppInfo.Name, AppInfo.Version, Paths.DataDir);
        Logger.Head($"BeatTag {AppInfo.Version} iniciado.");
        var pruned = Logger.PruneOldLogs(Paths.LogsDir);
        if (pruned > 0) Logger.Detail($"Limpieza de logs: {pruned} antiguos borrados (>30 días).");

        Fase("Leyendo los ajustes…");
        Config = AppConfig.Load(Paths, out var cfgErr);
        if (cfgErr.Length > 0) Logger.Err(cfgErr);
        Api = new ApiClient(Paths) { CacheOn = Config.Cache, Log = Logger };
        Logger.Detail($"Config: caché={Config.Cache} · fuentes: deezer={Config.UseDeezer} itunes={Config.UseItunes} "
                    + $"spotify={Config.UseSpotify} discogs={Config.UseDiscogs} mb={Config.UseMusicBrainz} "
                    + $"acoustid={Config.UseAcoustId} ia={Config.UseAi}");
        var modeloIa = Config.AiModel.Length > 0 ? Config.AiModel : "(automático)";
        Logger.Detail($"Claves presentes: spotify={Config.SpotifyId.Length > 0 && Config.SpotifySecret.Length > 0} "
                    + $"discogs={Config.DiscogsToken.Length > 0} acoustid={Config.AcoustIdKey.Length > 0} ia-local={modeloIa}");

        Fase("Preparando las fuentes de datos…");
        Deezer = new DeezerProvider(Api) { Log = Logger };
        Candidates = new CandidateFinder(Api);
        Charts = new ChartsProvider(Api);
        Loudness = new LoudnessScanner(Paths.LoudnessCachePath, Logger);
        Itunes = new ItunesProvider(Api);
        Spotify = new SpotifyProvider(Api, Logger);
        Links = new LinkResolver(Api, Spotify);
        MusicBrainz = new MusicBrainzProvider(Api);
        Discogs = new DiscogsProvider(Api);
        AcoustId = new AcoustIdProvider(Api);
        Ai = new OllamaClient(Api, Logger);
        if (Config.AiHost.Length > 0) Ai.Host = Config.AiHost;
        Fase("Cargando las huellas de audio…");
        Fingerprint = new Fingerprint(Paths, Logger);
        // Detrás de Fingerprint a propósito: necesita su ruta de fpcalc, y antes estaría a null.
        Fingerprints = new FingerprintScanner(Paths.FingerprintCachePath, Fingerprint.FpcalcPath, Logger);
        Audd = new AuddProvider(Http, Logger);
        // Los dos motores encadenados: AcoustID (gratis) primero, AudD solo para lo que quede.
        Identificacion = new IdentificationScanner(
            Paths.IdentificacionCachePath, Fingerprint, AcoustId, Audd, Http, Logger);
        Covers = new CoverFetcher(Api);
        Fase("Cargando los nombres de artista…");
        // Los alias se cargan ANTES que las excepciones: estas los incorporan para escribir el nombre canónico.
        ArtistAliases.Current = ArtistAliases.Load(Paths.ArtistAliasesPath);
        ArtistExc = ArtistExceptions.Load(Paths.ArtistExceptionsPath);   // + grafías personalizadas del usuario

        Processor = new FileProcessor(Deezer, Itunes, Spotify, MusicBrainz, Discogs, AcoustId, Ai, Fingerprint, Http, ArtistExc, Logger);
        Apply = new ApplyEngine(Covers);
        Undo = new UndoEngine(Paths, Logger);
        Tester = new ConnectionTester(Api, Spotify, Ai);
        Fase("Cargando la caché de la biblioteca…");
        Library = new LibraryStore(Config, SaveConfig, new ScanCache(Paths.ScanCachePath)) { Log = Logger };
        Fase("Cargando lo ya analizado…");
        Analysis = new AnalysisCache(Paths.AnalysisCachePath);
        Ignored = new IgnoreList(Paths.IgnoredPath);
        Applied = new IgnoreList(Paths.AppliedPath);
        Marks = new ApplyMarks(Paths.ApplyMarksPath);
        AudioAceptadas = new IgnoreList(Paths.AudioAceptadasPath);

        Fase("Cargando fichas y colecciones…");
        Fichas = new AlmacenFichas(Paths.FichasDjPath);
        Colecciones = new AlmacenColecciones(Paths.ColeccionesPath);
        Bandeja = new AlmacenBandeja(Paths.BandejaPath);
        foreach (var aviso in new[] { Fichas.AvisoCarga, Colecciones.AvisoCarga, Bandeja.AvisoCarga })
            if (aviso.Length > 0) Logger.Err(aviso);
        Logger.Detail($"Fichas de DJ: {Fichas.Count} · colecciones: {Colecciones.Todas.Count}");

        // Cada vez que la biblioteca se reescanea -típicamente tras aplicar o deshacer renombrados-,
        // las fichas se recolocan con sus archivos. Se suscribe AQUÍ, antes de que existan las
        // páginas, para que cuando estas recalculen las fichas ya estén en su sitio.
        Library.Changed += ReubicarFichas;

        // La firma de la caché pasó a distinguir QUÉ credenciales se usan, no solo si las hay. Lo
        // ya analizado con las mismas claves sigue siendo válido, así que se le pone la firma nueva
        // en vez de tirarlo: de otro modo, el primer arranque tras actualizar reanalizaría la
        // biblioteca entera sin que nada hubiera cambiado de verdad.
        var opts = BuildOptions();
        var migradas = Analysis.MigrarFirma(opts.SignatureLegacy(), opts.Signature());
        if (migradas > 0)
        {
            Analysis.Save();
            Logger.Detail($"Caché de análisis: {migradas} entradas conservadas al cambiar el formato de la firma.");
        }

        Fase("");   // cierra la última fase en el log
        Logger.Detail($"Arranque · motor listo en {_arranque.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Devuelve a su archivo las fichas cuyo archivo ha cambiado de nombre o de sitio.
    ///
    /// Primero se pregunta si hace falta, que es barato: casi todas las fichas son de canciones de
    /// la biblioteca en memoria y no hay que ir al disco para saber que existen. Solo si alguna no
    /// aparece se leen los manifiestos de deshacer, que es lo caro.
    /// </summary>
    private void ReubicarFichas()
    {
        if (Fichas.Count == 0 || !Library.IsScanned) return;
        try
        {
            var enBiblioteca = new HashSet<string>(Library.Tracks.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase);
            bool Existe(string ruta) => enBiblioteca.Contains(ruta) || File.Exists(ruta);

            if (!Fichas.HayHuerfanas(Existe)) return;

            var movidas = Fichas.Reubicar(RekordboxRelocator.ReadRenames(Paths.UndoDir), Existe);
            if (movidas == 0) return;

            var err = Fichas.Guardar();
            Logger.Detail($"Fichas de DJ: {movidas} recolocada(s) tras renombrar archivos."
                        + (err.Length > 0 ? $" ⚠ No se pudo guardar: {err}" : ""));
        }
        catch (Exception e) { Logger.Error("Error al recolocar las fichas de DJ", e); }
    }

    // --- Fichas de DJ: lo comparten la página de fichas y la bandeja ---

    /// <summary>Aplica unos cambios a la ficha de cada archivo y lo guarda en disco. Devuelve "" o el error.</summary>
    public string GuardarFichas(IReadOnlyList<string> rutas, CambiosFicha cambios)
    {
        foreach (var r in rutas) Fichas.Poner(r, cambios.AplicarA(Fichas.Obtener(r)));
        var err = Fichas.Guardar();
        if (err.Length > 0) Logger.Err("No se pudieron guardar las fichas de DJ: " + err);
        return err;
    }

    /// <summary>
    /// Aplica a cada archivo SU cambio de ficha y guarda en disco una sola vez. Es lo que usa aceptar
    /// propuestas de la IA: cada canción trae la suya, y guardar el archivo por cada una serían cientos
    /// de escrituras.
    /// </summary>
    public string GuardarFichas(IEnumerable<(string Ruta, CambiosFicha Cambios)> cambios)
    {
        foreach (var (r, c) in cambios) Fichas.Poner(r, c.AplicarA(Fichas.Obtener(r)));
        var err = Fichas.Guardar();
        if (err.Length > 0) Logger.Err("No se pudieron guardar las fichas de DJ: " + err);
        return err;
    }

    public sealed record ResultadoGeneros(int Escritas, int SinCambio, IReadOnlyList<string> Fallos, string UndoErr, bool Cancelado);

    /// <summary>
    /// Escribe el género nuevo en cada archivo («» lo quita). Como toda escritura de etiquetas, queda
    /// anotada para deshacer EN CUANTO se hace, así que cancelar a medias deja reversible lo ya
    /// escrito. Hay que llamarla fuera del hilo de la interfaz y con el reproductor parado.
    /// </summary>
    public ResultadoGeneros AplicarGeneros(IReadOnlyList<(string Ruta, string Genero)> cambios,
                                           IProgress<double>? progreso, CancellationToken ct)
    {
        var undo = Path.Combine(Paths.UndoDir, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
        int escritas = 0, sinCambio = 0;
        var fallos = new List<string>();
        var undoErr = "";

        for (var i = 0; i < cambios.Count; i++)
        {
            if (ct.IsCancellationRequested)
                return new ResultadoGeneros(escritas, sinCambio, fallos, undoErr, true);

            var (ruta, genero) = cambios[i];
            try
            {
                using var f = TagLib.File.Create(ruta);
                var antes = f.Tag.Genres ?? Array.Empty<string>();
                var despues = genero.Length == 0 ? Array.Empty<string>() : new[] { genero };
                if (antes.SequenceEqual(despues, StringComparer.Ordinal)) { sinCambio++; continue; }

                f.Tag.Genres = despues;
                f.Save();
                escritas++;

                var rec = new UndoRecord
                {
                    OrigPath = ruta, FinalPath = ruta, Renamed = false,
                    Fields = new Dictionary<string, FieldChange> { ["Genre"] = FieldChange.Arr(antes, despues) },
                };
                try { File.AppendAllText(undo, System.Text.Json.JsonSerializer.Serialize(rec) + "\n"); }
                catch (Exception e) { undoErr = e.Message; }
            }
            catch (Exception e)
            {
                fallos.Add(Path.GetFileName(ruta) + ": " + e.Message);
                Logger.Log($"Género: {Path.GetFileName(ruta)}: {e.Message}", LogKind.Err);
            }
            progreso?.Report(100.0 * (i + 1) / cambios.Count);
        }
        Logger.Detail($"Géneros unificados: {escritas} escritos · {sinCambio} ya estaban · {fallos.Count} fallos.");
        return new ResultadoGeneros(escritas, sinCambio, fallos, undoErr, false);
    }

    public sealed record ResultadoRenombrado(IReadOnlyList<(string Origen, string Destino)> Hechos, IReadOnlyList<string> Fallos,
                                             string UndoErr, bool Cancelado);

    /// <summary>
    /// Renombra archivos dentro de su carpeta. Cada renombrado queda anotado para deshacer en cuanto
    /// se hace, y con eso también sirve para reparar la colección de rekordbox. La ficha de DJ sigue
    /// al archivo.
    ///
    /// A diferencia de Enriquecer, si el nombre ya existe NO se añade «(2)»: dos archivos que acaban
    /// llamándose igual suelen ser la misma canción, y eso se dice en vez de esconderlo.
    /// Hay que llamarla fuera del hilo de la interfaz y con el reproductor parado.
    /// </summary>
    public ResultadoRenombrado RenombrarArchivos(IReadOnlyList<(string Ruta, string NuevoNombre)> cambios,
                                                 IProgress<double>? progreso, CancellationToken ct)
    {
        var undo = Path.Combine(Paths.UndoDir, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
        var hechos = new List<(string, string)>();
        var fallos = new List<string>();
        var undoErr = "";

        for (var i = 0; i < cambios.Count; i++)
        {
            if (ct.IsCancellationRequested) return new ResultadoRenombrado(hechos, fallos, undoErr, true);
            var (ruta, nuevo) = cambios[i];
            var nombre = Path.GetFileName(ruta);
            var t = Etiquetador.Core.Ai.RenombradoIa.RenombrarArchivo(ruta, nuevo);
            if (t.Ok)
            {
                hechos.Add((t.Origen, t.Destino));
                Fichas.Mover(t.Origen, t.Destino);
                var rec = new UndoRecord { OrigPath = t.Origen, FinalPath = t.Destino, Renamed = true };
                try { File.AppendAllText(undo, System.Text.Json.JsonSerializer.Serialize(rec) + "\n"); }
                catch (Exception e) { undoErr = e.Message; }
                Logger.Detail($"Renombrado con IA: «{nombre}» → «{Path.GetFileName(t.Destino)}»");
            }
            else
            {
                fallos.Add($"{nombre}: {t.Error}");
                Logger.Log($"Renombrado con IA: {nombre}: {t.Error}", LogKind.Err);
            }
            progreso?.Report(100.0 * (i + 1) / cambios.Count);
        }

        if (hechos.Count > 0)
        {
            var err = Fichas.Guardar();
            if (err.Length > 0) Logger.Err("No se pudieron guardar las fichas tras renombrar: " + err);
        }
        return new ResultadoRenombrado(hechos, fallos, undoErr, false);
    }

    /// <summary>Borra la ficha de cada archivo. Devuelve "" o el error.</summary>
    public string BorrarFichas(IReadOnlyList<string> rutas)
    {
        foreach (var r in rutas) Fichas.Quitar(r);
        var err = Fichas.Guardar();
        if (err.Length > 0) Logger.Err("No se pudieron guardar las fichas de DJ: " + err);
        return err;
    }

    public sealed record ResultadoVolcado(int Escritas, int SinCambio, IReadOnlyList<string> Fallos, string UndoErr);

    /// <summary>
    /// Copia la ficha de cada archivo a su comentario, conservando lo que el comentario ya tenía.
    ///
    /// Es la única acción de las fichas que toca la música, y por eso queda anotada para deshacer
    /// como cualquier otra escritura. Cada cambio se anota EN CUANTO se hace, no al final: si algo se
    /// interrumpe a medias, lo ya escrito sigue siendo reversible. Hay que llamarla fuera del hilo de
    /// la interfaz y con el reproductor parado (<see cref="ReleaseAudio"/>).
    /// </summary>
    public ResultadoVolcado VolcarFichasAlComentario(IReadOnlyList<string> rutas)
    {
        var undo = Path.Combine(Paths.UndoDir, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
        int escritas = 0, sinCambio = 0;
        var fallos = new List<string>();
        var undoErr = "";

        foreach (var ruta in rutas)
        {
            try
            {
                var ficha = Fichas.Obtener(ruta) ?? new FichaDj();
                using var f = TagLib.File.Create(ruta);
                var antes = f.Tag.Comment ?? "";
                var despues = Etiquetador.Core.Dj.Fichas.ComentarioCombinado(antes, ficha);
                if (despues == antes) { sinCambio++; continue; }

                f.Tag.Comment = despues.Length == 0 ? null : despues;
                f.Save();
                escritas++;

                var rec = new UndoRecord
                {
                    OrigPath = ruta, FinalPath = ruta, Renamed = false,
                    Fields = new Dictionary<string, FieldChange> { ["Comment"] = FieldChange.Str(antes, despues) },
                };
                try { File.AppendAllText(undo, System.Text.Json.JsonSerializer.Serialize(rec) + "\n"); }
                catch (Exception e) { undoErr = e.Message; }
            }
            catch (Exception e)
            {
                fallos.Add(Path.GetFileName(ruta) + ": " + e.Message);
                Logger.Log($"Ficha al comentario: {Path.GetFileName(ruta)}: {e.Message}", LogKind.Err);
            }
        }
        Logger.Detail($"Fichas al comentario: {escritas} escritas · {sinCambio} ya estaban al día · {fallos.Count} fallos.");
        return new ResultadoVolcado(escritas, sinCambio, fallos, undoErr);
    }

    /// <summary>Opciones de proceso a partir de la config actual.</summary>
    public ProcessOptions BuildOptions() => new()
    {
        Deezer = Config.UseDeezer,
        Itunes = Config.UseItunes,
        Spotify = Config.UseSpotify,
        MusicBrainz = Config.UseMusicBrainz,
        Discogs = Config.UseDiscogs,
        AcoustId = Config.UseAcoustId,
        Ai = Config.UseAi,
        SpotifyId = Config.SpotifyId,
        SpotifySecret = Config.SpotifySecret,
        DiscogsToken = Config.DiscogsToken,
        AcoustIdKey = Config.AcoustIdKey,
        AiModel = Config.AiModel,
        CleanOnly = Config.CleanOnly,
    };

    public FieldFlags BuildFields() => new()
    {
        Title = Config.WriteTitle,
        Artist = Config.WriteArtist,
        Album = Config.WriteAlbum,
        Genre = Config.WriteGenre,
        Year = Config.WriteYear,
        Bpm = Config.WriteBpm,
    };

    /// <summary>Guarda la config informando del resultado (para la pantalla de Ajustes).</summary>
    public bool SaveConfig(out string error)
    {
        Api.CacheOn = Config.Cache;
        var ok = Config.Save(Paths, out error);
        if (!ok) Logger.Log("Configuración: " + error, LogKind.Err);
        return ok;
    }

    /// <summary>Guardado best-effort para auto-guardados (opciones, carpetas): registra el fallo pero no lanza.</summary>
    public void SaveConfig() => SaveConfig(out _);

    /// <summary>Procesa un archivo usando la caché de análisis (si <paramref name="force"/>, la ignora y recalcula).</summary>
    public async Task<ProcessResult> AnalyzeCachedAsync(string filePath, ProcessOptions opts, string sig, bool force, CancellationToken ct = default)
    {
        if (!force)
        {
            var cached = Analysis.Get(filePath, sig);
            if (cached != null) return cached;
        }
        var r = await Processor.ProcessAsync(filePath, isAcapella: false, opts, ct).ConfigureAwait(false);
        Analysis.Set(filePath, sig, r);
        return r;
    }

    /// <summary>Progreso dentro de la canción en curso (fase, 0..1) para la barra secundaria.</summary>
    public IProgress<(string Phase, double Fraction)>? StepProgress
    {
        get => Processor.StepProgress;
        set => Processor.StepProgress = value;
    }

    public void ClearAnalysisCache() => Analysis.Clear();

    // --- Servicios del diálogo "Reanalizar…" (los usan Enriquecer y No encontradas por igual) ---

    /// <summary>Coincidencias del catálogo para que el usuario elija a mano.</summary>
    public Task<IReadOnlyList<Candidate>> FindCandidatesAsync(string artist, string title)
    {
        // Si el usuario tiene ambas fuentes apagadas se usa Deezer (no necesita clave) para poder listar.
        var dz = Config.UseDeezer || !Config.UseItunes;
        return Candidates.FindAsync(artist, title, dz, Config.UseItunes);
    }

    /// <summary>Resuelve un enlace de Deezer/Spotify/Apple Music a la canción concreta.</summary>
    public Task<LinkResolver.Result> ResolveLinkAsync(string url)
        => Links.ResolveAsync(url, Config.SpotifyId, Config.SpotifySecret);

    /// <summary>
    /// Identifica por HUELLA ACÚSTICA (AcoustID): no depende del nombre del archivo, así que es la
    /// última bala para las pistas cuyo nombre no dice nada. Necesita la clave de AcoustID.
    /// </summary>
    public async Task<LinkResolver.Result> IdentifyByFingerprintAsync(string filePath)
    {
        var key = Config.AcoustIdKey;
        if (string.IsNullOrWhiteSpace(key))
            return new LinkResolver.Result(null, "Para identificar por huella hace falta tu clave de AcoustID (pestaña Ajustes).");
        try
        {
            var fp = await Task.Run(() => Fingerprint.GetAsync(filePath, Http)).ConfigureAwait(false);
            if (fp is not FingerprintResult f || f.Fingerprint.Length == 0)
                return new LinkResolver.Result(null, "No se pudo calcular la huella de este archivo.");

            var hit = await AcoustId.LookupAsync(f.Duration, f.Fingerprint, key).ConfigureAwait(false);
            if (hit == null || hit.Title.Length == 0)
                return new LinkResolver.Result(null, "La huella no coincide con ninguna canción conocida.");

            return new LinkResolver.Result(
                new Candidate("AcoustID", hit.Artist, hit.Title, hit.Album, hit.Year, (int)f.Duration), "");
        }
        catch (Exception e) { return new LinkResolver.Result(null, "Error al identificar por huella: " + e.Message); }
    }

    /// <summary>
    /// Suelta el archivo que esté sonando. OBLIGATORIO antes de escribir tags o renombrar: el
    /// reproductor mantiene el archivo abierto y, si no, la escritura o el renombrado fallan.
    /// </summary>
    public void ReleaseAudio()
    {
        try { Preview.Stop(); } catch { }
    }

    /// <summary>Descarta una canción: la olvida del análisis y la excluye de futuras pasadas.</summary>
    public void IgnoreTrack(string filePath)
    {
        Analysis.Remove(filePath);
        Analysis.Save();
        Ignored.Add(filePath);
        Ignored.Save();
    }

    /// <summary>
    /// Descarta VARIAS canciones de una vez. Igual que IgnoreTrack, pero guardando los dos ficheros
    /// una sola vez al final: descartar cincuenta filas seleccionadas no puede costar cien
    /// escrituras en disco.
    /// </summary>
    public void IgnoreTracks(IEnumerable<string> filePaths)
    {
        var n = 0;
        foreach (var p in filePaths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            Analysis.Remove(p);
            Ignored.Add(p);
            n++;
        }
        if (n == 0) return;
        Analysis.Save();
        Ignored.Save();
    }

    /// <summary>Vuelve a tener en cuenta todas las canciones descartadas.</summary>
    public void ClearIgnored() => Ignored.Clear();

    /// <summary>Vuelve a tener en cuenta SOLO las canciones indicadas.</summary>
    public void RestoreIgnored(IEnumerable<string> filePaths)
    {
        foreach (var p in filePaths) Ignored.Remove(p);
        Ignored.Save();
    }
}
