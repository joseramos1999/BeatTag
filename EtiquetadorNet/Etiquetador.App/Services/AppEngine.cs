using System.Collections.Generic;
using System;
using System.IO;
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

    /// <summary>Canciones ya aplicadas: se omiten en "Analizar" (pero no en "Reanalizar todo").</summary>
    public IgnoreList Applied { get; }
    public CandidateFinder Candidates { get; }

    /// <summary>Medida de sonoridad (EBU R128) con cache propia. Solo mide, no toca archivos.</summary>
    public LoudnessScanner Loudness { get; }
    public ChartsProvider Charts { get; }
    public LinkResolver Links { get; }

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

        Config = AppConfig.Load(Paths, out var cfgErr);
        if (cfgErr.Length > 0) Logger.Err(cfgErr);
        Api = new ApiClient(Paths) { CacheOn = Config.Cache, Log = Logger };
        Logger.Detail($"Config: caché={Config.Cache} · fuentes: deezer={Config.UseDeezer} itunes={Config.UseItunes} "
                    + $"spotify={Config.UseSpotify} discogs={Config.UseDiscogs} mb={Config.UseMusicBrainz} "
                    + $"acoustid={Config.UseAcoustId} ia={Config.UseAi}");
        var modeloIa = Config.AiModel.Length > 0 ? Config.AiModel : "(automático)";
        Logger.Detail($"Claves presentes: spotify={Config.SpotifyId.Length > 0 && Config.SpotifySecret.Length > 0} "
                    + $"discogs={Config.DiscogsToken.Length > 0} acoustid={Config.AcoustIdKey.Length > 0} ia-local={modeloIa}");

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
        Fingerprint = new Fingerprint(Paths, Logger);
        Covers = new CoverFetcher(Api);
        // Los alias se cargan ANTES que las excepciones: estas los incorporan para escribir el nombre canónico.
        ArtistAliases.Current = ArtistAliases.Load(Paths.ArtistAliasesPath);
        ArtistExc = ArtistExceptions.Load(Paths.ArtistExceptionsPath);   // + grafías personalizadas del usuario

        Processor = new FileProcessor(Deezer, Itunes, Spotify, MusicBrainz, Discogs, AcoustId, Ai, Fingerprint, Http, ArtistExc, Logger);
        Apply = new ApplyEngine(Covers);
        Undo = new UndoEngine(Paths, Logger);
        Tester = new ConnectionTester(Api, Spotify, Ai);
        Library = new LibraryStore(Config, SaveConfig, new ScanCache(Paths.ScanCachePath)) { Log = Logger };
        Analysis = new AnalysisCache(Paths.AnalysisCachePath);
        Ignored = new IgnoreList(Paths.IgnoredPath);
        Applied = new IgnoreList(Paths.AppliedPath);
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

    /// <summary>Vuelve a tener en cuenta todas las canciones descartadas.</summary>
    public void ClearIgnored() => Ignored.Clear();
}
