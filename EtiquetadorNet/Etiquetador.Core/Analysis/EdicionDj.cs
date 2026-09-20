using System.Text.RegularExpressions;

namespace Etiquetador.Core.Analysis;

/// <summary>
/// Ediciones hechas para pinchar: intros largas, open shows, acapellas, mashups, transiciones…
///
/// No son copias sobrantes de una canción: son HERRAMIENTAS distintas. «Bichota (Hype Intro)» existe
/// precisamente para poder entrar encima, y quien la tiene la quiere además del original. Por eso
/// Duplicados puede dejarlas fuera del análisis: si no, aparecen una y otra vez emparejadas con su
/// original y tapan los duplicados de verdad.
///
/// Lo que NO entra aquí, a propósito: «Extended», «Remix», «Radio Edit», «Club Mix», «Original Mix».
/// Son versiones que también publica el catálogo, no ediciones de DJ; excluirlas escondería
/// duplicados reales (dos copias de la misma extended, por ejemplo).
/// </summary>
public static class EdicionDj
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Los descriptores que marcan una edición para pinchar. «Intro» y «outro» a secas entran: en una
    /// biblioteca de DJ es lo que significan, y el riesgo es que se excluya una canción titulada
    /// «Intro», que solo dejaría de compararse con sus copias.
    /// </summary>
    /// El plural cuenta: las carpetas se llaman «Hype Intros», «Acapellas», «Transiciones».
    private static readonly Regex Descriptor = new(
        @"\b((?:hype|melodic|break|drop|long)\s*intros?|intro\s*hype|open\s*shows?|"
        + @"starters?|quick\s*(?:hits?|edits?)|short\s*edits?|redrums?|aca\s*(?:in|out)|acapellas?|percapellas?|"
        + @"intros?|outros?|transitions?|transiciones|segues?|blends?|mash\s*ups?|mashups?|loops?|dj\s*tools?)\b", IC);

    /// <summary>Es una edición de DJ, por lo que dice el nombre del archivo (o la carpeta que lo contiene).</summary>
    public static bool Es(string? rutaOArchivo)
    {
        if (string.IsNullOrWhiteSpace(rutaOArchivo)) return false;
        var nombre = Path.GetFileNameWithoutExtension(rutaOArchivo);

        // La carpeta cuenta igual que el nombre: quien guarda sus intros o sus acapellas en una
        // carpeta propia no las repite en cada archivo.
        var carpeta = Path.GetFileName(Path.GetDirectoryName(rutaOArchivo) ?? "");

        return Descriptor.IsMatch(nombre) || Descriptor.IsMatch(carpeta)
               || Matching.IsMezclaDeVariosTemas(nombre) || Identificacion.EsAcapella(rutaOArchivo);
    }
}
