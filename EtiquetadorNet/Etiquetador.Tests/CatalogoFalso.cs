using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Pipeline;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// Catálogos de mentira para probar FileProcessor de punta a punta SIN red: responde a las URLs de
/// Deezer, MusicBrainz y Discogs con el JSON que cada prueba le da y apunta lo que se le pide.
/// Lo que no conoce lo contesta con un 404, que para el cliente es «no hay resultados».
///
/// Así se reproducen tal cual los casos reales de los registros (el nombre del archivo y lo que
/// devolvía el catálogo) y se comprueba lo que de verdad sale en la propuesta.
/// </summary>
internal sealed class CatalogoFalso : HttpMessageHandler
{
    private readonly List<(Func<string, bool> Casa, string Json)> _rutas = new();

    /// <summary>URLs pedidas, ya decodificadas, en orden.</summary>
    public List<string> Pedidas { get; } = new();

    /// <summary>Responde con <paramref name="json"/> a las URLs (decodificadas) que cumplan <paramref name="casa"/>.</summary>
    public CatalogoFalso Responde(Func<string, bool> casa, JsonNode json)
    {
        _rutas.Add((casa, json.ToJsonString()));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
        lock (Pedidas) Pedidas.Add(url);
        foreach (var (casa, json) in _rutas)
            if (casa(url))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    }

    /// <summary>FileProcessor con todos los proveedores apuntando a este catálogo.</summary>
    public FileProcessor Procesador(string dir, Logger? log = null)
    {
        var paths = new AppPaths(dir);
        var api = new ApiClient(paths, new HttpClient(this)) { CacheOn = false };
        return new FileProcessor(
            new DeezerProvider(api), new ItunesProvider(api), new SpotifyProvider(api),
            new MusicBrainzProvider(api), new DiscogsProvider(api), new AcoustIdProvider(api),
            new OllamaClient(api), new Fingerprint(paths), new HttpClient(this),
            new ArtistExceptions(null, new ArtistAliases()), log);   // grafías y alias de serie, sin depender de otras pruebas
    }

    // ---------- Respuestas con la forma de cada API ----------

    /// <summary>Búsqueda de Deezer (/search) con una sola pista.</summary>
    public static JsonNode DeezerBusqueda(long id, string artista, string titulo, int duracion, long albumId = 77, string album = "Álbum")
        => new JsonObject
        {
            ["data"] = new JsonArray(new JsonObject
            {
                ["id"] = id,
                ["title"] = titulo,
                ["duration"] = duracion,
                ["artist"] = new JsonObject { ["name"] = artista },
                ["album"] = new JsonObject { ["title"] = album, ["id"] = albumId, ["cover_big"] = "http://portadas/big.jpg" },
            }),
        };

    /// <summary>Detalle de pista de Deezer (/track/{id}): BPM, fecha y contribuidores.</summary>
    public static JsonNode DeezerPista(int bpm, string fecha, params string[] contribuidores)
    {
        var c = new JsonArray();
        foreach (var n in contribuidores) c.Add(new JsonObject { ["name"] = n });
        return new JsonObject { ["bpm"] = bpm, ["release_date"] = fecha, ["contributors"] = c };
    }

    /// <summary>Álbum de Deezer (/album/{id}) con sus géneros.</summary>
    public static JsonNode DeezerAlbum(params string[] generos)
    {
        var g = new JsonArray();
        foreach (var n in generos) g.Add(new JsonObject { ["name"] = n });
        return new JsonObject { ["genres"] = new JsonObject { ["data"] = g } };
    }

    /// <summary>Búsqueda de grabaciones de MusicBrainz con una sola grabación.</summary>
    public static JsonNode MusicBrainz(int puntuacion, string titulo, string artista, string disco = "Disco", string fecha = "2019-01-01")
        => new JsonObject
        {
            ["recordings"] = new JsonArray(new JsonObject
            {
                ["score"] = puntuacion,
                ["title"] = titulo,
                ["artist-credit"] = new JsonArray(new JsonObject { ["name"] = artista }),
                ["releases"] = new JsonArray(new JsonObject { ["title"] = disco, ["date"] = fecha }),
            }),
        };

    /// <summary>Búsqueda de Discogs con un solo resultado («Artista - Disco»).</summary>
    public static JsonNode Discogs(string tituloResultado, string anio, params string[] estilos)
    {
        var s = new JsonArray();
        foreach (var e in estilos) s.Add(e);
        return new JsonObject
        {
            ["results"] = new JsonArray(new JsonObject
            {
                ["title"] = tituloResultado, ["year"] = anio, ["style"] = s, ["genre"] = new JsonArray(),
            }),
        };
    }

    /// <summary>¿Es una búsqueda de Deezer cuya consulta contiene todas estas palabras?</summary>
    public static Func<string, bool> BusquedaDeezer(params string[] palabras)
        => u => u.StartsWith("https://api.deezer.com/search", StringComparison.Ordinal)
                && palabras.All(p => u.Contains(p, StringComparison.OrdinalIgnoreCase));

    public static Func<string, bool> PistaDeezer(long id) => u => u == $"https://api.deezer.com/track/{id}";
    public static Func<string, bool> AlbumDeezer(long id) => u => u == $"https://api.deezer.com/album/{id}";
    public static Func<string, bool> BusquedaMusicBrainz() => u => u.StartsWith("https://musicbrainz.org/ws/2/recording", StringComparison.Ordinal);
    public static Func<string, bool> BusquedaDiscogs() => u => u.StartsWith("https://api.discogs.com/database/search", StringComparison.Ordinal);
}
