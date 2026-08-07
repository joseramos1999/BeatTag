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
