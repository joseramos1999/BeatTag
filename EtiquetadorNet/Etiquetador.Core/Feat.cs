using System.Text.RegularExpressions;

namespace Etiquetador.Core;

/// <summary>Gestión de "feat." redundante: quita invitados que ya están en el artista, conserva los nuevos.</summary>
public static class Feat
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly string SepRe = @"(?i)\s*(?:,|&| y | and | x )\s*";

    // Reconstruye el "feat." dejando solo los nombres que NO están ya en el artista.
    // $na = NK del artista completo; $artistNames = cada artista por separado.
    private static string FeatKeep(string namesStr, string na, IReadOnlyList<string> artistNames, string fmt)
    {
        var names = Regex.Split(namesStr, SepRe);
        var remain = new List<string>();
        foreach (var raw in names)
        {
            var nm = raw.Trim();
            if (nm.Length == 0) continue;

            // Caso simple: el invitado entero ya está en el artista.
            var k = TextUtils.Nk(nm);
            if (k.Length > 0 && na.Contains(k)) continue;

            // Caso real del material DJ: los invitados vienen pegados por espacios
            // ("feat. Natasja Alex Gaudino"), así que hay que quitar del trozo los nombres
            // que ya figuran en el artista y quedarse solo con lo nuevo.
            foreach (var a in artistNames.OrderByDescending(x => x.Length))
            {
                if (a.Length < 3) continue;
                var pat = Regex.Escape(a).Replace("\\ ", @"\s+");
                nm = Regex.Replace(nm, $@"\b{pat}\b", " ", IC);
            }
            nm = Regex.Replace(nm, @"\s{2,}", " ").Trim().Trim(',', '&', '-').Trim();

            if (nm.Length > 0 && TextUtils.Nk(nm).Length > 0) remain.Add(nm);
        }
        if (remain.Count == 0) return "";
        return string.Format(fmt, string.Join(", ", remain));
    }

    /// <summary>Quita del título los "feat." (entre paréntesis o sueltos) que ya figuran en el artista.</summary>
    public static string RemoveRedundantFeat(string? title, string? artists)
    {
        if (string.IsNullOrEmpty(title)) return title ?? "";
        var na = TextUtils.Nk(artists);
        var artistNames = Regex.Split(artists ?? "", SepRe)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();

        // feat entre paréntesis/corchetes: "(feat. X, Y)"
        title = Regex.Replace(title,
            @"(?i)\s*[\(\[]\s*(?:feat\.?|ft\.?|featuring|with|con)\s+([^\)\]]+)[\)\]]",
            m => FeatKeep(m.Groups[1].Value, na, artistNames, " (feat. {0})"), IC);
        // feat SUELTO (sin paréntesis): "... feat. X, Y" hasta un paréntesis/corchete o el final
        title = Regex.Replace(title,
            @"(?i)\s+(?:feat\.?|ft\.?|featuring)\s+([^\(\[]+?)(?=\s*[\(\[]|$)",
            m => FeatKeep(m.Groups[1].Value, na, artistNames, " feat. {0}"), IC);
        return Regex.Replace(title, @"\s{2,}", " ").Trim();
    }
}
