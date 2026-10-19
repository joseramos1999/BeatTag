using System.Text.RegularExpressions;
using Etiquetador.Core.Analysis;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Core.Dj;

/// <summary>Qué pieza es un archivo respecto a la canción de la que sale.</summary>
public enum TipoPieza
{
    /// <summary>La canción tal cual se publicó («Original Mix», «Radio Edit», «Clean»…).</summary>
    Original,
    /// <summary>Extended, hype intro y demás versiones con entrada larga para mezclar.</summary>
    Extendida,
    /// <summary>Otro artista la ha rehecho: remix, bootleg, rework, VIP, dub…</summary>
    Remix,
    /// <summary>Solo la base, sin voz.</summary>
    Instrumental,
    /// <summary>Solo la voz.</summary>
    Acapella,
    /// <summary>Otras herramientas de DJ: outros, loops, quick hits, DJ tools…</summary>
    EdicionDj,
    /// <summary>Mashups y transiciones: varias canciones en un mismo archivo.</summary>
    Mezcla,
}

/// <summary>Una pieza de la canción consultada.</summary>
/// <param name="EsLaConsultada">Es el archivo desde el que se ha pedido la consulta.</param>
/// <param name="Cancion">
/// Si la consultada es un mashup, la parte con la que casa esta pieza («Lovumba»); vacío en el resto.
/// </param>
public sealed record PiezaVersion(Track Track, TipoPieza Tipo, bool EsLaConsultada, string Cancion);

/// <summary>
/// Todas las piezas que tienes de una canción: la original, sus intros y extended, remixes,
/// acapellas, instrumentales, ediciones de DJ y los mashups que la usan.
///
/// Duplicados agrupa con otro fin -encontrar copias sobrantes- y por eso puede dejar fuera
/// justamente estas piezas. Aquí lo que se quiere es tenerlas juntas al preparar un tema.
///
/// La canción se reconoce por su TÍTULO sin adornos, con el mismo criterio que
/// <see cref="RevisionBandeja.ClaveCancion"/> (<see cref="Descriptors.CleanTitle"/> normalizado), y el
/// artista sirve para separar canciones distintas que se llaman igual. Pero el artista se compara
/// uno a uno, no el crédito entero: «Karol G» y «Karol G, Nicki Minaj» comparten artista, y con la
/// clave de la bandeja (artista|título con el crédito completo) serían canciones distintas.
///
/// Los mashups se parten por « x » (como <see cref="Matching.PierdeUnaCancion"/>) y cada parte se
/// indexa por su título. Visto en una biblioteca real: «La Reina x Lovumba (Try It Mashup 130Bpm)»,
/// con el artista «Lola Indigo x Daddy Yankee», o «LA RANGER x Cristina (DJ Eros Mashup)» sin ninguna
/// etiqueta. En los mashups el artista no es fiable (a menudo es el usuario que lo subió, «@…»), así
/// que no sirve para descartar; solo para confirmar.
///
/// MEDIDO en una biblioteca de 13.590 canciones (4.192 mashups y transiciones, 903 acapellas): el
/// índice se construye en unos 2 s, fuera de la interfaz y una sola vez mientras la biblioteca no
/// cambie; cada consulta después es inmediata. 8.196 canciones tienen alguna otra pieza (6,5 de media
/// por grupo, 51 la que más, que es un tema muy mezclado), y en una muestra revisada a mano los grupos
/// eran correctos salvo mashups de una canción homónima que no está en la biblioteca.
/// </summary>
public sealed class IndiceVersiones
{
    /// <summary>
    /// Longitud mínima (en letras y números, sin espacios) de un título para aceptar que dos piezas son
    /// la misma canción SIN que coincida ningún artista.
    ///
    /// Por debajo hay demasiados títulos que repiten canciones distintas: «Intro», «Bebé», «Tusa»,
    /// «Hello», «Fuego», «Amor». Con seis letras o más («Gasolina», «Bichota», «Closer») dos canciones
    /// distintas con el mismo título exacto son raras, y aun así, si las dos tienen artista fiable y no
    /// comparten ninguno, se tratan como canciones distintas (ver <see cref="Aceptadas"/>). Un título corto
    /// también agrupa, pero solo con algún artista en común.
    /// </summary>
    public const int MinimoTituloSinArtista = 6;

    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Títulos que no identifican ninguna canción por sí solos, ya normalizados.</summary>
    private static readonly HashSet<string> Genericos = new(StringComparer.Ordinal)
    {
        "untitled", "sintitulo", "unknown", "desconocido", "desconocida", "interlude", "interludio",
        "bonustrack", "intro", "outro", "instrumental", "acapella", "mashup", "remix", "original",
        "extended", "cancion", "song", "track", "pista", "tema", "demo",
    };

    /// <summary>Artistas que no dicen quién canta: se tratan como si no hubiera artista.</summary>
    private static readonly HashSet<string> ArtistasVacios = new(StringComparer.Ordinal)
    {
        "unknown", "unknownartist", "desconocido", "artistadesconocido", "variousartists", "various",
        "varios", "variosartistas", "va", "dj", "djs",
    };

    private sealed record Entrada(Track Track, TipoPieza Tipo, IReadOnlySet<string> Artistas, bool EsMezcla);

    private readonly Dictionary<string, List<Entrada>> _porTitulo = new(StringComparer.Ordinal);

    /// <summary>
    /// Artistas que la biblioteca conoce: los que aparecen en la etiqueta de artista de al menos
    /// <see cref="VecesParaConocerArtista"/> canciones que no son mashups.
    ///
    /// Sirve para no tomar por título la mitad del nombre que es un artista. En los mashups las dos
    /// mitades del nombre pueden ser cualquier cosa («Bad Bunny x Daddy Yankee - Rompe x Diles», o al
    /// revés), y en los nombres invertidos sin etiquetas («El Efecto - Bad Bunny») el «título» que
    /// saca el nombre es el artista. Visto en una biblioteca real: sin esta comprobación, «Bad Bunny»
    /// quedaba indexado como título de cientos de mashups y las versiones de cualquiera de ellos eran
    /// 400 archivos de Bad Bunny.
    /// </summary>
    private readonly HashSet<string> _conocidos = new(StringComparer.Ordinal);

    /// <summary>
    /// Veces que tiene que aparecer un artista para darlo por conocido. Con una sola bastaría una
    /// etiqueta con el título y el artista cambiados de sitio para que ese título dejara de contar.
    /// </summary>
    public const int VecesParaConocerArtista = 2;

    /// <summary>Canciones recorridas.</summary>
    public int Canciones { get; }

    /// <summary>Títulos distintos indexados (las partes de los mashups cuentan por separado).</summary>
    public int Titulos => _porTitulo.Count;

    /// <summary>
    /// Recorre la biblioteca. Es lo caro (cada canción pasa por la lectura del nombre y la limpieza de
    /// títulos, que son expresiones regulares): se llama fuera del hilo de la interfaz y el resultado se
    /// reutiliza mientras la biblioteca no cambie (<see cref="CacheIndiceVersiones"/>).
    /// </summary>
    public IndiceVersiones(IReadOnlyList<Track> canciones)
    {
        Canciones = canciones.Count;

        // Primera pasada: qué es cada archivo y quién lo firma según su etiqueta, para saber qué
        // artistas conoce la biblioteca antes de leer ningún nombre.
        var tipos = new TipoPieza[canciones.Count];
        var deEtiqueta = new IReadOnlySet<string>[canciones.Count];
        var veces = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < canciones.Count; i++)
        {
            tipos[i] = Clasificar(canciones[i].FilePath);
            deEtiqueta[i] = ClavesArtista(canciones[i].Artist);
            if (tipos[i] == TipoPieza.Mezcla) continue;
            foreach (var a in deEtiqueta[i]) veces[a] = veces.GetValueOrDefault(a) + 1;
        }
        foreach (var (a, n) in veces)
            if (n >= VecesParaConocerArtista) _conocidos.Add(a);

        for (var i = 0; i < canciones.Count; i++)
        {
            var t = canciones[i];
            var d = Describir(t, tipos[i], deEtiqueta[i]);
            var e = new Entrada(t, d.Tipo, d.Artistas, d.EsMezcla);
            foreach (var (clave, _) in d.Titulos)
            {
                if (!_porTitulo.TryGetValue(clave, out var l)) _porTitulo[clave] = l = new List<Entrada>();
                l.Add(e);
            }
        }
    }

    /// <summary>
    /// Las piezas de la misma canción que <paramref name="consultada"/>, ella incluida, ordenadas por
    /// tipo (original, extended, remix, instrumental, acapella, ediciones de DJ, mashups) y título. Si
    /// la consultada es un mashup, las de cada una de sus partes.
    /// </summary>
    public IReadOnlyList<PiezaVersion> De(Track consultada)
    {
        var q = Describir(consultada, Clasificar(consultada.FilePath), ClavesArtista(consultada.Artist));
        var piezas = new Dictionary<string, PiezaVersion>(StringComparer.OrdinalIgnoreCase)
        {
            [consultada.FilePath] = new PiezaVersion(consultada, q.Tipo, true, ""),
        };

        foreach (var (clave, texto) in q.Titulos)
        {
            if (!_porTitulo.TryGetValue(clave, out var lista)) continue;
            foreach (var e in Aceptadas(clave, q.Artistas, q.EsMezcla, lista))
                piezas.TryAdd(e.Track.FilePath, new PiezaVersion(e.Track, e.Tipo, false, q.EsMezcla ? texto : ""));
        }

        return piezas.Values
            .OrderBy(p => p.Cancion, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Tipo)
            .ThenByDescending(p => p.EsLaConsultada)
            .ThenBy(p => p.Track.Title ?? p.Track.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Track.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// De las piezas que comparten un título, las que son la misma canción que la consultada.
    ///
    ///   · Si comparten algún artista, sí, sea cual sea el título.
    ///   · Si no, el título tiene que ser distintivo (<see cref="MinimoTituloSinArtista"/>) y además no
    ///     puede haber una contradicción: si las dos tienen artista y ninguno coincide, son dos
    ///     canciones que se llaman igual («Closer» de The Chainsmokers y «Closer» de Ne-Yo).
    ///   · En un mashup el artista no basta para contradecir: suele ser solo el de una de sus partes,
    ///     o quien lo editó («TRY IT», «@…»). Pero sí sirve para elegir: si entre las canciones con ese
    ///     título hay una cuyo artista nombra el mashup, el mashup es de esa y no de las demás.
    ///     Visto en una biblioteca real: «Shakira x Mohombi - Soltera x Bumpy Ride» es la «Soltera» de
    ///     Shakira, y no debe salir entre las versiones de la «Soltera» de Lunay.
    /// </summary>
    private static IEnumerable<Entrada> Aceptadas(string clave, IReadOnlySet<string> artistasQ, bool mezclaQ, List<Entrada> lista)
    {
        var distintivo = TituloDistintivo(clave);
        var normales = lista.Where(e => !e.EsMezcla).ToList();
        // Si la consultada es un mashup, a qué canción de este título apuntan sus artistas (si apuntan a alguna).
        var apuntadas = mezclaQ && normales.Exists(n => n.Artistas.Overlaps(artistasQ));

        foreach (var e in lista)
        {
            if (e.Artistas.Overlaps(artistasQ)) { yield return e; continue; }
            if (!distintivo) continue;

            if (!mezclaQ && !e.EsMezcla)
            {
                if (artistasQ.Count == 0 || e.Artistas.Count == 0) yield return e;
            }
            else if (mezclaQ && !e.EsMezcla)
            {
                if (!apuntadas) yield return e;
            }
            else if (!mezclaQ)
            {
                // Un mashup que nombra al artista de OTRA canción con este título es de esa otra.
                if (!normales.Exists(n => n.Artistas.Overlaps(e.Artistas))) yield return e;
            }
            else yield return e;   // dos mashups con una parte en común
        }
    }

    /// <summary>¿Basta este título (ya normalizado) para reconocer la canción sin el artista?</summary>
    internal static bool TituloDistintivo(string clave)
        => clave.Length >= MinimoTituloSinArtista && !clave.All(char.IsDigit) && !Genericos.Contains(clave)
           && !TextUtils.Rx(@"^(?:track|pista|audio|cancion|song)\d+$").IsMatch(clave);

    // --- Qué es cada archivo ---

    private sealed record Descripcion(TipoPieza Tipo, IReadOnlySet<string> Artistas, bool EsMezcla,
                                      IReadOnlyList<(string Clave, string Texto)> Titulos);

    /// <summary>
    /// ¿El texto es un nombre de artista que la biblioteca conoce (o varios, «Bad Bunny x Daddy
    /// Yankee»), y no un título?
    /// </summary>
    private bool EsArtista(string? texto)
    {
        var claves = ClavesArtista(texto);
        return claves.Count > 0 && claves.All(_conocidos.Contains);
    }

    private Descripcion Describir(Track t, TipoPieza tipo, IReadOnlySet<string> deEtiqueta)
    {
        var esMezcla = tipo == TipoPieza.Mezcla;
        var p = FileNameParser.Parse(t.FileName);

        // El nombre puede venir al revés, «Título - Artista», como lo dejan varios pools. Si la mitad
        // del título del nombre es el artista de la etiqueta (o, sin etiqueta, un artista conocido), el
        // título está en la otra mitad. Sin esto, «Bichota - Karol G» y «Tusa - Karol G» compartirían
        // «título» (Karol G) y artista, y saldrían como versiones la una de la otra.
        var invertido = deEtiqueta.Count > 0
            ? ClavesArtista(p.FnTitle).Overlaps(deEtiqueta) && !ClavesArtista(p.FnArtist).Overlaps(deEtiqueta)
            : p.FnArtist.Length > 0 && EsArtista(p.FnTitle) && !EsArtista(p.FnArtist);
        var tituloNombre = invertido ? p.FnArtist : p.FnTitle;

        // Artistas: los de la etiqueta y los del nombre del archivo. Sumar los dos recupera, por
        // ejemplo, el remix que lleva en la etiqueta solo al remezclador y en el nombre al artista.
        var artistas = new HashSet<string>(deEtiqueta, StringComparer.Ordinal);
        artistas.UnionWith(ClavesArtista(invertido ? p.FnTitle : p.FnArtist));

        var titulos = new List<(string, string)>();
        void Anadir(string? texto)
        {
            var k = ClaveTitulo(texto);
            if (k.Length > 0 && !titulos.Exists(x => x.Item1 == k)) titulos.Add((k, Limpio(texto)));
        }

        if (esMezcla)
        {
            // Las partes pueden estar en la etiqueta de título o en cualquiera de las dos mitades del
            // nombre: los pools usan tanto «Artista - A x B» como «A x B - Artista». Lo que es un
            // artista conocido no se indexa como título, pero sí cuenta como pista de artista.
            foreach (var fuente in new[] { t.Title, p.FnTitle, p.FnArtist })
                foreach (var parte in Partes(fuente))
                {
                    artistas.UnionWith(ClavesArtista(parte));
                    if (!EsArtista(parte)) Anadir(parte);
                }
        }
        else
        {
            // Manda la etiqueta; el nombre del archivo solo si no hay título en ella. Sumar los dos
            // metía el ruido de los nombres mal partidos en canciones que estaban bien etiquetadas.
            Anadir(string.IsNullOrWhiteSpace(t.Title) ? tituloNombre : t.Title);
        }
        return new Descripcion(tipo, artistas, esMezcla, titulos);
    }

    private static readonly Regex Instrumental = new(@"\binstrumental(?:es|s)?\b", IC);

    private static readonly Regex NombreDeMashup = new(@"\b(?:mash\s*-?\s*ups?|mash\s*-?\s*boots?(?:legs?)?)\b", IC);

    /// <summary>
    /// Qué pieza es un archivo, por su nombre y por la carpeta en que está. Manda el nombre: una
    /// acapella guardada dentro de una carpeta de mashups es una acapella.
    /// </summary>
    public static TipoPieza Clasificar(string? ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta)) return TipoPieza.Original;
        var nombre = Path.GetFileNameWithoutExtension(ruta);
        var dir = Path.GetDirectoryName(ruta) ?? "";
        var carpeta = Path.GetFileName(dir);

        if (NombreDeMashup.IsMatch(nombre) || Matching.IsMezclaDeVariosTemas(nombre)) return TipoPieza.Mezcla;
        if (Identificacion.MencionaAcapella(nombre)) return TipoPieza.Acapella;
        if (Instrumental.IsMatch(nombre)) return TipoPieza.Instrumental;
        if (Matching.IsMashupFolder(dir)) return TipoPieza.Mezcla;
        if (Identificacion.EsAcapella(ruta)) return TipoPieza.Acapella;
        if (Instrumental.IsMatch(carpeta)) return TipoPieza.Instrumental;

        // Con la extensión: De la quita por su cuenta, y sin ella cortaría en el primer punto del
        // título («MUCHACHA feat. Fly Project (Hype Intro)» se quedaba en «MUCHACHA feat»).
        switch (PrioridadVersion.De(Path.GetFileName(ruta)))
        {
            case TipoVersion.Remix: return TipoPieza.Remix;
            case TipoVersion.Extendida: return TipoPieza.Extendida;
        }
        return EdicionDj.Es(ruta) ? TipoPieza.EdicionDj : TipoPieza.Original;
    }

    /// <summary>Cómo se llama en pantalla.</summary>
    public static string Nombre(TipoPieza tipo) => tipo switch
    {
        TipoPieza.Original => "Original",
        TipoPieza.Extendida => "Extended / Intro",
        TipoPieza.Remix => "Remix",
        TipoPieza.Instrumental => "Instrumental",
        TipoPieza.Acapella => "Acapella",
        TipoPieza.EdicionDj => "Edición de DJ",
        _ => "Mashup / transición",
    };

    // --- Claves ---

    private static readonly Regex Feat = new(@"\s(?:feat|ft|featuring)\b\.?.*$", IC);
    private static readonly Regex Pool = new(Descriptors.PoolRe);

    /// <summary>
    /// El título sin versión, invitados, BPM ni marcas de pool, normalizado. Parte de
    /// <see cref="Descriptors.CleanTitle"/>, que es lo que usa la bandeja para «otra versión».
    /// </summary>
    internal static string ClaveTitulo(string? titulo)
    {
        if (string.IsNullOrWhiteSpace(titulo)) return "";
        var s = Descriptors.CleanTitle(Pool.Replace(titulo, " "));
        s = Feat.Replace(s, "");
        return TextUtils.NkClave(s);
    }

    /// <summary>El título como se enseña: sin versión, paréntesis ni espacios de más («De Lejitos», no «De Lejitos Remix»).</summary>
    private static string Limpio(string? texto)
    {
        var limpio = Descriptors.CleanTitle(texto);
        return limpio.Length > 0
            ? limpio
            : TextUtils.Rx(@"\s+").Replace(TextUtils.Rx(@"\([^)]*\)?|\[[^\]]*\]?").Replace(texto ?? "", " "), " ").Trim();
    }

    /// <summary>Las partes de un título de mashup: lo que va entre « x » (o « vs »), sin paréntesis.</summary>
    internal static IReadOnlyList<string> Partes(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return Array.Empty<string>();
        // Lo que va entre paréntesis no cuenta: ahí la « x » une a los autores de la versión
        // («Dion Dobbe x Taron Mashup»), no canciones. Mismo criterio que Matching.PierdeUnaCancion.
        var t = TextUtils.Rx(@"\([^)]*\)?|\[[^\]]*\]?").Replace(texto, " ");
        return TextUtils.Rx(@"(?<=[\p{L}\p{N}])\s+(?:x|vs\.?)\s+(?=[\p{L}\p{N}])", IC).Split(t)
                        .Select(x => x.Trim())
                        .Where(x => x.Length > 0)
                        .ToList();
    }

    private static readonly Regex Separadores = new(
        @"\s*(?:[,;&+/]|\s(?:x|vs\.?|feat\.?|featuring|ft\.?|y|and|with)\s)\s*", IC);

    /// <summary>
    /// Cada artista del crédito, normalizado y con el nombre canónico si es un alias registrado. Se
    /// quitan los que no dicen quién canta: el usuario que subió el archivo («@…»), el pool («DJ
    /// City») y los «Varios artistas».
    /// </summary>
    internal static IReadOnlySet<string> ClavesArtista(string? credito)
    {
        var salida = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(credito)) return salida;
        var sinParentesis = TextUtils.Rx(@"\([^)]*\)?|\[[^\]]*\]?").Replace(credito, " ");
        foreach (var bruto in Separadores.Split(sinParentesis))
        {
            var parte = bruto.Trim();
            if (parte.Length == 0 || parte.StartsWith('@') || Pool.IsMatch(parte)) continue;
            var nk = TextUtils.NkClave(Descriptors.CleanKeywords(parte));
            if (nk.Length < 2 || ArtistasVacios.Contains(nk)) continue;
            salida.Add(ArtistAliases.Current.Canonical(nk) ?? nk);
        }
        return salida;
    }

    /// <summary>
    /// Huella de lo que el índice usa de cada canción: ruta, artista y título, en orden. Si no ha
    /// cambiado, el índice anterior sigue valiendo, también tras retocar etiquetas en el Editor sin
    /// reescanear. FNV-1a de 64 bits, como el índice de Tendencias: unos milisegundos con 15.000
    /// canciones, frente a rehacer el índice.
    /// </summary>
    public static ulong Firma(IReadOnlyList<Track> canciones)
    {
        var h = 14695981039346656037UL;
        void Mezclar(string? s)
        {
            foreach (var c in s ?? "") { h ^= c; h *= 1099511628211UL; }
            h ^= 0x1F; h *= 1099511628211UL;   // separador: «ab|c» y «a|bc» no pueden dar lo mismo
        }
        foreach (var t in canciones)
        {
            Mezclar(t.FilePath);
            Mezclar(t.Artist);
            Mezclar(t.Title);
        }
        return h;
    }
}

/// <summary>
/// El índice de versiones, reutilizado mientras la biblioteca no cambie: consultar las versiones de
/// varias canciones seguidas solo paga el índice la primera vez. Quien lo usa lo llama fuera del hilo
/// de la interfaz (Task.Run), con una instantánea de la biblioteca.
/// </summary>
public sealed class CacheIndiceVersiones
{
    private readonly object _cerrojo = new();
    private (ulong Firma, IndiceVersiones Indice)? _actual;

    /// <summary>Cuántas veces se ha construido. Para las pruebas.</summary>
    public int Construcciones { get; private set; }

    public IndiceVersiones Obtener(IReadOnlyList<Track> canciones, out bool reutilizado)
    {
        var firma = IndiceVersiones.Firma(canciones);
        lock (_cerrojo)
        {
            if (_actual is { } a && a.Firma == firma) { reutilizado = true; return a.Indice; }
        }
        var indice = new IndiceVersiones(canciones);
        lock (_cerrojo)
        {
            _actual = (firma, indice);
            Construcciones++;
        }
        reutilizado = false;
        return indice;
    }
}
