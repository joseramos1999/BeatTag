using System.Xml.Linq;

namespace Etiquetador.Core;

/// <summary>Una pista del XML de rekordbox: ruta local + BPM + clave musical (Tonality).</summary>
public sealed record RekordboxEntry(string FilePath, string Bpm, string Key);

/// <summary>
/// Lee un XML de biblioteca de rekordbox (DJ_PLAYLISTS) y extrae, por pista, su ruta local,
/// BPM (AverageBpm) y clave musical (Tonality) para importarlos a los tags.
/// </summary>
public static class RekordboxImporter
{
    public static IReadOnlyList<RekordboxEntry> Parse(string xmlPath)
        => ParseContent(File.ReadAllText(xmlPath));

    public static IReadOnlyList<RekordboxEntry> ParseContent(string xml)
    {
        var list = new List<RekordboxEntry>();
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { return list; }
        foreach (var tr in doc.Descendants("TRACK"))
        {
            var loc = (string?)tr.Attribute("Location");
            if (string.IsNullOrEmpty(loc)) continue;      // los TRACK de PLAYLISTS son referencias sin Location
            var path = LocationToPath(loc);
            if (string.IsNullOrEmpty(path)) continue;
            var bpm = ((string?)tr.Attribute("AverageBpm"))?.Trim() ?? "";
            var key = ((string?)tr.Attribute("Tonality"))?.Trim() ?? "";
            list.Add(new RekordboxEntry(path, bpm, key));
        }
        return list;
    }

    /// <summary>Convierte la Location de rekordbox ("file://localhost/D:/Música/x.mp3") en ruta local.</summary>
    public static string LocationToPath(string location)
    {
        var s = location;
        if (s.StartsWith("file://localhost/", StringComparison.OrdinalIgnoreCase)) s = s["file://localhost/".Length..];
        else if (s.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) s = s["file:///".Length..];
        else if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) s = s["file://".Length..];
        s = Uri.UnescapeDataString(s);
        return s.Replace('/', '\\');
    }

    /// <summary>BPM redondeado (de "128.00" a 128). 0 si no es válido.</summary>
    public static uint ParseBpm(string bpm)
        => double.TryParse(bpm, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0
            ? (uint)System.Math.Round(d) : 0u;
}
