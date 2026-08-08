using System.Text.RegularExpressions;

namespace Etiquetador.Core.Providers;

/// <summary>Enlace a una canción concreta reconocido en Deezer, Spotify o Apple Music/iTunes.</summary>
public readonly record struct TrackLink(string Source, string Id);

/// <summary>
/// Reconoce enlaces de canción pegados por el usuario y extrae la fuente y el id. Es puro (sin red):
/// resolverlo a metadatos lo hace <see cref="LinkResolver"/>.
/// </summary>
public static class TrackLinkParser
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Devuelve el enlace reconocido, o null si el texto no es un enlace de canción soportado.</summary>
    public static TrackLink? Parse(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return null;

        // Deezer: deezer.com/track/3135556, deezer.com/es/track/3135556
        var m = Regex.Match(s, @"(?:https?://)?(?:www\.)?deezer\.com/(?:[a-z]{2}/)?track/(\d+)", IC);
        if (m.Success) return new TrackLink("Deezer", m.Groups[1].Value);

        // Spotify: open.spotify.com/track/<id>, open.spotify.com/intl-es/track/<id>, spotify:track:<id>
        m = Regex.Match(s, @"(?:https?://)?open\.spotify\.com/(?:[a-z\-]+/)?track/([A-Za-z0-9]+)", IC);
        if (m.Success) return new TrackLink("Spotify", m.Groups[1].Value);
        m = Regex.Match(s, @"^spotify:track:([A-Za-z0-9]+)$", IC);
        if (m.Success) return new TrackLink("Spotify", m.Groups[1].Value);

        // Apple Music / iTunes: el id de la CANCIÓN va en ?i=; sin él, el enlace es de álbum.
        if (Regex.IsMatch(s, @"(?:music|itunes)\.apple\.com/", IC))
        {
            m = Regex.Match(s, @"[?&]i=(\d+)", IC);
            if (m.Success) return new TrackLink("iTunes", m.Groups[1].Value);
        }

        return null;
    }

    /// <summary>true si el texto parece un enlace (aunque no sea de una canción soportada).</summary>
    public static bool LooksLikeUrl(string? text)
    {
        var s = (text ?? "").Trim();
        return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase);
    }
}
