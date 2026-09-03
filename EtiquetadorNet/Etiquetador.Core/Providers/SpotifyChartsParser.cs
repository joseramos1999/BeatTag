using System.Net;
using System.Text.RegularExpressions;

namespace Etiquetador.Core.Providers;

/// <summary>
/// Lee el chart diario de Spotify de un país tal y como lo publica kworb.net.
///
/// Por qué no se pide directamente a Spotify: desde noviembre de 2024 su API devuelve 404 en todas
/// las listas editoriales -el Top 50 incluido- y 403 en todo `browse`. Comprobado con las
/// credenciales del propio usuario: la búsqueda y las pistas por ID responden 200, pero las listas,
/// no. No es cuestión de permisos que se puedan pedir: ese camino está cerrado.
///
/// Lo que kworb publica SÍ es el chart de Spotify, y trae el ID de pista de Spotify en cada enlace.
/// Con ese ID, la ficha exacta (duración, álbum, año) se pide a la API de Spotify, que para pistas
/// concretas sí responde. Es decir: los datos son de Spotify de principio a fin; kworb solo hace de
/// índice de lo que Spotify ya no deja consultar.
///
/// El parseo es deliberadamente tolerante: es HTML de un tercero y puede cambiar sin avisar. Ante
/// una fila que no encaje se la salta en vez de reventar, y quien llama decide qué hacer si el
/// resultado viene vacío.
/// </summary>
public static class SpotifyChartsParser
{
    // Una fila: posición en la primera celda, y la celda de título con los enlaces a artista y pista.
    private static readonly Regex FilaRe = new(
        @"<tr[^>]*>\s*<td[^>]*class=""np""[^>]*>(?<pos>\d+)</td>.*?<td[^>]*class=""[^""]*\bmp\b[^""]*""[^>]*>(?<celda>.*?)</td>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex ArtistaRe = new(@"<a[^>]+href=""[^""]*artist/([A-Za-z0-9]+)\.html""[^>]*>(?<n>.*?)</a>",
                                                  RegexOptions.IgnoreCase);
    private static readonly Regex PistaRe = new(@"<a[^>]+href=""[^""]*track/(?<id>[A-Za-z0-9]+)\.html""[^>]*>(?<t>.*?)</a>",
                                                RegexOptions.IgnoreCase);

    // El índice va por filas: el NOMBRE del país en la primera celda y el código dentro del enlace.
    //   <tr><td class="mp text">Spain</td><td ...><a href="country/es_daily.html">Daily</a> …
    // El nombre NO está en el texto del enlace (ahí pone "Daily"), que es el error fácil de cometer.
    private static readonly Regex FilaPaisRe = new(@"<tr[^>]*>(?<fila>.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex NombrePaisRe = new(@"<td[^>]*>(?<n>[^<]+)</td>", RegexOptions.IgnoreCase);
    private static readonly Regex CodigoPaisRe = new(@"country/(?<code>[a-z]+)_daily\.html", RegexOptions.IgnoreCase);

    /// <summary>Países disponibles, leídos del índice. Ordenados por nombre y sin repetidos.</summary>
    public static IReadOnlyList<(string Code, string Name)> ParseCountries(string html)
    {
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var salida = new List<(string, string)>();

        foreach (Match fila in FilaPaisRe.Matches(html ?? ""))
        {
            var texto = fila.Groups["fila"].Value;
            var cod = CodigoPaisRe.Match(texto);
            if (!cod.Success) continue;

            var nom = NombrePaisRe.Match(texto);
            var name = nom.Success ? Limpia(nom.Groups["n"].Value) : "";
            var code = cod.Groups["code"].Value.ToLowerInvariant();

            // Sin nombre en la fila se usa el código: mejor "es" que perder el país entero.
            if (name.Length == 0) name = code.ToUpperInvariant();
            if (!vistos.Add(code)) continue;
            salida.Add((code, name));
        }
        return salida.OrderBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Las canciones del chart, en orden. El artista principal va primero y los colaboradores
    /// detrás, separados por comas: para buscar en una biblioteca de DJ, "KAROL G, Judeline,
    /// rusowsky" acierta mucho más que solo "KAROL G".
    /// </summary>
    public static IReadOnlyList<ChartTrack> ParseTracks(string html, int limite = 50)
    {
        var salida = new List<ChartTrack>();
        foreach (Match m in FilaRe.Matches(html ?? ""))
        {
            if (salida.Count >= limite) break;
            if (!int.TryParse(m.Groups["pos"].Value, out var pos)) continue;

            var celda = m.Groups["celda"].Value;
            var pista = PistaRe.Match(celda);
            if (!pista.Success) continue;                    // sin pista no hay fila que mostrar

            var titulo = Limpia(pista.Groups["t"].Value);
            if (titulo.Length == 0) continue;

            var artistas = ArtistaRe.Matches(celda).Select(a => Limpia(a.Groups["n"].Value))
                                    .Where(n => n.Length > 0).ToList();
            if (artistas.Count == 0) continue;                // sin artista tampoco

            salida.Add(new ChartTrack(pos, string.Join(", ", artistas), titulo, 0,
                                      $"https://open.spotify.com/track/{pista.Groups["id"].Value}")
            {
                SpotifyId = pista.Groups["id"].Value,
            });
        }
        return salida;
    }

    /// <summary>Quita las etiquetas que puedan quedar dentro y decodifica las entidades HTML.</summary>
    private static string Limpia(string s)
        => WebUtility.HtmlDecode(Regex.Replace(s ?? "", "<[^>]+>", "")).Trim();
}
