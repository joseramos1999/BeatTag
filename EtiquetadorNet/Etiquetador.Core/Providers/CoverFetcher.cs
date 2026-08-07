namespace Etiquetador.Core.Providers;

/// <summary>Descarga carátulas y las convierte en TagLib.Picture (FrontCover). Port de Get-CoverPicture.</summary>
public sealed class CoverFetcher
{
    private readonly ApiClient _api;
    private readonly Dictionary<string, TagLib.Picture> _cache = new();

    public CoverFetcher(ApiClient api) => _api = api;

    public async Task<TagLib.Picture?> FetchAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (_cache.TryGetValue(url, out var cached)) return cached;
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
            _cache[url] = pic;
            return pic;
        }
        catch { return null; }
    }
}
