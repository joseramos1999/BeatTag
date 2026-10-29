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
    /// <summary>Playlists y álbumes de Spotify y Deezer a partir de su enlace (pestaña Playlists).</summary>
    public PlaylistProvider Playlists { get; }
    public LinkResolver Links { get; }

    /// <summary>Fichas de DJ: energía, momento, ambiente… Se guardan en local, no en el archivo.</summary>
    public AlmacenFichas Fichas { get; }

    /// <summary>Colecciones inteligentes guardadas.</summary>
    public AlmacenColecciones Colecciones { get; }

    /// <summary>Estado de cada canción de la bandeja de entrada.</summary>
    public AlmacenBandeja Bandeja { get; }

    /// <summary>
    /// Avisos que la ventana enseña de forma persistente: archivos que no se pudieron leer al
    /// arrancar, guardados que fallan, carpetas no disponibles. Antes solo iban al registro.
    /// </summary>
    public AvisosApp Avisos { get; } = new();

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

        // --- Copia de seguridad (equipo «volumen_copias», tanda 4, T15) ---
        // Una restauración preparada en Ajustes se completa AQUÍ, antes de leer ningún archivo de
        // datos; después, la copia automática del estado con que se cerró, en un hilo de fondo.
        CopiasAlArrancar.CompletarRestauracion(Paths, Logger, Avisos);
        CopiasAlArrancar.IniciarCopiaAutomatica(Paths, Logger);

        Fase("Leyendo los ajustes…");
        Config = AppConfig.Load(Paths, out var cfgErr);
        if (cfgErr.Length > 0)
        {
            Logger.Err(cfgErr);
            Avisos.Poner("carga:config", cfgErr);
            // Load aparta el archivo ilegible ANTES de que exista uno nuevo. Si la aplicación se
            // cerrase a la fuerza antes del primer guardado (el Stop-Process de Compilar-BeatTag.ps1
            // -CerrarApp, un corte de luz), el siguiente arranque no encontraría config.net.json y
            // empezaría con los valores de fábrica, con las carpetas solo en el «.ilegible». Por eso
            // lo rescatado se guarda ya.
            if (!Config.GuardadoBloqueado && !File.Exists(Paths.ConfigPath))
            {
                if (Config.Save(Paths, out var errRescate)) Logger.Detail("Configuración: lo rescatado queda guardado como config.net.json.");
                else Logger.Err("Configuración: no se pudo guardar lo rescatado: " + errRescate);
            }
        }
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
        Playlists = new PlaylistProvider(Api, Http);
        Loudness = new LoudnessScanner(Paths.LoudnessCachePath, Logger, Paths.VolumenAplicadoPath);
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
        // Al deshacer, las cachés de audio siguen al archivo igual que al escribir (hallazgo datos-3).
        Undo = new UndoEngine(Paths, Logger) { AlDeshacer = SeguirDeshecho };
        Tester = new ConnectionTester(Api, Spotify, Ai);
        Fase("Cargando la caché de la biblioteca…");
        Library = new LibraryStore(Config, SaveConfig, new ScanCache(Paths.ScanCachePath)) { Log = Logger };
        Fase("Cargando lo ya analizado…");
        Analysis = new AnalysisCache(Paths.AnalysisCachePath);
        PonerLetraDelCatalogo();   // C14 (equipo catalogo_bandeja): la columna «Explícito» con el dato de Deezer
        Ignored = new IgnoreList(Paths.IgnoredPath);
        Applied = new IgnoreList(Paths.AppliedPath);
        Marks = new ApplyMarks(Paths.ApplyMarksPath);
        AudioAceptadas = new IgnoreList(Paths.AudioAceptadasPath);

        Fase("Cargando fichas y colecciones…");
        Fichas = new AlmacenFichas(Paths.FichasDjPath);
        Colecciones = new AlmacenColecciones(Paths.ColeccionesPath);
        Bandeja = new AlmacenBandeja(Paths.BandejaPath);
        // Lo que no se pudo leer al arrancar se dice en la ventana, no solo en el registro: con
        // fichas-dj.json bloqueado por otra ventana de BeatTag el almacén queda en solo lectura, y
        // el usuario seguiría poniendo fichas que no se van a guardar sin nada que lo dijera.
        foreach (var (clave, aviso) in new[]
                 {
                     ("fichas", Fichas.AvisoCarga), ("colecciones", Colecciones.AvisoCarga), ("bandeja", Bandeja.AvisoCarga),
                     ("analisis", Analysis.AvisoCarga), ("descartadas", Ignored.AvisoCarga), ("aplicadas", Applied.AvisoCarga),
                     ("marcas", Marks.AvisoCarga), ("audio-aceptadas", AudioAceptadas.AvisoCarga), ("escaneo", Library.AvisoCache),
                 })
        {
            if (aviso.Length == 0) continue;
            Logger.Err(aviso);
            Avisos.Poner("carga:" + clave, aviso);
        }
        Logger.Detail($"Fichas de DJ: {Fichas.Count} · colecciones: {Colecciones.Todas.Count}");

        // Cada vez que la biblioteca se reescanea -típicamente tras aplicar o deshacer renombrados-,
        // las fichas se recolocan con sus archivos. Se suscribe AQUÍ, antes de que existan las
        // páginas, para que cuando estas recalculen las fichas ya estén en su sitio.
        Library.Changed += ConciliarSustituciones;   // I14 (equipo catalogo): ANTES de recolocar las huérfanas
        Library.Changed += ReubicarFichas;

        // Una carpeta marcada que no se ha podido leer (el disco externo sin enchufar) se dice en la
        // ventana, y el aviso se quita solo cuando vuelve a estar (hallazgo M7).
        Library.Changed += () => Avisos.Poner("carpetas", Library.AvisoNoDisponibles());

        // La firma de la caché pasó a distinguir QUÉ credenciales se usan, no solo si las hay. Lo
        // ya analizado con las mismas claves sigue siendo válido, así que se le pone la firma nueva
        // en vez de tirarlo: de otro modo, el primer arranque tras actualizar reanalizaría la
        // biblioteca entera sin que nada hubiera cambiado de verdad.
        var opts = BuildOptions();
        var migradas = Analysis.MigrarFirma(opts.SignatureLegacy(), opts.Signature());
        if (migradas > 0)
        {
            Registrar("la caché de análisis", Analysis.Save());
            Logger.Detail($"Caché de análisis: {migradas} entradas conservadas al cambiar el formato de la firma.");
        }

        IniciarGuardadoPeriodico();
        RecuperarSustitucionesCortadas();   // I14 (equipo catalogo): una descarga que se quedó oculta al cerrarse a mitad

        Fase("");   // cierra la última fase en el log
        Logger.Detail($"Arranque · motor listo en {_arranque.ElapsedMilliseconds} ms");
        Listo = true;
    }

    /// <summary>
    /// El motor terminó de montarse. Si el arranque falló a medias, <see cref="Current"/> ya apunta a
    /// este motor incompleto, y al cerrar no hay que intentar guardar nada con él.
    /// </summary>
    public bool Listo { get; private set; }

    // --- Guardar lo que vive en memoria (hallazgos M9, T3 y T10) ---
    //
    // Visto en los registros: un «Reanalizar todo» del 22-09 llevaba 66 minutos (18:23 → 19:29)
    // cuando se canceló. Cancelado, guardó; cerrado con la X, no habría guardado nada: los análisis,
    // las huellas, las medidas de sonoridad y las respuestas de «Comprobar audio» (algunas de pago)
    // solo estaban en memoria. Al cerrar solo se guardaban las casillas «Aplicar».

    /// <summary>
    /// Guarda todo lo que vive en memoria y tiene cambios: análisis, casillas «Aplicar», aplicadas,
    /// descartadas, avisos aceptados de «Comprobar audio», sonoridad, huellas e identificación. Cada
    /// uno solo escribe si hay algo nuevo, así que llamarlo de más no cuesta. Los fallos se anotan en
    /// el registro y en los avisos de la ventana, y se devuelven.
    ///
    /// Hay que llamarlo desde el hilo que modifica esas cachés (el de la interfaz) o con todo parado:
    /// la caché de análisis no admite que se guarde mientras otro hilo le añade resultados.
    /// </summary>
    public IReadOnlyList<string> GuardarTodo(string cuando)
    {
        var fallos = new List<string>();
        if (!Listo) return fallos;
        void Uno(string que, Func<string> guardar)
        {
            string error;
            try { error = guardar(); }
            catch (Exception e) { error = e.Message; }
            if (error.Length > 0) fallos.Add($"{que}: {error}");
        }

        Uno("el análisis", Analysis.Save);
        Uno("las casillas «Aplicar»", Marks.Save);
        Uno("las canciones aplicadas", Applied.Save);
        Uno("las canciones descartadas", Ignored.Save);
        Uno("los avisos aceptados de Comprobar audio", AudioAceptadas.Save);
        // Estas tres registran por su cuenta lo que les pase al guardar.
        Uno("la sonoridad", () => { Loudness.Save(); return ""; });
        Uno("las huellas", () => { EsperarColaDeHuellas(); Fingerprints.Save(); return ""; });
        Uno("la identificación", () => { Identificacion.Save(); return ""; });
        Uno("la calidad real", () => { EspectroCargado?.Save(); return ""; });   // P4 (equipo audio_medido)

        foreach (var f in fallos) Logger.Err($"No se pudo guardar ({cuando}) {f}");
        if (fallos.Count > 0) Avisos.Poner("guardar", "No se ha podido guardar " + string.Join(" · ", fallos));
        else Avisos.Quitar("guardar");
        return fallos;
    }

    /// <summary>
    /// Anota en el registro y en los avisos un guardado que ha fallado (antes el error se perdía:
    /// ninguna llamada miraba lo que devolvía Save). Si ahora ha ido bien, quita el aviso que hubiera
    /// de una vez anterior. Devuelve el error.
    /// </summary>
    private string Registrar(string que, string error)
    {
        if (error.Length == 0) { Avisos.Quitar("guardar:" + que); return error; }
        Logger.Err($"No se pudo guardar {que}: {error}");
        Avisos.Poner("guardar:" + que, $"No se ha podido guardar {que}: {error}");
        return error;
    }

    /// <summary>Cada cuántas canciones guarda un bucle largo lo que lleva hecho.</summary>
    public const int CancionesEntrePuntos = 200;

    /// <summary>Y como mucho cada cuánto tiempo, aunque vaya despacio (una canción puede tardar un minuto en red).</summary>
    public static readonly TimeSpan TiempoEntrePuntos = TimeSpan.FromMinutes(5);

    private int _desdeElUltimoPunto;
    private readonly System.Diagnostics.Stopwatch _relojPunto = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>¿Toca guardar? Cada <see cref="CancionesEntrePuntos"/> canciones o cada <see cref="TiempoEntrePuntos"/>.</summary>
    internal static bool TocaPuntoDeControl(int cancionesDesdeElUltimo, TimeSpan desdeElUltimo)
        => cancionesDesdeElUltimo >= CancionesEntrePuntos || desdeElUltimo >= TiempoEntrePuntos;

    /// <summary>
    /// Punto de control para los bucles largos que van canción a canción (Enriquecer, No
    /// encontradas): se llama tras cada canción y guarda lo hecho cada ~200 canciones o cada 5
    /// minutos. Así un cierre a la fuerza, un corte de luz o un reinicio de Windows Update pierden
    /// como mucho eso, y no la tirada entera. Devuelve si ha guardado.
    ///
    /// Hay que llamarlo desde el bucle, entre canción y canción (cuando nada está escribiendo en la
    /// caché de análisis). El análisis, que es lo que más pesa, se escribe fuera del hilo de la
    /// interfaz mientras el bucle espera.
    /// </summary>
    public async Task<bool> PuntoDeControlAsync()
    {
        _desdeElUltimoPunto++;
        if (!TocaPuntoDeControl(_desdeElUltimoPunto, _relojPunto.Elapsed)) return false;
        _desdeElUltimoPunto = 0;
        _relojPunto.Restart();

        // Lo que pesa (el análisis son varios MB; las huellas, más de cien), fuera del hilo de la
        // interfaz; lo pequeño, que se toca desde la interfaz, aquí.
        var errorAnalisis = await Task.Run(() =>
        {
            var e = Analysis.Save();
            EsperarColaDeHuellas();
            Fingerprints.Save();
            Identificacion.Save();
            Loudness.Save();
            EspectroCargado?.Save();   // sigue al archivo como la sonoridad (SeguirArchivo)
            return e;
        }).ConfigureAwait(true);
        Registrar("la caché de análisis", errorAnalisis);
        Registrar("las casillas «Aplicar»", Marks.Save());
        Registrar("las canciones aplicadas", Applied.Save());
        Registrar("las canciones descartadas", Ignored.Save());
        Logger.Detail("Punto de control: lo hecho hasta ahora queda guardado.");
        return true;
    }

    // Hay que guardar la referencia: un Timer al que no apunta nadie lo recoge el recolector y deja de sonar.
    private Timer? _guardadoPeriodico;
    private int _vueltasGuardado;

    /// <summary>
    /// Las huellas, la identificación y la sonoridad se calculan en bucles en paralelo que no pasan
    /// por la interfaz. Sus cachés admiten que se guarden mientras se calculan, así que un reloj las
    /// guarda cada 30 s (la identificación, con respuestas que pueden haberse pagado, y la
    /// sonoridad, que pesan poco) y cada 5 min las huellas (153 MB en una biblioteca real). Si no
    /// hay nada nuevo, guardar no escribe nada.
    /// </summary>
    private void IniciarGuardadoPeriodico()
    {
        _guardadoPeriodico = new Timer(_ =>
        {
            try
            {
                Identificacion.Save();
                Loudness.Save();
                EspectroCargado?.Save();   // P4 (equipo audio_medido)
                if (++_vueltasGuardado % 10 == 0) Fingerprints.Save();
            }
            catch (Exception e) { Logger.Error("Guardado periódico de las cachés de audio", e); }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>Guarda las cachés de audio y los avisos aceptados tras una operación por lotes (fuera del hilo de la interfaz).</summary>
    private void GuardarCachesDeAudio()
    {
        EsperarColaDeHuellas();
        Fingerprints.Save();
        Identificacion.Save();
        Loudness.Save();
        // La calidad real también sigue al archivo (SeguirArchivo): se guarda con la sonoridad, y no
        // solo con el reloj de 30 s, para que un cierre forzado tras un lote no la deje atrás.
        EspectroCargado?.Save();
        Registrar("los avisos aceptados de Comprobar audio", AudioAceptadas.Save());
    }

    // --- Las cachés de audio siguen al archivo (hallazgos A2 y X4) ---

    /// <summary>
    /// La aplicación acaba de escribir SOLO etiquetas en <paramref name="origen"/> y quizá lo ha
    /// renombrado a <paramref name="destino"/>: el audio es el mismo. Las huellas, las respuestas de
    /// «Comprobar audio» (algunas pagadas en AudD), las medidas de sonoridad y los avisos ya
    /// descartados lo siguen, en vez de caducar por la fecha o perderse con la ruta. Visto en los
    /// datos reales: de 15.241 huellas guardadas solo valían 8.205, y unificar el género de 3.000
    /// canciones volvía a mandar a AudD las que AcoustID no reconocía.
    ///
    /// <paramref name="antes"/> es el sello tomado justo antes de escribir: solo se pone al día lo
    /// que valía entonces.
    /// </summary>
    public void SeguirArchivo(string origen, string destino, SelloArchivo antes)
    {
        var mueve = !string.Equals(origen, destino, StringComparison.Ordinal);
        if (mueve) SeguirSustituciones(origen, destino);
        if (mueve) SeguirExclusionDeVolumen(origen, destino);
        try
        {
            if (mueve)
            {
                Identificacion.Mover(origen, destino);
                Loudness.Mover(origen, destino);
                if (AudioAceptadas.Contains(origen))
                {
                    AudioAceptadas.Remove(origen);
                    AudioAceptadas.Add(destino);
                }
            }
            if (antes.Existe)
            {
                Identificacion.Retocar(destino, antes.M, antes.S);
                Loudness.Retocar(destino, antes.M, antes.S);
            }
        }
        catch (Exception e) { Logger.Error($"No se pudieron poner al día las cachés de audio de «{Path.GetFileName(destino)}»", e); }
        if (mueve || antes.Existe) SeguirEspectro(origen, destino, antes);   // P4 (equipo audio_medido)

        if (!mueve && !antes.Existe) return;
        EnColaDeHuellas(() =>
        {
            if (mueve) Fingerprints.Mover(origen, destino);
            if (antes.Existe) Fingerprints.Retocar(destino, antes.M, antes.S);
        });
    }

    /// <summary>
    /// Cómo se lleva un cambio de la configuración al hilo de la interfaz, que es donde se lee y se toca
    /// (Volumen añade y quita exclusiones ahí). SeguirArchivo llega también desde hilos de fondo.
    /// </summary>
    internal Action<Action> EnLaInterfaz { get; set; } = a => Avalonia.Threading.Dispatcher.UIThread.Post(a);

    /// <summary>
    /// Una canción excluida del ajuste de volumen sigue excluida al renombrarla o moverla. La exclusión
    /// va por ruta exacta: sin esto, tras renombrarla (Enriquecer, el Editor, la Bandeja…) su medida de
    /// sonoridad pasaba a la ruta nueva, la fila volvía a salir sin excluir y con «Ajustar» marcada, y
    /// el siguiente «Ajustar volumen» la cambiaba en contra de lo que pidió el DJ.
    /// </summary>
    private void SeguirExclusionDeVolumen(string origen, string destino)
    {
        // Sin exclusiones (lo normal) no se molesta a la interfaz por cada renombrado de un lote.
        if (Config is not { VolumenCancionesExcluidas.Count: > 0 }) return;
        EnLaInterfaz(() =>
        {
            if (!CambiarRutaExcluida(Config.VolumenCancionesExcluidas, origen, destino)) return;
            SaveConfig();
            Logger.Detail($"Volumen: «{Path.GetFileName(destino)}» sigue excluida del ajuste con su nombre nuevo.");
        });
    }

    /// <summary>
    /// Cambia <paramref name="origen"/> por <paramref name="destino"/> en una lista de rutas excluidas
    /// (sin distinguir mayúsculas, como la compara Volumen). Si el destino ya estaba, solo se quita el
    /// origen. Devuelve si cambió algo.
    /// </summary>
    internal static bool CambiarRutaExcluida(List<string> lista, string origen, string destino)
    {
        var i = lista.FindIndex(r => string.Equals(r, origen, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        // Un cambio solo de mayúsculas encuentra el origen como destino: entonces se sustituye.
        var j = lista.FindIndex(r => string.Equals(r, destino, StringComparison.OrdinalIgnoreCase));
        if (j >= 0 && j != i) lista.RemoveAt(i);
        else if (string.Equals(lista[i], destino, StringComparison.Ordinal)) return false;
        else lista[i] = destino;
        return true;
    }

    /// <summary>
    /// La aplicación acaba de ajustar el volumen del archivo. Eso sí cambia el audio de cara a la
    /// sonoridad (la pone al día quien ajusta, con la ganancia aplicada), pero no la huella ni QUÉ
    /// canción es: esas dos siguen valiendo.
    /// </summary>
    public void VolumenAjustado(string ruta, SelloArchivo antes)
    {
        if (!antes.Existe) return;
        try { Identificacion.Retocar(ruta, antes.M, antes.S); }
        catch (Exception e) { Logger.Error($"No se pudo poner al día la identificación de «{Path.GetFileName(ruta)}»", e); }
        SeguirEspectro(ruta, ruta, antes);   // P4 (equipo audio_medido): el volumen no mueve el corte
        EnColaDeHuellas(() => Fingerprints.Retocar(ruta, antes.M, antes.S));
    }

    /// <summary>
    /// Un deshacer acaba de renombrar o reescribir un archivo (lo avisa UndoEngine en el hilo que
    /// deshace). Es lo mismo que al escribir, al revés: el audio de un registro de etiquetas es el
    /// mismo; el de uno de volumen cambia, pero no la canción ni su huella.
    ///
    /// POR QUÉ (hallazgo datos-3). Sin esto, deshacer los géneros por reglas de 2.597 canciones, o una
    /// limpieza de packs, hacía caducar sus huellas y sus respuestas de «Comprobar audio» por la fecha,
    /// y la siguiente comprobación volvía a mandar al servicio de pago las que AcoustID no reconoce.
    /// </summary>
    private void SeguirDeshecho(ArchivoDeshecho a) => SeguirDeshecho(a, SeguirArchivo, VolumenDeshecho);

    /// <summary>
    /// Un deshacer acaba de devolver el volumen del archivo <paramref name="db"/> dB. Como al ajustar
    /// (<see cref="VolumenAjustado"/>), la huella y la identificación siguen valiendo; y además la
    /// medida de sonoridad se desplaza con los pasos que se conocen, con su ganancia aplicada al día.
    ///
    /// POR QUÉ. Antes la medida solo caducaba por la fecha. La pestaña Volumen seguía con el nivel de
    /// antes del deshacer, y el siguiente análisis tenía que volver a medir el archivo y deducir los
    /// pasos con una tolerancia (ver LoudnessScanner.GananciaTrasMedir).
    ///
    /// Solo <paramref name="dbAnotados"/> se descuenta de la ganancia aplicada: es lo que deshace
    /// ajustes que la anotaron (ver UndoRecord.GananciaAnotada). Un ajuste de BeatTag 1.10 no la anotó,
    /// y descontarlo inventaba una ganancia con el signo cambiado.
    /// </summary>
    private void VolumenDeshecho(string ruta, SelloArchivo antes, double db, double dbAnotados)
    {
        VolumenAjustado(ruta, antes);
        if (!antes.Existe) return;
        try { Loudness.Reajustar(ruta, antes.M, antes.S, db, dbAnotados); }
        catch (Exception e) { Logger.Error($"No se pudo poner al día la medida de volumen de «{Path.GetFileName(ruta)}»", e); }
    }

    /// <summary>La regla de <see cref="SeguirDeshecho(ArchivoDeshecho)"/>, aparte para poder probarla sin el motor.</summary>
    /// <param name="a">Lo que ha hecho el deshacer con el archivo.</param>
    /// <param name="seguir">Las cachés siguen al archivo (renombrado, etiquetas): origen, destino y sello de antes.</param>
    /// <param name="volumen">
    /// Se ha devuelto el volumen: ruta, sello de antes, los dB aplicados y la parte de ellos que deshace
    /// ajustes anotados en la ganancia aplicada.
    /// </param>
    internal static void SeguirDeshecho(ArchivoDeshecho a, Action<string, string, SelloArchivo> seguir,
                                        Action<string, SelloArchivo, double, double> volumen)
    {
        var sello = a.ExistiaAntes ? new SelloArchivo(a.FechaAntes, a.TamanoAntes, true) : default;
        if (a.Volumen)
        {
            if (!string.Equals(a.Desde, a.Hasta, StringComparison.Ordinal)) seguir(a.Desde, a.Hasta, default);
            volumen(a.Hasta, sello, a.PasosVolumen * Etiquetador.Core.Analysis.Mp3Gain.DbPerStep,
                    a.PasosAnotados * Etiquetador.Core.Analysis.Mp3Gain.DbPerStep);
        }
        // Solo renombrado (sin sello): las entradas se mueven y nada más, porque la fecha no cambia.
        else seguir(a.Desde, a.Hasta, a.Etiquetas ? sello : default);
    }

    /// <summary>
    /// «Deshacer última», con las cachés de audio al día al terminar. Fuera del hilo de la interfaz.
    /// </summary>
    public UndoResult DeshacerUltima() => TrasDeshacer(Undo.UndoLastRun());

    /// <summary>
    /// Deshace desde el historial los cambios elegidos de una ejecución (null: todos), con las cachés
    /// de audio al día al terminar. Fuera del hilo de la interfaz.
    /// </summary>
    public UndoResult DeshacerCambios(string manifiesto, IReadOnlyCollection<string>? huellas)
        => TrasDeshacer(Undo.DeshacerCambios(manifiesto, huellas));

    /// <summary>
    /// Guarda lo ligero (identificación, que puede llevar respuestas pagadas, y sonoridad) si el
    /// deshacer tocó algo. Las huellas (más de 150 MB en una biblioteca grande) las guarda el reloj
    /// cada cinco minutos y el cierre, como tras las herramientas del Asistente.
    /// </summary>
    private UndoResult TrasDeshacer(UndoResult res)
    {
        if (res.None || res.Tocadas.Count == 0) return res;
        GuardarCachesLigeras();
        return res;
    }

    /// <summary>
    /// Guarda las cachés de audio que pesan poco (identificación, que puede llevar respuestas pagadas,
    /// sonoridad y calidad real) y los avisos aceptados, tras una escritura por lotes que las ha hecho
    /// seguir al archivo: deshacer, o importar BPM y tonalidad de rekordbox. Las huellas las guardan el
    /// reloj y el cierre. Fuera del hilo de la interfaz.
    /// </summary>
    public void GuardarCachesLigeras()
    {
        try
        {
            Identificacion.Save();
            Loudness.Save();
            // Los seguimientos del espectro pueden esperar aún en la cola de las huellas (SeguirEspectro).
            bool enCola;
            lock (_cerrojoEspectro) enCola = _espectroEnCola > 0;
            if (enCola) EsperarColaDeHuellas();
            EspectroCargado?.Save();
            Registrar("los avisos aceptados de Comprobar audio", AudioAceptadas.Save());
        }
        catch (Exception e) { Logger.Error("Guardar las cachés de audio tras escribir por lotes", e); }
    }

    private readonly object _cerrojoHuellas = new();
    private Task _colaHuellas = Task.CompletedTask;

    /// <summary>
    /// Las huellas se ponen al día en segundo plano y en orden. Su caché se lee del disco la primera
    /// vez que hace falta, y MEDIDO: 153 MB, ~1,3 s (hallazgo A3); el Editor, Volumen y la Bandeja
    /// llaman desde el hilo de la interfaz, y la ventana no puede quedarse parada por eso.
    /// </summary>
    private void EnColaDeHuellas(Action accion)
    {
        lock (_cerrojoHuellas)
            _colaHuellas = _colaHuellas.ContinueWith(_ =>
            {
                try { accion(); }
                catch (Exception e) { Logger.Error("No se pudieron poner al día las huellas de audio", e); }
            }, TaskScheduler.Default);
    }

    /// <summary>Espera (como mucho un rato) a que las huellas pendientes de poner al día lo estén, antes de guardarlas.</summary>
    private void EsperarColaDeHuellas()
    {
        Task cola;
        lock (_cerrojoHuellas) cola = _colaHuellas;
        try { cola.Wait(TimeSpan.FromSeconds(10)); } catch { /* lo que falte se pone al día la próxima vez */ }
    }

    /// <summary>
    /// Aplica una propuesta (etiquetas, carátula y nombre) igual que <see cref="ApplyEngine.ApplyOneAsync"/>,
    /// y además hace que las cachés de audio sigan al archivo. Es lo que deben llamar Enriquecer, No
    /// encontradas e Incompletas en lugar de ir directamente a <see cref="Apply"/>.
    /// </summary>
    public async Task<ApplyOneResult> AplicarUnoAsync(ProcessResult info, bool over, FieldFlags fields,
        string coverMode, TagLib.Picture? pngPic, string? undoFile, string doneLogPath, CancellationToken ct = default,
        bool aplicada = false, string? origen = null)
    {
        var antes = SelloArchivo.De(info.FilePath);
        var res = await Apply.ApplyOneAsync(info, over, fields, coverMode, pngPic, undoFile, doneLogPath, ct, aplicada, origen).ConfigureAwait(false);
        SeguirArchivo(info.FilePath, res.FinalPath, antes);
        return res;
    }

    /// <summary>
    /// Devuelve a su archivo las fichas cuyo archivo ha cambiado de nombre o de sitio.
    ///
    /// Se hace FUERA del hilo de la interfaz y solo cuando aparecen fichas huérfanas NUEVAS
    /// (hallazgo I8). Antes, una sola ficha de una canción enviada a la papelera dejaba la condición
    /// «hay huérfanas» a true para siempre (las huérfanas no se borran, a propósito), y cada escaneo,
    /// cada envío a la papelera y cada renombrado volvían a leer TODOS los manifiestos de deshacer en
    /// el hilo de la interfaz. Datos reales: 177 manifiestos, 15 MB, 27.173 líneas y 7.761
    /// renombrados, y creciendo con cada ejecución.
    ///
    /// Aquí solo se copian las rutas de la biblioteca (unos milisegundos con 15.000 canciones);
    /// mirar el disco y leer los manifiestos va en segundo plano. Si llega otro cambio mientras tanto,
    /// se atiende al terminar, una sola vez y con la biblioteca más reciente.
    /// </summary>
    private void ReubicarFichas()
    {
        if (Fichas.Count == 0 || !Library.IsScanned) return;
        var enBiblioteca = new HashSet<string>(Library.Tracks.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase);
        var hiloInterfaz = SynchronizationContext.Current;
        lock (_cerrojoReubicar)
        {
            _reubicarCon = enBiblioteca;
            if (_reubicando) return;
            _reubicando = true;
        }
        _ = Task.Run(() => BucleReubicar(hiloInterfaz));
    }

    private readonly object _cerrojoReubicar = new();
    private HashSet<string>? _reubicarCon;
    private bool _reubicando;

    /// <summary>Huérfanas que ya se buscaron en los manifiestos sin encontrar adónde fueron.</summary>
    private IReadOnlySet<string> _huerfanasConocidas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private void BucleReubicar(SynchronizationContext? hiloInterfaz)
    {
        while (true)
        {
            HashSet<string> enBiblioteca;
            lock (_cerrojoReubicar)
            {
                if (_reubicarCon == null) { _reubicando = false; return; }
                enBiblioteca = _reubicarCon;
                _reubicarCon = null;
            }
            try
            {
                var r = ReubicarHuerfanas(Fichas, ruta => enBiblioteca.Contains(ruta) || File.Exists(ruta),
                                          () => RekordboxRelocator.ReadRenames(Paths.UndoDir), _huerfanasConocidas);
                _huerfanasConocidas = r.Huerfanas;
                if (r.Movidas == 0) continue;

                var err = Fichas.Guardar();
                Logger.Detail($"Fichas de DJ: {r.Movidas} recolocada(s) tras renombrar archivos."
                            + (err.Length > 0 ? $" ⚠ No se pudo guardar: {err}" : ""));
                if (err.Length > 0) Avisos.Poner("guardar:fichas", "No se han podido guardar las fichas de DJ: " + err);
                // Las páginas ya se recalcularon con el escaneo: hay que decirles que las fichas se movieron.
                if (hiloInterfaz != null) hiloInterfaz.Post(_ => AvisarFichasCambiadas(), null);
                else AvisarFichasCambiadas();
            }
            catch (Exception e) { Logger.Error("Error al recolocar las fichas de DJ", e); }
        }
    }

    /// <summary>Lo que dio buscar adónde fueron las fichas huérfanas.</summary>
    /// <param name="Movidas">Fichas devueltas a su archivo.</param>
    /// <param name="Huerfanas">Las que siguen sin archivo (para no volver a buscarlas mientras no cambie nada).</param>
    /// <param name="LeyoManifiestos">Si hizo falta leer los manifiestos de deshacer, que es lo caro.</param>
    internal sealed record Reubicacion(int Movidas, IReadOnlySet<string> Huerfanas, bool LeyoManifiestos);

    /// <summary>
    /// La regla de <see cref="ReubicarFichas"/>, aparte para poder probarla: solo se leen los
    /// manifiestos si hay alguna huérfana que no se hubiera buscado ya. Una huérfana ya buscada no
    /// puede aparecer de repente en un manifiesto nuevo: la aplicación solo anota renombrados de
    /// archivos que existen, y si su archivo volviera a existir ya no sería huérfana.
    /// </summary>
    internal static Reubicacion ReubicarHuerfanas(AlmacenFichas fichas, Func<string, bool> existe,
        Func<IReadOnlyDictionary<string, string>> leerRenombres, IReadOnlySet<string> conocidas)
    {
        var huerfanas = new HashSet<string>(fichas.Rutas.Where(r => !existe(r)), StringComparer.OrdinalIgnoreCase);
        if (huerfanas.Count == 0 || huerfanas.All(conocidas.Contains))
            return new Reubicacion(0, huerfanas, false);

        var movidas = fichas.Reubicar(leerRenombres(), existe);
        var quedan = movidas == 0
            ? huerfanas
            : new HashSet<string>(fichas.Rutas.Where(r => !existe(r)), StringComparer.OrdinalIgnoreCase);
        return new Reubicacion(movidas, quedan, true);
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

    /// <param name="UndoErr">Cambios que están en disco y NO quedaron anotados para deshacer ("" si ninguno).</param>
    /// <param name="Detenido">
    /// Por qué se paró antes de terminar porque el historial para deshacer no se podía escribir, o ""
    /// (hallazgo M10). Lo que no se llegó a anotar no se ha tocado.
    /// </param>
    public sealed record ResultadoGeneros(int Escritas, int SinCambio, IReadOnlyList<string> Fallos, string UndoErr, bool Cancelado,
                                          string Detenido = "");

    /// <summary>
    /// Escribe el género nuevo en cada archivo («» lo quita). Como toda escritura de etiquetas, queda
    /// anotada para deshacer, así que cancelar a medias deja reversible lo ya escrito; y si el
    /// historial no se puede escribir, no se toca ningún archivo más. Hay que llamarla fuera del hilo
    /// de la interfaz y con el reproductor parado.
    /// </summary>
    public ResultadoGeneros AplicarGeneros(IReadOnlyList<(string Ruta, string Genero)> cambios,
                                           IProgress<double>? progreso, CancellationToken ct)
    {
        ResultadoGeneros res;
        using (var manifiesto = ManifiestoDeshacer.Abrir(Paths.NuevoManifiesto(), out var error))
            res = EscribirGeneros(cambios, manifiesto, error, SeguirArchivo, Logger, progreso, ct);
        GuardarCachesDeAudio();
        return res;
    }

    /// <summary>
    /// Un género propuesto como lista se escribe como VARIOS géneros, uno por valor (hallazgo I11).
    /// «Unificar géneros» propone «Hip Hop; Latino» para una etiqueta que tenía dos, y antes se
    /// escribía como un único valor con ese texto: BeatTag lo leía igual, pero rekordbox, Serato o el
    /// Explorador veían un solo género llamado «Hip Hop; Latino».
    /// </summary>
    internal static string[] GenerosAEscribir(string genero)
        => GenerosIa.ValoresParaEscribir(genero);   // la misma regla que propone «Unificar géneros»

    /// <summary>
    /// El bucle de <see cref="AplicarGeneros"/>, aparte para poder probar qué pasa si el historial
    /// falla. <paramref name="historial"/> null significa que no se pudo abrir (el motivo, en
    /// <paramref name="errorHistorial"/>): entonces no se toca nada.
    /// </summary>
    internal static ResultadoGeneros EscribirGeneros(IReadOnlyList<(string Ruta, string Genero)> cambios,
        IHistorialDeshacer? historial, string errorHistorial, Action<string, string, SelloArchivo>? seguir,
        Logger? log, IProgress<double>? progreso, CancellationToken ct)
    {
        int escritas = 0, sinCambio = 0;
        var fallos = new List<string>();
        if (historial == null)
            return new ResultadoGeneros(0, 0, fallos, "", true, SinHistorial(errorHistorial, log));

        for (var i = 0; i < cambios.Count; i++)
        {
            if (ct.IsCancellationRequested)
                return new ResultadoGeneros(escritas, sinCambio, fallos, "", true);

            var (ruta, genero) = cambios[i];
            try
            {
                SelloArchivo? sello = null;
                string[] antes = Array.Empty<string>(), despues = Array.Empty<string>(), previstos = Array.Empty<string>();
                using (var f = Tagging.AbrirParaEscribir(ruta))   // TagLib no escribe AIFF-C: así el motivo sale en español
                {
                    antes = f.Tag.Genres ?? Array.Empty<string>();
                    despues = GenerosAEscribir(genero);
                    // Lo que se leerá después de escribir: en ID3v2.3 «Dance/Electrónica» vuelve
                    // como dos géneros (ver Relectura). Si ya es lo que tiene, no hay nada que escribir.
                    previstos = Relectura.Generos(f, despues);
                    if (antes.SequenceEqual(despues, StringComparer.Ordinal) || antes.SequenceEqual(previstos, StringComparer.Ordinal)) sinCambio++;
                    else
                    {
                        // Se anota ANTES de tocar el archivo: si el historial ya no se puede escribir,
                        // este archivo y los siguientes se quedan como estaban. Que quede anotado un
                        // cambio que luego falle no estorba: deshacer solo devuelve un campo si sigue
                        // teniendo lo que escribió la aplicación. Se anota lo que se LEERÁ, que es con
                        // lo que deshacer lo compara.
                        var anotado = historial.Anotar(new UndoRecord
                        {
                            OrigPath = ruta, FinalPath = ruta, Renamed = false,
                            Fields = new Dictionary<string, FieldChange> { ["Genre"] = FieldChange.Arr(antes, previstos) },
                            Origen = nameof(TipoEjecucion.Generos),
                        });
                        if (anotado.Length > 0)
                            return new ResultadoGeneros(escritas, sinCambio, fallos, "", true, Parada(ruta, anotado, log));

                        sello = SelloArchivo.De(ruta);
                        f.Tag.Genres = despues;
                        f.Save();
                        escritas++;
                    }
                }
                // Con el archivo ya cerrado, que es cuando su fecha queda al día.
                if (sello is { } s)
                {
                    seguir?.Invoke(ruta, ruta, s);

                    // La predicción se comprueba: si el archivo guardó otra cosa, se anota un segundo
                    // registro con lo que de verdad quedó, que deshacer procesa primero (la misma
                    // solución que EtiquetasPorReglas.Corregidos para el Asistente).
                    if (Relectura.DebeReleerse(despues, previstos))
                    {
                        string[]? quedo = null;
                        try { using var releido = TagLib.File.Create(ruta); quedo = releido.Tag.Genres ?? Array.Empty<string>(); }
                        catch (Exception e) { log?.Log($"Género: {Path.GetFileName(ruta)} se escribió, pero no se pudo volver a leer para comprobarlo: {e.Message}", LogKind.Err); }
                        if (quedo != null && !quedo.SequenceEqual(previstos, StringComparer.Ordinal))
                        {
                            var corregido = historial.Anotar(new UndoRecord
                            {
                                OrigPath = ruta, FinalPath = ruta, Renamed = false,
                                Fields = new Dictionary<string, FieldChange> { ["Genre"] = FieldChange.Arr(antes, quedo) },
                                Origen = nameof(TipoEjecucion.Generos),
                            });
                            if (corregido.Length > 0)
                            {
                                var sinAnotar = $"«{Path.GetFileName(ruta)}» quedó escrito, pero lo que guardó el archivo no se pudo anotar tal cual ({corregido}): deshacerlo no lo devolverá";
                                log?.Err("Género: " + sinAnotar);
                                return new ResultadoGeneros(escritas, sinCambio, fallos, sinAnotar, true, Parada(ruta, corregido, log));
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                fallos.Add(Path.GetFileName(ruta) + ": " + e.Message);
                log?.Log($"Género: {Path.GetFileName(ruta)}: {e.Message}", LogKind.Err);
            }
            progreso?.Report(100.0 * (i + 1) / cambios.Count);
        }
        log?.Detail($"Géneros unificados: {escritas} escritos · {sinCambio} ya estaban · {fallos.Count} fallos.");
        return new ResultadoGeneros(escritas, sinCambio, fallos, "", false);
    }

    /// <summary>El historial no se pudo ni abrir: no se ha tocado nada.</summary>
    internal static string SinHistorial(string error, Logger? log)
    {
        var texto = $"No se ha tocado ningún archivo: no se pudo crear el historial para deshacer ({error}). " +
                    "Revisa que la carpeta de datos tenga espacio libre y permiso de escritura.";
        log?.Err(texto);
        return texto;
    }

    /// <summary>El historial dejó de poder escribirse a mitad: se para en ese archivo.</summary>
    internal static string Parada(string ruta, string error, Logger? log)
    {
        var texto = $"Se ha detenido en «{Path.GetFileName(ruta)}»: no se pudo anotar en el historial para deshacer ({error}). " +
                    "Ese archivo y los siguientes se han quedado como estaban; lo anterior sí se puede deshacer. " +
                    "Revisa que la carpeta de datos tenga espacio libre y permiso de escritura.";
        log?.Err(texto);
        return texto;
    }

    /// <param name="UndoErr">Renombrados que están hechos y NO quedaron anotados para deshacer ("" si ninguno).</param>
    /// <param name="Detenido">Por qué se paró porque el historial no se podía escribir, o "" (hallazgo M10).</param>
    public sealed record ResultadoRenombrado(IReadOnlyList<(string Origen, string Destino)> Hechos, IReadOnlyList<string> Fallos,
                                             string UndoErr, bool Cancelado, string Detenido = "");

    /// <summary>
    /// Renombra archivos dentro de su carpeta. Cada renombrado queda anotado para deshacer en cuanto
    /// se hace, y con eso también sirve para reparar la colección de rekordbox. La ficha de DJ y las
    /// cachés de audio siguen al archivo. Si el historial no se puede escribir, el que no quedó
    /// anotado vuelve a su nombre y no se renombra ninguno más.
    ///
    /// A diferencia de Enriquecer, si el nombre ya existe NO se añade «(2)»: dos archivos que acaban
    /// llamándose igual suelen ser la misma canción, y eso se dice en vez de esconderlo.
    /// Hay que llamarla fuera del hilo de la interfaz y con el reproductor parado.
    /// </summary>
    public ResultadoRenombrado RenombrarArchivos(IReadOnlyList<(string Ruta, string NuevoNombre)> cambios,
                                                 IProgress<double>? progreso, CancellationToken ct)
    {
        ResultadoRenombrado res;
        using (var manifiesto = ManifiestoDeshacer.Abrir(Paths.NuevoManifiesto(), out var error))
            res = Renombrar(cambios, manifiesto, error, (de, a) =>
            {
                Fichas.Mover(de, a);
                SeguirArchivo(de, a, default);   // renombrar no cambia ni la fecha ni el tamaño
            }, Logger, progreso, ct);

        if (res.Hechos.Count > 0)
        {
            var err = Fichas.Guardar();
            if (err.Length > 0) Registrar("las fichas de DJ", err);
        }
        GuardarCachesDeAudio();
        return res;
    }

    /// <summary>El bucle de <see cref="RenombrarArchivos"/>, aparte para poder probar qué pasa si el historial falla.</summary>
    internal static ResultadoRenombrado Renombrar(IReadOnlyList<(string Ruta, string NuevoNombre)> cambios,
        IHistorialDeshacer? historial, string errorHistorial, Action<string, string>? alRenombrar,
        Logger? log, IProgress<double>? progreso, CancellationToken ct)
    {
        var hechos = new List<(string, string)>();
        var fallos = new List<string>();
        if (historial == null)
            return new ResultadoRenombrado(hechos, fallos, "", true, SinHistorial(errorHistorial, log));

        for (var i = 0; i < cambios.Count; i++)
        {
            if (ct.IsCancellationRequested) return new ResultadoRenombrado(hechos, fallos, "", true);
            var (ruta, nuevo) = cambios[i];
            var nombre = Path.GetFileName(ruta);
            var t = Etiquetador.Core.Ai.RenombradoIa.RenombrarArchivo(ruta, nuevo);
            if (t.Ok)
            {
                // El destino solo se sabe después de renombrar, así que aquí se anota después; si no
                // se puede, se le devuelve su nombre y se para.
                var anotado = historial.Anotar(new UndoRecord
                {
                    OrigPath = t.Origen, FinalPath = t.Destino, Renamed = true, Origen = nameof(TipoEjecucion.Renombrado),
                });
                if (anotado.Length > 0)
                {
                    try { File.Move(t.Destino, t.Origen); }
                    catch (Exception e)
                    {
                        hechos.Add((t.Origen, t.Destino));
                        alRenombrar?.Invoke(t.Origen, t.Destino);
                        var sinVuelta = $"«{Path.GetFileName(t.Destino)}» se renombró, pero no quedó anotado ni se pudo devolver a su nombre ({e.Message})";
                        log?.Err("Renombrado con IA: " + sinVuelta);
                        return new ResultadoRenombrado(hechos, fallos, sinVuelta, true, Parada(ruta, anotado, log));
                    }
                    return new ResultadoRenombrado(hechos, fallos, "", true, Parada(ruta, anotado, log));
                }
                hechos.Add((t.Origen, t.Destino));
                alRenombrar?.Invoke(t.Origen, t.Destino);
                log?.Detail($"Renombrado con IA: «{nombre}» → «{Path.GetFileName(t.Destino)}»");
            }
            else
            {
                fallos.Add($"{nombre}: {t.Error}");
                log?.Log($"Renombrado con IA: {nombre}: {t.Error}", LogKind.Err);
            }
            progreso?.Report(100.0 * (i + 1) / cambios.Count);
        }
        return new ResultadoRenombrado(hechos, fallos, "", false);
    }

    /// <summary>Borra la ficha de cada archivo. Devuelve "" o el error.</summary>
    public string BorrarFichas(IReadOnlyList<string> rutas)
    {
        foreach (var r in rutas) Fichas.Quitar(r);
        var err = Fichas.Guardar();
        if (err.Length > 0) Logger.Err("No se pudieron guardar las fichas de DJ: " + err);
        return err;
    }

    /// <param name="UndoErr">Cambios que están en disco y NO quedaron anotados para deshacer ("" si ninguno).</param>
    /// <param name="Detenido">Por qué se paró porque el historial no se podía escribir, o "" (hallazgo M10).</param>
    public sealed record ResultadoVolcado(int Escritas, int SinCambio, IReadOnlyList<string> Fallos, string UndoErr,
                                          string Detenido = "");

    /// <summary>
    /// Copia la ficha de cada archivo a su comentario, conservando lo que el comentario ya tenía.
    ///
    /// Es la única acción de las fichas que toca la música, y por eso queda anotada para deshacer
    /// como cualquier otra escritura: cada cambio se anota ANTES de hacerlo, y si el historial no se
    /// puede escribir no se toca ningún archivo más. Hay que llamarla fuera del hilo de la interfaz y
    /// con el reproductor parado (<see cref="ReleaseAudio"/>).
    /// </summary>
    public ResultadoVolcado VolcarFichasAlComentario(IReadOnlyList<string> rutas)
    {
        ResultadoVolcado res;
        using (var manifiesto = ManifiestoDeshacer.Abrir(Paths.NuevoManifiesto(), out var error))
            res = VolcarComentarios(rutas, r => Fichas.Obtener(r), manifiesto, error, SeguirArchivo, Logger);
        GuardarCachesDeAudio();
        return res;
    }

    /// <summary>El bucle de <see cref="VolcarFichasAlComentario"/>, aparte para poder probar qué pasa si el historial falla.</summary>
    internal static ResultadoVolcado VolcarComentarios(IReadOnlyList<string> rutas, Func<string, FichaDj?> fichaDe,
        IHistorialDeshacer? historial, string errorHistorial, Action<string, string, SelloArchivo>? seguir, Logger? log)
    {
        int escritas = 0, sinCambio = 0;
        var fallos = new List<string>();
        if (historial == null)
            return new ResultadoVolcado(0, 0, fallos, "", SinHistorial(errorHistorial, log));

        foreach (var ruta in rutas)
        {
            try
            {
                SelloArchivo? sello = null;
                var ficha = fichaDe(ruta) ?? new FichaDj();
                using (var f = Tagging.AbrirParaEscribir(ruta))   // TagLib no escribe AIFF-C: así el motivo sale en español
                {
                    var antes = f.Tag.Comment ?? "";
                    var despues = Etiquetador.Core.Dj.Fichas.ComentarioCombinado(antes, ficha);
                    if (despues == antes) sinCambio++;
                    else
                    {
                        // Antes de tocar el archivo, como en los géneros.
                        var anotado = historial.Anotar(new UndoRecord
                        {
                            OrigPath = ruta, FinalPath = ruta, Renamed = false,
                            Fields = new Dictionary<string, FieldChange> { ["Comment"] = FieldChange.Str(antes, despues) },
                            Origen = nameof(TipoEjecucion.Comentario),
                        });
                        if (anotado.Length > 0)
                            return new ResultadoVolcado(escritas, sinCambio, fallos, "", Parada(ruta, anotado, log));

                        sello = SelloArchivo.De(ruta);
                        f.Tag.Comment = despues.Length == 0 ? null : despues;
                        f.Save();
                        escritas++;
                    }
                }
                if (sello is { } s) seguir?.Invoke(ruta, ruta, s);
            }
            catch (Exception e)
            {
                fallos.Add(Path.GetFileName(ruta) + ": " + e.Message);
                log?.Log($"Ficha al comentario: {Path.GetFileName(ruta)}: {e.Message}", LogKind.Err);
            }
        }
        log?.Detail($"Fichas al comentario: {escritas} escritas · {sinCambio} ya estaban al día · {fallos.Count} fallos.");
        return new ResultadoVolcado(escritas, sinCambio, fallos, "");
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
        Isrc = Config.WriteIsrc,   // C14 (equipo catalogo_bandeja): opción de Ajustes, desactivada por defecto
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
        // Se cuentan los fallos de red de ESTA canción (va por flujo asíncrono, así que vale también
        // con varias en paralelo).
        using var medicion = Api.MedirFallos();
        var r = await Processor.ProcessAsync(filePath, isAcapella: false, opts, ct).ConfigureAwait(false);
        if (!SeGuardaEnCache(r, medicion.Fallos))
        {
            Logger.Detail($"    {Path.GetFileName(filePath)}: no encontrada con {medicion.Fallos} fallo(s) de red; no se guarda, se volverá a buscar.");
            return r;
        }
        Analysis.Set(filePath, sig, r);
        AnotarLetraDelCatalogo(filePath, r);   // C14 (equipo catalogo_bandeja)
        return r;
    }

    /// <summary>
    /// ¿Se guarda este resultado en la caché de análisis? Un «no encontrada» al que le fallaron
    /// peticiones (sin conexión, tiempo agotado, 503, cuota agotada) no dice nada de la canción: no
    /// se pudo preguntar. Guardarlo la dejaba como no encontrada para siempre, porque «Analizar» no
    /// vuelve a mirar lo que ya está en la caché (hallazgo C8). Visto en los registros: 98 respuestas
    /// «musicbrainz.org HTTP 503» y 23 «se abandona tras 2 intentos», todas en canciones que acabaron
    /// guardadas como no encontradas.
    /// </summary>
    internal static bool SeGuardaEnCache(ProcessResult r, int fallosDeRed)
        => r.Found || r.Skip || r.CleanOnly || fallosDeRed == 0;

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
        Registrar("la caché de análisis", Analysis.Save());
        Ignored.Add(filePath);
        Registrar("las canciones descartadas", Ignored.Save());
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
        Registrar("la caché de análisis", Analysis.Save());
        Registrar("las canciones descartadas", Ignored.Save());
    }

    /// <summary>Vuelve a tener en cuenta todas las canciones descartadas.</summary>
    public void ClearIgnored() => Ignored.Clear();

    /// <summary>Vuelve a tener en cuenta SOLO las canciones indicadas.</summary>
    public void RestoreIgnored(IEnumerable<string> filePaths)
    {
        foreach (var p in filePaths) Ignored.Remove(p);
        Registrar("las canciones descartadas", Ignored.Save());
    }

    // --- Letra según el catálogo (C14, equipo catalogo_bandeja) ---
    //
    // Deezer dice si cada pista tiene letra explícita, y el análisis lo guarda en la caché. La columna
    // «Explícito», las estadísticas y la letra de la ficha (cuando el DJ no la ha indicado) lo usan a
    // través de ExplicitDetector, que solo mira en memoria: aquí se le da al arrancar y se le pone al
    // día con cada canción analizada. Visto en revisión: sin esto el dato no llegaba a ninguna parte.

    /// <summary>
    /// Todo lo que dice la caché de análisis. Solo al arrancar, recién cargada: recorre la caché
    /// entera, y no admite que se recorra mientras un análisis le añade resultados.
    /// </summary>
    private void PonerLetraDelCatalogo()
    {
        try { Etiquetador.Core.Analysis.ExplicitDetector.PonerCatalogo(Analysis.LetraDelCatalogo()); }
        catch (Exception e) { Logger.Error("No se pudo leer la letra explícita guardada en la caché de análisis", e); }
    }

    /// <summary>Lo que acaba de decir el catálogo de una canción analizada (también para el nombre que tendrá al aplicarla).</summary>
    private static void AnotarLetraDelCatalogo(string ruta, ProcessResult r)
    {
        var primera = true;
        foreach (var p in AnalysisCache.RutasDeLaLetra(ruta, r))
        {
            Etiquetador.Core.Analysis.ExplicitDetector.AnotarCatalogo(p, r.Explicit, sustituir: primera);
            primera = false;
        }
    }

    // --- Sustituir desde la Bandeja (I14, equipo catalogo) ---

    /// <summary>
    /// Al arrancar: si la aplicación se cerró a mitad de una sustitución, la descarga se quedó con un
    /// nombre oculto («.beattag-sustituir-…») en la carpeta de la biblioteca, donde el escáner no la
    /// ve. Antes solo se recogía al analizar la bandeja; quien no volvía a ella no la encontraba. Se
    /// devuelve ahora a la bandeja y se dice en la ventana. En segundo plano (mover entre dos discos
    /// puede tardar), y sin coste en el caso de siempre: la carpeta de lo apuntado está vacía.
    /// </summary>
    private void RecuperarSustitucionesCortadas()
    {
        var diario = SustitucionBandeja.CarpetaDiario(Paths.DataDir);
        if (!SustitucionBandeja.HayQueRecuperar(diario)) return;
        _ = Task.Run(() =>
        {
            try
            {
                var avisos = SustitucionBandeja.Recuperar(diario);
                foreach (var a in avisos) Logger.Err("Bandeja: " + a);
                if (avisos.Count > 0) Avisos.Poner("bandeja:sustitucion-cortada", string.Join(" ", avisos));
            }
            catch (Exception e) { Logger.Error("No se pudo revisar si quedó alguna sustitución a medias", e); }
        });
    }

    private AlmacenSustituciones? _sustituciones;
    private readonly object _cerrojoSustituciones = new();

    /// <summary>
    /// Las sustituciones hechas desde la Bandeja, con lo que había de la copia antigua (ver
    /// <see cref="AlmacenSustituciones"/>). Se lee la primera vez que hace falta: casi siempre está
    /// vacío y no merece la pena retrasar el arranque. Con cerrojo: también lo usa
    /// <see cref="SeguirArchivo"/>, que llega desde los hilos de fondo de aplicar y deshacer.
    /// </summary>
    public AlmacenSustituciones Sustituciones
    {
        get
        {
            if (Volatile.Read(ref _sustituciones) is { } ya) return ya;
            lock (_cerrojoSustituciones)
            {
                if (_sustituciones != null) return _sustituciones;
                var leido = new AlmacenSustituciones(AlmacenSustituciones.RutaEn(Paths.DataDir));
                if (leido.AvisoCarga.Length > 0)
                {
                    Logger.Err(leido.AvisoCarga);
                    Avisos.Poner("carga:sustituciones", leido.AvisoCarga);
                }
                Volatile.Write(ref _sustituciones, leido);
                return leido;
            }
        }
    }

    /// <summary>
    /// Un archivo que BeatTag acaba de mover o renombrar: el destino de las sustituciones hechas lo
    /// sigue, y si es la descarga que deshacer devuelve a la bandeja, la sustitución queda deshecha
    /// (<see cref="AlmacenSustituciones.Seguir"/>). Solo se guarda si cambia algo, que es raro.
    /// </summary>
    private void SeguirSustituciones(string origen, string destino)
    {
        try
        {
            if (SeguirSustituciones(Sustituciones, origen, destino) is { } error)
                Registrar("las sustituciones de la bandeja", error);
        }
        catch (Exception e) { Logger.Error("No se pudo poner al día una sustitución de la bandeja tras mover su archivo", e); }
    }

    /// <summary>
    /// La regla de <see cref="SeguirSustituciones(string, string)"/>, aparte para poder probarla sin
    /// el motor (que solo se construye sobre la carpeta de datos real de Documentos): el almacén sigue
    /// el traslado y, si ha cambiado algo, se guarda. Devuelve null si no había nada que seguir, o lo
    /// que devolvió el guardado ("" si fue bien).
    /// </summary>
    internal static string? SeguirSustituciones(AlmacenSustituciones almacen, string origen, string destino)
        => almacen.Count > 0 && almacen.Seguir(origen, destino) ? almacen.Guardar() : null;

    /// <summary>
    /// Se ha sustituido una copia de la biblioteca: lo calculado sobre esas rutas (una propuesta de
    /// Enriquecer con su casilla marcada) se hizo con el archivo anterior y ya no vale.
    /// </summary>
    public event Action<IReadOnlyCollection<string>>? ArchivosSustituidos;
    public void AvisarArchivosSustituidos(IReadOnlyCollection<string> rutas) => ArchivosSustituidos?.Invoke(rutas);

    /// <summary>
    /// Con cada cambio de la biblioteca, mira si se ha deshecho alguna sustitución o si ha vuelto de
    /// la papelera la copia antigua de una deshecha, y devuelve a cada archivo su ficha y lo decidido
    /// sobre él. Va ANTES que <see cref="ReubicarFichas"/>: si no, esta seguiría la vuelta deshecha
    /// (destino → bandeja) y se llevaría a la descarga la ficha fusionada.
    ///
    /// En el hilo de la interfaz, pero sin nada pesado: sin sustituciones apuntadas no hace nada; con
    /// ellas, recorre una vez la lista de la biblioteca (sin tocar el disco) y solo mira en disco las
    /// pocas cuyo archivo ha dejado de estar (o ha vuelto a estar) en ella.
    /// </summary>
    private void ConciliarSustituciones()
    {
        if (!Library.IsScanned) return;
        try
        {
            var almacen = Sustituciones;
            if (almacen.Count == 0) return;
            var vigilar = new HashSet<string>(almacen.RutasAVigilar(), StringComparer.OrdinalIgnoreCase);
            var presentes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in Library.Tracks)
                if (vigilar.Contains(t.FilePath)) presentes.Add(t.FilePath);

            var c = almacen.Conciliar(Fichas, presentes.Contains, SustitucionesBandeja.Tamano, DateTime.Now);
            if (!c.HayCambios) return;

            var decisiones = SustitucionesBandeja.Asentar(c, Applied, Ignored, Marks, AudioAceptadas,
                                                         (de, a) => SeguirArchivo(de, a, default));
            foreach (var texto in SustitucionesBandeja.TextosRegistro(c)) Logger.Detail(texto);
            Registrar("las sustituciones de la bandeja", almacen.Guardar());
            if (c.Deshechas.Count == 0 && c.Recuperadas.Count == 0) return;

            Registrar("las fichas de DJ", Fichas.Guardar());
            if (decisiones)
            {
                Registrar("las canciones aplicadas", Applied.Save());
                Registrar("las canciones descartadas", Ignored.Save());
                Registrar("las casillas «Aplicar»", Marks.Save());
                Registrar("los avisos aceptados de Comprobar audio", AudioAceptadas.Save());
            }
            // Las páginas recalculan con este mismo cambio de la biblioteca; el aviso les llega después.
            if (SynchronizationContext.Current is { } hilo) hilo.Post(_ => AvisarFichasCambiadas(), null);
            else AvisarFichasCambiadas();
        }
        catch (Exception e) { Logger.Error("Error al devolver las fichas de una sustitución deshecha de la bandeja", e); }
    }

    // --- P4 (tanda 4, equipo audio_medido): calidad real por espectro ---
    //
    // La caché de los cortes medidos (espectro.json) se carga la primera vez que hace falta y no en el
    // arranque: quien nunca abre esa función no paga nada. Sigue al archivo igual que la sonoridad
    // (SeguirArchivo y VolumenAjustado llaman a SeguirEspectro) y se guarda con las demás cachés de
    // audio (GuardarTodo y el reloj de 30 s).

    private readonly object _cerrojoEspectro = new();
    private EspectroScanner? _espectro;

    /// <summary>
    /// Solo para cargar la caché: que dos peticiones a la vez no la lean dos veces. Va aparte de
    /// <see cref="_cerrojoEspectro"/> porque la carga (leer y deserializar de 1,5 a 3 MB) tarda, y ese
    /// otro cerrojo lo toman SeguirEspectro y EspectroCargado, a los que llama el Editor y Volumen desde
    /// el hilo de la interfaz: con un solo cerrojo, un seguimiento que llegara justo durante la carga
    /// dejaba la ventana esperando a que terminase.
    /// </summary>
    private readonly object _cerrojoCargaEspectro = new();

    /// <summary>Medidas del espectro (calidad real) con su caché. Se carga al pedirla por primera vez.</summary>
    public EspectroScanner Espectro
    {
        get
        {
            if (EspectroCargado is { } ya) return ya;
            lock (_cerrojoCargaEspectro)
            {
                if (EspectroCargado is { } otraYa) return otraYa;   // la cargó otro mientras se esperaba
                var cargada = new EspectroScanner(EspectroScanner.RutaCache(Paths), Logger);   // fuera de _cerrojoEspectro
                lock (_cerrojoEspectro) _espectro = cargada;
                return cargada;
            }
        }
    }

    /// <summary>La caché del espectro si ya se cargó; null si nadie la ha pedido aún (no hay nada que guardar).</summary>
    private EspectroScanner? EspectroCargado { get { lock (_cerrojoEspectro) return _espectro; } }

    /// <summary>Seguimientos del espectro que esperan en la cola de las huellas (bajo <see cref="_cerrojoEspectro"/>).</summary>
    private int _espectroEnCola;

    /// <summary>
    /// La medida del espectro acompaña al archivo que la aplicación acaba de renombrar, etiquetar o
    /// ajustar de volumen: nada de eso cambia hasta dónde llega el audio. Se carga la caché si existe
    /// en disco, aunque nadie la haya pedido en esta sesión: si no, una medida guardada ayer se quedaría
    /// apuntando al nombre antiguo.
    ///
    /// Con la caché ya en memoria se sigue al momento. Sin cargar, cargarla es leer y deserializar de
    /// 1,5 a 3 MB, y el Editor y Volumen llaman desde el hilo de la interfaz: eso va a la cola serie de
    /// las huellas, que conserva el orden de los traslados y que GuardarTodo espera antes de guardar.
    /// Mientras quede alguno en esa cola, los siguientes van detrás para no adelantarlo (un A→B en
    /// cola y un B→C directo dejarían la medida en B).
    /// </summary>
    private void SeguirEspectro(string origen, string destino, SelloArchivo antes)
    {
        EspectroScanner? cache;
        lock (_cerrojoEspectro)
        {
            cache = _espectroEnCola == 0 ? _espectro : null;
            if (cache == null) _espectroEnCola++;
        }
        if (cache != null)
        {
            try { cache.Seguir(origen, destino, antes); }
            catch (Exception e) { Logger.Error($"No se pudo poner al día la calidad real de «{Path.GetFileName(destino)}»", e); }
            return;
        }
        EnColaDeHuellas(() =>
        {
            try
            {
                if (EspectroCargado != null || File.Exists(EspectroScanner.RutaCache(Paths)))
                    Espectro.Seguir(origen, destino, antes);
            }
            catch (Exception e) { Logger.Error($"No se pudo poner al día la calidad real de «{Path.GetFileName(destino)}»", e); }
            finally { lock (_cerrojoEspectro) _espectroEnCola--; }
        });
    }
}
