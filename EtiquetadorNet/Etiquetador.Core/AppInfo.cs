namespace Etiquetador.Core;

/// <summary>Identidad de la app para cabeceras HTTP (User-Agent de Spotify/MusicBrainz/Discogs).</summary>
public static class AppInfo
{
    public const string Name = "BeatTag";
    public const string Version = "1.2.0";
    public const string UserAgent = Name + "/" + Version;
    // MusicBrainz exige una URL de contacto en el User-Agent.
    public const string MusicBrainzUserAgent = Name + "/" + Version + " ( https://github.com/joseramos1999/BeatTag )";
}
