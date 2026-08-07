namespace Etiquetador.Core.Analysis;

public enum QualityTier { Unknown, Low, Medium, High, Lossless }

/// <summary>Clasifica la calidad de audio de un track a partir de su formato y bitrate.</summary>
public static class AudioQuality
{
    private static readonly HashSet<string> LosslessExt = new(StringComparer.OrdinalIgnoreCase)
    { ".flac", ".wav", ".aiff", ".aif", ".alac", ".ape", ".wv" };

    public static QualityTier Rate(Track t)
    {
        var ext = Path.GetExtension(t.FilePath);
        if (LosslessExt.Contains(ext)) return QualityTier.Lossless;
        if (t.Bitrate <= 0) return QualityTier.Unknown;
        if (t.Bitrate < 128) return QualityTier.Low;
        if (t.Bitrate < 256) return QualityTier.Medium;
        return QualityTier.High;
    }

    /// <summary>¿Calidad pobre para un DJ? (por debajo de 192 kbps con pérdida).</summary>
    public static bool IsPoor(Track t)
    {
        var tier = Rate(t);
        return tier == QualityTier.Low || (tier == QualityTier.Medium && t.Bitrate < 192);
    }

    public static string Label(QualityTier tier) => tier switch
    {
        QualityTier.Lossless => "Sin pérdida",
        QualityTier.High => "Alta",
        QualityTier.Medium => "Media",
        QualityTier.Low => "Baja",
        _ => "Desconocida",
    };
}
