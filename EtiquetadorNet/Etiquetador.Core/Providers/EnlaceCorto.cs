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
        => (await AbrirAsync(http, url, ct).ConfigureAwait(false)).Reconocer(reconocer, patron);

    /// <summary>
    /// Lo que dio seguir un enlace corto: la dirección final y el cuerpo de la página, o por qué no
    /// se pudo abrir.
    /// </summary>
    /// <param name="DireccionFinal">La dirección a la que llevó tras las redirecciones ("" si no respondió).</param>
    /// <param name="Cuerpo">La página final, por si redirige por script.</param>
    /// <param name="Error">"" si respondió; si no, el motivo (sin conexión, tiempo agotado…).</param>
    public sealed record Seguido(string DireccionFinal, string Cuerpo, string Error)
    {
        /// <summary>El servidor contestó (aunque sea con algo que no se reconoce).</summary>
        public bool Respondio => Error.Length == 0;

        /// <summary>Lo que <paramref name="reconocer"/> saque de la dirección final o, si no, de la página.</summary>
        public T? Reconocer<T>(Func<string?, T?> reconocer, string patron) where T : struct
        {
            if (!Respondio) return null;
            if (reconocer(DireccionFinal) is { } final) return final;
            var m = Regex.Match(Cuerpo, patron, IC);
            return m.Success ? reconocer(m.Value) : null;
        }
    }

    /// <summary>
    /// Sigue el enlace y devuelve a dónde lleva, distinguiendo «no responde» de «lleva a otra
    /// cosa». Antes los dos acababan en un null, y «Reanalizar… por enlace» decía sin conexión lo
    /// mismo que con el enlace de una lista: «no lleva a una canción», que confunde.
    /// </summary>
    public static async Task<Seguido> AbrirAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            url = url.Trim();
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var final = resp.RequestMessage?.RequestUri?.ToString() ?? url;
            var cuerpo = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new Seguido(final, cuerpo, "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new Seguido("", "", "no respondió a tiempo"); }
        catch (Exception e) { return new Seguido("", "", e is HttpRequestException ? "sin conexión o el servidor no responde" : e.Message); }
    }
}
