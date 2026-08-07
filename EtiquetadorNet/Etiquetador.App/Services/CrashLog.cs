using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Etiquetador.Core;

namespace Etiquetador.App.Services;

/// <summary>Registro de crashes/errores no controlados con información para depurar.</summary>
public static class CrashLog
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Etiquetador de Musica", "logs");

    /// <summary>Instala los manejadores globales de excepciones no controladas.</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Write("UnhandledException", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    public static void Write(string kind, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var file = Path.Combine(Dir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss_fff}.log");
            var sb = new StringBuilder();
            sb.AppendLine($"BeatTag {AppInfo.Version} — {kind} — {DateTime.Now:O}");
            sb.AppendLine($"OS: {Environment.OSVersion} · .NET: {Environment.Version} · 64-bit: {Environment.Is64BitProcess}");
            sb.AppendLine(new string('-', 60));
            sb.AppendLine(ex.ToString());
            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
        }
        catch { /* si ni siquiera podemos registrar, no hay nada que hacer */ }
    }
}
