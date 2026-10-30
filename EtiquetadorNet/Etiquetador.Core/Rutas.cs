namespace Etiquetador.Core;

/// <summary>Comparaciones entre rutas de carpeta que no dependen de que existan en disco.</summary>
public static class Rutas
{
    /// <summary>
    /// <paramref name="ruta"/> cuelga de <paramref name="raiz"/>. Se compara con la barra final
    /// puesta a propósito: sin ella «C:\Musica2» pasaría por estar dentro de «C:\Musica». Ser la
    /// MISMA carpeta no cuenta como estar dentro.
    /// </summary>
    public static bool EstaDentroDe(string ruta, string raiz)
    {
        if (string.IsNullOrWhiteSpace(ruta) || string.IsNullOrWhiteSpace(raiz)) return false;
        var r = ConBarra(ruta);
        var q = ConBarra(raiz);
        return r.Length > q.Length && r.StartsWith(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Las dos rutas nombran la misma carpeta (sin distinguir mayúsculas ni la barra final).</summary>
    public static bool MismaCarpeta(string a, string b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
           && string.Equals(ConBarra(a), ConBarra(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Una contiene a la otra, en cualquier sentido, o son la misma.</summary>
    public static bool Solapan(string a, string b)
        => MismaCarpeta(a, b) || EstaDentroDe(a, b) || EstaDentroDe(b, a);

    // --- Unidades (Organizar por género, B2) ---
    //
    // File.Move entre dos unidades no renombra: copia el archivo entero y borra el original. MEDIDO en
    // el disco de origen de una biblioteca real (129 MB/s), 114 GB serían unos 15 minutos, y un corte a
    // mitad deja un archivo copiado a medias. Por eso Organizar por género solo mueve dentro de la misma
    // unidad, y para saberlo no basta la letra ni la ruta:
    //   · en macOS cada disco es un punto de montaje bajo /Volumes (DriveInfo los lista todos, también
    //     los montados en cualquier carpeta), y una carpeta puede ser un enlace simbólico a otro disco;
    //   · en Windows DriveInfo solo da letras de unidad (revisión 1: «C:\, D:\, E:\»), y una carpeta puede
    //     ser una unión, un enlace o el punto de montaje de otro disco.
    // DiscoReal (Organizar por género) compara en Windows el número de serie del volumen de cada carpeta
    // (VolumenDe, que sigue uniones, enlaces y montajes) y, si no lo tiene, el punto de montaje de la
    // ruta con los enlaces resueltos (RutaReal + UnidadDe). UnidadDe por sí sola solo mira la ruta.

    /// <summary>
    /// La unidad de una ruta: el punto de montaje MÁS LARGO que la contiene («/Volumes/USB» antes que
    /// «/»). Si ninguno la contiene (una ruta de red, una lista vacía), la raíz de la ruta
    /// (<see cref="Path.GetPathRoot(string)"/>).
    /// </summary>
    public static string UnidadDe(string ruta, IEnumerable<string> puntosDeMontaje)
    {
        string? mejor = null;
        foreach (var p in puntosDeMontaje)
        {
            if (string.IsNullOrWhiteSpace(p) || !(MismaCarpeta(ruta, p) || EstaDentroDe(ruta, p))) continue;
            if (mejor == null || ConBarra(p).Length > ConBarra(mejor).Length) mejor = p;
        }
        return mejor ?? Path.GetPathRoot(ruta) ?? "";
    }

    /// <summary>Las dos rutas están en la misma unidad (ver <see cref="UnidadDe"/>).</summary>
    public static bool MismaUnidad(string a, string b, IEnumerable<string> puntosDeMontaje)
    {
        var puntos = puntosDeMontaje as IReadOnlyCollection<string> ?? puntosDeMontaje.ToList();
        var ua = UnidadDe(a, puntos);
        var ub = UnidadDe(b, puntos);
        return ua.Length == 0 ? ub.Length == 0 : MismaCarpeta(ua, ub);
    }

    /// <summary>
    /// Los puntos de montaje de las unidades preparadas de este equipo. Una unidad que no responde
    /// (un lector de tarjetas vacío, una unidad de red caída) no está; si el sistema no deja ni
    /// listarlas, la lista sale vacía y <see cref="UnidadDe"/> se queda con la raíz de cada ruta.
    /// </summary>
    public static IReadOnlyList<string> PuntosDeMontaje()
    {
        var puntos = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try { if (d.IsReady) puntos.Add(d.RootDirectory.FullName); }
                catch { /* esa unidad no responde: no cuenta */ }
            }
        }
        catch { /* sin lista de unidades: cada ruta con su raíz */ }
        return puntos;
    }

    /// <summary>
    /// La ruta con los enlaces de cada tramo resueltos: si una carpeta del camino es un enlace
    /// simbólico o una unión que lleva a otro sitio, la ruta sigue desde allí, y ese destino se vuelve a
    /// resolver entero, porque también puede pasar por otros enlaces. Los tramos que no existen (una
    /// carpeta que aún no se ha creado) se quedan como están. Sin enlaces, la misma ruta.
    /// </summary>
    /// <param name="destinoDeEnlace">
    /// Adónde lleva una carpeta si es un enlace, o null si no lo es. Por defecto,
    /// <see cref="DestinoDeEnlace"/>; las pruebas simulan uno.
    /// </param>
    public static string RutaReal(string ruta, Func<string, string?>? destinoDeEnlace = null)
    {
        var saltos = 0;
        return RutaReal(ruta, destinoDeEnlace ?? DestinoDeEnlace, ref saltos);
    }

    /// <summary>Cuántos enlaces se siguen como mucho para una ruta: evita dar vueltas si dos enlaces se apuntan.</summary>
    private const int SaltosMaximos = 40;

    private static string RutaReal(string ruta, Func<string, string?> destinoDeEnlace, ref int saltos)
    {
        var raiz = Path.GetPathRoot(ruta);
        if (string.IsNullOrEmpty(raiz)) return ruta;
        var actual = raiz;
        var tramos = ruta[raiz.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var tramo in tramos)
        {
            actual = Path.Combine(actual, tramo);
            if (saltos >= SaltosMaximos || destinoDeEnlace(actual) is not { Length: > 0 } destino) continue;
            saltos++;
            var absoluto = Path.IsPathRooted(destino) ? destino : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(actual) ?? "", destino));
            // El destino puede pasar a su vez por otro enlace (revisión 2). En macOS, ResolveLinkTarget solo
            // sigue el último tramo: un enlace a «/var/…/Destino» se queda ahí, y «/var» es a su vez un
            // enlace a «/private/var». Sin volver a resolverlo, la misma carpeta daba dos rutas reales, y un
            // enlace cuyo destino pasa por otro enlace a otro disco se daba por la misma unidad.
            actual = RutaReal(absoluto, destinoDeEnlace, ref saltos);
        }
        return actual;
    }

    /// <summary>
    /// Adónde lleva <paramref name="carpeta"/> si es un enlace simbólico o una unión (en Windows, también
    /// una carpeta donde está montado otro disco), siguiendo los enlaces hasta el final; null si no es un
    /// enlace, si no existe o si no se puede saber. Una carpeta de OneDrive (otro tipo de punto de
    /// análisis) no es un enlace.
    /// </summary>
    public static string? DestinoDeEnlace(string carpeta)
    {
        try
        {
            var info = new DirectoryInfo(carpeta);
            if (!info.Exists || info.LinkTarget == null) return null;
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch { return null; }
    }

    /// <summary>
    /// En Windows, el número de serie del volumen donde está de verdad <paramref name="carpeta"/>, que
    /// tiene que existir: abrirla sigue las uniones, los enlaces y las carpetas donde está montado otro
    /// disco, así que dos carpetas con el mismo número están en el mismo disco aunque la ruta diga otra
    /// cosa. null fuera de Windows o si no se puede abrir.
    /// </summary>
    public static string? VolumenDe(string carpeta)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return VolumenWindows(carpeta); }
        catch { return null; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? VolumenWindows(string carpeta)
    {
        const uint LeerAtributos = 0x80;                  // FILE_READ_ATTRIBUTES
        const uint CompartirTodo = 0x1 | 0x2 | 0x4;      // FILE_SHARE_READ | WRITE | DELETE
        const uint AbrirExistente = 3;                    // OPEN_EXISTING
        const uint Carpetas = 0x02000000;                 // FILE_FLAG_BACKUP_SEMANTICS: deja abrir una carpeta
        using var h = CreateFile(carpeta, LeerAtributos, CompartirTodo, IntPtr.Zero, AbrirExistente, Carpetas, IntPtr.Zero);
        if (h.IsInvalid || !GetFileInformationByHandle(h, out var info)) return null;
        return info.VolumeSerialNumber.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct InformacionArchivo   // BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string nombre, uint acceso, uint compartir, IntPtr seguridad,
                                                                                uint disposicion, uint opciones, IntPtr plantilla);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle archivo, out InformacionArchivo info);

    private static string ConBarra(string ruta)
    {
        var normal = Path.AltDirectorySeparatorChar == Path.DirectorySeparatorChar
            ? ruta
            : ruta.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normal.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }
}
