using System;
using System.IO;

namespace Etiquetador.Core.Pipeline;

/// <summary>Valida el nombre destino de un renombrado: solo nombre de archivo, dentro de la carpeta, extensión origen.</summary>
public static class RenameSafety
{
    /// <summary>
    /// Convierte un nombre propuesto (posiblemente editado por el usuario) en un nombre de archivo seguro:
    /// rechaza rutas/separadores/'..'/rutas absolutas, fuerza la extensión del original y deduplica.
    /// </summary>
    public static bool TryResolveTarget(string? proposedNew, string originalName, string dir, out string targetName, out string error)
    {
        targetName = "";
        error = "";
        if (string.IsNullOrWhiteSpace(proposedNew)) { error = "nombre vacío"; return false; }
        if (proposedNew.IndexOfAny(new[] { '/', '\\' }) >= 0) { error = "el nombre no puede contener separadores de ruta"; return false; }
        if (proposedNew.Contains("..")) { error = "el nombre no puede contener '..'"; return false; }
        if (Path.IsPathRooted(proposedNew)) { error = "el nombre no puede ser una ruta absoluta"; return false; }

        var name = Path.GetFileName(proposedNew);
        if (!string.Equals(name, proposedNew, StringComparison.Ordinal)) { error = "nombre de archivo inválido"; return false; }

        var origExt = Path.GetExtension(originalName);
        var baseName = TextUtils.Sanitize(Path.GetFileNameWithoutExtension(name));
        if (baseName.Length == 0) { error = "nombre vacío tras sanear"; return false; }

        var candidate = baseName + origExt;
        int nn = 2;
        var t = candidate;
        while (File.Exists(Path.Combine(dir, t)) && !string.Equals(t, originalName, StringComparison.OrdinalIgnoreCase))
        {
            t = baseName + $" ({nn})" + origExt;
            nn++;
        }
        targetName = t;
        return true;
    }
}
