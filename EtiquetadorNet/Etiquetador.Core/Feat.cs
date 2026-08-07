using System.Text.RegularExpressions;

namespace Etiquetador.Core;

/// <summary>Gestión de "feat." redundante: quita invitados que ya están en el artista, conserva los nuevos.</summary>
public static class Feat
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Reconstruye el "feat." dejando solo los nombres que NO están ya en el artista ($na = NK del artista).
    private static string FeatKeep(string namesStr, string na, string fmt)
    {
        var names = Regex.Split(namesStr, @"(?i)\s*(?:,|&| y | and | x )\s*");
        var remain = new List<string>();
        foreach (var raw in names)
        {
            var nm = raw.Trim();
            var k = TextUtils.Nk(nm);
            if (k.Length > 0 && !na.Contains(k)) remain.Add(nm);
        }
        if (remain.Count == 0) return "";
        return string.Format(fmt, string.Join(", ", remain));
    }

    /// <summary>Quita del título los "feat." (entre paréntesis o sueltos) que ya figuran en el artista.</summary>
    public static string RemoveRedundantFeat(string? title, string? artists)
    {
        if (string.IsNullOrEmpty(title)) return title ?? "";
        var na = TextUtils.Nk(artists);
        // feat entre paréntesis/corchetes: "(feat. X, Y)"
        title = Regex.Replace(title,
            @"(?i)\s*[\(\[]\s*(?:feat\.?|ft\.?|featuring|with|con)\s+([^\)\]]+)[\)\]]",
            m => FeatKeep(m.Groups[1].Value, na, " (feat. {0})"), IC);
        // feat SUELTO (sin paréntesis): "... feat. X, Y" hasta un paréntesis/corchete o el final
        title = Regex.Replace(title,
            @"(?i)\s+(?:feat\.?|ft\.?|featuring)\s+([^\(\[]+?)(?=\s*[\(\[]|$)",
            m => FeatKeep(m.Groups[1].Value, na, " feat. {0}"), IC);
        return Regex.Replace(title, @"\s{2,}", " ").Trim();
    }
}
