using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etiquetador.Core.Dj;

namespace Etiquetador.Core.Providers;

/// <summary>Qué tipo de lanzamiento es, según Deezer («record_type»).</summary>
public enum TipoLanzamiento { Album, Ep, Single, Recopilatorio }

/// <summary>Un artista de Deezer, tal como sale en su búsqueda.</summary>
/// <param name="Lanzamientos">Cuántos lanzamientos tiene («nb_album»); 0 si no se sabe.</param>
public sealed record ArtistaDeezer(long Id, string Nombre, int Fans, int Lanzamientos)
{
    public string Enlace => NovedadesDeezer.EnlaceArtista(Id);
}

/// <summary>Un lanzamiento (álbum, EP, single…) de un artista de Deezer.</summary>
public sealed record LanzamientoDeezer(long Id, string Titulo, DateTime Fecha, TipoLanzamiento Tipo, string Enlace);

/// <summary>Una canción de un lanzamiento reciente de alguno de los artistas de la biblioteca.</summary>
/// <param name="Artista">El artista principal de la canción, según Deezer.</param>
/// <param name="Titulo">El título completo, con la versión («Remix»).</param>
/// <param name="Duracion">En segundos.</param>
/// <param name="Isrc">El código ISRC de la grabación, si Deezer lo da.</param>
public sealed record PistaNovedad(string Artista, string Titulo, int Duracion, string Enlace, string Isrc,
                                  LanzamientoDeezer Lanzamiento, int Disco, int Posicion)
{
    /// <summary>Los artistas de la biblioteca por los que ha salido («Bad Bunny», o «Bad Bunny, Feid»).</summary>
    public string Por { get; init; } = "";

    /// <summary>Ha salido solo por artistas que no se pudieron confirmar en Deezer (puede ser de un homónimo).</summary>
    public bool DeArtistaDudoso { get; init; }

    /// <summary>
    /// Cuándo se publicó por primera vez, si fue ANTES del plazo que se está mirando: la canción ya había
    /// salido (en un single anterior, en otro disco) y este lanzamiento la recoge de nuevo. Null si es un
    /// estreno o no se sabe.
    /// </summary>
    public DateTime? PublicadaAntes { get; init; }

    /// <summary>Dónde se publicó antes («Now Or Never»), si <see cref="PublicadaAntes"/>.</summary>
    public string PublicadaEn { get; init; } = "";

    /// <summary>El lanzamiento se presenta como remasterización, reedición o aniversario.</summary>
    public bool EsReedicion => NovedadesDeezer.Reedicion.IsMatch(Titulo) || NovedadesDeezer.Reedicion.IsMatch(Lanzamiento.Titulo);

    /// <summary>Se publica por primera vez en este lanzamiento: ni había salido antes ni es una reedición.</summary>
    public bool EsEstreno => PublicadaAntes == null && !EsReedicion;

    /// <summary>Versión acelerada, ralentizada, karaoke o similar (lo mismo que el catálogo descarta al identificar).</summary>
    public bool EsVersionAlterada => NovedadesDeezer.VersionAlterada.IsMatch(Titulo) || NovedadesDeezer.VersionAlterada.IsMatch(Lanzamiento.Titulo);

    /// <summary>
    /// El año de alta de la grabación según su ISRC (0 si no se sabe). Solo sirve de pista para preguntar
    /// a Deezer si ya se publicó antes (ver <see cref="NovedadesDeezer.MerecePreguntarPorIsrc"/>).
    /// </summary>
    public int AnioIsrc => NovedadesDeezer.AnioDeIsrc(Isrc, Lanzamiento.Fecha.Year);

    /// <summary>La canción como fila de una lista, para cruzarla con la biblioteca.</summary>
    public ChartTrack ComoFila(int posicion) => new(posicion, Artista, Titulo, Duracion, Enlace);
}

/// <summary>Cómo ha ido la identificación de un artista en Deezer.</summary>
/// <param name="Artista">El elegido, o null si no hay ninguno con ese nombre.</param>
/// <param name="Coincidencias">Canciones de la biblioteca que casan con su catálogo.</param>
/// <param name="Candidatos">Cuántos artistas de Deezer se llaman así.</param>
/// <param name="Lanzamientos">El catálogo del elegido, si ya se ha leído al comprobarlo (para no pedirlo dos veces).</param>
public sealed record ArtistaIdentificado(ArtistaDeezer? Artista, EstadoArtistaDeezer Estado, int Coincidencias, int Candidatos,
                                         IReadOnlyList<LanzamientoDeezer>? Lanzamientos = null);

/// <summary>Lo que se sabe de un artista tras buscar sus novedades.</summary>
/// <param name="Deezer">Su artista en Deezer (lo guardado), o null si no se llegó a identificar.</param>
/// <param name="Recientes">Lanzamientos suyos dentro del plazo.</param>
/// <param name="FalloDeRed">Deezer no respondió a alguna petición: lo que se dice de él puede estar incompleto.</param>
public sealed record ConsultaArtista(ArtistaBiblioteca Artista, ArtistaDeezerGuardado? Deezer, int Recientes, bool FalloDeRed);

/// <summary>Por dónde va la búsqueda de novedades.</summary>
/// <param name="Fase">1 = consultando artistas; 2 = leyendo las canciones de los lanzamientos; 3 = comprobando si ya se habían publicado.</param>
public sealed record ProgresoNovedades(int Fase, int Hechos, int Total, string Que);

/// <summary>Lo que ha dado la búsqueda de novedades.</summary>
/// <param name="Pistas">Las canciones, sin repetir y de la más reciente a la más antigua.</param>
/// <param name="Artistas">Cada artista consultado que llegó a terminarse, en el orden en que se pidieron.</param>
/// <param name="Lanzamientos">Lanzamientos recientes distintos encontrados.</param>
/// <param name="LanzamientosSinLeer">Lanzamientos cuyas canciones no se pudieron leer (Deezer no respondió).</param>
/// <param name="Cancelada">Se canceló a medias: es lo encontrado hasta ese momento.</param>
public sealed record ResultadoNovedades(IReadOnlyList<PistaNovedad> Pistas, IReadOnlyList<ConsultaArtista> Artistas,
                                        int Lanzamientos, int LanzamientosSinLeer, bool Cancelada, TimeSpan Duracion);

/// <summary>
/// Novedades de los artistas de la biblioteca en Deezer: quién es cada uno en Deezer, qué ha publicado
/// en los últimos días y qué canciones trae cada lanzamiento.
///
/// Se usa Deezer porque su API es pública y sin clave, y deja leer el catálogo de cualquier artista. La
/// de Spotify con credenciales de aplicación ya no da el contenido de las listas (ver
/// <see cref="PlaylistProvider"/>), y no se depende de ella.
///
/// LÍMITE CONOCIDO: «/artist/{id}/albums» solo trae los lanzamientos en los que el artista figura como
/// principal. Una colaboración en la que es invitado («feat.») en el single de otro no aparece.
///
/// MEDIDO antes de construirlo (octubre de 2026):
///   · La búsqueda de artista devuelve homónimos, y no siempre el bueno primero: «bad bunny» da primero un
///     «Bad Bunny» con 26 seguidores y 3 lanzamientos, y el de verdad (8 millones) segundo. Por eso cada
///     candidato se valida contra las canciones de la biblioteca (<see cref="Elegir"/>).
///   · «/artist/{id}/albums» NO viene ordenado por fecha: primero los álbumes, luego los EP y luego los
///     singles, y dentro de cada grupo casi, pero no del todo, por fecha (un álbum de 2023 delante de uno
///     de 2018 y de uno de 2021). Hay que leerlo entero. Admite «limit=500»: los 397 lanzamientos de un
///     artista muy prolífico llegan en una sola petición.
///   · La caché de red dura 30 días: el catálogo se pide siempre sin caché, o nunca saldría nada nuevo.
/// </summary>
public sealed class NovedadesDeezer
{
    /// <summary>Lanzamientos por página: los de casi cualquier artista caben en una.</summary>
    public const int LanzamientosPorPagina = 500;

    /// <summary>Páginas de lanzamientos como máximo por artista (2.000 lanzamientos).</summary>
    public const int MaximoPaginas = 4;

    /// <summary>Homónimos que se comprueban como mucho, los de más seguidores.</summary>
    public const int MaximoCandidatos = 3;

    /// <summary>
    /// Canciones en común con la biblioteca para dar a un artista de Deezer por confirmado. Con una sola
    /// no basta: MEDIDO con los 200 artistas de una biblioteca real, «Manu» casó por UNA canción con un
    /// homónimo brasileño que en 90 días había publicado 28 canciones de otro estilo. Los demás con una
    /// sola coincidencia eran buenos, pero con un título común el azar basta para casar.
    /// </summary>
    public const int MinimoCoincidencias = 2;

    /// <summary>Artistas que se consultan a la vez. El ritmo lo pone el limitador de Deezer de ApiClient.</summary>
    public const int Simultaneos = 2;

    /// <summary>
    /// Cuánto vale la lista de canciones de un lanzamiento guardada en la caché. Un lanzamiento reciente
    /// puede ir sumando canciones los primeros días (los álbumes que se publican por partes): una semana
    /// es un compromiso entre verlas y no pedir cada vez lo mismo.
    /// </summary>
    public static readonly TimeSpan VigenciaPistas = TimeSpan.FromDays(7);

    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Reediciones que se presentan como tales en el título.</summary>
    internal static readonly Regex Reedicion = new(@"\b(remaster(?:ed)?|remasterizad[oa]|anniversary|aniversario|reissue|reedici[oó]n)\b", IC);

    /// <summary>
    /// Versiones que no son la canción para pinchar: aceleradas, ralentizadas, karaoke, homenajes. Las
    /// mismas palabras con que el catálogo las descarta al identificar (<see cref="DeezerProvider.BadRe"/>),
    /// sin las de reedición, que van aparte.
    /// </summary>
    internal static readonly Regex VersionAlterada = new(@"\b(sped\s*up|speed\s*up|slowed|nightcore|reverb|8d\s*audio|karaoke|tribute|made\s+famous|cover\s+version)\b", IC);

    private readonly ApiClient _api;

    public NovedadesDeezer(ApiClient api) => _api = api;

    public static string EnlaceArtista(long id) => $"https://www.deezer.com/artist/{id}";

    // =============================================================================================
    // Lectura de las respuestas (pura: se prueba con JSON de ejemplo)
    // =============================================================================================

    /// <summary>Los artistas de una respuesta de «/search/artist».</summary>
    public static List<ArtistaDeezer> LeerArtistas(JsonNode? respuesta)
    {
        var lista = new List<ArtistaDeezer>();
        foreach (var x in J.A(J.P(respuesta, "data")) ?? new JsonArray())
        {
            var id = J.L(J.P(x, "id"));
            var nombre = J.S(J.P(x, "name")).Trim();
            if (id <= 0 || nombre.Length == 0) continue;
            lista.Add(new ArtistaDeezer(id, nombre, J.I(J.P(x, "nb_fan")), J.I(J.P(x, "nb_album"))));
        }
        return lista;
    }

    /// <summary>Un artista de la respuesta de «/artist/{id}», o null si Deezer dice que no existe.</summary>
    public static ArtistaDeezer? LeerArtista(JsonNode? respuesta)
    {
        if (respuesta == null || J.P(respuesta, "error") != null) return null;
        var id = J.L(J.P(respuesta, "id"));
        var nombre = J.S(J.P(respuesta, "name")).Trim();
        return id > 0 && nombre.Length > 0 ? new ArtistaDeezer(id, nombre, J.I(J.P(respuesta, "nb_fan")), J.I(J.P(respuesta, "nb_album"))) : null;
    }

    /// <summary>Los lanzamientos de una página de «/artist/{id}/albums». Los que no traen fecha legible no sirven y se saltan.</summary>
    public static List<LanzamientoDeezer> LeerLanzamientos(JsonNode? respuesta)
    {
        var lista = new List<LanzamientoDeezer>();
        foreach (var x in J.A(J.P(respuesta, "data")) ?? new JsonArray())
        {
            var id = J.L(J.P(x, "id"));
            var titulo = J.S(J.P(x, "title")).Trim();
            if (id <= 0 || titulo.Length == 0 || LeerFecha(J.S(J.P(x, "release_date"))) is not { } fecha) continue;
            var enlace = J.S(J.P(x, "link"));
            lista.Add(new LanzamientoDeezer(id, titulo, fecha, Tipo(J.S(J.P(x, "record_type"))),
                                            enlace.Length > 0 ? enlace : $"https://www.deezer.com/album/{id}"));
        }
        return lista;
    }

    private static DateTime? LeerFecha(string texto)
        => DateTime.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;

    /// <summary>«album», «ep», «single», «compile». Lo desconocido cuenta como álbum.</summary>
    public static TipoLanzamiento Tipo(string? recordType) => (recordType ?? "").Trim().ToLowerInvariant() switch
    {
        "single" => TipoLanzamiento.Single,
        "ep" => TipoLanzamiento.Ep,
        "compile" or "compilation" => TipoLanzamiento.Recopilatorio,
        _ => TipoLanzamiento.Album,
    };

    /// <summary>Las canciones de una página de «/album/{id}/tracks».</summary>
    public static List<PistaNovedad> LeerPistas(JsonNode? respuesta, LanzamientoDeezer lanzamiento)
    {
        var lista = new List<PistaNovedad>();
        foreach (var x in J.A(J.P(respuesta, "data")) ?? new JsonArray())
        {
            var titulo = J.S(J.P(x, "title")).Trim();
            if (titulo.Length == 0) continue;
            lista.Add(new PistaNovedad(J.S(J.P(x, "artist", "name")).Trim(), titulo, J.I(J.P(x, "duration")),
                                       J.S(J.P(x, "link")), J.S(J.P(x, "isrc")).Trim().ToUpperInvariant(), lanzamiento,
                                       Math.Max(1, J.I(J.P(x, "disk_number"))), J.I(J.P(x, "track_position"))));
        }
        return lista;
    }

    /// <summary>Los títulos de una respuesta con canciones («/artist/{id}/top»), sin versión.</summary>
    public static List<string> LeerTitulos(JsonNode? respuesta)
    {
        var lista = new List<string>();
        foreach (var x in J.A(J.P(respuesta, "data")) ?? new JsonArray())
        {
            var t = J.S(J.P(x, "title_short"));
            if (t.Length == 0) t = J.S(J.P(x, "title"));
            if (t.Length > 0) lista.Add(t);
        }
        return lista;
    }

    /// <summary>El lanzamiento de la ficha de una canción («/track/isrc:…»): su fecha y su título. Null si no hay.</summary>
    public static (DateTime Fecha, string Lanzamiento)? LeerPublicacion(JsonNode? respuesta)
    {
        if (respuesta == null || J.P(respuesta, "error") != null) return null;
        var fecha = LeerFecha(J.S(J.P(respuesta, "album", "release_date"))) ?? LeerFecha(J.S(J.P(respuesta, "release_date")));
        return fecha is { } f ? (f, J.S(J.P(respuesta, "album", "title")).Trim()) : null;
    }

    /// <summary>
    /// El número de artista de un enlace de Deezer («deezer.com/es/artist/10583405», con o sin
    /// «https://www.» y con lo que lleve detrás) o el número tal cual. Null si no es ninguna de las dos cosas.
    /// </summary>
    public static long? IdDeEnlace(string? texto)
    {
        var s = (texto ?? "").Trim();
        if (s.Length == 0) return null;
        if (Regex.IsMatch(s, @"^\d{1,15}$") && long.TryParse(s, out var solo) && solo > 0) return solo;
        var m = Regex.Match(s, @"(?:https?://)?(?:www\.)?deezer\.com/(?:[a-z]{2}(?:-[a-z]{2})?/)?artist/(\d{1,15})", IC);
        return m.Success && long.TryParse(m.Groups[1].Value, out var id) && id > 0 ? id : null;
    }

    // =============================================================================================
    // Elegir entre homónimos (pura)
    // =============================================================================================

    /// <summary>
    /// Los nombres con que se busca al artista en Deezer: el suyo y sus alias (<see cref="ArtistAliases"/>).
    /// Si la biblioteca une «Cruzzi» con «Cruz Cafuné», en Deezer puede figurar con cualquiera de los dos.
    /// </summary>
    public static IReadOnlyList<string> NombresDeBusqueda(ArtistaBiblioteca artista, ArtistAliases? alias = null)
    {
        alias ??= ArtistAliases.Current;
        var nombres = new List<string> { artista.Nombre };
        foreach (var (otro, canonico) in alias.Pairs())
        {
            if (IndiceArtistas.Clave(canonico) != artista.Clave) continue;
            foreach (var n in new[] { canonico, otro })
                if (!nombres.Contains(n, StringComparer.OrdinalIgnoreCase)) nombres.Add(n);
        }
        return nombres;
    }

    /// <summary>
    /// Los resultados de la búsqueda que se llaman como el artista de la biblioteca (sin distinguir
    /// mayúsculas, acentos ni signos, y con los alias), de más a menos seguidores, como mucho
    /// <see cref="MaximoCandidatos"/>. «Bad Bunny &amp; Arcangel» o «BAD BUNNY BAND» no son él.
    /// </summary>
    public static List<ArtistaDeezer> CandidatosMismoNombre(ArtistaBiblioteca artista, IEnumerable<ArtistaDeezer> resultados,
                                                            ArtistAliases? alias = null)
    {
        alias ??= ArtistAliases.Current;
        var nombres = NombresDeBusqueda(artista, alias).Select(TextUtils.NkClave).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
        return resultados
            .Where(r =>
            {
                var otro = TextUtils.NkClave(r.Nombre);
                if (otro.Length > 0 && nombres.Contains(otro)) return true;
                if (IndiceArtistas.Clave(r.Nombre) == artista.Clave) return true;
                var canonico = alias.Canonical(TextUtils.Nk(TextUtils.ToAscii(r.Nombre)));
                return canonico != null && IndiceArtistas.Clave(canonico) == artista.Clave;
            })
            .GroupBy(r => r.Id).Select(g => g.First())
            .OrderByDescending(r => r.Fans)
            .ThenBy(r => r.Id)
            .Take(MaximoCandidatos)
            .ToList();
    }

    /// <summary>Cuántos títulos distintos del catálogo están entre los de la biblioteca de ese artista.</summary>
    public static int Coincidencias(IEnumerable<string> titulosCatalogo, IReadOnlySet<string> titulosBiblioteca)
    {
        var vistos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in titulosCatalogo)
        {
            var k = ArtistasDeLaBiblioteca.ClaveTitulo(t);
            if (k.Length > 0 && titulosBiblioteca.Contains(k)) vistos.Add(k);
        }
        return vistos.Count;
    }

    /// <summary>
    /// El artista de Deezer que corresponde al de la biblioteca: el candidato con más canciones en común
    /// con ella (a igualdad, el de más seguidores). Queda confirmado con <see cref="MinimoCoincidencias"/>
    /// o más; si no, se toma igualmente como <see cref="EstadoArtistaDeezer.Dudoso"/>: el famoso suele ser
    /// el bueno, pero no se puede asegurar.
    /// </summary>
    public static ArtistaIdentificado Elegir(IReadOnlyList<(ArtistaDeezer Candidato, int Coincidencias, IReadOnlyList<LanzamientoDeezer>? Lanzamientos)> comprobados)
    {
        if (comprobados.Count == 0) return new ArtistaIdentificado(null, EstadoArtistaDeezer.NoEncontrado, 0, 0);
        var mejor = comprobados.OrderByDescending(c => c.Coincidencias)
                               .ThenByDescending(c => c.Candidato.Fans)
                               .ThenBy(c => c.Candidato.Id)
                               .First();
        return new ArtistaIdentificado(mejor.Candidato,
            mejor.Coincidencias >= MinimoCoincidencias ? EstadoArtistaDeezer.Confirmado : EstadoArtistaDeezer.Dudoso,
            mejor.Coincidencias, comprobados.Count, mejor.Lanzamientos);
    }

    // =============================================================================================
    // Qué es reciente y qué es un estreno (pura)
    // =============================================================================================

    /// <summary>El primer día del plazo: hace <paramref name="dias"/> días.</summary>
    public static DateTime Desde(DateTime hoy, int dias) => hoy.Date.AddDays(-Math.Max(0, dias));

    /// <summary>
    /// Los lanzamientos publicados en los últimos <paramref name="dias"/> días, hoy incluido. Los que
    /// llevan fecha futura (preestrenos anunciados) no cuentan todavía: no se pueden conseguir.
    /// </summary>
    public static List<LanzamientoDeezer> Recientes(IEnumerable<LanzamientoDeezer> lanzamientos, DateTime hoy, int dias)
    {
        var desde = Desde(hoy, dias);
        return lanzamientos.Where(l => l.Fecha.Date >= desde && l.Fecha.Date <= hoy.Date).ToList();
    }

    /// <summary>
    /// Si la canción ya había salido en un lanzamiento ANTERIOR AL PLAZO del mismo artista (un single con
    /// su título, o un disco que se llama como ella), cuándo y dónde. Es lo más habitual: el álbum recoge
    /// los singles de los meses anteriores. Se compara el título sin versión, como se cruza con la
    /// biblioteca. Null si no hay ninguno.
    /// </summary>
    public static (DateTime Fecha, string Lanzamiento)? PublicadaEnElCatalogo(PistaNovedad pista,
        IEnumerable<LanzamientoDeezer> catalogo, DateTime desde)
    {
        var clave = ArtistasDeLaBiblioteca.ClaveTitulo(pista.Titulo);
        if (clave.Length == 0) return null;
        (DateTime Fecha, string Lanzamiento)? primera = null;
        foreach (var l in catalogo)
        {
            if (l.Fecha.Date >= desde || l.Id == pista.Lanzamiento.Id) continue;
            if (ArtistasDeLaBiblioteca.ClaveTitulo(l.Titulo) != clave) continue;
            if (primera is not { } p || l.Fecha < p.Fecha) primera = (l.Fecha, l.Titulo);
        }
        return primera;
    }

    /// <summary>
    /// El año de alta de la grabación que lleva su ISRC («USUM71703861» → 2017), o 0 si no se puede
    /// leer. El año va en las cifras 6-7. Un año de dos cifras más allá del año del lanzamiento se lee
    /// como del siglo pasado («QMFMF9800001» → 1998).
    /// </summary>
    public static int AnioDeIsrc(string? isrc, int anioLanzamiento)
    {
        var s = (isrc ?? "").Replace("-", "").Trim();
        if (s.Length != 12 || !char.IsDigit(s[5]) || !char.IsDigit(s[6])) return 0;
        var yy = (s[5] - '0') * 10 + (s[6] - '0');
        var siglo = anioLanzamiento > 0 ? anioLanzamiento / 100 * 100 : 2000;
        var anio = siglo + yy;
        if (anioLanzamiento > 0 && anio > anioLanzamiento + 1) anio -= 100;
        return anio;
    }

    /// <summary>
    /// ¿Merece la pena preguntar a Deezer si la grabación ya se publicó antes? Sí cuando su ISRC es de dos
    /// años o más antes del lanzamiento.
    ///
    /// El año del ISRC NO basta por sí solo para decir que es antigua. MEDIDO en 932 canciones de los
    /// últimos 90 días de 200 artistas: 149 tenían un ISRC de dos o más años antes; de una muestra de 38,
    /// solo 15 se habían publicado de verdad antes. Las demás eran estrenos con códigos de distribuidora
    /// que no siguen el año («QZDYA18…» en canciones de 2026, incluso «…88…» y «…86…»). Por eso solo se usa
    /// para decidir a qué canciones preguntar, y lo que se enseña es lo que contesta Deezer.
    /// </summary>
    public static bool MerecePreguntarPorIsrc(PistaNovedad p)
        => p.PublicadaAntes == null && p.AnioIsrc > 0 && p.AnioIsrc <= p.Lanzamiento.Fecha.Year - 2;

    /// <summary>
    /// Une las canciones de todos los lanzamientos: la misma canción (mismo artista y mismo título,
    /// versión incluida) puede salir en el single y después en el álbum. Se queda la primera publicada, y
    /// los artistas de la biblioteca por los que ha salido se suman. Devuelve de la más reciente a la más
    /// antigua.
    /// </summary>
    public static List<PistaNovedad> Unir(IEnumerable<PistaNovedad> pistas)
    {
        var porClave = new Dictionary<string, PistaNovedad>(StringComparer.Ordinal);
        var orden = new List<string>();
        foreach (var p in pistas)
        {
            var clave = TextUtils.NkClave(p.Artista) + "|" + TextUtils.NkClave(p.Titulo);
            if (clave.Length <= 1) clave = "#" + p.Enlace;
            if (!porClave.TryGetValue(clave, out var antes)) { porClave[clave] = p; orden.Add(clave); continue; }

            var primera = p.Lanzamiento.Fecha < antes.Lanzamiento.Fecha
                       || (p.Lanzamiento.Fecha == antes.Lanzamiento.Fecha && p.Lanzamiento.Id < antes.Lanzamiento.Id) ? p : antes;
            var otra = ReferenceEquals(primera, p) ? antes : p;
            porClave[clave] = primera with
            {
                Por = UnirNombres(antes.Por, p.Por),
                // Basta con que la traiga un artista confirmado para que no sea dudosa.
                DeArtistaDudoso = antes.DeArtistaDudoso && p.DeArtistaDudoso,
                PublicadaAntes = primera.PublicadaAntes ?? otra.PublicadaAntes,
                PublicadaEn = primera.PublicadaAntes != null ? primera.PublicadaEn : otra.PublicadaEn,
            };
        }
        return Ordenar(orden.Select(k => porClave[k]));
    }

    /// <summary>De la más reciente a la más antigua; dentro de un lanzamiento, en su orden.</summary>
    public static List<PistaNovedad> Ordenar(IEnumerable<PistaNovedad> pistas)
        => pistas.OrderByDescending(p => p.Lanzamiento.Fecha)
                 .ThenBy(p => p.Por, StringComparer.CurrentCultureIgnoreCase)
                 .ThenBy(p => p.Lanzamiento.Titulo, StringComparer.CurrentCultureIgnoreCase)
                 .ThenBy(p => p.Lanzamiento.Id)
                 .ThenBy(p => p.Disco)
                 .ThenBy(p => p.Posicion)
                 .ToList();

    private static string UnirNombres(string a, string b)
    {
        var nombres = a.Split(", ", StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var n in b.Split(", ", StringSplitOptions.RemoveEmptyEntries))
            if (!nombres.Contains(n, StringComparer.OrdinalIgnoreCase)) nombres.Add(n);
        return string.Join(", ", nombres);
    }

    // =============================================================================================
    // Red
    // =============================================================================================

    /// <summary>
    /// Los artistas de Deezer con ese nombre (la búsqueda se guarda en la caché). Null si Deezer no
    /// respondió; una lista vacía si respondió y no hay nadie.
    /// </summary>
    /// <param name="sinCache">Preguntar aunque esté en la caché («Volver a identificar»).</param>
    public async Task<List<ArtistaDeezer>?> BuscarArtistasAsync(string nombre, CancellationToken ct, bool sinCache = false)
    {
        var r = await _api.GetRespuestaAsync($"https://api.deezer.com/search/artist?q={TextUtils.UrlEnc(nombre)}&limit=10",
                                             ct: ct, useCache: !sinCache).ConfigureAwait(false);
        return r.FalloDeRed ? null : LeerArtistas(r.Json());
    }

    /// <summary>Un artista por su número (para la corrección a mano). Artista null si no existe.</summary>
    public async Task<(ArtistaDeezer? Artista, bool FalloDeRed)> ArtistaAsync(long id, CancellationToken ct)
    {
        var r = await _api.GetRespuestaAsync($"https://api.deezer.com/artist/{id}", ct: ct).ConfigureAwait(false);
        return (r.FalloDeRed ? null : LeerArtista(r.Json()), r.FalloDeRed);
    }

    /// <summary>
    /// Todos los lanzamientos del artista, SIN caché (la de red dura 30 días y nunca saldría nada nuevo).
    /// Null si Deezer no respondió a la primera página; si falla una de las siguientes, lo leído hasta ahí.
    /// </summary>
    public async Task<List<LanzamientoDeezer>?> LanzamientosAsync(long artistaId, CancellationToken ct)
    {
        var lista = new List<LanzamientoDeezer>();
        string? url = $"https://api.deezer.com/artist/{artistaId}/albums?limit={LanzamientosPorPagina}";
        for (var pagina = 1; url != null && pagina <= MaximoPaginas; pagina++)
        {
            var r = await _api.GetRespuestaAsync(url, ct: ct, useCache: false).ConfigureAwait(false);
            if (r.FalloDeRed) return pagina == 1 ? null : lista;
            var json = r.Json();
            lista.AddRange(LeerLanzamientos(json));
            var siguiente = J.S(J.P(json, "next"));
            url = siguiente.Length > 0 ? siguiente : null;
        }
        return lista;
    }

    /// <summary>Los títulos de las 50 canciones más escuchadas del artista (de la caché, si están). Null si Deezer no respondió.</summary>
    public async Task<List<string>?> TitulosPopularesAsync(long artistaId, CancellationToken ct)
    {
        var r = await _api.GetRespuestaAsync($"https://api.deezer.com/artist/{artistaId}/top?limit=50", ct: ct).ConfigureAwait(false);
        return r.FalloDeRed ? null : LeerTitulos(r.Json());
    }

    /// <summary>Las canciones de un lanzamiento (de la caché si tiene menos de <see cref="VigenciaPistas"/>). Null si Deezer no respondió.</summary>
    public async Task<List<PistaNovedad>?> PistasAsync(LanzamientoDeezer lanzamiento, CancellationToken ct)
    {
        var lista = new List<PistaNovedad>();
        string? url = $"https://api.deezer.com/album/{lanzamiento.Id}/tracks?limit=200";
        for (var pagina = 1; url != null && pagina <= 5; pagina++)
        {
            var r = await _api.GetRespuestaAsync(url, ct: ct, maxAge: VigenciaPistas).ConfigureAwait(false);
            if (r.FalloDeRed) return pagina == 1 ? null : lista;
            var json = r.Json();
            lista.AddRange(LeerPistas(json, lanzamiento));
            var siguiente = J.S(J.P(json, "next"));
            url = siguiente.Length > 0 ? siguiente : null;
        }
        return lista;
    }

    /// <summary>
    /// El lanzamiento de esa grabación que da Deezer al buscarla por su ISRC (se guarda en la caché: no
    /// cambia). Null si no lo sabe o no respondió.
    /// </summary>
    public async Task<(DateTime Fecha, string Lanzamiento)?> PublicacionPorIsrcAsync(string isrc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(isrc)) return null;
        var r = await _api.GetRespuestaAsync($"https://api.deezer.com/track/isrc:{TextUtils.UrlEnc(isrc.Trim())}", ct: ct).ConfigureAwait(false);
        return r.FalloDeRed ? null : LeerPublicacion(r.Json());
    }

    /// <summary>
    /// Identifica al artista en Deezer: busca por su nombre (y sus alias), se queda con los que se llaman
    /// igual y comprueba cuál tiene canciones de la biblioteca. Primero con su catálogo de lanzamientos
    /// (los títulos de los singles son los de sus canciones), que de todos modos hay que leer para ver las
    /// novedades; si ahí no casan bastantes, también con sus 50 canciones más escuchadas, que incluyen las
    /// de los álbumes y las colaboraciones. Null si Deezer no respondió (no se sabe nada: no se guarda).
    /// </summary>
    public async Task<ArtistaIdentificado?> IdentificarAsync(ArtistaBiblioteca artista, CancellationToken ct, bool sinCache = false)
    {
        var resultados = new List<ArtistaDeezer>();
        foreach (var nombre in NombresDeBusqueda(artista))
        {
            var r = await BuscarArtistasAsync(nombre, ct, sinCache).ConfigureAwait(false);
            if (r == null) return null;
            resultados.AddRange(r);
        }
        var candidatos = CandidatosMismoNombre(artista, resultados);
        if (candidatos.Count == 0) return new ArtistaIdentificado(null, EstadoArtistaDeezer.NoEncontrado, 0, 0);

        var comprobados = new List<(ArtistaDeezer, int, IReadOnlyList<LanzamientoDeezer>?)>();
        foreach (var c in candidatos)
        {
            var lanzamientos = await LanzamientosAsync(c.Id, ct).ConfigureAwait(false);
            if (lanzamientos == null) return null;
            IEnumerable<string> titulos = lanzamientos.Select(l => l.Titulo);
            if (Coincidencias(titulos, artista.Titulos) < MinimoCoincidencias)
            {
                var populares = await TitulosPopularesAsync(c.Id, ct).ConfigureAwait(false);
                if (populares == null) return null;
                titulos = titulos.Concat(populares);
            }
            comprobados.Add((c, Coincidencias(titulos, artista.Titulos), lanzamientos));
        }
        return Elegir(comprobados);
    }

    /// <summary>
    /// Busca las novedades de esos artistas: identifica en Deezer a los que no lo estén (y guarda lo
    /// averiguado en <paramref name="almacen"/>), lee sus lanzamientos y, de los publicados en los
    /// últimos <paramref name="dias"/> días, sus canciones. Después mira cuáles ya se habían publicado
    /// antes del plazo (<see cref="PistaNovedad.PublicadaAntes"/>).
    ///
    /// Cancelar no tira lo averiguado de cada artista, que queda guardado; si se cancela mientras se leen
    /// las canciones, se devuelven las leídas hasta ese momento con <see cref="ResultadoNovedades.Cancelada"/>.
    /// Un artista al que Deezer no respondió queda marcado (<see cref="ConsultaArtista.FalloDeRed"/>) y no
    /// se guarda nada de él.
    /// </summary>
    /// <param name="alGuardar">Recibe el resultado de cada guardado del almacén ("" si fue bien).</param>
    /// <param name="guardarCada">Cada cuántos artistas identificados se guarda el almacén (0 = solo al final).</param>
    public async Task<ResultadoNovedades> BuscarAsync(IReadOnlyList<ArtistaBiblioteca> artistas, AlmacenArtistasDeezer almacen,
        int dias, DateTime hoy, IProgress<ProgresoNovedades>? progreso, CancellationToken ct, Action<string>? alGuardar = null,
        int guardarCada = 20)
    {
        var reloj = Stopwatch.StartNew();
        var desde = Desde(hoy, dias);
        var consultas = new ConsultaArtista?[artistas.Count];
        var recientesDe = new ConcurrentDictionary<long, (LanzamientoDeezer Lanzamiento, ConcurrentQueue<(string Por, bool Dudoso, long ArtistaId)> Por)>();
        var catalogos = new ConcurrentDictionary<long, IReadOnlyList<LanzamientoDeezer>>();
        var cancelada = false;
        int hechos = 0, identificados = 0;

        // Fase 1: quién es cada uno en Deezer y qué ha publicado.
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, artistas.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Simultaneos, CancellationToken = ct },
                async (i, tok) =>
                {
                    var a = artistas[i];
                    using var medicion = _api.MedirFallos();
                    var guardado = almacen.Buscar(a.Clave);
                    // El catálogo leído al identificarlo (sin caché) sirve si es el del artista que ha quedado guardado.
                    (long Id, IReadOnlyList<LanzamientoDeezer>? Lista) leidos = (0, null);

                    if (guardado is not { Excluido: true } && AlmacenArtistasDeezer.HayQueIdentificar(guardado, hoy))
                    {
                        var id = await IdentificarAsync(a, tok).ConfigureAwait(false);
                        if (id != null)
                        {
                            almacen.Poner(new ArtistaDeezerGuardado
                            {
                                Clave = a.Clave, Nombre = a.Nombre, DeezerId = id.Artista?.Id ?? 0,
                                DeezerNombre = id.Artista?.Nombre ?? "", Fans = id.Artista?.Fans ?? 0,
                                Estado = id.Estado, Coincidencias = id.Coincidencias, Fecha = hoy,
                            });
                            guardado = almacen.Buscar(a.Clave);   // con la exclusión que tuviera
                            leidos = (id.Artista?.Id ?? 0, id.Lanzamientos);
                            if (guardarCada > 0 && Interlocked.Increment(ref identificados) % guardarCada == 0)
                                alGuardar?.Invoke(almacen.Guardar());
                        }
                    }

                    var recientes = 0;
                    if (guardado is { Excluido: false, DeezerId: > 0 } g)
                    {
                        var lanzamientos = leidos.Id == g.DeezerId && leidos.Lista != null
                            ? leidos.Lista
                            : await LanzamientosAsync(g.DeezerId, tok).ConfigureAwait(false);
                        if (lanzamientos != null)
                        {
                            foreach (var l in Recientes(lanzamientos, hoy, dias))
                            {
                                recientes++;
                                recientesDe.GetOrAdd(l.Id, _ => (l, new ConcurrentQueue<(string, bool, long)>()))
                                           .Por.Enqueue((a.Nombre, g.Estado == EstadoArtistaDeezer.Dudoso, g.DeezerId));
                            }
                            if (recientes > 0) catalogos[g.DeezerId] = lanzamientos;
                        }
                    }

                    consultas[i] = new ConsultaArtista(a, guardado, recientes, medicion.HuboFallos);
                    var n = Interlocked.Increment(ref hechos);
                    progreso?.Report(new ProgresoNovedades(1, n, artistas.Count, a.Nombre));
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelada = true; }

        // Fase 2: las canciones de cada lanzamiento reciente, una vez aunque salga por varios artistas.
        var pistas = new ConcurrentBag<PistaNovedad>();
        var lanzamientos = recientesDe.Values.OrderByDescending(v => v.Lanzamiento.Fecha).ThenBy(v => v.Lanzamiento.Id).ToList();
        var sinLeer = 0;
        if (!cancelada)
        {
            var leidos = 0;
            try
            {
                await Parallel.ForEachAsync(lanzamientos,
                    new ParallelOptions { MaxDegreeOfParallelism = Simultaneos, CancellationToken = ct },
                    async (v, tok) =>
                    {
                        var deEste = await PistasAsync(v.Lanzamiento, tok).ConfigureAwait(false);
                        if (deEste == null) Interlocked.Increment(ref sinLeer);
                        var por = v.Por.ToList();
                        var nombres = string.Join(", ", por.Select(p => p.Por).Distinct(StringComparer.OrdinalIgnoreCase));
                        var dudoso = por.All(p => p.Dudoso);
                        var suyos = por.Select(p => p.ArtistaId).Distinct()
                                       .SelectMany(id => catalogos.TryGetValue(id, out var c) ? c : Array.Empty<LanzamientoDeezer>())
                                       .ToList();
                        foreach (var p in deEste ?? new List<PistaNovedad>())
                        {
                            var antes = PublicadaEnElCatalogo(p, suyos, desde);
                            pistas.Add(p with
                            {
                                Por = nombres, DeArtistaDudoso = dudoso,
                                PublicadaAntes = antes?.Fecha, PublicadaEn = antes?.Lanzamiento ?? "",
                            });
                        }
                        var n = Interlocked.Increment(ref leidos);
                        progreso?.Report(new ProgresoNovedades(2, n, lanzamientos.Count, v.Lanzamiento.Titulo));
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelada = true; }
        }

        // Fase 3: las que por su ISRC parecen antiguas, se le pregunta a Deezer si ya se habían publicado.
        var unidas = Unir(pistas);
        if (!cancelada)
        {
            var dudosas = Enumerable.Range(0, unidas.Count).Where(i => MerecePreguntarPorIsrc(unidas[i])).ToList();
            var comprobadas = 0;
            try
            {
                await Parallel.ForEachAsync(dudosas,
                    new ParallelOptions { MaxDegreeOfParallelism = Simultaneos, CancellationToken = ct },
                    async (i, tok) =>
                    {
                        var p = unidas[i];
                        if (await PublicacionPorIsrcAsync(p.Isrc, tok).ConfigureAwait(false) is { } pub && pub.Fecha.Date < desde)
                            unidas[i] = p with { PublicadaAntes = pub.Fecha, PublicadaEn = pub.Lanzamiento };
                        var n = Interlocked.Increment(ref comprobadas);
                        progreso?.Report(new ProgresoNovedades(3, n, dudosas.Count, p.Titulo));
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelada = true; }
        }

        alGuardar?.Invoke(almacen.Guardar());
        return new ResultadoNovedades(unidas, consultas.Where(c => c != null).Select(c => c!).ToList(),
                                      lanzamientos.Count, sinLeer, cancelada, reloj.Elapsed);
    }
}
