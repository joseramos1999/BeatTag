using System.Text.RegularExpressions;

namespace Etiquetador.Core;

/// <summary>
/// Limpieza de títulos y construcción de queries + extracción de descriptores DJ
/// (Remix/Extended/Intro/Acapella…). Portado 1:1 de Enriquecer-App.ps1.
/// </summary>
public static class Descriptors
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Pools/record-pools de DJ que ensucian el nombre (equivalente a $PoolRe). Lleva (?i) inline.
    public const string PoolRe =
        @"(?i)\b(latin\s*box|unlimited\s+latin|intensa(?:\s+music)?|dj\s*tools\s*vip|djtoolsvip|dj\s*tools|bpm\s+supreme|bpm\s+latino|dj\s*city|djcity|zip\s*dj|heavy\s+hits|crooklyn\s+clan|digital\s+dj\s+pool|direct\s+music\s+service|franchise\s+record\s+pool|club\s+killers|late\s+night\s+record\s+pool|mymp3pool|beat\s*junkies|prime\s*djs|smash\s+vision|barba\s+dj|remix\s+planet|latino\s+music\s+pool|latin\s*urbano|latinos\s+unidos|dj\s+pool\s+records|remixes4djs|digiwaxx|club\s+queen|brgs\s+(?:pro\s+)?(?:qatar\s+)?20\d\d|brgs|qatar\s*20\d\d)\b";
    // OJO: "Try It" NO es un record pool, es un editor que firma sus edits ("Try It Hype Intro"),
    // así que no va en esta lista: debe conservarse como autor de la versión.

    // Descriptores DJ reconocidos (equivalente a $DescRe). Lleva (?i) inline.
    // «Mashboot» (mashup + bootleg) es como lo firman varios editores de pool: «FIEL (Ayuxx
    // Mashboot)». Sin él, ni se conservaba como versión ni se reconocía como mezcla.
    public const string DescRe =
        @"(?i)\b(" + AcapellaAbreviada + @"|remix|rmx|vip|bootleg|flip|rework|mashup|mash\s*-?\s*boot(?:leg)?|blend|redrum|extended(?:\s+mix)?|original(?:\s+[a-z]+)?\s+(?:mix|version)|club\s+mix|radio\s+edit|quick\s+(?:hit|edit)|short\s+edit|intro\s*-?\s*outro|hype\s+intro|melodic\s+intro|break\s+intro|acapella(?:\s+(?:in|out|intro|outro|studio|starter|break|vocals))?|aca\s*(?:in|out)|open\s+show|intro|outro|instrumental|transition|segue|starter|live(?:\s+edit)?|clean|dirty|edit|version|remaster(?:ed)?|dub|percapella|loop|hype)\b";

    // Mirror de PowerShell -replace (case-insensitive por defecto). Los patrones son fijos y van a
    // la caché de TextUtils.Rx (ver allí la medición).
    private static string Rep(string s, string pat, string rep) => TextUtils.Rx(pat, IC).Replace(s, rep);

    /// <summary>Limpia artista/título para construir una query de búsqueda (quita adornos, BPM, feats, editores…).</summary>
    public static string CleanKeywords(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Rep(s, @"\([^)]*\)", " ");
        s = Rep(s, @"\[[^\]]*\]", " ");
        s = Rep(s, @"(?<=\p{L})¡", "í");                                  // mojibake CP850: í mal leída
        s = Rep(s, @"@\S+", " ");                                         // handles de editor
        s = Rep(s, @"\bdj\s+prof\w*", " ");                              // editor recurrente
        s = Rep(s, @"\bver\.?\s?\d+\b", " ");
        s = Rep(s, @"\bver\s+fin\b", " ");                              // VER2 / VER FIN
        s = Rep(s, @"\b\d{2,3}\s+a\s+\d{2,3}\b", " ");                   // transición de BPM "130 a 100"
        s = Rep(s, @"\b(hype intro|acapella intro|aca intro|aca out|acapella out|open show|break intro|melodic intro|hype|intro|outro|extended|segue|segway|redrum|break|starter|transition|djtools|dj tools|acapella|instrumental|version)\b", " ");
        // OJO con los límites: sin \b, el "ft" de Daft/Kraftwerk/Swift truncaba el nombre entero.
        s = Rep(s, @"\b(?:feat|ft|featuring)\b\.?.*$", " ");
        s = Rep(s, @"\b\d{2,3}\s*bpm\b", " ");
        s = Rep(s, @"\b\d{1,2}[AB]\b", " ");
        var s2 = Rep(s, @"\b\d{2,3}\b", " ");                            // quita números sueltos (BPM/pista)...
        if (TextUtils.Rx(@"[^\p{L}\d]").Replace(s2, "").Length > 0) s = s2;     // ...salvo que dejara vacío: título numérico ("404")
        s = Rep(s, "[,_/]", " ");
        // La "x" solo es separador de artistas cuando va SUELTA entre espacios ("Artista A x Artista B").
        // Con \b se comía la X de títulos como "X.O.X.O." (quedaba ".O. .O.") o "X" de Nicky Jam.
        // Además debe haber texto A AMBOS LADOS, o se llevaba por delante la X final de "Lil Nas X".
        s = Rep(s, @"(?<=\S\s)x(?=\s\S)", " ");
        return TextUtils.Rx(@"\s+").Replace(s, " ").Trim();
    }

    /// <summary>Query artista+título limpiando POR SEPARADO (el ft del artista no borra el título).</summary>
    public static string BuildKw(string? a, string? t)
    {
        var x = (CleanKeywords(a).Trim() + " " + CleanKeywords(t).Trim()).Trim();
        return x.Length > 0 ? x : CleanKeywords(t);
    }

    /// <summary>
    /// Query cuando el usuario dicta qué buscar (eligió una coincidencia, pegó un enlace o lo
    /// escribió a mano): se respeta TAL CUAL. Limpiarla es justo lo que sobra aquí — llegó a
    /// convertir "X.O.X.O." en ".O. .O." y a devolver otra canción.
    /// </summary>
    public static string BuildKwVerbatim(string? a, string? t)
    {
        var s = ((a ?? "").Trim() + " " + (t ?? "").Trim()).Trim();
        return TextUtils.Rx(@"\s{2,}").Replace(s, " ");
    }

    /// <summary>Limpia un título para query: quita corchetes/paréntesis, descriptores, BPM y clean/dirty.</summary>
    public static string CleanTitle(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Rep(s, @"\[[^\]]*\]", " ");
        s = Rep(s, @"\([^)]*\)", " ");
        s = Rep(s, @"[\[\]\(\)]", " ");
        s = Rep(s, DescRe, " ");
        s = Rep(s, @"\b\d{1,3}\s*bpm\b", " ");
        s = Rep(s, @"\b\d{1,2}[AB]\b", " ");
        s = Rep(s, @"\b(clean|dirty)\b", " ");
        return TextUtils.Rx(@"\s+").Replace(s, " ").Trim().Trim('-').Trim();
    }

    /// <summary>Normaliza un título venido de la BD (separa descriptores pegados, quita coletillas de versión).</summary>
    public static string CleanDbTitle(string? t, string? orig)
    {
        if (string.IsNullOrEmpty(t)) return t ?? "";
        orig ??= "";
        t = Rep(t, @"\b(extended|original|radio|club|instrumental|acapella)(version|mix|edit|remix|intro|outro)\b", "$1 $2");
        t = Rep(t, @"\s*[\(\[]\s*mixed\s*[\)\]]", "");
        t = Rep(t, @"\s*[\(\[][^\)\]]*remaster[^\)\]]*[\)\]]", "");
        t = Rep(t, @"\s*[\(\[]\s*(bonus track|single version|album version|explicit|clean|radio edit)\s*[\)\]]", "");
        t = Rep(t, @"\s*[\(\[]\s*(?:main|radio|album|single)\s+version\s*[\)\]]", "");
        t = Rep(t, @"\s*[\(\[]\s*version\s*[\)\]]", "");
        t = Rep(t, @"\s*_\s*[a-z0-9]+\s*fm\b", "");
        var pideDirecto = TextUtils.Rx(@"\b(live|vivo|directo|unplugged|sinf|en\s+vivo|en\s+directo)\b", IC).IsMatch(orig);
        if (!pideDirecto)
            t = Rep(t, @"\s*[\(\[][^\)\]]*\b(live|en vivo|en directo|vivo|unplugged|sinfonico|sinfónico)\b[^\)\]]*[\)\]]", "");
        if (!TextUtils.Rx(@"\bkaraoke\b", IC).IsMatch(orig))
            t = Rep(t, @"\s*[\(\[][^\)\]]*karaoke[^\)\]]*[\)\]]", "");
        if (!TextUtils.Rx(@"\bdeluxe\b", IC).IsMatch(orig))
            t = Rep(t, @"\s*[\(\[][^\)\]]*deluxe[^\)\]]*[\)\]]", "");
        t = ColetillaTrasGuion(t, pideDirecto);
        t = Rep(t, "_", " ");
        return TextUtils.Rx(@"\s{2,}").Replace(t, " ").Trim().Trim('-').Trim();
    }

    /// <summary>
    /// La versión que el catálogo escribe tras un guion y no entre paréntesis, que es la convención
    /// de Deezer y Spotify: «El Motorcito - Remix», «Poblado - Remix», «Song - Radio Edit»,
    /// «Song - Remastered 2011».
    ///
    /// Visto en una biblioteca real: «Gusty DJ, Lolo OG, DobleP - El Motorcito - (Remix).mp3» y
    /// «J Balvin, KAROL G, … - Poblado - (Remix).mp3». La limpieza de arriba solo miraba los
    /// paréntesis, así que «- Remix» pasaba tal cual y luego se quedaba un guion colgando; además, al
    /// releer «Artista - Título - (Remix)» el nombre se partía mal.
    ///
    /// Se trata igual que entre paréntesis: lo que allí se quita (remaster, «single version», «radio
    /// edit», el directo no pedido…) se quita, y el resto de versiones pasa a ir entre paréntesis. Solo
    /// la cola que nombra una versión: «Song - From "Barbie"» o «Part 1 - Intro» no se tocan.
    /// </summary>
    private static string ColetillaTrasGuion(string t, bool pideDirecto)
    {
        const string cola = @"\s+[-–—]\s+(?<v>(?:[^-–—()\[\]]|(?<=\S)-(?=\S)){1,60}?)\s*$";
        var m = TextUtils.Rx(cola, IC).Match(t);
        if (!m.Success) return t;
        var v = m.Groups["v"].Value.Trim();
        var antes = t[..m.Index];
        if (antes.Trim().Length == 0) return t;

        if (TextUtils.Rx(@"\bremaster", IC).IsMatch(v)
            || TextUtils.Rx(@"^(?:bonus\s+track|(?:main|radio|album|single)\s+version|version|explicit|clean|radio\s+edit|mixed)$", IC).IsMatch(v))
            return antes;
        if (!pideDirecto && TextUtils.Rx(@"\b(?:live|en\s+vivo|en\s+directo|vivo|unplugged|sinf[oó]nico)\b", IC).IsMatch(v))
            return antes;
        if (TextUtils.Rx(@"\b(?:remix|rmx|mix|edit|version|rework|bootleg|flip|vip|dub|instrumental|acapella|extended|refix|re-?edit|acoustic|ac[uú]stic[oa]|live|en\s+vivo|en\s+directo)\b", IC).IsMatch(v))
            return $"{antes} ({v})";
        return t;
    }

    /// <summary>
    /// Las abreviaturas de acapella de los pools latinos: «(In Acp)», «IOAcp», «(Acap)», «Out Acp».
    ///
    /// Visto en una biblioteca real: al no reconocerlas, el nombre nuevo las perdía y una intro en
    /// acapella quedaba como «Jowell &amp; Randy - Eh Oh Eh Oh», indistinguible del tema original.
    /// Va la primera en <see cref="DescRe"/> para que «In Acp» se tome entero y no como «Intro» suelto.
    /// </summary>
    private const string AcapellaAbreviada =
        @"(?:intro\s*outro|in\s*-?\s*out|io|intro|outro|in|out)?\s*acp|acap";

    /// <summary>Normaliza sinónimos de descriptor DJ (rmx → Remix, «In Acp» → Intro Acapella).</summary>
    public static string NormalizeDescriptor(string s)
    {
        var k = TextUtils.Nk(s);
        if (k == "rmx") return "Remix";
        if (k is "acp" or "acap") return "Acapella";
        if (k == "introoutro") return "Intro Outro";   // «IntroOutro», todo junto
        if (k.EndsWith("acp", StringComparison.Ordinal))
        {
            var antes = k[..^3];
            if (antes is "io" or "inout" or "introoutro") return "Intro Outro Acapella";
            if (antes is "in" or "intro") return "Intro Acapella";
            if (antes is "out" or "outro") return "Outro Acapella";
        }
        return s;
    }

    private static readonly string[] DjWords =
        { "Extended", "Instrumental", "Acapella", "Percapella", "Transition", "Bootleg", "Mashup", "Version" };

    /// <summary>Completa un descriptor DJ cortado al final ("Extend" -> "Extended"), preservando lo anterior.</summary>
    public static string CompleteTruncated(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        var t = s.TrimEnd();
        var m = TextUtils.Rx(@"\(?\s*([A-Za-z]{4,})$").Match(t);
        if (m.Success)
        {
            var frag = m.Groups[1].Value.ToLowerInvariant();
            foreach (var w in DjWords)
            {
                var wl = w.ToLowerInvariant();
                if (wl.Length > frag.Length && wl.StartsWith(frag))
                    return t.Substring(0, m.Groups[1].Index) + w;
            }
        }
        return s;
    }

    /// <summary>Quita el nombre del editor/DJ que quedó suelto antes de un descriptor final, conservando el descriptor.</summary>
    public static string RemoveEditorTags(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        const string d = @"(?:mashup|mash\s*up|remix|rmx|bootleg|flip|rework|redrum|blend|edit)";
        const string bpm = @"(?:\s*\d{2,3}\s*bpm)?";
        // a) editor tras un paréntesis de cierre y antes del descriptor final
        s = Rep(s, $@"(\))\s+\p{{L}}[\p{{L}}.'-]*(?:\s+\p{{L}}[\p{{L}}.'-]*){{0,2}}\s+({d}){bpm}\s*$", "$1 $2");
        // b) editor tras doble espacio (separador perdido) y antes del descriptor final
        s = Rep(s, $@"\s{{2,}}\p{{Lu}}[\p{{L}}.'-]*(?:\s+\p{{L}}[\p{{L}}.'-]*){{0,2}}\s+({d}){bpm}\s*$", " $1");
        return s;
    }

    /// <summary>
    /// Extrae los descriptores DJ del nombre original que NO estén ya en el título (para el campo "otros").
    ///
    /// <paramref name="artistas"/>: nombres de artista que da el catálogo, cuyas palabras no cuentan
    /// como descriptor fuera de paréntesis. <paramref name="artistaDelNombre"/>: el artista leído del
    /// nombre de archivo, del que solo se descartan los descriptores que suelen formar parte de un
    /// nombre (ver <see cref="SinArtistas"/>). Visto en una biblioteca real: «Born Dirty - Samba Sujo»
    /// salía como «… - Samba Sujo (Dirty)», «James Hype - Ferrari (Remix)» como «… (Remix) (Hype)» (al
    /// menos 8 propuestas así) y con Clean Bandit pasaría con «(Clean)».
    /// </summary>
    public static string[] ExtractOtros(string? origBase, string? title, IEnumerable<string?>? artistas = null,
        string? artistaDelNombre = null)
    {
        origBase = SinArtistas(origBase, artistas);
        if (!string.IsNullOrEmpty(artistaDelNombre)) origBase = SinArtistas(origBase, new[] { artistaDelNombre }, soloDebiles: true);
        var found = new List<string>();
        foreach (Match m in TextUtils.Rx(DescRe).Matches(origBase))
            found.Add(TextUtils.TitleCase(m.Value));
        var nt = TextUtils.Nk(title);
        var seen = new HashSet<string>();
        var outList = new List<string>();
        foreach (var raw in found)
        {
            var x = NormalizeDescriptor(raw);
            var k = TextUtils.Nk(x);
            if (k.Length > 0 && !seen.Contains(k) && !nt.Contains(k))
            {
                seen.Add(k);
                outList.Add(x);
            }
        }
        return outList.ToArray();
    }

    /// <summary>Lo que separa a varios intérpretes en un mismo campo.</summary>
    private const string SeparaArtistas = @"\s*(?:,|;|&|\+|/|\s(?:x|y|and|vs\.?|feat\.?|ft\.?|featuring|con|with)\s)\s*";

    /// <summary>
    /// Descriptores que también son palabras corrientes en nombres de artista: Clean Bandit, Born
    /// Dirty, James Hype, Live, Richie Loop, Manu Vip… Los demás («Remix», «Extended», «Intro»…) casi
    /// nunca forman parte de un nombre y sí del título en los nombres invertidos del pool.
    /// </summary>
    private static readonly HashSet<string> DescriptoresDebiles = new(StringComparer.Ordinal)
    {
        "clean", "dirty", "hype", "live", "dub", "loop", "version", "vip", "flip", "blend", "starter", "segue",
    };

    /// <summary>
    /// El texto con los descriptores que caen dentro de un nombre de artista tapados por espacios,
    /// pero SOLO fuera de paréntesis y corchetes: ahí va la versión («(Clean)», «(Dirty)», «(Hype
    /// Intro)») y eso sí es un descriptor aunque coincida con una palabra del artista. Se tapa con
    /// espacios de la misma longitud para no mover nada de sitio.
    ///
    /// Cada campo se parte en intérpretes («Born Dirty, Diplo» → «Born Dirty», «Diplo») y de cada uno
    /// se descarta lo que vaya entre paréntesis: en los nombres invertidos del pool la primera mitad
    /// es el título con su versión («Behaviour (Extended Mix) - James Hype»).
    ///
    /// Con <paramref name="soloDebiles"/> solo se tapan los descriptores que suelen ser parte de un
    /// nombre (<see cref="DescriptoresDebiles"/>). Es para el artista que se lee del nombre de archivo,
    /// que en los nombres invertidos del pool es en realidad el título: en «Si Tu Te Vas Remix - Mora»
    /// el «Remix» es la versión y no puede perderse.
    /// </summary>
    public static string SinArtistas(string? texto, IEnumerable<string?>? artistas, bool soloDebiles = false)
    {
        var s = texto ?? "";
        if (s.Length == 0 || artistas == null) return s;

        var nombres = artistas
            .SelectMany(a => TextUtils.Rx(SeparaArtistas, IC).Split(a ?? ""))
            .Select(n => TextUtils.Rx(@"\([^)]*\)?|\[[^\]]*\]?").Replace(n, " "))
            .Select(n => TextUtils.Rx(@"\s+").Replace(n, " ").Trim().Trim(',', '-', '.', ' '))
            .Where(n => TextUtils.Nk(n).Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length)
            .ToList();
        if (nombres.Count == 0) return s;

        // Profundidad de paréntesis de cada posición. Tapar con espacios no cambia ningún paréntesis,
        // así que basta con calcularla una vez.
        var dentro = new bool[s.Length];
        var nivel = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] is '(' or '[') nivel++;
            dentro[i] = nivel > 0;
            if (s[i] is ')' or ']' && nivel > 0) nivel--;
        }

        var desc = TextUtils.Rx(DescRe);
        string Tapar(Match m)
            => dentro[m.Index] ? m.Value
             : desc.Replace(m.Value, d => soloDebiles && !DescriptoresDebiles.Contains(TextUtils.Nk(d.Value)) ? d.Value : new string(' ', d.Length));

        foreach (var n in nombres)
        {
            // Patrón que depende de los datos: Regex estático, nunca la caché de TextUtils.Rx.
            var pat = @"(?<![\p{L}\p{N}])" + Regex.Escape(n).Replace(@"\ ", @"\s+") + @"(?![\p{L}\p{N}])";
            s = Regex.Replace(s, pat, Tapar, IC);
        }
        return s;
    }
}
