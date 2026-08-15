using System;
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
