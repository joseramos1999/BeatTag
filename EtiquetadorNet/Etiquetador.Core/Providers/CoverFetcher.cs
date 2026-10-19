namespace Etiquetador.Core.Providers;

/// <summary>Descarga carátulas y las convierte en TagLib.Picture (FrontCover). Port de Get-CoverPicture.</summary>
public sealed class CoverFetcher
{
    /// <summary>
    /// Carátulas que se guardan en memoria como mucho. La caché sirve para las canciones seguidas
    /// del mismo disco, que comparten URL. Antes no tenía límite y vive lo que la aplicación: al
    /// aplicar una biblioteca grande con carátula del catálogo se quedaban en memoria todas las
    /// descargadas (cientos de KB cada una, una por disco), aunque cada una solo se vuelva a pedir
    /// mientras se aplican las canciones de ese mismo disco.
    /// </summary>
    public const int MaxEnMemoria = 32;

    private readonly ApiClient _api;
    private readonly Dictionary<string, TagLib.Picture> _cache = new();
    private readonly Queue<string> _orden = new();   // de la más antigua a la más reciente
    private readonly object _cerrojo = new();

    public CoverFetcher(ApiClient api) => _api = api;

    /// <summary>Carátulas en memoria ahora mismo (para pruebas y diagnóstico).</summary>
    public int EnMemoria { get { lock (_cerrojo) return _cache.Count; } }

    public async Task<TagLib.Picture?> FetchAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(url)) return null;
        lock (_cerrojo) { if (_cache.TryGetValue(url, out var cached)) return cached; }
        try
        {
            var bytes = await _api.GetBytesAsync(url, ct).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0) return null;
            var pic = new TagLib.Picture(new TagLib.ByteVector(bytes))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = "image/jpeg",
                Description = "Cover",
            };
            Guardar(url, pic);
            return pic;
        }
        catch { return null; }
    }

    /// <summary>Guarda la carátula y, si se pasa del máximo, olvida la que lleva más tiempo guardada.</summary>
    private void Guardar(string url, TagLib.Picture pic)
    {
        lock (_cerrojo)
        {
            if (_cache.ContainsKey(url)) { _cache[url] = pic; return; }
            _cache[url] = pic;
            _orden.Enqueue(url);
            while (_orden.Count > MaxEnMemoria) _cache.Remove(_orden.Dequeue());
        }
    }
}
