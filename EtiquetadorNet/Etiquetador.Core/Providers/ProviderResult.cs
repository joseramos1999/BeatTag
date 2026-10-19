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

    /// <summary>
    /// Solo Discogs: el género sale de OTRO disco del mismo artista, no de esta canción. Vale para
    /// rellenar el hueco si ninguna otra fuente da género, pero no para pisar el de Deezer,
    /// Spotify o iTunes.
    /// </summary>
    public bool SoloArtista { get; init; }

    /// <summary>
    /// Código ISRC de la grabación («USUYG1017706»), ya comprobado, o "" si la fuente no lo da. Solo
    /// Deezer, y sin peticiones nuevas: viene en las respuestas de /search y de track/{id} que ya se
    /// piden (C14). Identifica una grabación concreta: la extended o la versión clean tienen el suyo.
    /// </summary>
    public string Isrc { get; init; } = "";

    /// <summary>
    /// Si la grabación del catálogo es explícita: <see cref="Analysis.Explicitness.Explicit"/>, la
    /// versión editada (<see cref="Analysis.Explicitness.Clean"/>) o sin dato. Solo Deezer (C14).
    /// </summary>
    public Analysis.Explicitness Explicit { get; init; }
}
