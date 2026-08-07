namespace Etiquetador.Core.Analysis;

public sealed record StatItem(string Label, int Count);

public sealed record LibrarySummary(
    int Total,
    int Incomplete,
    long TotalSeconds,
    IReadOnlyList<StatItem> ByBpm,
    IReadOnlyList<StatItem> ByGenre,
    IReadOnlyList<StatItem> ByQuality,
    IReadOnlyList<StatItem> ByDecade,
    IReadOnlyList<StatItem> ByExplicit);

/// <summary>Agrega estadísticas de la biblioteca (BPM, género, calidad, década, explícito).</summary>
public static class LibraryStats
{
    public static LibrarySummary Compute(IReadOnlyCollection<Track> tracks)
    {
        var byBpm = OrderedCounts(tracks, BpmBucket, BpmOrder);
        var byQuality = OrderedCounts(tracks, t => AudioQuality.Label(AudioQuality.Rate(t)),
            new[] { "Sin pérdida", "Alta", "Media", "Baja", "Desconocida" });
        var byDecade = Counts(tracks, DecadeBucket).OrderByDescending(s => s.Label).ToList();
        var byExplicit = OrderedCounts(tracks, ExplicitBucket, new[] { "Explícito", "Clean", "Sin marca" });

        // Género: normalizado, top 15 por frecuencia (+ "(sin género)")
        var byGenre = tracks
            .Select(t => string.IsNullOrWhiteSpace(t.Genre) ? "(sin género)" : GenreNormalizer.Canonical(t.Genre))
            .GroupBy(g => g)
            .Select(g => new StatItem(g.Key, g.Count()))
            .OrderByDescending(s => s.Count)
            .Take(15)
            .ToList();

        return new LibrarySummary(
            tracks.Count,
            tracks.Count(t => t.IsIncomplete),
            tracks.Sum(t => (long)t.DurationSeconds),
            byBpm, byGenre, byQuality, byDecade, byExplicit);
    }

    private static readonly string[] BpmOrder =
        { "(sin BPM)", "<90", "90–99", "100–109", "110–119", "120–127", "128–139", "140+" };

    private static string BpmBucket(Track t) => t.Bpm switch
    {
        0 => "(sin BPM)",
        < 90 => "<90",
        < 100 => "90–99",
        < 110 => "100–109",
        < 120 => "110–119",
        < 128 => "120–127",
        < 140 => "128–139",
        _ => "140+",
    };

    private static string DecadeBucket(Track t) => t.Year == 0 ? "(sin año)" : (t.Year / 10 * 10) + "s";

    private static string ExplicitBucket(Track t) => ExplicitDetector.Detect(t) switch
    {
        Explicitness.Explicit => "Explícito",
        Explicitness.Clean => "Clean",
        _ => "Sin marca",
    };

    private static List<StatItem> Counts(IEnumerable<Track> tracks, Func<Track, string> key)
        => tracks.GroupBy(key).Select(g => new StatItem(g.Key, g.Count())).ToList();

    // Cuenta y ordena según un orden fijo (incluye buckets con 0).
    private static List<StatItem> OrderedCounts(IEnumerable<Track> tracks, Func<Track, string> key, string[] order)
    {
        var counts = tracks.GroupBy(key).ToDictionary(g => g.Key, g => g.Count());
        return order.Select(o => new StatItem(o, counts.TryGetValue(o, out var c) ? c : 0)).ToList();
    }
}
