using System.Text.RegularExpressions;

namespace Etiquetador.Core;

/// <summary>
/// Normaliza el género devuelto por las fuentes: toma el segmento principal, unifica sinónimos y
/// grafías (hip-hop/rap→Hip Hop, rnb→R&B, reguetón→Reggaeton, tech-house→Tech House…) y da formato.
/// </summary>
public static class GenreNormalizer
{
    private static readonly (string[] Aliases, string Canonical)[] Map =
    {
        (new[] { "hip hop", "hip-hop", "hiphop", "rap" }, "Hip Hop"),
        (new[] { "r&b", "rnb", "r and b", "rhythm and blues", "r&b/soul" }, "R&B"),
        (new[] { "drum and bass", "drum & bass", "drum n bass", "dnb", "d&b" }, "Drum & Bass"),
        (new[] { "reggaeton", "reggaetón", "reguetón", "regueton", "perreo" }, "Reggaeton"),
        (new[] { "tech house", "tech-house", "techhouse" }, "Tech House"),
        (new[] { "deep house", "deep-house" }, "Deep House"),
        (new[] { "edm", "electronic dance music" }, "EDM"),
        (new[] { "latin urban", "latin urbano", "urbano latino", "urban latin", "musica urbana", "música urbana", "urbano" }, "Latin Urban"),
        (new[] { "dance pop", "dance-pop" }, "Dance-Pop"),
        (new[] { "synth pop", "synth-pop", "synthpop" }, "Synth-Pop"),
        (new[] { "electropop", "electro pop" }, "Electropop"),
        (new[] { "afrobeats", "afro beats", "afrobeat" }, "Afrobeats"),
        (new[] { "electronica", "electronic", "electrónica" }, "Electrónica"),
        (new[] { "dubstep" }, "Dubstep"),
        (new[] { "trance" }, "Trance"),
        (new[] { "drill" }, "Drill"),
        (new[] { "house" }, "House"),
        (new[] { "techno" }, "Techno"),
        (new[] { "trap" }, "Trap"),
        (new[] { "dembow" }, "Dembow"),
        (new[] { "dancehall" }, "Dancehall"),
        (new[] { "moombahton" }, "Moombahton"),
        (new[] { "bachata" }, "Bachata"),
        (new[] { "salsa" }, "Salsa"),
        (new[] { "merengue" }, "Merengue"),
        (new[] { "cumbia" }, "Cumbia"),
        (new[] { "flamenco" }, "Flamenco"),
        (new[] { "disco" }, "Disco"),
        (new[] { "funk" }, "Funk"),
        (new[] { "soul" }, "Soul"),
        (new[] { "pop" }, "Pop"),
        (new[] { "rock" }, "Rock"),
    };

    public static string Canonical(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        // Segmento principal si viene con separadores (Pop/Rock, "Hip-Hop; Rap"...)
        var first = raw;
        foreach (var part in Regex.Split(raw, @"\s*[/;,|]\s*"))
            if (!string.IsNullOrWhiteSpace(part)) { first = part.Trim(); break; }

        var key = Loose(first);
        foreach (var (aliases, canon) in Map)
            foreach (var a in aliases)
                if (key == Loose(a)) return canon;

        return TextUtils.TitleCase(first);
    }

    // Clave laxa para comparar: minúsculas, sin acentos, colapsa espacios, conserva '&'.
    private static string Loose(string s)
    {
        var d = TextUtils.RemoveDiacritics(s).ToLowerInvariant();
        d = Regex.Replace(d, "[^a-z0-9& ]", " ");
        return Regex.Replace(d, @"\s+", " ").Trim();
    }
}
