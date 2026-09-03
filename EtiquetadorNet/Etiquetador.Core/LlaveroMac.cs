using System;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Etiquetador.Core;

/// <summary>
/// La clave con la que se cifran los secretos en macOS, guardada en el Llavero del usuario.
///
/// Por qué el Llavero y no un archivo con permisos restringidos: en Windows, DPAPI ata el cifrado a
/// la contraseña de la cuenta, de modo que copiarse el config.net.json a otro equipo no sirve de
/// nada. Un archivo de clave junto al de configuración perdería justo esa propiedad -quien se lleve
/// los dos, se lleva las credenciales-. El Llavero da la misma garantía que DPAPI: el secreto va
/// ligado a la sesión del usuario, no a un archivo que se pueda copiar.
///
/// Se guarda UNA sola clave (32 bytes al azar) y con ella se cifra todo lo demás. Así el Llavero se
/// consulta una vez por sesión en vez de una vez por credencial, que además evita repetir la
/// petición de permiso que macOS muestra la primera vez.
/// </summary>
internal static class LlaveroMac
{
    private const string Servicio = "BeatTag";
    private const string Cuenta = "config";

    private static byte[]? _cache;
    private static readonly object _lock = new();

    /// <summary>
    /// ¿Se puede usar el Llavero en este equipo? Comprueba que la orden `security` responde. Si no,
    /// quien llama debe negarse a guardar en vez de escribir las credenciales en claro.
    /// </summary>
    public static bool Disponible()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return Ejecutar("help", out _, out _) is 0 or 1; }   // `security help` sale con 1, pero existe
        catch { return false; }
    }

    /// <summary>
    /// La clave de cifrado de este usuario, creándola la primera vez. Lanza excepción si el Llavero
    /// no está disponible: es preferible fallar el guardado a guardar en claro.
    /// </summary>
    public static byte[] Clave()
    {
        lock (_lock)
        {
            if (_cache != null) return _cache;

            var existente = Leer();
            if (existente != null) { _cache = existente; return _cache; }

            var nueva = RandomNumberGenerator.GetBytes(32);
            Guardar(nueva);

            // Se relee para confirmar que de verdad quedó guardada: si el Llavero aceptó la orden
            // pero no persistió nada, mejor enterarse ahora que al reabrir la aplicación y
            // encontrarse las credenciales ilegibles.
            var comprobacion = Leer()
                ?? throw new InvalidOperationException("El Llavero de macOS no conservó la clave de cifrado.");

            _cache = comprobacion;
            return _cache;
        }
    }

    private static byte[]? Leer()
    {
        var codigo = Ejecutar($"find-generic-password -s {Servicio} -a {Cuenta} -w", out var salida, out _);
        if (codigo != 0) return null;

        var texto = salida.Trim();
        if (texto.Length == 0) return null;
        try { return Convert.FromBase64String(texto); }
        catch { return null; }
    }

    private static void Guardar(byte[] clave)
    {
        // -U actualiza si ya existiera. El valor viaja como argumento porque `security` no ofrece
        // una vía fiable por entrada estándar; es una clave que se genera UNA vez, y quien pudiera
        // leer la lista de procesos en ese instante ya tendría acceso a la sesión del usuario.
        var b64 = Convert.ToBase64String(clave);
        var codigo = Ejecutar($"add-generic-password -s {Servicio} -a {Cuenta} -w {b64} -U", out _, out var error);
        if (codigo != 0)
            throw new InvalidOperationException($"No se pudo guardar la clave en el Llavero: {error.Trim()}");
    }

    private static int Ejecutar(string argumentos, out string salida, out string error)
    {
        var psi = new ProcessStartInfo("security")
        {
            Arguments = argumentos,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo ejecutar `security`.");
        salida = p.StandardOutput.ReadToEnd();
        error = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return p.HasExited ? p.ExitCode : -1;
    }

    /// <summary>Solo para pruebas: olvida la clave cacheada para forzar que se relea del Llavero.</summary>
    internal static void OlvidarCache()
    {
        lock (_lock) _cache = null;
    }
}
