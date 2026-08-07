namespace Etiquetador.Core.Analysis;

/// <summary>Criterio para considerar dos canciones duplicadas.</summary>
public enum DuplicateMode
{
    /// <summary>Mismo título (ignora el artista). El más agresivo.</summary>
    TitleOnly,
    /// <summary>Mismo artista y título (por defecto).</summary>
    ArtistTitle,
    /// <summary>Mismo artista, título y duración parecida (≤5 s de diferencia). El más estricto.</summary>
    ArtistTitleDuration,
}

/// <summary>Un grupo de posibles duplicados (misma canción según el criterio elegido).</summary>
public sealed record DuplicateGroup(string Key, string Artist, string Title, IReadOnlyList<Track> Tracks);

/// <summary>Detecta canciones duplicadas por clave normalizada, con distintos criterios.</summary>
public static class DuplicateFinder
{
    private const int DurationToleranceSec = 5;

    /// <summary>Clave de comparación (para TitleOnly / ArtistTitle). Los descriptores cuentan como título.</summary>
    public static string KeyOf(Track t, DuplicateMode mode = DuplicateMode.ArtistTitle)
        => mode == DuplicateMode.TitleOnly
            ? TextUtils.Nk(t.Title)
            : TextUtils.Nk(t.Artist) + "|" + TextUtils.Nk(t.Title);

    /// <summary>Agrupa los tracks con la misma clave (2+ copias) según <paramref name="mode"/>.</summary>
    public static IReadOnlyList<DuplicateGroup> Find(IEnumerable<Track> tracks, DuplicateMode mode = DuplicateMode.ArtistTitle)
    {
        var withTitle = tracks.Where(t => !string.IsNullOrWhiteSpace(t.Title)).ToList();

        if (mode == DuplicateMode.ArtistTitleDuration)
            return FindByDuration(withTitle);

        return withTitle
            .GroupBy(t => KeyOf(t, mode))
            .Where(g => g.Count() > 1 && TextUtils.Nk(g.First().Title).Length > 0)
            .Select(g => { var f = g.First(); return new DuplicateGroup(g.Key, f.Artist ?? "", f.Title ?? "", g.ToList()); })
            .OrderByDescending(g => g.Tracks.Count)
            .ThenBy(g => g.Artist)
            .ToList();
    }

    // Dentro de cada grupo artista|título, agrupa por CERCANÍA de duración (enlace por diferencia ≤ tolerancia),
    // no por bloques fijos: así "diferencia < 5 s" se respeta sin artefactos de frontera.
    private static IReadOnlyList<DuplicateGroup> FindByDuration(List<Track> withTitle)
    {
        var result = new List<DuplicateGroup>();
        foreach (var g in withTitle.GroupBy(t => TextUtils.Nk(t.Artist) + "|" + TextUtils.Nk(t.Title)))
        {
            if (TextUtils.Nk(g.First().Title).Length == 0) continue;
            var sorted = g.OrderBy(t => t.DurationSeconds).ToList();
            int i = 0;
            while (i < sorted.Count)
            {
                int j = i + 1;
                while (j < sorted.Count && sorted[j].DurationSeconds - sorted[j - 1].DurationSeconds <= DurationToleranceSec) j++;
                if (j - i > 1)
                {
                    var run = sorted.GetRange(i, j - i);
                    result.Add(new DuplicateGroup(g.Key, run[0].Artist ?? "", run[0].Title ?? "", run));
                }
                i = j;
            }
        }
        return result.OrderByDescending(x => x.Tracks.Count).ThenBy(x => x.Artist).ToList();
    }
}
