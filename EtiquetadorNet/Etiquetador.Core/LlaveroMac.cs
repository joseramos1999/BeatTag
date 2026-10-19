using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

// Las pruebas comprueban la decisión «leer, crear o fallar» con un `security` simulado (en Windows
// no hay Llavero, y es justo la parte que no puede equivocarse). El InternalsVisibleTo va en
// Etiquetador.Core.csproj, como en el proyecto de la aplicación.

namespace Etiquetador.Core;

/// <summary>Lo que devolvió una llamada a la orden `security` de macOS.</summary>
internal readonly record struct ResultadoSecurity(int Codigo, string Salida, string Error);

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

    /// <summary>Código de salida de `security` cuando el elemento no existe (errSecItemNotFound).</summary>
    internal const int CodigoNoExiste = 44;

    /// <summary>Código de salida de `security` cuando el elemento ya existe (errSecDuplicateItem).</summary>
    internal const int CodigoYaExiste = 45;

    private const int EsperaMaxima = 15_000;

    // La clave de esta sesión, o el motivo por el que no se pudo obtener.
    private static readonly ClaveDeSesion Sesion = new(Ejecutar);

    /// <summary>
    /// ¿Se puede usar el Llavero en este equipo? Comprueba que la orden `security` responde. Si no,
    /// quien llama debe negarse a guardar en vez de escribir las credenciales en claro.
    /// </summary>
    public static bool Disponible()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return Ejecutar("help").Codigo is 0 or 1; }   // `security help` sale con 1, pero existe
        catch { return false; }
    }

    /// <summary>
    /// La clave de cifrado de este usuario, creándola la primera vez. Lanza excepción si el Llavero
    /// no está disponible: es preferible fallar el guardado a guardar en claro.
    /// </summary>
    public static byte[] Clave() => Sesion.Obtener();

    /// <summary>
    /// Olvida el fallo recordado para que la siguiente lectura vuelva a preguntar al Llavero (por
    /// ejemplo, cuando el usuario dice que ya lo ha desbloqueado). Ver <see cref="ClaveDeSesion"/>.
    /// </summary>
    public static void Reintentar() => Sesion.Reintentar();

    /// <summary>
    /// La clave de una sesión: se pide a `security` UNA vez, y se recuerda tanto si sale bien como
    /// si falla.
    ///
    /// POR QUÉ el fallo también. Antes solo se guardaba el éxito: con el Llavero bloqueado, cada
    /// credencial que se descifra al cargar la configuración (cuatro setters *Enc) y cada vuelta del
    /// rescate de una configuración dañada volvía a lanzar `security`, con hasta 15 s de espera y,
    /// según el sistema, otro aviso de desbloqueo cada vez: hasta un minuto al arrancar. Ahora el
    /// primer fallo vale para toda la sesión, hasta un <see cref="Reintentar"/> explícito.
    /// </summary>
    internal sealed class ClaveDeSesion
    {
        private readonly Func<string, ResultadoSecurity> _ejecutar;
        private readonly object _lock = new();
        private byte[]? _clave;
        private ExceptionDispatchInfo? _fallo;

        public ClaveDeSesion(Func<string, ResultadoSecurity> ejecutar) => _ejecutar = ejecutar;

        public byte[] Obtener()
        {
            lock (_lock)
            {
                if (_clave != null) return _clave;
                _fallo?.Throw();   // el mismo motivo, sin volver a molestar al Llavero
                try
                {
                    _clave = ObtenerOCrear(_ejecutar);
                    return _clave;
                }
                catch (Exception e)
                {
                    _fallo = ExceptionDispatchInfo.Capture(e);
                    throw;
                }
            }
        }

        public void Reintentar()
        {
            lock (_lock) _fallo = null;
        }

        /// <summary>Olvida la clave y el fallo: la siguiente lectura vuelve a preguntar.</summary>
        public void Olvidar()
        {
            lock (_lock) { _clave = null; _fallo = null; }
        }
    }

    /// <summary>
    /// Lee la clave del Llavero y SOLO si `security` dice que no existe (código 44) crea una nueva.
    ///
    /// Antes cualquier fallo de lectura se trataba como «no existe»: con el Llavero bloqueado tras
    /// un reposo, o cancelando el aviso de desbloqueo, se generaba una clave nueva y se guardaba
    /// con «-U», que SUSTITUYE la existente. Todas las credenciales cifradas con la buena quedaban
    /// ilegibles para siempre. Ahora cualquier otro código es una excepción -AppConfig la convierte
    /// en credenciales ilegibles para esta sesión, sin escribir nada- y la clave se crea sin «-U»,
    /// de modo que nunca puede pisar una que ya esté.
    /// </summary>
    internal static byte[] ObtenerOCrear(Func<string, ResultadoSecurity> ejecutar)
    {
        var existente = Leer(ejecutar);
        if (existente != null) return existente;

        var nueva = RandomNumberGenerator.GetBytes(32);
        Guardar(ejecutar, nueva);

        // Se relee para confirmar que de verdad quedó guardada: si el Llavero aceptó la orden pero
        // no persistió nada, mejor enterarse ahora que al reabrir la aplicación y encontrarse las
        // credenciales ilegibles. Y si otra instancia la creó a la vez, se usa la suya.
        return Leer(ejecutar)
            ?? throw new InvalidOperationException("El Llavero de macOS no conservó la clave de cifrado.");
    }

    /// <summary>La clave guardada; null SOLO si `security` dice que no existe. Cualquier otro fallo lanza.</summary>
    private static byte[]? Leer(Func<string, ResultadoSecurity> ejecutar)
    {
        var r = ejecutar($"find-generic-password -s {Servicio} -a {Cuenta} -w");
        if (r.Codigo == CodigoNoExiste) return null;
        if (r.Codigo != 0)
            throw new InvalidOperationException(
                $"No se pudo leer la clave de cifrado del Llavero de macOS (security salió con el código {r.Codigo}: {r.Error.Trim()}). " +
                "Las credenciales no se pueden usar en esta sesión; no se crea otra clave para no dejar ilegibles las guardadas.");

        try
        {
            var clave = Convert.FromBase64String(r.Salida.Trim());
            if (clave.Length == 32) return clave;
        }
        catch (FormatException) { }

        // Existe pero no es una clave nuestra: sustituirla dejaría ilegible lo que se cifró con ella.
        throw new InvalidOperationException(
            $"La clave de cifrado guardada en el Llavero de macOS (servicio «{Servicio}») está dañada. " +
            "Si no necesitas las credenciales guardadas, bórrala con «Acceso a Llaveros» y vuelve a introducirlas.");
    }

    private static void Guardar(Func<string, ResultadoSecurity> ejecutar, byte[] clave)
    {
        // SIN -U: si ya existiera, `security` falla con 45 en vez de sustituirla, y entonces se usa la
        // que hay (otra instancia de la aplicación pudo crearla a la vez). El valor viaja como
        // argumento porque `security` no ofrece una vía fiable por entrada estándar; es una clave
        // que se genera UNA vez, y quien pudiera leer la lista de procesos en ese instante ya
        // tendría acceso a la sesión del usuario.
        var b64 = Convert.ToBase64String(clave);
        var r = ejecutar($"add-generic-password -s {Servicio} -a {Cuenta} -w {b64}");
        if (r.Codigo is 0 or CodigoYaExiste) return;
        throw new InvalidOperationException($"No se pudo guardar la clave en el Llavero (código {r.Codigo}): {r.Error.Trim()}");
    }

    /// <summary>
    /// Ejecuta `security`. La salida y los errores se leen a la vez, y si no termina en 15 s se
    /// mata: antes se leía todo ANTES de esperar, así que el límite nunca actuaba y un aviso de
    /// desbloqueo sin contestar dejaba la aplicación colgada.
    /// </summary>
    private static ResultadoSecurity Ejecutar(string argumentos)
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
        var salida = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(EsperaMaxima))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("El Llavero de macOS no respondió en 15 s (¿está esperando a que se desbloquee?).");
        }
        p.WaitForExit();   // asegura que la salida redirigida se ha leído entera
        return new ResultadoSecurity(p.ExitCode, salida.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    /// <summary>Solo para pruebas: olvida la clave cacheada para forzar que se relea del Llavero.</summary>
    internal static void OlvidarCache() => Sesion.Olvidar();
}
