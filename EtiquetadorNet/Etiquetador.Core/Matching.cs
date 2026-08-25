using System.Text.RegularExpressions;

namespace Etiquetador.Core;

/// <summary>Matching difuso de artistas/títulos y detección de mezclas a saltar.</summary>
public static class Matching
{
    /// <summary>Casa dos artistas ya normalizados con NK: igualdad, prefijo o inclusión (≥6 chars).</summary>
    public static bool ArtistMatch(string? na, string? an)
    {
        if (string.IsNullOrEmpty(na) || string.IsNullOrEmpty(an)) return false;
        if (an == na) return true;
        if (an.StartsWith(na) || na.StartsWith(an)) return true;
        if (na.Length >= 6 && an.Contains(na)) return true;
        if (an.Length >= 6 && na.Contains(an)) return true;
        // Mismo artista publicando con otro nombre (p. ej. Cruz Cafuné aparece como "Cruzzi").
        if (ArtistAliases.Current.SameArtist(na, an)) return true;
        return false;
    }

    /// <summary>Similitud Jaro-Winkler (0..1): tolera typos y transposiciones. Para matching difuso.</summary>
    public static double JaroWinkler(string s1, string s2)
    {
        if (s1 == s2) return 1.0;
        int l1 = s1.Length, l2 = s2.Length;
        if (l1 == 0 || l2 == 0) return 0.0;
        int md = Math.Max((int)Math.Floor(Math.Max(l1, l2) / 2.0) - 1, 0);
        var m1 = new bool[l1];
        var m2 = new bool[l2];
        int matches = 0;
        for (int i = 0; i < l1; i++)
        {
            int lo = Math.Max(0, i - md), hi = Math.Min(i + md + 1, l2);
            for (int j = lo; j < hi; j++)
                if (!m2[j] && s1[i] == s2[j]) { m1[i] = true; m2[j] = true; matches++; break; }
        }
        if (matches == 0) return 0.0;
        int k = 0;
        double trans = 0.0;
        for (int i = 0; i < l1; i++)
            if (m1[i]) { while (!m2[k]) k++; if (s1[i] != s2[k]) trans++; k++; }
        trans /= 2.0;
        double jaro = ((matches / (double)l1) + (matches / (double)l2) + ((matches - trans) / matches)) / 3.0;
        int p = 0, maxp = Math.Min(4, Math.Min(l1, l2));
        while (p < maxp && s1[p] == s2[p]) p++;
        return jaro + p * 0.1 * (1.0 - jaro);
    }

    /// <summary>
    /// Ajuste de confianza según cuánto concuerdan los TAGS ya embebidos en el archivo con la
    /// propuesta encontrada. Positivo si coinciden (refuerza que el match es correcto), negativo
    /// si difieren (posible identificación errónea → marcar para revisar). Neutro (0) cuando el
    /// archivo no trae ese tag: no penaliza a los archivos sin metadatos (lo normal en material DJ).
    /// </summary>
    public static double TagCoherence(string? tagArtist, string? tagTitle, string? resArtist, string? resTitle)
    {
        double d = 0;

        // Título: se limpian descriptores (Remix, Extended…) para comparar el núcleo.
        var ntTag = TextUtils.Nk(Descriptors.CleanKeywords(tagTitle));
        var ntRes = TextUtils.Nk(Descriptors.CleanKeywords(resTitle));
        if (ntTag.Length > 0 && ntRes.Length > 0)
        {
            if (ntTag == ntRes || ntRes.Contains(ntTag) || ntTag.Contains(ntRes)) d += 3;
            else if (ntTag.Length >= 5 && ntRes.Length >= 5 && JaroWinkler(ntTag, ntRes) >= 0.90) d += 1;
            else d -= 2;
        }

        // Artista.
        var naTag = TextUtils.Nk(tagArtist);
        var naRes = TextUtils.Nk(resArtist);
        if (naTag.Length > 0 && naRes.Length > 0)
        {
            if (ArtistMatch(naTag, naRes)) d += 2;
            else d -= 1.5;
        }

        return d;
    }

    /// <summary>
    /// Mezclas que se saltan por completo: mashup/bootleg/transition/blend, "X vs Y", o un "Edit"
    /// de DJ (edición propia que no existe en catálogo). Se EXCLUYEN las ediciones de catálogo
    /// (radio/extended/short/quick/club/melodic/original edit), que sí se etiquetan.
    /// </summary>
    /// <summary>
    /// La canción vive dentro de una carpeta de mashups. Todo lo que hay ahí es material mezclado,
    /// así que no tiene sentido buscarlo en el catálogo: no existe como lanzamiento.
    ///
    /// Se mira la ruta ENTERA de la carpeta, no solo la última: quien guarda mashups suele
    /// organizarlos en subcarpetas por año o por estilo dentro de una carpeta "Mashups".
    /// </summary>
    public static bool IsMashupFolder(string? folderPath)
        => !string.IsNullOrEmpty(folderPath)
           && Regex.IsMatch(folderPath, @"\b(mashups?|mash\s*ups?|mash-ups?)\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// El nombre PODRÍA ser una mezcla, pero no está claro. Sirve para decidir a quién preguntar:
    /// estos casos no los resuelve una expresión regular, porque la misma "x" separa colaboradores
    /// en "Nicky Jam x J. Balvin - X (EQUIS)" y canciones distintas en "Ella Me Levanto x Gul".
    /// Incluso puede ser parte del título, como en "ROSALÍA - Yo x Ti, Tu x Mi".
    ///
    /// No decide nada por su cuenta: solo marca los que merece la pena consultar con la IA local,
    /// que sí puede juzgar si son dos canciones o una con varios artistas.
    /// </summary>
    public static bool LooksAmbiguousMix(string? baseName)
    {
        var s = baseName ?? "";
        if (s.Length == 0) return false;

        // Los que ya resuelve IsSkipMix no son ambiguos: esos se saltan sin preguntar a nadie.
        if (Regex.IsMatch(s, @"\b(mashup|mash\s*up|mash-up|transition|segue|blend)\b", RegexOptions.IgnoreCase))
            return false;

        // Dos o más "x" sueltas entre palabras: puede ser lista de artistas o unión de temas.
        // A los lados tiene que haber letra o número: sin eso, el título "X (EQUIS)" de
        // "Nicky Jam x J. Balvin - X (EQUIS)" contaba como un separador más y lo marcaba.
        var equis = Regex.Matches(s, @"(?<=[\p{L}\p{N}])\s+x\s+(?=[\p{L}\p{N}])", RegexOptions.IgnoreCase).Count;
        if (equis >= 2) return true;

        // Una "x" a cada lado del guion separador: sospechoso de unir dos canciones.
        var partes = Regex.Split(s, @"\s+-\s+");
        if (partes.Length >= 2
            && Regex.IsMatch(partes[0], @"(?<=[\p{L}\p{N}])\s+x\s+(?=[\p{L}\p{N}])", RegexOptions.IgnoreCase)
            && Regex.IsMatch(partes[^1], @"(?<=[\p{L}\p{N}])\s+x\s+(?=[\p{L}\p{N}])", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Palabras de relleno que no aportan identidad a una canción. Se ignoran al comprobar si la
    /// IA se ha inventado algo: que sobre o falte un "la" no dice nada sobre si acertó.
    /// </summary>
    private static readonly HashSet<string> Relleno = new(StringComparer.Ordinal)
    {
        "el", "la", "los", "las", "un", "una", "de", "del", "y", "e", "o", "al", "en", "con",
        "the", "of", "and", "feat", "ft", "featuring", "vs", "remix", "mix", "edit",
        "intro", "extended", "version", "original", "radio", "clean", "dirty", "break", "acapella",
    };

    /// <summary>
    /// ¿La propuesta de la IA se limita a REORDENAR y LIMPIAR lo que ya había en el nombre, sin
    /// añadir datos que no estuvieran?
    ///
    /// Es la guarda que decide qué sugerencias se le enseñan al usuario. Cuando la IA no reconoce
    /// una canción tiende a rellenar el hueco con algo plausible -un artista famoso del estilo, un
    /// título parecido-, y ese error es justo el que una persona NO puede cazar de un vistazo,
    /// porque el resultado suena verosímil. En cambio intercambiar artista y título salta a la
    /// vista. Por eso aquí se filtra lo inventado y se deja pasar lo reordenado.
    ///
    /// La comparación tolera erratas (Jaro-Winkler ≥ 0,85): corregir "Resentia" a "Resentía" es
    /// justo para lo que sirve la IA, y no debe contar como invención.
    /// </summary>
    public static bool SoloReordena(string? original, string? artist, string? title)
    {
        var origen = Palabras(original);
        if (origen.Count == 0) return false;

        foreach (var palabra in Palabras(artist).Concat(Palabras(title)))
            if (!origen.Any(o => o == palabra || JaroWinkler(o, palabra) >= 0.85))
                return false;

        return true;
    }

    /// <summary>Palabras con contenido de un nombre: normalizadas, sin relleno y sin extensión.</summary>
    private static List<string> Palabras(string? s)
        => Regex.Split(Regex.Replace(s ?? "", @"\.(mp3|flac|wav|m4a|aiff?|ogg)$", "", RegexOptions.IgnoreCase),
                       @"[^\p{L}\p{N}]+")
                .Select(TextUtils.Nk)
                .Where(w => w.Length >= 2 && !Relleno.Contains(w))
                .ToList();

    public static bool IsSkipMix(string? baseName, string? fnTitle, string? fnArtist)
    {
        baseName ??= ""; fnTitle ??= ""; fnArtist ??= "";
        if (Regex.IsMatch(baseName, @"\b(mashup|mash\s*up|mash-up|spice\s+mash|bootleg|transition|segue|blend)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(fnTitle, @"\svs\.?\s", RegexOptions.IgnoreCase)
            || Regex.IsMatch(fnArtist, @"\svs\.?\s", RegexOptions.IgnoreCase))
            return true;

        // "Edit" de DJ, salvo las ediciones de catálogo conocidas.
        return Regex.IsMatch(baseName, @"\bedit\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(baseName, @"\b(radio|extended|short|quick|club|melodic|original|album|single)\s+edit\b", RegexOptions.IgnoreCase);
    }
}
