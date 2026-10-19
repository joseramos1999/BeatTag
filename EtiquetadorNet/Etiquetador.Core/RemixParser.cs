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
        // «Mashboot» (mashup + bootleg): «FIEL (Ayuxx Mashboot)», «(DOUBLE & MATTIA CIPRIANI MASHBOOT)».
        @"mash\s*-?\s*boot(?:leg)?|" +
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

    // Palabras de versión que pueden quedar pegadas al principio o al final del autor y nunca son
    // parte de un nombre (ver Autor). Va aparte de Generic porque ahí están «DJ» o «The», que al borde
    // sí forman parte del nombre («DJ Nano», «The Magician»). Tampoco están «Hype», «Dirty» o «Clean»:
    // pueden ser el final de un nombre de verdad (James Hype, Born Dirty).
    private static readonly HashSet<string> VersionAlBorde = new(StringComparer.OrdinalIgnoreCase)
    {
        "extended", "instrumental", "acapella", "percapella", "aca", "intro", "outro", "starter",
        "transition", "segue", "redrum", "version", "mix", "edit", "remix", "rmx", "bootleg", "rework",
        "flip", "blend", "coro",
    };

    // Lo que, si es TODO lo que queda, describe la parte del tema y no a una persona:
    // «(Coro Acapella Intro)», «(Break Out Acapella)».
    private static readonly HashSet<string> NoEsAutor = new(StringComparer.OrdinalIgnoreCase)
    {
        "coro", "in", "out", "break",
    };

    // Tipos de dos palabras que pueden quedar al final del autor cuando el tipo de la versión es
    // otro: «Juankii Hype Intro» (de «… Hype Intro Acapella Out»), «Ahora Dice Acapella Break».
    private const string TipoCompuestoAlFinal =
        @"\s+(?:(?:hype|melodic|break|drop|short|long)\s*intro|aca(?:pella)?\s*(?:intro|outro|in|out|starter|break|vocals|studio)|intro\s*outro)$";

    // Una minúscula seguida de una mayúscula. Va con IgnoreCase desactivado: el resto del patrón lo
    // lleva activado, y con él «a» y «E» valdrían igual.
    private const string CambioDeCaja = @"(?-i:(?<=\p{Ll})(?=\p{Lu}))";

    // Fin del nombre sin paréntesis: de más fiable a menos. Doble espacio (lo típico del pool:
    // "Titulo  Editor Mashup"), luego guion, luego a pelo.
    private static readonly string[] Colas =
    {
        $@"\s{{2,}}(?<who>[^\-–—\(\)\[\]]{{1,60}}?)\s+{KindRe}\s*$",
        $@"[-–—]\s*(?<who>[^\-–—\(\)\[\]]{{1,60}}?)\s+{KindRe}\s*$",
        // Con límite de palabra delante del tipo: sin él, «Firestarter» era «Fire» + «Starter» y
        // «@SAMUVIP» era «@Samu» + «VIP».
        $@"(?<who>[^\-–—\(\)\[\]]{{0,60}}?)\s*\b{KindRe}\s*$",
    };

    /// <summary>
    /// Las versiones con autor que van entre paréntesis o corchetes, en el orden en que aparecen.
    /// Sirve para conservarlas TODAS en el nombre propuesto: «CHEVERE (premium remix) (Davidmusic Slam
    /// Acapella)» lleva dos firmas, y quedarse solo con una borraba la otra.
    /// </summary>
    public static IEnumerable<RemixInfo> ConAutor(string? text)
        => TextUtils.Rx(@"[\(\[]([^)\]]{2,80})[\)\]]").Matches(text ?? "")
               .Select(g => FromFragment(g.Groups[1].Value))
               .Where(r => r.HasRemixer);

    /// <summary>Analiza un nombre de archivo o un título y devuelve el tipo de versión y su autor.</summary>
    public static RemixInfo Parse(string? text)
    {
        foreach (var r in Candidates(text)) return r;
        return RemixInfo.None;
    }

    /// <summary>
    /// Todas las versiones que nombra el texto, de la que más probablemente es la del archivo a la
    /// que menos. <see cref="Parse"/> se queda con la primera; quien ya sabe que esa la trae el
    /// catálogo (FileProcessor) puede pasar a la siguiente.
    ///
    /// Entre paréntesis manda el ÚLTIMO que tenga autor: los pools añaden su edición al final del
    /// nombre. Visto en una biblioteca real: «No Te Quieren Conmigo (Remix) (Miguel Roma Hype Intro)»
    /// salía como «(Remix) (Hype Intro)», «Titanium (Remix) (Bayne Remix 128bpm)» como «Titanium
    /// (Remix)», y «RITMO (Bad Boys For Life) (Steve Aoki Remix) (Yaniss X Greg Lassierra Remix)» se
    /// quedaba con Steve Aoki, que ya traía el catálogo, y perdía la versión del archivo. Hasta 22
    /// nombres de una tirada acababan sin el editor que los firma.
    /// </summary>
    public static IEnumerable<RemixInfo> Candidates(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) yield break;

        // 1) "Remix by X" / "Remixed by X" (explícito, el más fiable).
        var explicita = PorEscrito(s);
        if (explicita.HasRemixer) yield return explicita;

        // 2) Entre paréntesis o corchetes: "(Tiesto Remix)", "[Pedro Cabrera Bootleg]". Primero los
        //    que tienen autor, del último al primero; si ninguno lo tiene, el primero que sea versión.
        var fragmentos = TextUtils.Rx(@"[\(\[]([^)\]]{2,80})[\)\]]").Matches(s)
            .Select(g => FromFragment(g.Groups[1].Value))
            .Where(r => r.IsVersion)
            .ToList();
        for (int i = fragmentos.Count - 1; i >= 0; i--)
            if (fragmentos[i].HasRemixer) yield return fragmentos[i];
        var sinAutor = fragmentos.FirstOrDefault(r => !r.HasRemixer);
        if (sinAutor.IsVersion) yield return sinAutor;

        // 3) Al final y sin paréntesis. Antes se quita la morralla que los pools dejan detrás del
        //    tipo: «Look At Me Now  Beauz Hard Techno Remix 154bpm» no casaba por el «154bpm» final.
        var cola = TextUtils.Rx(@"(?:[\s\-–—]*(?:\b\d{2,3}(?:[.,]\d{1,2})?\s*bpm\b|\b\d{1,2}[AB]\b))+[\s\-–—]*$", IC).Replace(s, "");
        foreach (var pat in Colas)
        {
            var tail = TextUtils.Rx(pat, IC).Match(cola);
            if (!tail.Success) continue;
            var who = Clean(tail.Groups["who"].Value);
            if (who.Length > 0) { yield return new RemixInfo(who, Norm(tail.Groups["kind"].Value)); yield break; }
            // Sin autor reconocible: se devuelve solo el tipo (lo hace el último patrón).
            if (ReferenceEquals(pat, Colas[^1])) { yield return new RemixInfo("", Norm(tail.Groups["kind"].Value)); yield break; }
        }
    }

    /// <summary>
    /// La firma que con más probabilidad trae el CATÁLOGO: la primera del nombre, en su orden. Es la
    /// que sirve para BUSCAR, mientras que <see cref="Parse"/> (la última) es la del archivo y va al
    /// nombre y al tag.
    ///
    /// Los pools añaden su edición detrás de la versión publicada: en «Hello (Sidney Samson Remix)
    /// (Cesar Vilo Remix)» el catálogo tiene la de Sidney Samson y no la de Cesar Vilo. Buscando con
    /// el último, Deezer dejaba de reconocer la versión publicada («versión de X», +9) y la puntuaba
    /// como cualquier otro remix (+6): sin el original entre los resultados, ganaba el primero que
    /// saliera, «Hello (Bob Sinclar Remix)».
    /// </summary>
    public static RemixInfo PrimeraFirma(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return RemixInfo.None;
        var explicita = PorEscrito(s);
        if (explicita.HasRemixer) return explicita;
        foreach (var r in ConAutor(s)) return r;
        return Parse(s);
    }

    /// <summary>«Remix by X», «Remixed by X», «Edit by X»…; None si el texto no lo dice así.</summary>
    private static RemixInfo PorEscrito(string s)
    {
        var m = TextUtils.Rx(@"\b(?<kind>remix|rework|edit|bootleg|flip)(?:ed)?\s+by\s+(?<who>[^)\]\-–—]{2,60})", IC).Match(s);
        if (!m.Success) return RemixInfo.None;
        var who = Clean(m.Groups["who"].Value);
        return who.Length > 0 ? new RemixInfo(who, Norm(m.Groups["kind"].Value)) : RemixInfo.None;
    }

    /// <summary>Analiza el contenido de un paréntesis: "Tiesto Remix" -> (Tiesto, Remix).</summary>
    private static RemixInfo FromFragment(string fragment)
    {
        // El tipo tiene que quedar al final, así que antes se quita la morralla que suelen dejar los
        // pools detrás: "(Myke Roldan Acapella Intro 145Bpm)" o "(… Intro Clean)".
        var f = TextUtils.Rx(@"\b\d{2,3}\s*bpm\b", IC).Replace(fragment, " ");
        f = TextUtils.Rx(@"\b\d{1,2}[AB]\b").Replace(f, " ");            // clave Camelot
        f = TextUtils.Rx(@"\b(clean|dirty|explicit)\b", IC).Replace(f, " ");
        f = TextUtils.Rx(@"\s{2,}").Replace(f, " ").Trim();
        // El tipo empieza una palabra, o va pegado detrás de una minúscula con su mayúscula
        // («B00STaVIPaEdit»): sin esto último, «(Smurfy x B00STaVIPaEdit)» se quedaba sin versión y
        // el archivo se proponía con el nombre del tema original. Pegado sin ese cambio de caja
        // («Firestarter», «@SAMUVIP») sigue sin contar.
        var m = TextUtils.Rx($@"^(?<who>.*?)\s*(?:\b|{CambioDeCaja}){KindRe}\s*$", IC).Match(f);
        if (!m.Success) return RemixInfo.None;
        return new RemixInfo(Clean(m.Groups["who"].Value), Norm(m.Groups["kind"].Value));
    }

    /// <summary>Deja el nombre del autor, o vacío si lo que hay es una palabra genérica de versión.</summary>
    private static string Clean(string? who)
    {
        var w = (who ?? "").Trim().Trim('-', '–', '—', ',', '&', '.', ' ');
        w = TextUtils.Rx(@"\b\d{2,3}\s*bpm\b", IC).Replace(w, " ");   // "128 BPM"
        w = TextUtils.Rx(@"\b\d{1,2}[AB]\b").Replace(w, " ");          // clave Camelot
        w = TextUtils.Rx(@"\s{2,}").Replace(w, " ").Trim();
        if (w.Length == 0) return "";

        var words = w.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 4) return "";                        // demasiado largo: no es un nombre

        // Si TODAS las palabras son genéricas ("Extended", "Radio", "Club"…), no hay autor.
        if (words.All(x => Generic.Contains(x.Trim('.', ',', '&')))) return "";

        // Un record pool NO es el autor de la versión: "…- DJTOOLSVIP" o "(Latin Box Extended)"
        // no firman nada, solo son quien lo distribuye. Si no, acababan pegados al nombre.
        if (TextUtils.Rx(Descriptors.PoolRe).IsMatch(w)) return "";

        // "DJ" y "MC" van en mayúsculas (TitleCase los dejaría como "Dj"/"Mc").
        var t = TextUtils.TitleCase(w);
        t = TextUtils.Rx(@"\bDj\b").Replace(t, "DJ");
        t = TextUtils.Rx(@"\bMc\b(?=\s)").Replace(t, "MC");
        return t;
    }

    /// <summary>
    /// Solo la persona que firma la versión, sin las palabras de versión que el pool deja pegadas a su
    /// nombre. Es lo que va al tag Remixer (TPE4): en el nombre de archivo se conserva el texto tal cual
    /// lo escribió el pool, pero en una columna por la que se filtra y se ordena «Alex Torres Intro» y
    /// «Alex Torres» serían dos editores distintos.
    ///
    /// Al preferir el último paréntesis con autor aparecían cosas como «(Intro, DJ Nev Intro
    /// Acapella)» → «Intro, DJ Nev Intro», «(Saul Gallego Hype Intro, Extended)» → «Saul Gallego Hype
    /// Intro» o «(Antonio Guevara Acapella Break Intro)» → «Antonio Guevara Acapella». Devuelve "" si
    /// lo que queda no es el nombre de nadie («Coro», «Break Out»).
    /// </summary>
    public static string Autor(string? remixer)
    {
        var w = (remixer ?? "").Trim();
        if (w.Length == 0) return "";

        // Lista de descriptores delante, separada por comas («Intro, DJ Nev»): fuera.
        var partes = w.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        while (partes.Count > 1 && partes[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).All(VersionAlBorde.Contains))
            partes.RemoveAt(0);

        // Palabras de versión detrás («DJ Nev Intro»). Los tipos compuestos van antes que las palabras
        // sueltas para no dejar el «Hype» de «Hype Intro» colgando.
        var trozos = string.Join(", ", partes).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (trozos.Count > 1)
        {
            var junto = string.Join(" ", trozos).TrimEnd(',', '.', '&');
            var cola = TextUtils.Rx(TipoCompuestoAlFinal, IC).Match(junto);
            if (cola.Success && cola.Index > 0) { trozos = junto[..cola.Index].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(); continue; }
            if (VersionAlBorde.Contains(trozos[^1].Trim(',', '.', '&'))) { trozos.RemoveAt(trozos.Count - 1); continue; }
            break;
        }
        w = string.Join(" ", trozos).Trim().Trim(',', '&', '-', ' ');
        if (w.Length == 0) return "";

        var palabras = w.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim('.', ',', '&')).ToList();
        if (palabras.All(x => Generic.Contains(x) || NoEsAutor.Contains(x))) return "";
        return w;
    }

    /// <summary>Unifica el nombre del tipo: rmx -> Remix, re edit -> Re-Edit, mash boot -> Mashboot…</summary>
    private static string Norm(string kind)
    {
        var k = TextUtils.Rx(@"\s+").Replace(kind.Trim().ToLowerInvariant(), " ");
        if (TextUtils.Rx(@"^mash\s*-?\s*boot").IsMatch(k)) return "Mashboot";
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
