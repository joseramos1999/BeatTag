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
        @"(?i)\b(latin\s*box|unlimited\s+latin|intensa|dj\s*tools\s*vip|djtoolsvip|dj\s*tools|bpm\s+supreme|bpm\s+latino|dj\s*city|djcity|zip\s*dj|heavy\s+hits|crooklyn\s+clan|digital\s+dj\s+pool|direct\s+music\s+service|franchise\s+record\s+pool|club\s+killers|late\s+night\s+record\s+pool|mymp3pool|beat\s*junkies|prime\s*djs|smash\s+vision|barba\s+dj|remix\s+planet|latino\s+music\s+pool|latin\s*urbano|latinos\s+unidos|dj\s+pool\s+records|remixes4djs|digiwaxx|club\s+queen|try\s+it|brgs\s+(?:pro\s+)?(?:qatar\s+)?20\d\d|brgs|qatar\s*20\d\d)\b";

    // Descriptores DJ reconocidos (equivalente a $DescRe). Lleva (?i) inline.
    public const string DescRe =
        @"(?i)\b(remix|rmx|vip|bootleg|flip|rework|mashup|blend|redrum|extended(?:\s+mix)?|original(?:\s+[a-z]+)?\s+(?:mix|version)|club\s+mix|radio\s+edit|quick\s+(?:hit|edit)|short\s+edit|hype\s+intro|melodic\s+intro|break\s+intro|acapella(?:\s+(?:in|out|intro|outro|studio))?|aca\s*(?:in|out)|open\s+show|intro|outro|instrumental|transition|segue|starter|live(?:\s+edit)?|clean|dirty|edit|version|remaster(?:ed)?|dub|percapella|loop|hype)\b";

    // Mirror de PowerShell -replace (case-insensitive por defecto).
    private static string Rep(string s, string pat, string rep) => Regex.Replace(s, pat, rep, IC);

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
        if (Regex.Replace(s2, @"[^\p{L}\d]", "").Length > 0) s = s2;     // ...salvo que dejara vacío: título numérico ("404")
        s = Rep(s, "[,_/]", " ");
        s = Rep(s, @"\bx\b", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>Query artista+título limpiando POR SEPARADO (el ft del artista no borra el título).</summary>
    public static string BuildKw(string? a, string? t)
    {
        var x = (CleanKeywords(a).Trim() + " " + CleanKeywords(t).Trim()).Trim();
        return x.Length > 0 ? x : CleanKeywords(t);
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
        return Regex.Replace(s, @"\s+", " ").Trim().Trim('-').Trim();
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
        if (!Regex.IsMatch(orig, @"\b(live|vivo|directo|unplugged|sinf|en\s+vivo|en\s+directo)\b", IC))
            t = Rep(t, @"\s*[\(\[][^\)\]]*\b(live|en vivo|en directo|vivo|unplugged|sinfonico|sinfónico)\b[^\)\]]*[\)\]]", "");
        if (!Regex.IsMatch(orig, @"\bkaraoke\b", IC))
            t = Rep(t, @"\s*[\(\[][^\)\]]*karaoke[^\)\]]*[\)\]]", "");
        if (!Regex.IsMatch(orig, @"\bdeluxe\b", IC))
            t = Rep(t, @"\s*[\(\[][^\)\]]*deluxe[^\)\]]*[\)\]]", "");
        t = Rep(t, "_", " ");
        return Regex.Replace(t, @"\s{2,}", " ").Trim().Trim('-').Trim();
    }

    /// <summary>Normaliza sinónimos de descriptor DJ (rmx → Remix).</summary>
    public static string NormalizeDescriptor(string s)
        => TextUtils.Nk(s) == "rmx" ? "Remix" : s;

    private static readonly string[] DjWords =
        { "Extended", "Instrumental", "Acapella", "Percapella", "Transition", "Bootleg", "Mashup", "Version" };

    /// <summary>Completa un descriptor DJ cortado al final ("Extend" -> "Extended"), preservando lo anterior.</summary>
    public static string CompleteTruncated(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        var t = s.TrimEnd();
        var m = Regex.Match(t, @"\(?\s*([A-Za-z]{4,})$");
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

    /// <summary>Extrae los descriptores DJ del nombre original que NO estén ya en el título (para el campo "otros").</summary>
    public static string[] ExtractOtros(string? origBase, string? title)
    {
        origBase ??= "";
        var found = new List<string>();
        foreach (Match m in Regex.Matches(origBase, DescRe))
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
}
