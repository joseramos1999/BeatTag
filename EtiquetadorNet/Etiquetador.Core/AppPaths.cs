namespace Etiquetador.Core;

/// <summary>
/// Distribución de carpetas de datos. Comparte la MISMA carpeta que la app PowerShell
/// (Documentos\Etiquetador de Musica) para reutilizar caché, lib y fpcalc ya poblados,
/// pero usa un config propio (config.net.json) para no pisar el config.json del .ps1.
/// </summary>
public sealed class AppPaths
{
    public string DataDir { get; }
    public string LibDir { get; }
    public string LogsDir { get; }
    public string ReportsDir { get; }
    public string CacheDir { get; }
    public string UndoDir { get; }

    /// <summary>
    /// Copias de las carátulas frontales que la aplicación ha sustituido, para poder devolverlas
    /// al deshacer (contrato «Cover» de <see cref="Pipeline.UndoEngine.CoverField"/>).
    /// </summary>
    public string PortadasDir => Path.Combine(UndoDir, "portadas");
    public string ConfigPath { get; }
    public string DoneLog { get; }
    public string ArtistExceptionsPath { get; }
    public string ArtistAliasesPath { get; }
    public string ScanCachePath { get; }
    public string AnalysisCachePath { get; }
    public string LoudnessCachePath { get; }
    public string FingerprintCachePath { get; }

    /// <summary>Nombre de la caché de la calidad real por espectro (P4).</summary>
    public const string EspectroCacheNombre = "espectro.json";

    /// <summary>
    /// Caché de la calidad real por espectro (P4). Se declara aquí, y no solo en el escáner de la
    /// aplicación, para que la copia de seguridad (Core) la reconozca como caché y la deje fuera: si no,
    /// entraba como «Otros datos de BeatTag» y restaurar una copia antigua devolvía medidas viejas.
    /// </summary>
    public string EspectroCachePath { get; }

    /// <summary>Lo que resultó ser cada archivo al escuchar su audio. Cada entrada costó una consulta de pago.</summary>
    public string IdentificacionCachePath { get; }

    /// <summary>Avisos de «Comprobar audio» que el usuario ha revisado y dado por buenos.</summary>
    public string AudioAceptadasPath { get; }
    public string IgnoredPath { get; }

    /// <summary>Casillas "Aplicar" que el usuario ha tocado a mano en Enriquecer.</summary>
    public string ApplyMarksPath { get; }

    /// <summary>Fichas de DJ (energía, momento, ambiente…). Trabajo hecho a mano: no es una caché.</summary>
    public string FichasDjPath { get; }

    /// <summary>Colecciones inteligentes guardadas.</summary>
    public string ColeccionesPath { get; }

    /// <summary>Estado de cada canción de la bandeja de entrada.</summary>
    public string BandejaPath { get; }
    public string AppliedPath { get; }

    public AppPaths(string? dataDir = null)
    {
        DataDir = dataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Etiquetador de Musica");
        LibDir = Path.Combine(DataDir, "lib");
        LogsDir = Path.Combine(DataDir, "logs");
        ReportsDir = Path.Combine(DataDir, "informes");
        CacheDir = Path.Combine(DataDir, "cache");
        UndoDir = Path.Combine(DataDir, "deshacer");
        ConfigPath = Path.Combine(DataDir, "config.net.json");   // propio de la app C#
        DoneLog = Path.Combine(DataDir, "procesadas.net.log");
        ArtistExceptionsPath = Path.Combine(DataDir, "ArtistsExceptions.json");
        ArtistAliasesPath = Path.Combine(DataDir, "ArtistAliases.json");
        ScanCachePath = Path.Combine(DataDir, "scan-cache.json");
        AnalysisCachePath = Path.Combine(DataDir, "analysis-cache.json");
        LoudnessCachePath = Path.Combine(DataDir, "sonoridad.json");
        FingerprintCachePath = Path.Combine(DataDir, "huellas.json");
        EspectroCachePath = Path.Combine(DataDir, EspectroCacheNombre);
        IdentificacionCachePath = Path.Combine(DataDir, "identificacion.json");
        AudioAceptadasPath = Path.Combine(DataDir, "audio-aceptadas.json");
        IgnoredPath = Path.Combine(DataDir, "descartadas.json");
        AppliedPath = Path.Combine(DataDir, "aplicadas.json");
        ApplyMarksPath = Path.Combine(DataDir, "marcas-aplicar.json");
        FichasDjPath = Path.Combine(DataDir, "fichas-dj.json");
        ColeccionesPath = Path.Combine(DataDir, "colecciones.json");
        BandejaPath = Path.Combine(DataDir, "bandeja.json");
    }

    /// <summary>
    /// Ruta del manifiesto de deshacer para una ejecución nueva: «run_aaaammdd_hhmmss.jsonl», o con
    /// «_2», «_3»… si ya hay uno de ese mismo segundo. Es la forma que reconoce
    /// <see cref="Pipeline.UndoEngine.EsManifiestoPendiente"/>; hoy el nombre se compone a mano en
    /// once sitios, y dos ejecuciones en el mismo segundo acababan mezcladas en un solo manifiesto.
    /// </summary>
    public string NuevoManifiesto(DateTime? cuando = null)
    {
        var raiz = $"run_{(cuando ?? DateTime.Now):yyyyMMdd_HHmmss}";
        var ruta = Path.Combine(UndoDir, raiz + ".jsonl");
        for (var n = 2; File.Exists(ruta); n++) ruta = Path.Combine(UndoDir, $"{raiz}_{n}.jsonl");
        return ruta;
    }

    // --- Copia de seguridad (equipo «volumen_copias», tanda 4, T15) ---

    /// <summary>
    /// Los archivos de trabajo del usuario: lo que NO se regenera solo y entra en la copia de
    /// seguridad (Ajustes → «Copia de seguridad…» y la copia automática de cada arranque). Decisiones
    /// tomadas a mano canción a canción, fichas, listas, nombres de artista, ajustes y las
    /// identificaciones por audio, que costaron una consulta de pago cada una.
    ///
    /// Un archivo de datos nuevo en la raíz de la carpeta de datos entra igualmente aunque no esté
    /// aquí (ver <see cref="Copias.CopiaSeguridad.ArchivosAIncluir"/>); esta lista le pone nombre en
    /// la vista previa y fija que los conocidos entren siempre.
    /// </summary>
    public IReadOnlyList<string> ArchivosDeTrabajo() => new[]
    {
        ConfigPath,
        FichasDjPath,
        ColeccionesPath,
        BandejaPath,
        IgnoredPath,
        AppliedPath,
        ApplyMarksPath,
        AudioAceptadasPath,
        ArtistAliasesPath,
        ArtistExceptionsPath,
        IdentificacionCachePath,
        Path.Combine(DataDir, Dj.AlmacenPendientes.NombreArchivo),
        Path.Combine(DataDir, Dj.AlmacenListasSeguidas.NombreArchivo),
        Dj.MapaGenerosCarpeta.RutaEn(this),
        Dj.AlmacenSustituciones.RutaEn(DataDir),
        // Correcciones y exclusiones de artistas hechas a mano en Novedades (P5): sin rutas de música.
        Path.Combine(DataDir, Providers.AlmacenArtistasDeezer.NombreArchivo),
    };

    /// <summary>
    /// Archivos .json de la carpeta de datos que NO entran en la copia: cachés que se regeneran solas
    /// (la de huellas pasa de 150 MB en una biblioteca grande) y el config.json de la versión en
    /// PowerShell, que puede guardar claves. Además de excluirlos al crear la copia, se rechazan al
    /// restaurar: así una copia hecha antes de que una caché figurase aquí tampoco la devuelve.
    /// </summary>
    public IReadOnlyList<string> FueraDeLaCopia() => new[]
    {
        ScanCachePath,
        AnalysisCachePath,
        LoudnessCachePath,
        FingerprintCachePath,
        EspectroCachePath,
        Path.Combine(DataDir, "config.json"),
    };

    /// <summary>Crea las carpetas de datos si no existen (idempotente).</summary>
    public void EnsureDirectories()
    {
        foreach (var p in new[] { DataDir, LibDir, LogsDir, ReportsDir, CacheDir, UndoDir })
            try { Directory.CreateDirectory(p); } catch { /* best-effort */ }
    }
}
