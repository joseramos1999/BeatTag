using System.Text.RegularExpressions;

namespace Etiquetador.Core.Providers;

/// <summary>
/// Los enlaces cortos que dan los botones «Compartir» de las aplicaciones móviles
/// (link.deezer.com, deezer.page.link, spotify.link). No dicen a qué apuntan hasta seguirlos.
///
/// Está en un solo sitio porque lo necesitan las listas (pestaña Playlists) y las canciones
/// («Reanalizar… por enlace»): antes solo lo sabían seguir las listas, y el enlace de una canción
/// copiado del móvil -la identificación más exacta que hay- daba «Enlace no reconocido».
/// </summary>
public static class EnlaceCorto
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>¿Es un enlace corto de los que hay que seguir?</summary>
    public static bool Es(string? texto)
        => Regex.IsMatch((texto ?? "").Trim(), @"^(?:https?://)?(?:link\.deezer\.com|deezer\.page\.link|spotify\.link|spotify\.app\.link)/\S+$", IC);

    /// <summary>
    /// Sigue el enlace y devuelve lo que <paramref name="reconocer"/> saque de él: primero de la
    /// dirección final tras las redirecciones; si la página redirige por script, buscando dentro
    /// de ella con <paramref name="patron"/>. Null si no lleva a nada reconocible o no responde.
    /// </summary>
    public static async Task<T?> SeguirAsync<T>(HttpClient http, string url, Func<string?, T?> reconocer, string patron,
        CancellationToken ct) where T : struct
    {
        try
        {
            url = url.Trim();
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (reconocer(resp.RequestMessage?.RequestUri?.ToString()) is { } final) return final;

            var cuerpo = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var m = Regex.Match(cuerpo, patron, IC);
            return m.Success ? reconocer(m.Value) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}
