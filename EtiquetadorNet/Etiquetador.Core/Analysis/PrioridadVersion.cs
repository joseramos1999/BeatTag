using System.Text.RegularExpressions;

namespace Etiquetador.Core.Analysis;

/// <summary>Qué clase de versión es un archivo, a efectos de cuál prefiere pinchar un DJ.</summary>
public enum TipoVersion
{
    /// <summary>Extended, hype intro y demás ediciones pensadas para mezclar. Lo primero que se quiere.</summary>
    Extendida,

    /// <summary>La canción tal cual se publicó: sin adornos, «Original Mix», «Radio Edit», «Clean».</summary>
    Original,

    /// <summary>Otro artista la ha rehecho. Suena distinta, así que va la última.</summary>
    Remix,
}

/// <summary>
/// De varias copias de la misma canción, cuál se quiere.
///
/// Responde a una pregunta distinta de la de <see cref="RemixParser"/>, que averigua QUIÉN firma una
/// versión. Aquí solo importa el orden en que las quiere quien va a pinchar:
///
///   1. <b>Extendida</b> — extended, hype intro y las ediciones con entrada larga. Son las que
///      permiten mezclar, y por eso van primero.
///   2. <b>Original</b> — la canción publicada. Sirve siempre.
///   3. <b>Remix</b> — la última: es otra canción hecha con esta, y quien busca el tema del chart
///      normalmente no busca la relectura de otro.
///
/// Antes no había criterio: el índice de Tendencias se quedaba con la primera copia que apareciera
/// en el escaneo, así que la lista y la carpeta que se genera podían llevarse un remix teniendo la
/// extended al lado.
/// </summary>
public static class PrioridadVersion
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Alguien la ha rehecho. Se mira PRIMERO: «Extended Remix» es un remix largo, no la versión
    /// larga del original, y confundirlos es justo el error que esto viene a evitar.
    /// </summary>
    private static readonly Regex EsRemix = new(
        @"\b(remix|rmx|bootleg|rework|refix|flip|vip|dub|redrum)\b", IC);

    /// <summary>
    /// Preparada para mezclar: la versión larga o con una entrada pensada para entrar encima.
    /// «Intro» a secas entra aquí porque en una biblioteca de DJ es siempre eso; el riesgo de que
    /// sea el título de un tema es pequeño y lo único que cambiaría es qué copia se prefiere.
    /// </summary>
    private static readonly Regex EsExtendida = new(
        @"\b(extended|hype\s*intro|melodic\s*intro|break\s*intro|drop\s*intro|long\s*intro|open\s*show|starter|intro)\b", IC);

    /// <summary>Qué clase de versión es, a partir del nombre del archivo.</summary>
    public static TipoVersion De(string? nombreArchivo)
    {
        var s = Path.GetFileNameWithoutExtension(nombreArchivo ?? "");
        if (s.Length == 0) return TipoVersion.Original;

        if (EsRemix.IsMatch(s)) return TipoVersion.Remix;
        if (EsExtendida.IsMatch(s)) return TipoVersion.Extendida;
        return TipoVersion.Original;
    }

    /// <summary>Para ordenar: cuanto MENOR, antes se quiere.</summary>
    public static int Orden(TipoVersion tipo) => tipo switch
    {
        TipoVersion.Extendida => 0,
        TipoVersion.Original => 1,
        _ => 2,
    };

    /// <summary>Atajo: el orden directamente desde el nombre del archivo.</summary>
    public static int OrdenDe(string? nombreArchivo) => Orden(De(nombreArchivo));

    /// <summary>Cómo se llama en pantalla.</summary>
    public static string Nombre(TipoVersion tipo) => tipo switch
    {
        TipoVersion.Extendida => "Extended / Intro",
        TipoVersion.Original => "Original",
        _ => "Remix",
    };
}
