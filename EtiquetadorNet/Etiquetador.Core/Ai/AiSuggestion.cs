using System.Text.RegularExpressions;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Core.Ai;

/// <summary>
/// Convierte la propuesta que la IA local no pudo verificar en una sugerencia que enseñarle al
/// usuario en "No encontradas".
///
/// El reparto de responsabilidades es deliberado: la IA propone y la persona decide. Estas
/// propuestas aciertan aproximadamente dos de cada tres veces, y el fallo típico -intercambiar
/// artista y título- se ve de un vistazo. Suficiente para ofrecerlas como atajo de un clic, muy
/// lejos de lo que hace falta para escribirlas solas en los archivos de alguien.
/// </summary>
public static class AiSuggestion
{
    /// <summary>Compone el nombre de archivo (sin extensión) que resultaría de aceptar la propuesta.</summary>
    public static string Compose(string? artist, string? title, string? version)
    {
        var a = Limpia(artist);
        var t = Limpia(title);
        if (t.Length == 0) return "";

        var v = Limpia(version);
        var name = a.Length > 0 ? $"{a} - {t}" : t;
        if (v.Length > 0) name = $"{name} ({v})";
        return TextUtils.Sanitize(TextUtils.ToAscii(name));
    }

    /// <summary>
    /// La sugerencia que corresponde a un resultado, o "" si no hay ninguna que ofrecer.
    ///
    /// Se calla cuando propondría lo mismo que la limpieza normal ya iba a dejar: enseñar una
    /// sugerencia que no cambia nada solo gasta la atención del usuario.
    /// </summary>
    public static string For(ProcessResult r)
    {
        var name = Compose(r.AiArtist, r.AiTitle, r.AiVersion);
        if (name.Length == 0) return "";

        // A veces el modelo devuelve el nombre sucio casi tal cual, con los BPM o la tonalidad
        // todavía pegados. Ahí la limpieza normal de la aplicación ya lo hace mejor, así que la
        // sugerencia sobra: sería ofrecerle al usuario un cambio a peor.
        if (Regex.IsMatch(name, @"\b\d{2,3}\s*(-\s*\d{2,3}\s*)?bpm\b", RegexOptions.IgnoreCase)) return "";

        var actual = TextUtils.Nk(Path.GetFileNameWithoutExtension(r.New));
        return TextUtils.Nk(name) == actual ? "" : name;
    }

    /// <summary>Quita comillas, guiones sueltos y espacios dobles de lo que devuelve el modelo.</summary>
    private static string Limpia(string? s)
    {
        var t = (s ?? "").Trim().Trim('"', '\'', '-', '_').Trim();
        return Regex.Replace(t, @"\s{2,}", " ");
    }
}
