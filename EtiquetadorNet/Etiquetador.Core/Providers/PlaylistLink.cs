using System.Text.RegularExpressions;

namespace Etiquetador.Core.Providers;

/// <summary>Plataforma de la que sale una lista.</summary>
public enum PlataformaLista { Spotify, Deezer }

/// <summary>Una playlist (o un álbum) reconocida en un enlace pegado por el usuario.</summary>
public readonly record struct PlaylistLink(PlataformaLista Plataforma, string Tipo, string Id)
{
    /// <summary>«playlist» o «album».</summary>
    public bool EsAlbum => Tipo == "album";
}

/// <summary>
/// Reconoce enlaces de playlist de Spotify y Deezer. Es puro (sin red): los enlaces cortos que hay
/// que seguir para saber a dónde apuntan los resuelve <see cref="PlaylistProvider"/>.
///
/// Se aceptan también álbumes: la operación es la misma (una lista de canciones contra la
/// biblioteca) y es habitual querer saber qué temas de un EP de remixes ya se tienen.
/// </summary>
public static class PlaylistLinkParser
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>El enlace reconocido, o null si no es una playlist o un álbum de Spotify o Deezer.</summary>
    public static PlaylistLink? Parse(string? texto)
    {
        var s = (texto ?? "").Trim();
        if (s.Length == 0) return null;

        // Deezer: deezer.com/playlist/123, deezer.com/es/playlist/123, deezer.com/en/album/456
        var m = Regex.Match(s, @"(?:https?://)?(?:www\.)?deezer\.com/(?:[a-z]{2}(?:-[a-z]{2})?/)?(playlist|album)/(\d+)", IC);
        if (m.Success) return new PlaylistLink(PlataformaLista.Deezer, m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value);

        // Spotify: open.spotify.com/playlist/<id>, open.spotify.com/intl-es/playlist/<id>?si=…,
        // open.spotify.com/embed/playlist/<id>, spotify:playlist:<id>
        m = Regex.Match(s, @"(?:https?://)?open\.spotify\.com/(?:[a-z\-]+/)*?(playlist|album)/([A-Za-z0-9]{22})", IC);
        if (m.Success) return new PlaylistLink(PlataformaLista.Spotify, m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value);
        m = Regex.Match(s, @"^spotify:(playlist|album):([A-Za-z0-9]{22})$", IC);
        if (m.Success) return new PlaylistLink(PlataformaLista.Spotify, m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value);

        return null;
    }

    /// <summary>
    /// Un enlace corto de los que dan los botones «Compartir» de las aplicaciones móviles
    /// (link.deezer.com, deezer.page.link, spotify.link). No dice a qué lista apunta hasta seguirlo.
    /// </summary>
    public static bool EsCorto(string? texto)
        => Regex.IsMatch((texto ?? "").Trim(), @"^(?:https?://)?(?:link\.deezer\.com|deezer\.page\.link|spotify\.link|spotify\.app\.link)/\S+$", IC);
}
