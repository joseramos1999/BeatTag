using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Core.Providers;

/// <summary>
/// Un artista de la biblioteca cuyas novedades se buscan (página Novedades).
/// </summary>
/// <param name="Clave">Su clave (<see cref="IndiceArtistas.Clave"/>), ya con el nombre canónico si es un alias.</param>
/// <param name="Nombre">Cómo lo escribe la biblioteca (la grafía de más canciones), o el canónico si es un alias.</param>
/// <param name="Canciones">En cuántas canciones de la biblioteca figura como artista.</param>
/// <param name="Titulos">
/// Los títulos de esas canciones, normalizados con <see cref="ArtistasDeLaBiblioteca.ClaveTitulo"/>. Con
/// ellos se reconoce en Deezer al artista de verdad entre sus homónimos.
/// </param>
public sealed record ArtistaBiblioteca(string Clave, string Nombre, int Canciones, IReadOnlySet<string> Titulos);

/// <summary>
/// Los artistas con más canciones en la biblioteca: de quiénes merece buscar lanzamientos nuevos.
///
/// Se cuentan por CADA artista del crédito («Ozuna, Daddy Yankee Ft Quevedo» suma a los tres), con los
/// alias unidos (<see cref="ArtistAliases"/>: «Cruzzi» cuenta como «Cruz Cafuné») y sin lo que no es un
/// intérprete: un «@usuario» o un record pool en el campo Artista (la misma regla que
/// <see cref="LimpiezaPacks.ArtistaEsPublicidad"/>, que es la de <see cref="Ai.NombreCortado"/>), ni
/// «Various Artists» y similares. Si la canción no tiene artista en las etiquetas, se lee del nombre
/// del archivo.
/// </summary>
public static class ArtistasDeLaBiblioteca
{
    /// <summary>
    /// Un artista con una sola canción no se consulta. Ese único archivo suele ser un mashup que lleva
    /// títulos en el campo Artista o una etiqueta mal puesta, y en una biblioteca grande los artistas con
    /// una sola canción son miles: no son «los que más pinchas».
    /// </summary>
    public const int MinimoCanciones = 2;

    /// <summary>Títulos de menos letras que esto no sirven para reconocer a un artista («Yo», «Ti»).</summary>
    public const int MinimoLetrasTitulo = 3;

    // Lo que no es el nombre de nadie, ya como clave (minúsculas, sin signos).
    private static readonly HashSet<string> NoSonArtistas = new(StringComparer.Ordinal)
    {
        "various artists", "various", "varios artistas", "varios", "va", "v a", "unknown", "unknown artist",
        "desconocido", "artista desconocido", "otros", "dj", "intro", "remix", "mashup", "acapella",
    };

    /// <summary>
    /// Un trozo del crédito que describe ediciones en vez de nombrar a un intérprete: «40 Mashups by
    /// Alex Gonzalez», «Remixes by…», «Bootlegs 2025». MEDIDO con los 200 artistas de una biblioteca
    /// real: «40 mashups by Alex Gonzalez» entraba entre los principales (24 canciones) y Deezer no lo
    /// encontraba; solo quitaba el sitio a un artista de verdad. Las palabras van completas para no
    /// apartar nombres como «Edith» o «Remixa».
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex DescribeEdiciones = new(
        @"\b(?:mashups?|remix(?:es)?|bootlegs?|edits|bpm|transici[oó]n(?:es)?|transitions?)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Los <paramref name="cuantos"/> artistas con más canciones (al menos <see cref="MinimoCanciones"/>),
    /// de más a menos, con los títulos de sus canciones. Recorre la biblioteca dos veces: la primera solo
    /// cuenta; la segunda lee los títulos (y los nombres de archivo) solo de las canciones de los elegidos.
    /// Se llama fuera del hilo de la interfaz.
    /// </summary>
    /// <param name="excluido">
    /// Artistas (por su clave) que el usuario no quiere consultar. No ocupan sitio: con 100 y dos
    /// excluidos se consultan los 100 siguientes que no lo están.
    /// </param>
    public static IReadOnlyList<ArtistaBiblioteca> Principales(IReadOnlyList<Track> canciones, int cuantos, ArtistAliases? alias = null,
                                                               Func<string, bool>? excluido = null)
    {
        alias ??= ArtistAliases.Current;
        if (cuantos <= 0 || canciones.Count == 0) return Array.Empty<ArtistaBiblioteca>();

        var cuenta = new Dictionary<string, int>(StringComparer.Ordinal);
        var grafias = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var claveDe = new Dictionary<string, (string Clave, string Nombre)?>(StringComparer.Ordinal);   // por parte del crédito: se repiten mucho
        var clavesPorCancion = new List<string>?[canciones.Count];

        for (var i = 0; i < canciones.Count; i++)
        {
            var credito = Credito(canciones[i]);
            if (credito.Length == 0) continue;
            List<string>? suyas = null;
            foreach (var parte in IndiceArtistas.Partir(credito))
            {
                if (!claveDe.TryGetValue(parte, out var c)) claveDe[parte] = c = ClaveDeParte(parte, alias);
                if (c is not { } k) continue;
                suyas ??= new List<string>(2);
                if (suyas.Contains(k.Clave)) continue;   // una canción cuenta una vez por artista
                suyas.Add(k.Clave);
                cuenta[k.Clave] = cuenta.GetValueOrDefault(k.Clave) + 1;
                if (!grafias.TryGetValue(k.Clave, out var formas)) grafias[k.Clave] = formas = new Dictionary<string, int>(StringComparer.Ordinal);
                formas[k.Nombre] = formas.GetValueOrDefault(k.Nombre) + 1;
            }
            clavesPorCancion[i] = suyas;
        }

        var elegidos = cuenta.Where(kv => kv.Value >= MinimoCanciones && excluido?.Invoke(kv.Key) != true)
                             .OrderByDescending(kv => kv.Value)
                             .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                             .Take(cuantos)
                             .Select(kv => kv.Key)
                             .ToList();
        if (elegidos.Count == 0) return Array.Empty<ArtistaBiblioteca>();

        // Segunda pasada: los títulos, solo de las canciones de los elegidos.
        var titulos = elegidos.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        for (var i = 0; i < canciones.Count; i++)
        {
            var suyas = clavesPorCancion[i];
            if (suyas == null) continue;
            HashSet<string>? deEsta = null;
            foreach (var k in suyas)
            {
                if (!titulos.TryGetValue(k, out var conjunto)) continue;
                deEsta ??= TitulosDe(canciones[i]);
                conjunto.UnionWith(deEsta);
            }
        }

        return elegidos.Select(k => new ArtistaBiblioteca(k, Grafia(grafias[k]), cuenta[k], titulos[k])).ToList();
    }

    /// <summary>
    /// El crédito de artistas de la canción: el de las etiquetas o, si no hay, el del nombre del
    /// archivo. Vacío si el crédito entero es publicidad (<see cref="SoloPublicidad"/>).
    /// </summary>
    internal static string Credito(Track t)
    {
        var credito = (t.Artist ?? "").Trim();
        if (credito.Length == 0) credito = FileNameParser.Parse(t.FileName).FnArtist.Trim();
        return credito.Length == 0 || SoloPublicidad(credito) ? "" : credito;
    }

    /// <summary>
    /// El crédito no nombra a ningún intérprete: empieza por «@» (un usuario de redes) o es solo el
    /// nombre de un pool, con su fecha o su volumen si acaso («Latin Box», «Latin Box 15 12 2025»;
    /// ver <see cref="LimpiezaPacks.RestoDelPool"/>). Si el pool va junto a artistas de verdad («Ozuna,
    /// Latin Box», «Bad Bunny (Latin Box Intro)»), el crédito vale y el pool se aparta por partes
    /// (<see cref="ClaveDeParte"/>). Antes se descartaba el crédito entero en cuanto nombraba un pool,
    /// y esas canciones no contaban para sus artistas ni servían para reconocerlos en Deezer.
    /// </summary>
    internal static bool SoloPublicidad(string credito)
    {
        var c = credito.Trim();
        if (c.StartsWith('@')) return true;
        return LimpiezaPacks.ArtistaEsPublicidad(c) && LimpiezaPacks.RestoDelPool(c).Length == 0;
    }

    /// <summary>
    /// La clave y el nombre de un trozo del crédito, o null si no puede ser un intérprete: un
    /// «@usuario» o un pool metidos entre otros artistas («Bad Bunny, Latin Box»), algo sin letras, o
    /// una palabra de versión («Remix»).
    /// </summary>
    private static (string Clave, string Nombre)? ClaveDeParte(string parte, ArtistAliases alias)
    {
        var p = parte.Trim();
        if (p.Count(char.IsLetter) < 2 || LimpiezaPacks.ArtistaEsPublicidad(p) || DescribeEdiciones.IsMatch(p)) return null;
        var canonico = alias.Canonical(TextUtils.Nk(TextUtils.ToAscii(p)));
        var nombre = canonico ?? p;
        var clave = IndiceArtistas.Clave(nombre);
        if (clave.Replace(" ", "").Length < 2 || NoSonArtistas.Contains(clave)) return null;
        // Lo que se queda en nada al quitarle las palabras de versión no es un nombre.
        if (Descriptors.CleanTitle(nombre).Length == 0) return null;
        return (clave, nombre);
    }

    /// <summary>
    /// La grafía de más canciones. A igualdad, la que no va toda en mayúsculas, y después por orden
    /// alfabético, para que salga lo mismo cada vez (la misma regla que <see cref="IndiceArtistas.Grafia"/>).
    /// </summary>
    private static string Grafia(Dictionary<string, int> formas)
        => formas.OrderByDescending(f => f.Value)
                 .ThenBy(f => f.Key.Any(char.IsLetter) && !f.Key.Any(char.IsLower))
                 .ThenBy(f => f.Key, StringComparer.Ordinal)
                 .First().Key;

    /// <summary>Los títulos con los que se puede reconocer la canción: el de las etiquetas y el del nombre del archivo.</summary>
    private static HashSet<string> TitulosDe(Track t)
    {
        var salida = new HashSet<string>(StringComparer.Ordinal);
        var deEtiqueta = ClaveTitulo(t.Title);
        if (deEtiqueta.Length > 0) salida.Add(deEtiqueta);
        var delNombre = ClaveTitulo(FileNameParser.Parse(t.FileName).QTitle);
        if (delNombre.Length > 0) salida.Add(delNombre);
        return salida;
    }

    /// <summary>
    /// El título sin versión, «feat.» ni adornos, normalizado: «DtMF (Extended)» y «DtMF» dan lo mismo.
    /// Vacío si se queda en menos de <see cref="MinimoLetrasTitulo"/> letras o cifras, que no reconocen
    /// a nadie.
    /// </summary>
    public static string ClaveTitulo(string? titulo)
    {
        if (string.IsNullOrWhiteSpace(titulo)) return "";
        var k = TextUtils.NkClave(Descriptors.CleanKeywords(titulo));
        return k.Length >= MinimoLetrasTitulo ? k : "";
    }
}
