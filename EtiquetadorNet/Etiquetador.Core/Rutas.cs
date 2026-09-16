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

    private static string ConBarra(string ruta)
    {
        var normal = Path.AltDirectorySeparatorChar == Path.DirectorySeparatorChar
            ? ruta
            : ruta.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normal.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }
}
