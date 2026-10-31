using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etiquetador.Core.Dj;

namespace Etiquetador.Core.Providers;

/// <summary>Elige el género "más útil para DJ" de las listas style/genre (port de Pick-Genre).</summary>
public static class GenrePicker
{
    // «Rap/Hip Hop» es como lo nombra Deezer; al normalizarlo queda en «Hip Hop».
    private const string Pref =
        "Reggaeton|Trap|Dembow|Moombahton|Dancehall|Reggae|Latin|Hip Hop|Hip-Hop|Rap/Hip Hop|House|Tech House|" +
        "Deep House|Techno|EDM|Electro|Salsa|Bachata|Merengue|Cumbia|Afrobeats|Afrobeat|Dance-pop|" +
        "Synth-pop|Pop|R&B|Funk|Soul|Rock|Disco|Flamenco";

    /// <summary>Lo que se escribe cuando un álbum de Deezer es a la vez latino y de rap.</summary>
    public const string LatinUrban = "Latin Urban";

    /// <summary>
    /// El género de un disco de Discogs: el primero de sus estilos y después de sus géneros que está en
    /// la lista de preferencias, salvo que entre los ESTILOS haya uno concreto: ese manda sobre uno
    /// genérico (<see cref="FamiliasGenero.EsGenerico"/>, como «Latin» o «Electro») que vaya antes.
    /// Discogs también tiene «Latin» como estilo, y en una biblioteca real «Feid - Vente Conmigo», con
    /// los estilos [Latin, Reggaeton], y «Toman - Verano En NY», con [Latin, Tech House], se
    /// etiquetaban Latin (unos 4 de 78 discos).
    ///
    /// Solo entre los estilos. Los géneros de Discogs son amplios: en un disco sin estilos,
    /// [Latin, Hip Hop] sigue dando Latin, que «Género por carpeta y artista» puede afinar sin
    /// preguntar, y no un «Hip Hop» que puede ser reggaeton (ver <see cref="PickDeezer"/>). Por lo
    /// mismo, <see cref="PickDeezer"/>, que llama a este método sin estilos, elige como antes.
    /// </summary>
    public static string Pick(IReadOnlyList<string>? style, IReadOnlyList<string>? genre)
    {
        if (style != null)
            foreach (var x in style)
                if (EsPreferido(x) && !FamiliasGenero.EsGenerico(x)) return x;
        var c = new List<string>();
        if (style != null) c.AddRange(style);
        if (genre != null) c.AddRange(genre);
        foreach (var x in c)
            if (EsPreferido(x)) return x;
        if (style is { Count: > 0 }) return style[0];
        if (genre is { Count: > 0 }) return genre[0];
        return "";
    }

    /// <summary>
    /// Elige entre los géneros de un álbum de Deezer, ya con su nombre en inglés
    /// (<see cref="GeneroDeezer.EnIngles"/>) y en el orden en que los da. Deezer clasifica sobre todo
    /// por géneros gruesos (Latin, Pop, Dance, Electro, Rap/Hip Hop…), y quedarse con el primero que
    /// reconoce <see cref="Pick"/> no siempre da el que más dice:
    /// - Un estilo concreto manda sobre los gruesos que lo acompañan: [Latin, Reggaeton] → Reggaeton.
    /// - Latin con Rap/Hip Hop es urbano latino: [Rap/Hip Hop, Latin] → Latin Urban. Antes se escribía
    ///   «Hip Hop», y en la medición de Dj.GeneroDeezer el álbum «Rap/Hip Hop» era Reggaeton en 9
    ///   canciones y Hip Hop en 8.
    /// - Latin con otro grueso se queda en Latin (o en Latin Pop si viene): Pop o Dance no dicen si es
    ///   reggaeton, bachata o pop latino, y Latin al menos dice que es latino.
    /// - Si no hay nada latino, como <see cref="Pick"/>.
    ///
    /// Latin, Latin Urban, Dance y Electro son genéricos (<see cref="FamiliasGenero.EsGenerico"/>):
    /// «Género por carpeta y artista» los cambia por el estilo del artista sin preguntar, cosa que no
    /// hace con un «Hip Hop» o un «Pop» equivocados.
    /// </summary>
    public static string PickDeezer(IReadOnlyList<string> generos)
    {
        foreach (var g in generos)
            if (!GeneroDeezer.EsGrueso(g) && EsPreferido(g)) return g;
        if (generos.Any(EsLatino))
        {
            if (generos.Any(g => Igual(g, "Rap/Hip Hop"))) return LatinUrban;
            return generos.FirstOrDefault(g => Igual(g, "Latin Pop")) ?? generos.First(EsLatino);
        }
        return Pick(null, generos);
    }

    /// <summary>Extrae style/genre (arrays de strings) de un item JSON y aplica Pick.</summary>
    public static string Pick(JsonNode? item)
        => Pick(StringList(J.P(item, "style")), StringList(J.P(item, "genre")));

    internal static List<string> StringList(JsonNode? n)
    {
        var list = new List<string>();
        if (n is JsonArray arr)
            foreach (var e in arr) { var s = J.S(e); if (s.Length > 0) list.Add(s); }
        return list;
    }

    private static bool EsPreferido(string genero) => Regex.IsMatch(genero, "^(" + Pref + ")$", RegexOptions.IgnoreCase);

    private static bool EsLatino(string genero) => Igual(genero, "Latin") || Igual(genero, "Latin Pop");

    private static bool Igual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
