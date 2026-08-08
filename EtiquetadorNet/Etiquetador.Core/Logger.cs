using System.Text;

namespace Etiquetador.Core;

/// <summary>Severidad/estilo de una línea de log (equivale a los colores del RichTextBox del .ps1).</summary>
public enum LogKind { Default, Ok, Clean, No, Err, Head, Dim, Sum }

public readonly record struct LogEntry(DateTime Time, string Message, LogKind Kind);

/// <summary>
/// Log dual: archivo de texto detallado + evento para la UI.
/// La UI recibe solo lo relevante; al archivo va TODO (incluida la traza de diagnóstico), que es
/// lo que sirve para afinar el algoritmo después. Sin acoplarse a WinForms/Avalonia.
/// </summary>
public sealed class Logger
{
    private readonly object _fileLock = new();

    /// <summary>Ruta del archivo de log de la ejecución actual (null = no se escribe a disco).</summary>
    public string? LogFile { get; set; }

    /// <summary>Si es false, las líneas de detalle (traza por canción, peticiones) no se escriben.</summary>
    public bool Verbose { get; set; } = true;

    /// <summary>La UI se suscribe aquí para pintar cada línea.</summary>
    public event Action<LogEntry>? OnLog;

    public void Log(string message, LogKind kind = LogKind.Default, bool fileOnly = false)
    {
        var now = DateTime.Now;
        if (!fileOnly)
        {
            try { OnLog?.Invoke(new LogEntry(now, message, kind)); } catch { /* la UI no debe tumbar el proceso */ }
        }
        WriteFile(now, Tag(kind), message);
    }

    // --- Atajos por intención (más legibles en las llamadas) ---

    /// <summary>Encabezado de una fase (visible en la UI).</summary>
    public void Head(string message) => Log(message, LogKind.Head);

    /// <summary>Algo salió bien (visible).</summary>
    public void Ok(string message) => Log(message, LogKind.Ok);

    /// <summary>Resumen de una fase (visible).</summary>
    public void Sum(string message) => Log(message, LogKind.Sum);

    /// <summary>Aviso o error (visible).</summary>
    public void Err(string message) => Log(message, LogKind.Err);

    /// <summary>Traza de diagnóstico: SOLO al archivo, para no inundar la UI.</summary>
    public void Detail(string message)
    {
        if (!Verbose) return;
        WriteFile(DateTime.Now, "DBG", message);
    }

    /// <summary>Excepción: mensaje corto a la UI y traza completa al archivo.</summary>
    public void Error(string context, Exception ex)
    {
        Log($"{context}: {ex.Message}", LogKind.Err);
        WriteFile(DateTime.Now, "ERR", $"{context} -> {ex.GetType().Name}: {ex.Message}");
        if (ex.StackTrace != null) WriteFile(DateTime.Now, "ERR", ex.StackTrace);
        if (ex.InnerException != null)
            WriteFile(DateTime.Now, "ERR", $"  causa: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    }

    /// <summary>Cabecera del archivo con el entorno (lo primero que se mira al depurar).</summary>
    public void SessionHeader(string appName, string version, string dataDir)
    {
        WriteFile(DateTime.Now, "INF", new string('=', 70));
        WriteFile(DateTime.Now, "INF", $"{appName} {version}");
        WriteFile(DateTime.Now, "INF", $"SO      : {Environment.OSVersion}");
        WriteFile(DateTime.Now, "INF", $".NET    : {Environment.Version}");
        WriteFile(DateTime.Now, "INF", $"Equipo  : {Environment.MachineName} · {Environment.ProcessorCount} núcleos · 64-bit={Environment.Is64BitProcess}");
        WriteFile(DateTime.Now, "INF", $"Datos   : {dataDir}");
        WriteFile(DateTime.Now, "INF", $"Log     : {LogFile}");
        WriteFile(DateTime.Now, "INF", new string('=', 70));
    }

    /// <summary>Borra los logs con más de <paramref name="days"/> días para que la carpeta no crezca sin fin.</summary>
    public static int PruneOldLogs(string logsDir, int days = 30)
    {
        var n = 0;
        try
        {
            if (!Directory.Exists(logsDir)) return 0;
            var limit = DateTime.Now.AddDays(-days);
            foreach (var f in Directory.EnumerateFiles(logsDir, "beattag_*.log"))
            {
                try { if (File.GetLastWriteTime(f) < limit) { File.Delete(f); n++; } } catch { }
            }
        }
        catch { }
        return n;
    }

    private static string Tag(LogKind k) => k switch
    {
        LogKind.Err => "ERR",
        LogKind.Head => "===",
        LogKind.Sum => "SUM",
        LogKind.Dim => "DBG",
        _ => "INF",
    };

    private void WriteFile(DateTime now, string tag, string message)
    {
        if (string.IsNullOrEmpty(LogFile)) return;
        var line = $"{now:HH:mm:ss.fff} [{tag}] {message}{Environment.NewLine}";
        try
        {
            lock (_fileLock)
                File.AppendAllText(LogFile!, line, Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }
}
