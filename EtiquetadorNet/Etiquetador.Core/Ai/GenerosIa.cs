using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etiquetador.Core.Providers;

namespace Etiquetador.Core.Ai;

public enum OrigenGenero
{
    /// <summary>Se queda como está: no hay nada que proponer.</summary>
    SinCambio,
    /// <summary>Grafía o sinónimo conocido («Hip-Hop» → «Hip Hop»). Fiable.</summary>
    Regla,
    /// <summary>No es un género: un usuario, un record pool, «desconocido». Se propone vaciarlo.</summary>
    NoEsGenero,
    /// <summary>Lo ha propuesto la IA, eligiendo entre los géneros que ya usa la biblioteca.</summary>
    Ia,
}

/// <summary>Cómo quedaría un valor de género. <see cref="Propuesto"/> vacío = quitar el género.</summary>
public sealed record PropuestaGenero(string Original, int Canciones, string Propuesto, OrigenGenero Origen)
{
    public bool Cambia => Origen != OrigenGenero.SinCambio && !string.Equals(Original, Propuesto, StringComparison.Ordinal);
}

/// <summary>
/// Unificar los géneros de la biblioteca: «Latin», «Latino» y «Latijnse muziek» son lo mismo, y
/// «@luigi.beltran» no es un género.
///
/// MEDIDO (llama3.2 3B, 30 géneros reales de una biblioteca con 340 distintos): pidiéndole el
/// género canónico libremente acertaba la mitad, y los fallos eran de los que destruyen datos:
/// «Folk», «Soundtrack», «Dance-Pop» y «Electronica» los daba por «Desconocido» (se borrarían), y
/// «Mambo» o «Electro Latino» los convertía en «Reggaeton».
///
/// Por eso la IA aquí tiene las manos atadas:
///   · Primero van las reglas fijas: las grafías conocidas (<see cref="GenreNormalizer"/>) y lo que
///     claramente no es un género. Eso no necesita IA.
///   · La IA solo ve los géneros RAROS (pocos usos), y solo puede elegir uno de los géneros que la
///     biblioteca ya usa mucho, o dejarlo igual. No puede inventar un nombre ni vaciar nada: si
///     devuelve algo fuera de la lista, se ignora.
///   · Nada se escribe sin que el usuario lo marque, y lo que propone la IA sale DESMARCADO.
/// </summary>
public static class GenerosIa
{
    /// <summary>
    /// Qué parte de la biblioteca tiene que usar un género para contar como referencia, además de los
    /// que ya conoce <see cref="GenreNormalizer"/>. Es una PROPORCIÓN y no un número fijo, para que
    /// valga igual con quinientas canciones que con quince mil.
    /// </summary>
    public const double ProporcionParaDestino = 0.005;

    /// <summary>Por debajo de esto nunca cuenta como referencia, por pequeña que sea la biblioteca.</summary>
    public const int MinimoParaDestino = 10;

    /// <summary>
    /// UN género por consulta, y es medido: con los veinte a la vez, llama3.2 devolvió uno solo; de
    /// uno en uno acertó quince y no estropeó ninguno (dejó «Soundtrack», «Rumba» y «Techno Pop»
    /// como estaban). Curiosamente llama3.1:8b lo hizo PEOR de uno en uno: «Urban House» → Hip Hop,
    /// «Soundtrack» → Dance, «Rumba» → Salsa. Más grande no es más prudente.
    /// </summary>
    public const string Sistema =
        "Eres un experto en generos musicales para DJs. Recibes un GENERO RARO y una lista de GENEROS VALIDOS. Si el genero " +
        "raro es claramente el mismo estilo que uno de los validos (sinonimo, traduccion u otra forma de escribirlo), devuelve " +
        "ese valido EXACTAMENTE como aparece en la lista. Si no es claramente el mismo estilo, devuelve el genero raro sin " +
        "cambios. Nunca inventes un genero nuevo.\n" +
        "Responde SOLO JSON: {\"propuesto\":\"...\"}";

    // El dominio («z1.fm», «…blogspot.com») salió en una biblioteca real: la IA lo convertía en «EDM».
    private static readonly Regex NoGenero = new(
        @"^\s*@|\b(desconocid[oa]|unknown|other|otros?|genre|g[eé]nero|none|n/?a|sin g[eé]nero|www)\b|\w\.(com|net|org|fm|es|mx|ru|info|io)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Pool = new(Descriptors.PoolRe);

    /// <summary>Cada valor de género tal cual está escrito, con cuántas canciones lo llevan. Los vacíos no cuentan.</summary>
    public static IReadOnlyList<(string Original, int Canciones)> Contar(IEnumerable<Track> canciones)
        => canciones.Where(t => !string.IsNullOrWhiteSpace(t.Genre))
                    .GroupBy(t => t.Genre!, StringComparer.Ordinal)
                    .Select(g => (g.Key, g.Count()))
                    .OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

    /// <summary>No es un nombre de estilo: un usuario de redes, un record pool, «desconocido», un número.</summary>
    public static bool NoEsGenero(string original)
    {
        var s = original.Trim();
        if (s.Length == 0) return false;
        if (!Regex.IsMatch(s, @"\p{L}")) return true;
        return NoGenero.IsMatch(s) || Pool.IsMatch(s);
    }

    /// <summary>Lo que proponen las reglas, sin IA. <see cref="OrigenGenero.SinCambio"/> si no saben qué hacer.</summary>
    public static PropuestaGenero PorReglas(string original, int canciones)
    {
        if (NoEsGenero(original)) return new PropuestaGenero(original, canciones, "", OrigenGenero.NoEsGenero);
        // Solo cuenta como regla si lleva a un nombre que el normalizador CONOCE. Para lo demás,
        // Canonical() solo pone mayúsculas («Latijnse muziek» → «Latijnse Muziek»): reescribir las
        // etiquetas de cientos de archivos por eso no compensa, y además lo escondía de la IA, que es
        // la que puede ver que es «Latino».
        var canonico = GenreNormalizer.Canonical(original);
        var conocido = GenreNormalizer.Conocidos.Contains(canonico, StringComparer.Ordinal);
        if (conocido && !string.Equals(canonico, original, StringComparison.Ordinal))
            return new PropuestaGenero(original, canciones, canonico, OrigenGenero.Regla);
        if (!string.Equals(original.Trim(), original, StringComparison.Ordinal))
            return new PropuestaGenero(original, canciones, original.Trim(), OrigenGenero.Regla);
        return new PropuestaGenero(original, canciones, original, OrigenGenero.SinCambio);
    }

    /// <summary>
    /// Los géneros entre los que puede elegir la IA: los que la biblioteca ya usa en muchas canciones,
    /// contados DESPUÉS de aplicar las reglas (así «Latin» y «Latino» suman juntos).
    /// </summary>
    /// <remarks>
    /// Antes bastaba con tener diez canciones para ser referencia, y la prueba lo pilló: «Latijnse
    /// muziek», con doce, pasaba a ser un destino más y nunca se preguntaba por él.
    /// </remarks>
    public static IReadOnlyList<string> Destinos(IReadOnlyCollection<PropuestaGenero> propuestas)
    {
        var total = propuestas.Sum(p => p.Canciones);
        var umbral = Math.Max(MinimoParaDestino, (int)Math.Ceiling(total * ProporcionParaDestino));
        var conocidos = new HashSet<string>(GenreNormalizer.Conocidos, StringComparer.OrdinalIgnoreCase);

        return propuestas.Where(p => p.Origen != OrigenGenero.NoEsGenero)
                         .GroupBy(p => p.Propuesto, StringComparer.OrdinalIgnoreCase)
                         .Where(g => conocidos.Contains(g.Key) || g.Sum(p => p.Canciones) >= umbral)
                         .Select(g => g.OrderByDescending(p => p.Canciones).First().Propuesto)
                         .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
                         .ToList();
    }

    /// <summary>
    /// Los que se le preguntan a la IA: los que las reglas dejaron igual y no son un nombre que el
    /// normalizador ya conozca. Un género frecuente también se pregunta («Latin» puede ser «Latino»):
    /// si la IA no ve un equivalente claro, lo devuelve igual y no cambia nada.
    /// </summary>
    public static IReadOnlyList<string> ParaIa(IEnumerable<PropuestaGenero> propuestas)
    {
        var conocidos = new HashSet<string>(GenreNormalizer.Conocidos, StringComparer.OrdinalIgnoreCase);
        return propuestas.Where(p => p.Origen == OrigenGenero.SinCambio && !conocidos.Contains(p.Original.Trim()))
                         .Select(p => p.Original).ToList();
    }

    public static string Peticion(string raro, IEnumerable<string> destinos)
        => "GENEROS VALIDOS:\n" + string.Join("\n", destinos) + "\n\nGENERO RARO: " + raro;

    /// <summary>
    /// Lo que devuelve la IA, reducido a lo aceptable: tiene que ser uno de los destinos, escrito como
    /// esté en la lista, y distinto del original. Si no, null: se queda como estaba.
    /// </summary>
    public static string? Interpretar(JsonNode? json, string raro, IEnumerable<string> destinos)
    {
        var propuesto = J.S(J.P(json, "propuesto")).Trim();
        var destino = destinos.FirstOrDefault(d => string.Equals(d, propuesto, StringComparison.OrdinalIgnoreCase));
        if (destino == null || string.Equals(destino, raro.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        return destino;
    }
}
