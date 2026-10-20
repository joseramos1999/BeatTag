using System.Text.RegularExpressions;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Core.Dj;

/// <summary>Qué se hace con el texto publicitario que se quita de una etiqueta.</summary>
public enum DestinoPublicidad
{
    /// <summary>Se guarda en el comentario («[Pack: Latin Box 15 12 2025]»). Es lo que se propone.</summary>
    Comentario,
    /// <summary>Se borra sin más.</summary>
    Borrar,
}

/// <summary>La etiqueta que se limpia.</summary>
public enum CampoPublicidad { Album, Artista }

/// <summary>
/// Una etiqueta con publicidad de un pack y cómo quedaría. <see cref="Nuevo"/> es "" para un álbum
/// (se vacía) y el artista leído en el nombre del archivo para un artista.
/// </summary>
/// <param name="Marcada">Si sale marcada: solo cuando la regla es fuerte.</param>
public sealed record PropuestaPublicidad(string Ruta, CampoPublicidad Campo, string Actual, string Nuevo, string PorQue, bool Marcada);

/// <summary>
/// Limpiar la publicidad de los record pools en las etiquetas (idea P8).
///
/// MEDIDO en una biblioteca real de 13.590 canciones: 2.330 llevan el nombre del pool en el Álbum
/// («UNLIMITED LATIN» 235, «Latin Box 15 12 2025» 53…), 82 un usuario de redes («@tryitmusic» 39) y
/// 257 tienen como Artista un «@usuario» (el editor que hizo el mashup, no el intérprete). rekordbox
/// enseña esos campos y busca en ellos, y BeatTag compara el Artista para encontrar duplicados
/// (RevisionBandeja.ClaveExacta, el mismo criterio que Duplicados). Enriquecer no lo arregla en los
/// mashups y las ediciones porque, a propósito, no las identifica.
///
/// Solo reglas, sin IA:
///   · Álbum que es un record pool (<see cref="Descriptors.PoolRe"/>) o un usuario («@…»): se vacía.
///     El de un usuario sale marcado. El de un pool sale marcado solo si el álbum es esencialmente
///     el pool, es decir, si al quitar el pool y lo que acompaña a una entrega de pack (fechas,
///     números, volumen…) no queda nada (ver <see cref="RestoDelPool"/>); con más texto («FIFA World
///     Cup Qatar 2022») sale DESMARCADO y «Por qué» dice qué más lleva. «Try It» NO está en PoolRe a
///     propósito (es un editor que firma sus ediciones, no un pool), así que un álbum «Try It
///     Mashups» no se toca.
///   · Álbum que parece una web: se propone DESMARCADO. Visto en la misma biblioteca: «El Cangri.com»
///     (Daddy Yankee) y «souljaboytellem.com» (Soulja Boy) son álbumes de verdad.
///   · Artista «@usuario» o un pool: se propone el artista que dice el nombre del archivo, la misma
///     regla que <see cref="NombreCortado"/> aplica al completar nombres, pero solo si el nombre deja
///     claro cuál es el artista (ver <see cref="ArtistaDelNombre"/>). Si no, no se propone nada.
/// </summary>
public static class LimpiezaPacks
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex Pool = new(Descriptors.PoolRe);

    /// <summary>Una dirección web: «www.rmxvip.com», «LatinRemixes.com 05 04 2023», «https://…».</summary>
    private static readonly Regex Web = new(
        @"https?://|\bwww\.|\b[\w-]+\.(?:com|net|org|fm|es|mx|ru|info|io|vip|club|to|me|cc|co|biz)\b", IC);

    /// <summary>
    /// Por qué el álbum es publicidad y no un álbum, o null si parece un álbum.
    /// <paramref name="fuerte"/> dice si la regla es lo bastante segura para salir marcada.
    ///
    /// Con el nombre de un pool solo es fuerte si el álbum ES el pool: el nombre con su fecha, su
    /// volumen o su edición («Latin Box 15 12 2025», «Heavy Hits Vol. 3», «Dj City Latino»). PoolRe
    /// se escribió para limpiar nombres de archivo, donde un falso positivo cuesta poco; en el Álbum,
    /// «Intensa», «Latin Urbano» o «Qatar 2022» pueden ir dentro de un álbum de verdad («FIFA World
    /// Cup Qatar 2022 …»). Si queda más texto, se propone igualmente, pero desmarcado y diciendo qué
    /// texto queda, para que el usuario lo revise.
    /// </summary>
    public static string? PorQueAlbum(string? album, out bool fuerte)
    {
        fuerte = false;
        var a = (album ?? "").Trim();
        if (a.Length == 0) return null;
        if (a.StartsWith('@'))
        {
            fuerte = true;
            return "Empieza por «@»: es un usuario de redes, no un álbum";
        }
        var pool = Pool.Match(a);
        if (pool.Success)
        {
            var resto = RestoDelPool(a);
            if (resto.Length == 0)
            {
                fuerte = true;
                return $"«{pool.Value.Trim()}» es un record pool, no un álbum";
            }
            return $"Contiene «{pool.Value.Trim()}», el nombre de un record pool, pero también «{resto}». Revísalo: puede ser un álbum de verdad";
        }
        if (Web.IsMatch(a))
            return "Parece una dirección web. Revísalo: algunos álbumes reales lo son, como «El Cangri.com»";
        return null;
    }

    /// <summary>
    /// Lo que deja la publicidad de un pack, que no dice nada del disco: fechas y números, usuarios
    /// («@…»), y las palabras con que los pools nombran sus entregas (volumen, semana, mes, edición
    /// regional…). Son palabras de cualquier pool, no de una biblioteca concreta.
    /// </summary>
    private static readonly Regex RellenoDePack = new(
        @"@\S+|\d+|\b(?:vol(?:s|ume|umen)?|packs?|week(?:ly)?|semana(?:l)?|month(?:ly)?|mes|mensual|edition|edici[oó]n|update|actualizaci[oó]n|parte?|issue|n[oº°]|series?|collection|colecci[oó]n|music|records?|pool|djs?|exclusives?|exclusivos?|latino|latin|pro|plus|premium|vip|uk|us|usa|de|del|of|the|and|y|"
        + @"enero|febrero|marzo|abril|mayo|junio|julio|agosto|septiembre|setiembre|octubre|noviembre|diciembre|"
        + @"january|february|march|april|may|june|july|august|september|october|november|december|"
        + @"ene|feb|mar|abr|jun|jul|ago|sept?|oct|nov|dic|jan|apr|aug|dec)\b", IC);

    /// <summary>
    /// El texto del álbum que no es publicidad del pack («FIFA World Cup» en «FIFA World Cup Qatar
    /// 2022»), sin signos sueltos, o "" si el álbum es solo el pool.
    /// </summary>
    internal static string RestoDelPool(string album)
    {
        var resto = RellenoDePack.Replace(Pool.Replace(album, " "), " ");
        resto = Regex.Replace(resto, @"[^\p{L}\p{N}'’]+", " ");
        return Regex.Replace(resto, @"\s{2,}", " ").Trim();
    }

    /// <summary>
    /// El artista no es el intérprete: un usuario de redes («@liyo_dj98») o un record pool. Es la
    /// misma regla que usa <see cref="NombreCortado"/> para no poner a un editor delante del nombre.
    /// </summary>
    public static bool ArtistaEsPublicidad(string? artista)
    {
        var a = (artista ?? "").Trim();
        return a.Length > 0 && (a.StartsWith('@') || Pool.IsMatch(a));
    }

    /// <summary>El artista según el nombre del archivo, y por qué se considera fiable.</summary>
    /// <param name="Fuerte">
    /// Lo confirman artistas que la biblioteca ya conoce. Sin ellos (solo el título de las etiquetas
    /// confirma el orden), la propuesta sale desmarcada.
    /// </param>
    public sealed record ArtistaLeido(string Artista, string PorQue, bool Fuerte = true);

    /// <summary>
    /// El artista que dice el nombre del archivo, SOLO si se puede saber cuál de los dos lados del
    /// « - » es el artista. Si no, null: es preferible no proponer nada a escribir un título en el
    /// campo Artista.
    ///
    /// Visto en una biblioteca real, en los 257 archivos con un «@usuario» como artista: casi todos son
    /// mashups y transiciones, y su nombre va tan a menudo «Artistas - Títulos» («Khea, Cazzu, Katty
    /// Perry - Loca x Firework») como «Títulos - Artistas» («La Ocasion x Heroes - De La Ghetto,
    /// Arcangel»), muchas veces cortado («… - Bad Gyal x E»). Tomar siempre la parte de delante
    /// (FnArtist) habría puesto «La Prendo x Superman Sin Capa» como artista.
    ///
    /// Por eso se mira qué lado está formado por artistas que la biblioteca ya conoce
    /// (<see cref="IndiceArtistas"/>), y el otro no:
    ///   · Delante del guion basta con más de la mitad de los nombres: una errata («Katty Perry») no
    ///     debe impedirlo.
    ///   · Detrás tienen que serlo TODOS, porque es el final del nombre, que es por donde lo cortan los
    ///     pools: «Farruko, Ana Mena &amp; B» o «Wisin, Ozuna, Bad» tienen dos artistas conocidos y un
    ///     trozo, y escribirlos así en el Artista sería estropearlo de otra manera.
    /// Si ningún lado está formado por artistas conocidos, vale también que el título de las
    /// etiquetas coincida con lo que va detrás del guion, que confirma el orden «Artista - Título».
    /// Esa vía sale DESMARCADA (<see cref="ArtistaLeido.Fuerte"/>): lo medido abajo es con canciones de
    /// artista normal, que casi siempre se nombran «Artista - Título», y los mashups con «@usuario», que
    /// son el caso real de esta herramienta, mezclan los dos órdenes. Si una herramienta de nombres puso
    /// como título la segunda mitad del nombre, esta vía escribiría títulos en el Artista.
    ///
    /// Los artistas conocidos se escriben como los escribe la biblioteca (<see cref="IndiceArtistas.ComoEnLaBiblioteca"/>):
    /// los packs suelen nombrar en mayúsculas («KEVIN ROLDAN - …») y copiarlo crearía otra variante de
    /// un artista que ya está como «Kevin Roldan».
    ///
    /// MEDIDO sobre la misma biblioteca, con las canciones que SÍ tienen un artista normal (sacando
    /// cada una del índice al evaluarla): de 11.810 propuestas, 72 no coinciden con la etiqueta, y en
    /// casi todas la etiqueta es la que está mal (el editor como artista: «Eneko Rivas», «F3LY»). Antes
    /// de exigir todos los nombres detrás del guion y de descartar los créditos que solo dicen la
    /// versión, «REMIX» pasaba por artista y salían nombres cortados como «Wisin, Ozuna, Bad». De los
    /// 262 artistas «@usuario» o de pool, 112 reciben propuesta; el resto se queda sin ella, a propósito.
    /// </summary>
    public static ArtistaLeido? ArtistaDelNombre(Track t, IndiceArtistas indice)
    {
        var fn = FileNameParser.Parse(Path.GetFileName(t.FilePath));
        if (fn.FnArtist.Trim().Length == 0) return null;   // sin « - » no hay forma de saber qué es el artista

        var delante = Limpio(fn.FnArtist);
        var detras = Limpio(SinParentesis(fn.FnTitle));
        var (conocidosDelante, nombresDelante, primeroDelante) = Evaluar(delante, indice);
        var (conocidosDetras, nombresDetras, primeroDetras) = Evaluar(detras, indice);
        var artistasDelante = nombresDelante > 0 && conocidosDelante * 2 > nombresDelante;
        var artistasDetras = nombresDetras > 0 && conocidosDetras == nombresDetras;
        var algunoDetras = conocidosDetras * 2 > nombresDetras;   // para no tomar «delante» si detrás también parecen artistas

        if (artistasDelante && !algunoDetras && Valido(delante))
            return Conocidos(delante, primeroDelante, "", indice);
        if (artistasDetras && !artistasDelante && Valido(detras))
            return Conocidos(detras, primeroDetras, "; en este nombre los artistas van detrás del guion, como es habitual en los mashups", indice);

        // Nadie conocido a ningún lado: solo si las etiquetas confirman el orden «Artista - Título».
        if (!artistasDelante && !algunoDetras && Valido(delante)
            && MismoTitulo(t.Title, fn.FnTitle) && !MismoTitulo(t.Title, fn.FnArtist))
            return new ArtistaLeido(delante,
                "Ninguno de los nombres es artista de otras canciones de tu biblioteca; solo lo confirma el título de las etiquetas, "
                + "que coincide con lo que va detrás del guion. Revísalo: en los mashups el orden del nombre cambia",
                Fuerte: false);
        return null;
    }

    /// <summary>La propuesta de los artistas conocidos, escrita como en la biblioteca.</summary>
    private static ArtistaLeido Conocidos(string lado, string primero, string coletilla, IndiceArtistas indice)
    {
        var artista = indice.ComoEnLaBiblioteca(lado);
        var grafia = string.Equals(artista, lado, StringComparison.Ordinal) ? "" : "; escrito como en el resto de tu biblioteca";
        return new ArtistaLeido(artista, Conocido(primero, indice) + coletilla + grafia);
    }

    private static (int Conocidos, int Total, string Primero) Evaluar(string lado, IndiceArtistas indice)
    {
        var nombres = IndiceArtistas.Partir(lado);
        var conocidos = nombres.Where(indice.Conoce).ToList();
        return (conocidos.Count, nombres.Count, conocidos.FirstOrDefault() ?? "");
    }

    // Conocido quiere decir al menos IndiceArtistas.MinimoCanciones (dos): siempre en plural.
    private static string Conocido(string nombre, IndiceArtistas indice)
        => $"«{indice.Grafia(nombre)}» ya es artista de {indice.Canciones(nombre)} canciones de tu biblioteca";

    /// <summary>
    /// Lo que delata que el texto no es solo el nombre de los artistas: la versión o el BPM que se
    /// quedaron sin paréntesis («… K-Narias Myke Roldan Mashup 95-104Bpm») o la extensión repetida.
    /// </summary>
    private static readonly Regex NoEsArtista = new(
        @"\b(?:mashup|remix|rmx|bootleg|transici[oó]n|transition|edit|intro|outro|extended|bpm|mp3)\b|\d{2,3}\s*bpm", IC);

    /// <summary>Un artista que se pueda escribir: con letras, sin versión ni BPM, que no sea otro usuario ni un pool.</summary>
    private static bool Valido(string artista)
        => artista.Count(char.IsLetter) >= 2 && !NoEsArtista.IsMatch(artista) && !ArtistaEsPublicidad(artista);

    private static bool MismoTitulo(string? etiqueta, string delNombre)
    {
        var a = TextUtils.Nk(Descriptors.CleanTitle(etiqueta));
        var b = TextUtils.Nk(Descriptors.CleanTitle(delNombre));
        return a.Length >= 3 && a == b;
    }

    /// <summary>Sin lo que va entre paréntesis o corchetes, ni un paréntesis que se quedó abierto al final.</summary>
    private static string SinParentesis(string s)
        => Regex.Replace(Regex.Replace(s, @"\([^)]*\)|\[[^\]]*\]", " "), @"\s*[\(\[][^\)\]]*$", " ");

    /// <summary>
    /// Sin espacios de más ni un separador colgando al final («Feid,», «Bad Bunny ft.»). La «x» final
    /// no se quita: es el final de «Lil Nas X».
    /// </summary>
    private static string Limpio(string s)
    {
        var t = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return Regex.Replace(t, @"(?:\s*(?:[,;&/+\-]|\b(?:feat|ft|featuring|vs)\b\.?))+\s*$", "", IC).Trim();
    }

    /// <summary>
    /// Todas las propuestas para la biblioteca: una fila por etiqueta (una canción puede tener dos, el
    /// álbum y el artista, y se marcan por separado). Va fuera del hilo de la interfaz: lee el nombre
    /// de cada archivo con expresiones regulares.
    /// </summary>
    public static List<PropuestaPublicidad> Proponer(IReadOnlyList<Track> canciones, CancellationToken ct = default)
    {
        var indice = new IndiceArtistas(canciones);
        var res = new List<PropuestaPublicidad>();
        foreach (var t in canciones)
        {
            ct.ThrowIfCancellationRequested();
            if (PorQueAlbum(t.Album, out var fuerte) is { } porQue)
                res.Add(new PropuestaPublicidad(t.FilePath, CampoPublicidad.Album, t.Album!, "", porQue, fuerte));

            if (ArtistaEsPublicidad(t.Artist) && ArtistaDelNombre(t, indice) is { } leido)
            {
                var actual = t.Artist!.Trim();
                var que = actual.StartsWith('@') ? $"«{actual}» es un usuario de redes, no el intérprete" : $"«{actual}» lleva el nombre de un record pool";
                res.Add(new PropuestaPublicidad(t.FilePath, CampoPublicidad.Artista, t.Artist!, leido.Artista, $"{que}. {leido.PorQue}", leido.Fuerte));
            }
        }
        return res;
    }

    /// <summary>
    /// Cuántas canciones tienen un artista que es publicidad y se quedan sin propuesta porque el
    /// nombre del archivo no deja claro el artista. Para decirlo en el resumen.
    /// </summary>
    public static int ArtistasSinPropuesta(IReadOnlyList<Track> canciones, IReadOnlyCollection<PropuestaPublicidad> propuestas)
    {
        var conPropuesta = new HashSet<string>(propuestas.Where(p => p.Campo == CampoPublicidad.Artista).Select(p => p.Ruta),
                                               StringComparer.OrdinalIgnoreCase);
        return canciones.Count(t => ArtistaEsPublicidad(t.Artist) && !conPropuesta.Contains(t.FilePath));
    }

    // --- Lo que se guarda en el comentario ---
    //
    // Con un bloque entre corchetes, como la ficha de DJ («[DJ: …]», Fichas.ComentarioCombinado):
    // se reconoce a simple vista, no se mezcla con lo que el comentario ya tenía y volver a aplicar
    // no lo repite. Para algunos DJ la fecha del pack sirve para ordenar por pack: así no se pierde.

    /// <summary>«[Pack: Latin Box 15 12 2025]».</summary>
    public static string BloquePack(string album) => Bloque("Pack", album);

    /// <summary>«[Editor: @liyo_dj98]»: el usuario que firmaba el archivo como artista.</summary>
    public static string BloqueEditor(string usuario) => Bloque("Editor", usuario);

    // Un corchete de cierre dentro partiría el bloque, como en la ficha de DJ.
    private static string Bloque(string que, string texto) => $"[{que}: {texto.Trim().Replace(']', ')')}]";

    /// <summary>
    /// El comentario con <paramref name="bloque"/> añadido al final, o el mismo si ya lo contiene
    /// (aplicar dos veces no lo duplica). Si el comentario termina con la ficha de DJ, el bloque va
    /// delante de ella para que la ficha siga siendo lo último, que es donde la busca su volcado.
    /// </summary>
    public static string ComentarioCon(string? comentario, string bloque)
    {
        var actual = (comentario ?? "").Trim();
        if (bloque.Length == 0 || actual.Contains(bloque, StringComparison.OrdinalIgnoreCase)) return actual;
        if (actual.Length == 0) return bloque;
        var dj = Regex.Match(actual, @"\s*\[DJ:[^\]]*\]\s*$", IC);
        return dj.Success
            ? (actual[..dj.Index].TrimEnd() + " " + bloque + " " + dj.Value.Trim()).Trim()
            : actual + " " + bloque;
    }
}

/// <summary>
/// Los artistas que la biblioteca ya conoce por sus etiquetas, con cuántas canciones tiene cada uno.
/// Sirve para saber si un trozo del nombre de un archivo es un artista o el título de una canción.
/// </summary>
public sealed class IndiceArtistas
{
    /// <summary>
    /// Un artista cuenta como conocido con DOS canciones: con una sola bastaba que otro mashup
    /// llevara sus títulos en el campo Artista («Gatubela x Rompe») para darlos por artistas.
    /// </summary>
    public const int MinimoCanciones = 2;

    private readonly Dictionary<string, int> _canciones = new(StringComparer.Ordinal);

    /// <summary>Cómo escribe la biblioteca cada artista (por su clave): cada grafía y en cuántas canciones va.</summary>
    private readonly Dictionary<string, Dictionary<string, int>> _grafias = new(StringComparer.Ordinal);

    // Lo que no es el nombre de nadie: no puede confirmar que un trozo sea un artista.
    private static readonly HashSet<string> NoSonArtistas = new(StringComparer.Ordinal)
    {
        "various artists", "varios artistas", "va", "unknown", "unknown artist", "desconocido", "artista desconocido", "otros",
    };

    public IndiceArtistas(IEnumerable<Track> canciones)
    {
        // Util limpia el nombre con expresiones regulares: una vez por nombre distinto, no por canción.
        var utiles = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var t in canciones)
        {
            if (LimpiezaPacks.ArtistaEsPublicidad(t.Artist)) continue;
            var vistas = new HashSet<string>(StringComparer.Ordinal);
            foreach (var nombre in Partir(t.Artist))
            {
                var clave = Clave(nombre);
                if (!vistas.Add(clave)) continue;   // una canción cuenta una vez por artista
                if (!utiles.TryGetValue(clave, out var util)) utiles[clave] = util = Util(clave);
                if (!util) continue;
                _canciones[clave] = _canciones.GetValueOrDefault(clave) + 1;
                if (!_grafias.TryGetValue(clave, out var formas)) _grafias[clave] = formas = new Dictionary<string, int>(StringComparer.Ordinal);
                formas[nombre] = formas.GetValueOrDefault(nombre) + 1;
            }
        }
    }

    /// <summary>
    /// Cómo escribe la biblioteca a ese artista: la grafía de más canciones («Kevin Roldan» frente a
    /// «KEVIN ROLDAN»). A igualdad, la que no va toda en mayúsculas, y después por orden alfabético,
    /// para que salga lo mismo cada vez. Si no lo conoce, el nombre tal cual.
    /// </summary>
    public string Grafia(string nombre)
    {
        if (!_grafias.TryGetValue(Clave(nombre), out var formas) || formas.Count == 0) return nombre.Trim();
        return formas.OrderByDescending(f => f.Value)
                     .ThenBy(f => TodoMayusculas(f.Key))
                     .ThenBy(f => f.Key, StringComparer.Ordinal)
                     .First().Key;
    }

    private static bool TodoMayusculas(string s) => s.Any(char.IsLetter) && !s.Any(char.IsLower);

    // El mismo separador, capturado: Regex.Split deja así los separadores en el resultado.
    private static readonly Regex SeparadorConservado = new("(" + Separador + ")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// El crédito con cada artista conocido escrito como en la biblioteca, sin tocar los separadores ni
    /// los nombres que no conoce: «KEVIN ROLDAN, Nuevo» → «Kevin Roldan, Nuevo».
    /// </summary>
    public string ComoEnLaBiblioteca(string credito)
    {
        var trozos = SeparadorConservado.Split(credito);
        // Los separadores quedan en las posiciones impares.
        for (var i = 0; i < trozos.Length; i += 2)
        {
            var nombre = trozos[i].Trim();
            if (nombre.Length == 0 || !Conoce(nombre)) continue;
            var grafia = Grafia(nombre);
            if (!string.Equals(grafia, nombre, StringComparison.Ordinal)) trozos[i] = trozos[i].Replace(nombre, grafia);
        }
        return string.Concat(trozos);
    }

    /// <summary>
    /// Lo que puede ser el nombre de alguien. Un crédito que solo dice la versión no lo es: visto en
    /// una biblioteca real, varias canciones tenían «Remix» como artista, y eso hacía pasar «REMIX»
    /// por un artista conocido.
    /// </summary>
    private static bool Util(string clave)
        => clave.Replace(" ", "").Length >= 2 && !NoSonArtistas.Contains(clave) && Descriptors.CleanTitle(clave).Length > 0;

    /// <summary>En cuántas canciones de la biblioteca figura como artista.</summary>
    public int Canciones(string artista) => _canciones.GetValueOrDefault(Clave(artista));

    /// <summary>Figura como artista en al menos <see cref="MinimoCanciones"/> canciones.</summary>
    public bool Conoce(string artista) => Canciones(artista) >= MinimoCanciones;

    // Lo que separa a dos artistas en un crédito. La «x» solo cuando va suelta entre dos nombres, para
    // no partir «Lil Nas X» (la misma condición que Descriptors.CleanKeywords).
    private const string Separador = @"\s*(?:[;,&/+]|\b(?:feat|ft|featuring|vs)\b\.?|(?<=\S\s)x(?=\s\S))\s*";

    private static readonly Regex SeparadorRe = new(Separador, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Los nombres de un crédito: «Ozuna, Daddy Yankee Ft Quevedo (Bayne Mashup)» → Ozuna, Daddy Yankee, Quevedo.</summary>
    public static IReadOnlyList<string> Partir(string? credito)
    {
        if (string.IsNullOrWhiteSpace(credito)) return Array.Empty<string>();
        var sinVersion = Regex.Replace(credito, @"\([^)]*\)|\[[^\]]*\]", " ");
        return SeparadorRe.Split(sinVersion).Select(p => p.Trim().Trim('.', '-').Trim()).Where(p => p.Length > 0).ToList();
    }

    /// <summary>Minúsculas, sin acentos ni signos: «Beyoncé» y «BEYONCE» son el mismo artista.</summary>
    public static string Clave(string? nombre) => VocabularioBiblioteca.Clave(nombre);
}
