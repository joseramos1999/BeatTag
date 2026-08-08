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
    public GeminiClient Ai { get; }
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
    public CandidateFinder Candidates { get; }
    public LinkResolver Links { get; }

    /// <summary>Reproductor de vista previa (uno a la vez), compartido entre pestañas.</summary>
    public AudioPreview Preview { get; } = new();

    /// <summary>Petición de "editar esta canción" desde otras pestañas (lo atiende el shell).</summary>
    public event Action<string>? EditRequested;
    public void RequestEdit(string filePath) => EditRequested?.Invoke(filePath);

    public AppEngine()
    {
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
        Logger.Detail($"Claves presentes: spotify={Config.SpotifyId.Length > 0 && Config.SpotifySecret.Length > 0} "
                    + $"discogs={Config.DiscogsToken.Length > 0} acoustid={Config.AcoustIdKey.Length > 0} ia={Config.AiKey.Length > 0}");

        Deezer = new DeezerProvider(Api);
        Candidates = new CandidateFinder(Api);
        Itunes = new ItunesProvider(Api);
        Spotify = new SpotifyProvider(Api, Logger);
        Links = new LinkResolver(Api, Spotify);
        MusicBrainz = new MusicBrainzProvider(Api);
        Discogs = new DiscogsProvider(Api);
        AcoustId = new AcoustIdProvider(Api);
        Ai = new GeminiClient(Api, Logger);
        Fingerprint = new Fingerprint(Paths, Logger);
        Covers = new CoverFetcher(Api);
        ArtistExc = ArtistExceptions.Load(Paths.ArtistExceptionsPath);   // + grafías personalizadas del usuario

        Processor = new FileProcessor(Deezer, Itunes, Spotify, MusicBrainz, Discogs, AcoustId, Ai, Fingerprint, Http, ArtistExc, Logger);
        Apply = new ApplyEngine(Covers);
        Undo = new UndoEngine(Paths, Logger);
        Tester = new ConnectionTester(Api, Spotify, Ai);
        Library = new LibraryStore(Config, SaveConfig, new ScanCache(Paths.ScanCachePath)) { Log = Logger };
        Analysis = new AnalysisCache(Paths.AnalysisCachePath);
        Ignored = new IgnoreList(Paths.IgnoredPath);
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
        AiKey = Config.AiKey,
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
