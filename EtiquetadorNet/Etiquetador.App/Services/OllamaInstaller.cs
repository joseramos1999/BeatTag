using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Etiquetador.App.Services;

/// <summary>
/// Instala Ollama usando winget, el gestor de paquetes que ya trae Windows.
///
/// Se hace así a propósito, en lugar de descargar el instalador por nuestra cuenta: winget obtiene el
/// paquete del repositorio oficial de Microsoft, comprueba su firma y permite actualizarlo después.
/// Descargar un .exe a mano obligaría a fijar un hash que quedaría obsoleto en cada versión de Ollama.
///
/// Si winget no está disponible, se ofrece abrir la página oficial de descarga.
///
/// Nota sobre System.Diagnostics.Process: lo que tumbaba el ejecutable único era usarlo con
/// UseShellExecute=true (por eso Shell abre carpetas con el Launcher de Avalonia). Aquí se usa
/// con UseShellExecute=false y redirección, igual que Fingerprint para fpcalc, que sí funciona
/// en el ejecutable publicado.
/// </summary>
public static class OllamaInstaller
{
    public const string PaqueteWinget = "Ollama.Ollama";
    public const string PaginaDescarga = "https://ollama.com/download";

    /// <summary>
    /// Ruta del ejecutable de Ollama, o null si no está instalado. Se prefiere «ollama app.exe»,
    /// que es el que levanta el servicio y deja el icono en la bandeja, igual que si lo abriera
    /// el usuario a mano. «ollama.exe» sirve de recambio (arranca el servicio con «serve»).
    /// </summary>
    public static string? RutaEjecutable()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programas = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidatos =
        {
            Path.Combine(local, "Programs", "Ollama", "ollama app.exe"),
            Path.Combine(programas, "Ollama", "ollama app.exe"),
            Path.Combine(local, "Programs", "Ollama", "ollama.exe"),
            Path.Combine(programas, "Ollama", "ollama.exe"),
        };
        foreach (var c in candidatos)
            try { if (File.Exists(c)) return c; } catch { }

        // Último recurso: que esté en el PATH.
        try
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (dir.Length == 0) continue;
                var c = Path.Combine(dir.Trim(), "ollama.exe");
                if (File.Exists(c)) return c;
            }
        }
        catch { }
        return null;
    }

    /// <summary>true si Ollama está instalado en este equipo (aunque no esté en marcha).</summary>
    public static bool EstaInstalado() => RutaEjecutable() != null;

    /// <summary>
    /// Lanza el servicio de Ollama. Devuelve false si no está instalado o no se pudo arrancar.
    /// No espera a que responda: de eso se encarga quien llama.
    /// </summary>
    public static bool Lanzar(Action<string>? traza = null)
    {
        var exe = RutaEjecutable();
        if (exe == null) { traza?.Invoke("Ollama no está instalado en este equipo."); return false; }
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            // «ollama.exe» a secas es la herramienta de línea de órdenes: hay que pedirle el servicio.
            if (Path.GetFileName(exe).Equals("ollama.exe", StringComparison.OrdinalIgnoreCase))
                psi.ArgumentList.Add("serve");
            Process.Start(psi);
            traza?.Invoke($"Ollama lanzado desde {exe}");
            return true;
        }
        catch (Exception e) { traza?.Invoke($"No se pudo arrancar Ollama: {e.Message}"); return false; }
    }

    /// <summary>true si este Windows trae winget.</summary>
    public static bool HayWinget()
    {
        try
        {
            var psi = new ProcessStartInfo("winget", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Ejecuta la instalación. Va informando de cada línea de salida. Devuelve "" si fue bien,
    /// o el motivo del fallo. Windows pedirá confirmación de administrador (UAC).
    /// </summary>
    public static async Task<string> InstalarAsync(Action<string>? traza, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo("winget")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("install");
            psi.ArgumentList.Add("--id");
            psi.ArgumentList.Add(PaqueteWinget);
            psi.ArgumentList.Add("--exact");
            // Solo el repositorio oficial de winget: evita que resuelva a la Microsoft Store.
            psi.ArgumentList.Add("--source");
            psi.ArgumentList.Add("winget");
            psi.ArgumentList.Add("--accept-source-agreements");
            psi.ArgumentList.Add("--accept-package-agreements");
            // Sin esto winget puede quedarse esperando una respuesta que nadie va a teclear.
            psi.ArgumentList.Add("--disable-interactivity");

            using var p = Process.Start(psi);
            if (p == null) return "No se pudo ejecutar winget.";

            var salida = Task.Run(async () =>
            {
                string? l;
                while ((l = await p.StandardOutput.ReadLineAsync(ct)) != null)
                {
                    var t = l.Trim();
                    if (t.Length > 0) traza?.Invoke(t);
                }
            }, ct);
            var errores = p.StandardError.ReadToEndAsync(ct);

            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            await salida.ConfigureAwait(false);
            var err = await errores.ConfigureAwait(false);

            if (p.ExitCode == 0) return "";
            // winget devuelve este código cuando el paquete ya está puesto: no es un fallo.
            if (unchecked((uint)p.ExitCode) == 0x8A150061) return "";
            return $"winget terminó con código {p.ExitCode}. {err.Trim()}".Trim();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return "Instalación cancelada."; }
        catch (Exception e) { return e.Message; }
    }
}
