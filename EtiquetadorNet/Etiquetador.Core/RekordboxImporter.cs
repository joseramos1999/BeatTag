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

    /// <summary>
    /// Convierte la Location de rekordbox en ruta local:
    ///   file://localhost/D:/Música/x.mp3        → D:\Música\x.mp3
    ///   file://localhost/Users/dj/Music/x.mp3   → /Users/dj/Music/x.mp3   (macOS)
    ///   file://servidor/musica/x.mp3            → \\servidor\musica\x.mp3 (red, en Windows)
    ///
    /// Se decide por la FORMA de la ruta y no solo por el sistema. Antes todo se trataba como de
    /// Windows: en un Mac, «file://localhost/Users/dj/Music/x.mp3» salía como «Users\dj\Music\x.mp3»
    /// -sin la barra inicial y con barras invertidas, que allí son parte del nombre-, nunca casaba
    /// con la biblioteca, e Importar y Reparar rekordbox daban 0 coincidencias.
    /// </summary>
    public static string LocationToPath(string location)
    {
        var s = location ?? "";
        var servidor = false;
        // Se conserva la barra que separa el host de la ruta: en macOS es la raíz «/».
        if (s.StartsWith("file://localhost/", StringComparison.OrdinalIgnoreCase)) s = s["file://localhost".Length..];
        else if (s.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) s = s["file://".Length..];
        else if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) { s = s["file://".Length..]; servidor = true; }
        s = Uri.UnescapeDataString(s);

        // Unidad de Windows: «/D:/Música/x.mp3» → «D:\Música\x.mp3».
        if (s.Length >= 3 && s[0] == '/' && char.IsAsciiLetter(s[1]) && s[2] == ':')
            return s[1..].Replace('/', '\\');

        // Ruta de red (el host es el servidor): «\\servidor\recurso\x.mp3» en Windows.
        if (servidor)
            return OperatingSystem.IsWindows() ? @"\\" + s.Replace('/', '\\') : "//" + s;

        // Ruta absoluta de macOS/Linux: tal cual, en cualquier sistema (es su forma).
        if (s.StartsWith('/')) return s;

        // Sin raíz reconocible (no debería darse): como antes, con el separador de Windows.
        return OperatingSystem.IsWindows() ? s.Replace('/', '\\') : s;
    }

    /// <summary>BPM redondeado (de "128.00" a 128). 0 si no es válido.</summary>
    public static uint ParseBpm(string bpm)
        => double.TryParse(bpm, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0
            ? (uint)System.Math.Round(d) : 0u;
}
