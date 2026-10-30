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
    // unidad, y para saberlo no basta la letra: en macOS cada disco es un punto de montaje bajo
    // /Volumes, y en Windows una carpeta puede ser el punto de montaje de otro disco.

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

    private static string ConBarra(string ruta)
    {
        var normal = Path.AltDirectorySeparatorChar == Path.DirectorySeparatorChar
            ? ruta
            : ruta.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normal.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }
}
