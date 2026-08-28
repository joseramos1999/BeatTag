namespace Etiquetador.Core.Providers;

/// <summary>
/// Resultado normalizado de cualquier proveedor de metadatos. Campos opcionales según la fuente
/// (Deezer trae Bpm/AlbumId, Spotify ArtistId, Discogs solo Genre/Album/Year, etc.).
/// </summary>
public sealed record ProviderResult
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string Year { get; init; } = "";
    public string Genre { get; init; } = "";
    public string CoverUrl { get; init; } = "";
    public string ArtistId { get; init; } = "";
    public string AlbumId { get; init; } = "";
    public int Bpm { get; init; }
    public double Score { get; init; }
    public int Dur { get; init; }

    /// <summary>
    /// Por que gano este candidato ("titulo exacto +10, dur =1s +5"). Es el razonamiento del
    /// scoring, que antes solo se escribia en el registro y se perdia: llevarlo hasta la pantalla
    /// permite juzgar una propuesta dudosa sin salir de la aplicacion.
    /// </summary>
    public string Why { get; init; } = "";
}
