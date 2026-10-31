using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Etiquetador.Core;
using Microsoft.VisualBasic.FileIO;

namespace Etiquetador.App.Services;

/// <summary>
/// Enviar archivos a la papelera del sistema, recuperables. Lo usan Duplicados y el Asistente IA.
/// </summary>
public static class Papelera
{
    /// <summary>
    /// Envía el archivo a la papelera. Nunca lo borra del todo: si no puede, lo deja y lo dice.
    ///
    /// En Windows eso exige mirar ANTES la unidad. FileSystem.DeleteFile con «sin diálogos» pide al
    /// shell que lo recicle sin preguntar, y el shell, cuando la unidad no tiene papelera (memoria
    /// USB, tarjeta, unidad de red, papelera desactivada), lo destruye en silencio y devuelve éxito.
    /// El caso típico: la exportación de rekordbox en un pendrive y 300 copias enviadas desde
    /// Duplicados; la aplicación decía «enviadas a la papelera» y en la papelera no había nada.
    /// </summary>
    public static (bool Ok, string Error) Enviar(string ruta)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var motivo = MotivoSinPapeleraWindows(ruta);
                if (motivo.Length > 0) return (false, motivo);
                FileSystem.DeleteFile(ruta, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                return (true, "");
            }
            return PapeleraMac(ruta);
        }
        catch (Exception e) { return (false, e.Message); }
    }

    /// <summary>
    /// Por qué este archivo NO se puede mandar a la papelera sin destruirlo, o "" si se puede. Fuera de
    /// Windows siempre "": en macOS el Finder respeta el disco del archivo y, si no puede, se mueve a
    /// mano a ~/.Trash (ver <see cref="PapeleraMac"/>), así que no hay borrado definitivo escondido.
    /// </summary>
    public static string MotivoSinPapelera(string ruta)
    {
        if (!OperatingSystem.IsWindows()) return "";
        try { return MotivoSinPapeleraWindows(ruta); }
        catch (Exception e) { return "No se ha podido comprobar la papelera de esa unidad: " + e.Message; }
    }

    /// <summary>
    /// La nota del diálogo de confirmación. Antes prometía siempre «puedes recuperarlos desde ahí», y
    /// con la biblioteca en una memoria USB era justo al revés. Ahora dice cuántos no van a ir.
    /// </summary>
    public static string NotaConfirmacion(IEnumerable<string> rutas)
    {
        var lista = rutas.ToList();
        var sin = lista.Select(MotivoSinPapelera).Where(m => m.Length > 0).ToList();
        if (sin.Count == 0)
            return "Van a la papelera del sistema, así que puedes recuperarlos desde ahí si te arrepientes.";
        if (sin.Count == lista.Count)
            return lista.Count == 1
                ? $"No se va a enviar: {Minuscula(sin[0])}"
                : $"No se va a enviar ninguno: {Minuscula(sin[0])}";
        return $"{sin.Count} de ellos no se van a enviar: {Minuscula(sin[0])} " +
               "El resto va a la papelera del sistema y se puede recuperar desde ahí.";
    }

    private static string Minuscula(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    // --- Comprobar la unidad (Windows) ---

    /// <summary>Qué clase de unidad es, a efectos de papelera.</summary>
    internal enum TipoUnidad { Fija, Extraible, Red, Optica, Memoria, Desconocida }

    /// <summary>
    /// La regla, sin tocar el sistema, para poder probarla con los casos reales.
    /// </summary>
    /// <param name="tipo">Tipo de la unidad del archivo.</param>
    /// <param name="unidad">Cómo se nombra la unidad en el aviso («E:», «\\nas\musica»).</param>
    /// <param name="papeleraDesactivada">
    /// En Windows, «No mover archivos a la papelera» marcado para esa unidad, o la directiva que la
    /// desactiva en todas.
    /// </param>
    /// <param name="capacidadMaxMb">Tamaño máximo de la papelera de esa unidad en MB; 0 si no consta.</param>
    /// <param name="tamano">Tamaño del archivo en bytes.</param>
    internal static string Motivo(TipoUnidad tipo, string unidad, bool papeleraDesactivada, long capacidadMaxMb, long tamano)
    {
        const string cola = " No se ha tocado; si de verdad quieres eliminarlo, hazlo desde el Explorador de archivos.";
        return tipo switch
        {
            TipoUnidad.Red =>
                $"Está en una unidad de red ({unidad}), y Windows no tiene papelera para las unidades de red: se borraría para siempre." + cola,
            TipoUnidad.Extraible =>
                $"Está en una unidad extraíble ({unidad}, memoria USB o tarjeta), que no tiene papelera: se borraría para siempre." + cola,
            TipoUnidad.Optica or TipoUnidad.Memoria or TipoUnidad.Desconocida =>
                $"No se ha podido comprobar que la unidad {unidad} tenga papelera, y sin ella se borraría para siempre." + cola,
            _ when papeleraDesactivada =>
                $"La papelera de la unidad {unidad} está desactivada en Windows: se borraría para siempre." + cola,
            // Lo que no cabe en la papelera, el shell también lo destruye sin preguntar.
            _ when capacidadMaxMb > 0 && tamano > capacidadMaxMb * 1024L * 1024L =>
                $"Ocupa más de lo que admite la papelera de la unidad {unidad} ({capacidadMaxMb} MB): Windows lo borraría para siempre." + cola,
            _ => "",
        };
    }

    [SupportedOSPlatform("windows")]
    private static string MotivoSinPapeleraWindows(string ruta)
    {
        var tipo = TipoDeUnidad(ruta, out var raiz);
        if (tipo != TipoUnidad.Fija) return Motivo(tipo, NombreUnidad(raiz), false, 0, 0);

        var (desactivada, capacidad) = AjustesPapelera(raiz);
        long tamano = 0;
        if (capacidad > 0)
            try { tamano = new FileInfo(ruta).Length; } catch { /* sin tamaño no se puede comparar */ }
        return Motivo(tipo, NombreUnidad(raiz), desactivada, capacidad, tamano);
    }

    /// <summary>
    /// La unidad del archivo, solo mirando el texto y el tipo que da Windows: una ruta UNC no se
    /// toca (un servidor que no responde tardaría decenas de segundos en decir que no está).
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static TipoUnidad TipoDeUnidad(string ruta, out string raiz)
    {
        raiz = "";
        if (string.IsNullOrWhiteSpace(ruta)) return TipoUnidad.Desconocida;
        var r = ruta.Replace('/', '\\');

        // «\\?\UNC\servidor\recurso\…» y «\\servidor\recurso\…»: recurso de red.
        if (r.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            raiz = RaizUnc(r[8..]);
            return TipoUnidad.Red;
        }
        if (r.StartsWith(@"\\?\", StringComparison.Ordinal) || r.StartsWith(@"\\.\", StringComparison.Ordinal))
            r = r[4..];
        else if (r.StartsWith(@"\\", StringComparison.Ordinal))
        {
            raiz = RaizUnc(r[2..]);
            return TipoUnidad.Red;
        }

        try
        {
            raiz = Path.GetPathRoot(r) ?? "";
            if (raiz.Length < 2 || raiz[1] != ':') return TipoUnidad.Desconocida;
            return new DriveInfo(raiz).DriveType switch
            {
                DriveType.Fixed => TipoUnidad.Fija,
                DriveType.Removable => TipoUnidad.Extraible,
                DriveType.Network => TipoUnidad.Red,
                DriveType.CDRom => TipoUnidad.Optica,
                DriveType.Ram => TipoUnidad.Memoria,
                _ => TipoUnidad.Desconocida,
            };
        }
        catch { return TipoUnidad.Desconocida; }
    }

    private static string RaizUnc(string sinBarras)
    {
        var partes = sinBarras.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return partes.Length >= 2 ? $@"\\{partes[0]}\{partes[1]}" : @"\\" + sinBarras.TrimEnd('\\');
    }

    private static string NombreUnidad(string raiz)
    {
        if (raiz.Length == 0) return "del archivo";
        return raiz.Length >= 2 && raiz[1] == ':' ? raiz[..2] : raiz.TrimEnd('\\');
    }

    /// <summary>
    /// Lo que el usuario ha configurado para la papelera de esa unidad. Se lee del registro, igual
    /// que lo lee el Explorador: «No mover archivos a la papelera» (NukeOnDelete) y su tamaño máximo
    /// (MaxCapacity, en MB), por volumen; más la directiva NoRecycleFiles, que la quita en todas. Si
    /// algo no se puede leer se da por buena: es el valor de fábrica de Windows.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static (bool Desactivada, long CapacidadMb) AjustesPapelera(string raiz)
    {
        try
        {
            using (var politica = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"))
                if (politica?.GetValue("NoRecycleFiles") is int n && n != 0) return (true, 0);

            var guid = GuidDeVolumen(raiz);
            if (guid.Length == 0) return (false, 0);
            using var clave = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{guid}");
            if (clave == null) return (false, 0);
            var nuke = clave.GetValue("NukeOnDelete") is int d && d != 0;
            var max = clave.GetValue("MaxCapacity") is int m && m > 0 ? m : 0;
            return (nuke, max);
        }
        catch { return (false, 0); }
    }

    /// <summary>«{xxxxxxxx-…}» del volumen montado en esa raíz, o "" si no se puede saber.</summary>
    [SupportedOSPlatform("windows")]
    private static string GuidDeVolumen(string raiz)
    {
        var buf = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPoint(raiz.EndsWith('\\') ? raiz : raiz + "\\", buf, buf.Capacity)) return "";
        var s = buf.ToString();   // \\?\Volume{guid}\
        var a = s.IndexOf('{');
        var b = s.IndexOf('}');
        return a >= 0 && b > a ? s[a..(b + 1)] : "";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int bufferLength);

    /// <summary>
    /// Papelera en macOS. Microsoft.VisualBasic.FileIO es de Windows: compila fuera pero revienta al
    /// llamarlo.
    ///
    /// Se intenta primero por Finder, que es quien de verdad sabe hacerlo bien: respeta el disco
    /// donde vive el archivo -importante, porque una biblioteca de DJ suele estar en un disco
    /// externo- y deja el "Volver a poner" del menú contextual. La primera vez macOS pedirá permiso
    /// para controlar Finder; si se deniega, se recurre a mover el archivo a ~/.Trash a mano.
    ///
    /// Lo que NO se hace nunca es borrar de verdad. Si los dos caminos fallan, el archivo se queda
    /// donde está y se informa del error: esta función existe para poder deshacer, y un borrado
    /// definitivo disfrazado de papelera sería exactamente lo contrario.
    /// </summary>
    private static (bool Ok, string Error) PapeleraMac(string ruta)
    {
        var porFinder = MoverConFinder(ruta);
        if (porFinder.Ok || !System.IO.File.Exists(ruta)) return (true, "");

        var porCarpeta = MoverATrashDelUsuario(ruta);
        if (porCarpeta.Ok) return (true, "");

        return (false, $"Finder: {porFinder.Error} · ~/.Trash: {porCarpeta.Error}");
    }

    /// <summary>Lo que se espera a Finder. Si macOS está esperando a que des permiso, no se queda colgado.</summary>
    private const int EsperaFinderMs = 20_000;

    private static (bool Ok, string Error) MoverConFinder(string ruta)
    {
        try
        {
            var script = $"tell application \"Finder\" to delete POSIX file \"{EscaparAppleScript(ruta)}\"";
            var psi = new ProcessStartInfo("osascript")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);

            using var p = Process.Start(psi);
            if (p == null) return (false, "no se pudo lanzar osascript");

            // El error se lee A LA VEZ que se espera. Leerlo antes (ReadToEnd) esperaba a que
            // osascript terminase, y con el aviso de permisos de macOS en pantalla el límite de 20 s
            // de debajo no llegaba a contar nunca.
            var err = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(EsperaFinderMs))
            {
                Procesos.Matar(p);
                return (false, "Finder no respondió a tiempo (puede que macOS esté esperando a que le des permiso)");
            }
            p.WaitForExit();
            return p.ExitCode == 0 ? (true, "") : (false, Corto(err.GetAwaiter().GetResult()));
        }
        catch (Exception e) { return (false, e.Message); }
    }

    private static (bool Ok, string Error) MoverATrashDelUsuario(string ruta)
    {
        try
        {
            var casa = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (casa.Length == 0) return (false, "no se pudo localizar la carpeta del usuario");

            var papelera = System.IO.Path.Combine(casa, ".Trash");
            System.IO.Directory.CreateDirectory(papelera);
            System.IO.File.Move(ruta, DestinoLibre(papelera, System.IO.Path.GetFileName(ruta)));
            return (true, "");
        }
        catch (Exception e) { return (false, e.Message); }
    }

    /// <summary>
    /// Un nombre que no pise nada en la papelera. Si ya hay un "tema.mp3", el siguiente entra como
    /// "tema 2.mp3": machacar en la papelera el archivo de una limpieza anterior sería perder algo
    /// que el usuario todavía podía recuperar.
    /// </summary>
    internal static string DestinoLibre(string carpeta, string nombre)
    {
        var destino = System.IO.Path.Combine(carpeta, nombre);
        if (!System.IO.File.Exists(destino) && !System.IO.Directory.Exists(destino)) return destino;

        var baseName = System.IO.Path.GetFileNameWithoutExtension(nombre);
        var ext = System.IO.Path.GetExtension(nombre);
        for (var i = 2; i < 10_000; i++)
        {
            destino = System.IO.Path.Combine(carpeta, $"{baseName} {i}{ext}");
            if (!System.IO.File.Exists(destino) && !System.IO.Directory.Exists(destino)) return destino;
        }
        return System.IO.Path.Combine(carpeta, $"{baseName} {Guid.NewGuid():N}{ext}");
    }

    /// <summary>Escapa una ruta para meterla entre comillas en un AppleScript.</summary>
    internal static string EscaparAppleScript(string ruta)
        => ruta.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Corto(string s)
    {
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 160 ? s : s[..160];
    }
}
