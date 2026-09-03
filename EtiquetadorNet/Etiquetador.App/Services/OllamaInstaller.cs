using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Etiquetador.App.Services;

/// <summary>
/// Instala Ollama con el gestor de paquetes propio de cada sistema: winget en Windows, Homebrew en
/// macOS.
///
/// Se hace así a propósito, en lugar de descargar el instalador por nuestra cuenta: el gestor obtiene
/// el paquete de su repositorio oficial, comprueba su firma y permite actualizarlo después.
/// Descargarlo a mano obligaría a fijar un hash que quedaría obsoleto en cada versión de Ollama.
///
/// Si no hay gestor de paquetes, se ofrece abrir la página oficial de descarga.
///
/// Nota sobre System.Diagnostics.Process: lo que tumbaba el ejecutable único era usarlo con
/// UseShellExecute=true (por eso Shell abre carpetas con el Launcher de Avalonia). Aquí se usa
/// con UseShellExecute=false y redirección, igual que Fingerprint para fpcalc, que sí funciona
/// en el ejecutable publicado.
/// </summary>
public static class OllamaInstaller
{
    public const string PaqueteWinget = "Ollama.Ollama";
    public const string PaqueteBrew = "ollama";
    public const string PaginaDescarga = "https://ollama.com/download";

    private static bool EsWindows => OperatingSystem.IsWindows();

    /// <summary>Nombre del gestor de paquetes de este sistema, para poder nombrarlo en los mensajes.</summary>
    public static string GestorDePaquetes => EsWindows ? "winget" : "Homebrew";

    /// <summary>
    /// Ruta del ejecutable de Ollama, o null si no está instalado.
    ///
    /// En Windows se prefiere «ollama app.exe», que es el que levanta el servicio y deja el icono en
    /// la bandeja, igual que si lo abriera el usuario a mano; «ollama.exe» sirve de recambio.
    /// En macOS se buscan las dos rutas habituales de Homebrew -/opt/homebrew para Apple Silicon y
    /// /usr/local para los Intel- y el binario que trae dentro Ollama.app si se instaló la versión
    /// de escritorio.
    /// </summary>
    public static string? RutaEjecutable()
    {
        foreach (var c in Candidatos())
            try { if (File.Exists(c)) return c; } catch { }

        // Último recurso: que esté en el PATH.
        try
        {
            var nombre = EsWindows ? "ollama.exe" : "ollama";
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (dir.Length == 0) continue;
                var c = Path.Combine(dir.Trim(), nombre);
                if (File.Exists(c)) return c;
            }
        }
        catch { }
        return null;
    }

    private static string[] Candidatos()
    {
        if (!EsWindows)
            return new[]
            {
                "/opt/homebrew/bin/ollama",                          // Homebrew en Apple Silicon
                "/usr/local/bin/ollama",                             // Homebrew en Intel, y el enlace que crea Ollama.app
                "/Applications/Ollama.app/Contents/Resources/ollama", // la versión de escritorio
            };

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programas = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new[]
        {
            Path.Combine(local, "Programs", "Ollama", "ollama app.exe"),
            Path.Combine(programas, "Ollama", "ollama app.exe"),
            Path.Combine(local, "Programs", "Ollama", "ollama.exe"),
            Path.Combine(programas, "Ollama", "ollama.exe"),
        };
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
            // «ollama» a secas es la herramienta de línea de órdenes: hay que pedirle el servicio.
            // (En Windows el que NO lo necesita es «ollama app.exe», que ya levanta el servicio solo.)
            if (Path.GetFileNameWithoutExtension(exe).Equals("ollama", StringComparison.OrdinalIgnoreCase))
                psi.ArgumentList.Add("serve");
            Process.Start(psi);
            traza?.Invoke($"Ollama lanzado desde {exe}");
            return true;
        }
        catch (Exception e) { traza?.Invoke($"No se pudo arrancar Ollama: {e.Message}"); return false; }
    }

    /// <summary>true si este equipo tiene el gestor de paquetes que hace falta (winget o brew).</summary>
    public static bool HayGestorDePaquetes()
    {
        // brew no siempre está en el PATH del proceso (los instaladores lo añaden al perfil del
        // intérprete de órdenes, que una aplicación de escritorio no llega a leer), así que se
        // buscan también sus dos rutas de siempre.
        foreach (var orden in EsWindows
                     ? new[] { "winget" }
                     : new[] { "brew", "/opt/homebrew/bin/brew", "/usr/local/bin/brew" })
        {
            if (Responde(orden, "--version")) return true;
        }
        return false;
    }

    /// <summary>Ruta del gestor de paquetes utilizable, o null si no hay ninguno.</summary>
    private static string? RutaGestor()
    {
        foreach (var orden in EsWindows
                     ? new[] { "winget" }
                     : new[] { "brew", "/opt/homebrew/bin/brew", "/usr/local/bin/brew" })
        {
            if (Responde(orden, "--version")) return orden;
        }
        return null;
    }

    private static bool Responde(string orden, string argumento)
    {
        try
        {
            var psi = new ProcessStartInfo(orden, argumento)
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
    /// Ejecuta la instalación con el gestor de paquetes del sistema. Va informando de cada línea de
    /// salida. Devuelve "" si fue bien, o el motivo del fallo. En Windows aparecerá la confirmación
    /// de administrador (UAC).
    /// </summary>
    public static async Task<string> InstalarAsync(Action<string>? traza, CancellationToken ct = default)
    {
        try
        {
            var gestor = RutaGestor();
            if (gestor == null) return $"No se encontró {GestorDePaquetes} en este equipo.";

            var psi = new ProcessStartInfo(gestor)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("install");

            if (EsWindows)
            {
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
            }
            else
            {
                // «brew install ollama» instala la fórmula, que es justo lo que hace falta: el
                // servicio que escucha en localhost. La versión de escritorio (--cask) traería
                // además una aplicación con icono que aquí no aporta nada.
                psi.ArgumentList.Add(PaqueteBrew);
                // Homebrew pregunta cosas si cree que hay alguien delante; aquí no lo hay.
                psi.Environment["HOMEBREW_NO_AUTO_UPDATE"] = "1";
                psi.Environment["NONINTERACTIVE"] = "1";
            }

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
