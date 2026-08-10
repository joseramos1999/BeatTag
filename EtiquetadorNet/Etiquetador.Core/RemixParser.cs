using System.Text.RegularExpressions;

namespace Etiquetador.Core;

/// <summary>
/// Qué clase de versión es y quién la firma. <see cref="Kind"/> vacío = no es una versión de otro
/// (es el tema original). <see cref="Remixer"/> vacío = es un remix/edit pero sin autor identificable
/// (p. ej. "Extended Mix", que es de la propia discográfica).
/// </summary>
public readonly record struct RemixInfo(string Remixer, string Kind)
{
    /// <summary>Sin versión detectada (evita el default con cadenas nulas).</summary>
    public static readonly RemixInfo None = new("", "");

    /// <summary>Es una versión (remix, bootleg, edit, intro de pool…) y no el tema tal cual.</summary>
    public bool IsVersion => !string.IsNullOrEmpty(Kind);

    /// <summary>Se ha identificado a quién la firma.</summary>
    public bool HasRemixer => !string.IsNullOrEmpty(Remixer);

    /// <summary>Cómo se escribe en el nombre: "Tiesto Remix", o solo "Remix" si no hay autor.</summary>
    public string Label => HasRemixer ? $"{Remixer} {Kind}" : (Kind ?? "");
}

/// <summary>
/// Detecta remixes/bootlegs/edits y QUIÉN los firma, para no perder al remixer al limpiar el nombre
/// (para un DJ, "Hello (Tiesto Remix)" y "Hello" son pistas distintas).
/// </summary>
public static class RemixParser
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Tipos de versión que pueden llevar autor. El orden IMPORTA: los compuestos van primero,
    // si no "Hype Intro" se partiría y "Hype" acabaría contando como parte del nombre del autor.
    private const string KindRe = @"(?<kind>" +
        // compuestos (edits de pool)
        @"hype\s*intro|melodic\s*intro|break\s*intro|drop\s*intro|short\s*intro|long\s*intro|" +
        @"aca(?:pella)?\s*(?:intro|outro|in|out|starter|break|vocals|studio)|" +
        @"open\s*show|quick\s*(?:hit|edit)|short\s*edit|" +
        @"re\s*edit|re\s*drum|vip\s*mix|dub\s*mix|club\s*edit|party\s*starter|" +
        @"remix\s*pack|rmx\s*pack|edit\s*pack|extended\s*mix|extended|" +
        // simples
        @"remix|rmx|bootleg|rework|refix|mashup|flip|blend|vip|dub|edit|" +
        @"intro|outro|starter|transition|segue|redrum|loop|percapella|acapella|instrumental" +
        @")";

    // Palabras que describen la versión pero NO son el nombre de nadie ("Extended Mix", "Radio Edit").
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "extended", "radio", "club", "original", "album", "single", "short", "quick", "main",
        "clean", "dirty", "explicit", "instrumental", "acapella", "percapella", "intro", "outro",
        "hype", "melodic", "break", "starter", "transition", "segue", "redrum", "dj", "the",
        "official", "special", "new", "old", "live", "studio", "master", "remaster", "remastered",
        "version", "mix", "edit", "remix", "bootleg", "rework", "flip", "blend", "vip", "dub",
    };

    /// <summary>Analiza un nombre de archivo o un título y devuelve el tipo de versión y su autor.</summary>
    public static RemixInfo Parse(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return RemixInfo.None;

        // 1) "Remix by X" / "Remixed by X" (explícito, el más fiable).
        var m = Regex.Match(s, @"\b(?<kind>remix|rework|edit|bootleg|flip)(?:ed)?\s+by\s+(?<who>[^)\]\-–—]{2,60})", IC);
        if (m.Success)
        {
            var who = Clean(m.Groups["who"].Value);
            if (who.Length > 0) return new RemixInfo(who, Norm(m.Groups["kind"].Value));
        }

        // 2) Entre paréntesis o corchetes: "(Tiesto Remix)", "[Pedro Cabrera Bootleg]".
        foreach (Match g in Regex.Matches(s, @"[\(\[]([^)\]]{2,80})[\)\]]"))
        {
            var inner = g.Groups[1].Value;
            var r = FromFragment(inner);
            if (r.IsVersion) return r;
        }

        // 3) Al final y sin paréntesis. Se prueban los separadores de más fiable a menos:
        //    doble espacio (lo típico del pool: "Titulo  Editor Mashup"), luego guion, luego a pelo.
        foreach (var pat in new[]
        {
            $@"\s{{2,}}(?<who>[^\-–—\(\)\[\]]{{1,60}}?)\s+{KindRe}\s*$",
            $@"[-–—]\s*(?<who>[^\-–—\(\)\[\]]{{1,60}}?)\s+{KindRe}\s*$",
            $@"(?<who>[^\-–—\(\)\[\]]{{0,60}}?)\s*{KindRe}\s*$",
        })
        {
            var tail = Regex.Match(s, pat, IC);
            if (!tail.Success) continue;
            var who = Clean(tail.Groups["who"].Value);
            if (who.Length > 0) return new RemixInfo(who, Norm(tail.Groups["kind"].Value));
            // Sin autor reconocible: se devuelve solo el tipo (lo hace el último patrón).
            if (pat.StartsWith("(?<who>")) return new RemixInfo("", Norm(tail.Groups["kind"].Value));
        }

        return RemixInfo.None;
    }

    /// <summary>Analiza el contenido de un paréntesis: "Tiesto Remix" -> (Tiesto, Remix).</summary>
    private static RemixInfo FromFragment(string fragment)
    {
        // El tipo tiene que quedar al final, así que antes se quita la morralla que suelen dejar los
        // pools detrás: "(Myke Roldan Acapella Intro 145Bpm)" o "(… Intro Clean)".
        var f = Regex.Replace(fragment, @"\b\d{2,3}\s*bpm\b", " ", IC);
        f = Regex.Replace(f, @"\b\d{1,2}[AB]\b", " ");            // clave Camelot
        f = Regex.Replace(f, @"\b(clean|dirty|explicit)\b", " ", IC);
        f = Regex.Replace(f, @"\s{2,}", " ").Trim();
        var m = Regex.Match(f, $@"^(?<who>.*?)\s*{KindRe}\s*$", IC);
        if (!m.Success) return RemixInfo.None;
        return new RemixInfo(Clean(m.Groups["who"].Value), Norm(m.Groups["kind"].Value));
    }

    /// <summary>Deja el nombre del autor, o vacío si lo que hay es una palabra genérica de versión.</summary>
    private static string Clean(string? who)
    {
        var w = (who ?? "").Trim().Trim('-', '–', '—', ',', '&', '.', ' ');
        w = Regex.Replace(w, @"\b\d{2,3}\s*bpm\b", " ", IC);   // "128 BPM"
        w = Regex.Replace(w, @"\b\d{1,2}[AB]\b", " ");          // clave Camelot
        w = Regex.Replace(w, @"\s{2,}", " ").Trim();
        if (w.Length == 0) return "";

        var words = w.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 4) return "";                        // demasiado largo: no es un nombre

        // Si TODAS las palabras son genéricas ("Extended", "Radio", "Club"…), no hay autor.
        if (words.All(x => Generic.Contains(x.Trim('.', ',', '&')))) return "";

        // Un record pool NO es el autor de la versión: "…- DJTOOLSVIP" o "(Latin Box Extended)"
        // no firman nada, solo son quien lo distribuye. Si no, acababan pegados al nombre.
        if (Regex.IsMatch(w, Descriptors.PoolRe)) return "";

        // "DJ" y "MC" van en mayúsculas (TitleCase los dejaría como "Dj"/"Mc").
        var t = TextUtils.TitleCase(w);
        t = Regex.Replace(t, @"\bDj\b", "DJ");
        t = Regex.Replace(t, @"\bMc\b(?=\s)", "MC");
        return t;
    }

    /// <summary>Unifica el nombre del tipo: rmx -> Remix, re edit -> Re-Edit…</summary>
    private static string Norm(string kind)
    {
        var k = Regex.Replace(kind.Trim().ToLowerInvariant(), @"\s+", " ");
        return k switch
        {
            "rmx" => "Remix",
            "remix pack" or "remixpack" or "rmx pack" or "rmxpack" => "Remix",
            "edit pack" or "editpack" => "Edit",
            "re edit" or "reedit" => "Re-Edit",
            "vip mix" => "VIP",
            "extended mix" => "Extended",
            "dub mix" => "Dub",
            "vip" => "VIP",
            _ => TextUtils.TitleCase(k),
        };
    }
}
