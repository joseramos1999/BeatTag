using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Etiquetador.Core.Providers;

/// <summary>Elige el género "más útil para DJ" de las listas style/genre (port de Pick-Genre).</summary>
public static class GenrePicker
{
    private const string Pref =
        "Reggaeton|Trap|Dembow|Moombahton|Dancehall|Reggae|Latin|Hip Hop|Hip-Hop|House|Tech House|" +
        "Deep House|Techno|EDM|Electro|Salsa|Bachata|Merengue|Cumbia|Afrobeats|Afrobeat|Dance-pop|" +
        "Synth-pop|Pop|R&B|Funk|Soul|Rock|Disco|Flamenco";

    public static string Pick(IReadOnlyList<string>? style, IReadOnlyList<string>? genre)
    {
        var c = new List<string>();
        if (style != null) c.AddRange(style);
        if (genre != null) c.AddRange(genre);
        foreach (var x in c)
            if (Regex.IsMatch(x, "^(" + Pref + ")$", RegexOptions.IgnoreCase)) return x;
        if (style is { Count: > 0 }) return style[0];
        if (genre is { Count: > 0 }) return genre[0];
        return "";
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
}
