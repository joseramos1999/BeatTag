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
    public string ConfigPath { get; }
    public string DoneLog { get; }
    public string ArtistExceptionsPath { get; }
    public string ScanCachePath { get; }
    public string AnalysisCachePath { get; }
    public string LoudnessCachePath { get; }
    public string IgnoredPath { get; }
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
        ScanCachePath = Path.Combine(DataDir, "scan-cache.json");
        AnalysisCachePath = Path.Combine(DataDir, "analysis-cache.json");
        LoudnessCachePath = Path.Combine(DataDir, "sonoridad.json");
        IgnoredPath = Path.Combine(DataDir, "descartadas.json");
        AppliedPath = Path.Combine(DataDir, "aplicadas.json");
    }

    /// <summary>Crea las carpetas de datos si no existen (idempotente).</summary>
    public void EnsureDirectories()
    {
        foreach (var p in new[] { DataDir, LibDir, LogsDir, ReportsDir, CacheDir, UndoDir })
            try { Directory.CreateDirectory(p); } catch { /* best-effort */ }
    }
}
