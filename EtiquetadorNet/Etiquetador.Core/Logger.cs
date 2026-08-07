using System.Text;

namespace Etiquetador.Core;

/// <summary>Severidad/estilo de una línea de log (equivale a los colores del RichTextBox del .ps1).</summary>
public enum LogKind { Default, Ok, Clean, No, Err, Head, Dim, Sum }

public readonly record struct LogEntry(DateTime Time, string Message, LogKind Kind);

/// <summary>
/// Log dual: archivo de texto ("HH:mm:ss  mensaje") + evento para la UI.
/// Sin acoplarse a WinForms/Avalonia: la UI se suscribe a <see cref="OnLog"/>.
/// </summary>
public sealed class Logger
{
    private readonly object _fileLock = new();

    /// <summary>Ruta del archivo de log de la ejecución actual (null = no se escribe a disco).</summary>
    public string? LogFile { get; set; }

    /// <summary>La UI se suscribe aquí para pintar cada línea.</summary>
    public event Action<LogEntry>? OnLog;

    public void Log(string message, LogKind kind = LogKind.Default, bool fileOnly = false)
    {
        var now = DateTime.Now;
        if (!fileOnly)
        {
            try { OnLog?.Invoke(new LogEntry(now, message, kind)); } catch { /* la UI no debe tumbar el proceso */ }
        }
        if (!string.IsNullOrEmpty(LogFile))
        {
            var line = now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine;
            try
            {
                lock (_fileLock)
                    File.AppendAllText(LogFile!, line, Encoding.UTF8);
            }
            catch { /* best-effort */ }
        }
    }
}
