using Etiquetador.Core.Ai;

namespace Etiquetador.Core.Dj;

/// <summary>Qué regla propone el género. Las nuevas van al final.</summary>
public enum ReglaGenero
{
    /// <summary>El mapa carpeta → género que rellena el usuario. Es una decisión suya: manda.</summary>
    Carpeta,
    /// <summary>El género dominante del artista entre sus canciones que ya tienen género.</summary>
    Artista,
    /// <summary>El estilo que nombra la propia versión: «(Bachata Version)», «(Moombahton Remix)».</summary>
    Version,
    /// <summary>El género que el usuario ha asignado al artista (generos-por-artista.json).</summary>
    MapaArtista,
    /// <summary>Un subgénero concreto del álbum en Deezer, para lo que las reglas no cubren (ver <see cref="GeneroDeezer"/>).</summary>
    Deezer,
}

/// <summary>A qué canciones se proponen géneros.</summary>
public enum AlcanceGenero
{
    /// <summary>Solo a las que no tienen género (lo de antes).</summary>
    SinGenero,
    /// <summary>También a las que tienen un género genérico («Latin», «Dance», «Mashup»…).</summary>
    SinGeneroYGenericos,
    /// <summary>
    /// También a las que tienen otro estilo concreto: esas salen desmarcadas, para revisarlas, salvo
    /// cuando la propia versión indica su estilo (ver <see cref="GeneroPorReglas.Calcular"/>).
    /// </summary>
    Todas,
}

/// <summary>Qué hace la propuesta con el género que tiene la canción.</summary>
public enum CambioGenero
{
    /// <summary>La canción no tiene género.</summary>
    Rellenar,
    /// <summary>Tiene un género genérico, o algo que no es un género.</summary>
    SustituirGenerico,
    /// <summary>Tiene otro estilo concreto.</summary>
    Revisar,
}

/// <summary>Un género propuesto para una canción, con la regla y los datos que lo justifican.</summary>
/// <param name="Marcada">Sale marcada solo si la regla es fiable para ese caso (ver <see cref="GeneroPorReglas.Calcular"/>).</param>
/// <param name="GeneroActual">El género que tiene la canción al proponer, tal como está escrito ("" si ninguno).</param>
public sealed record PropuestaGeneroRegla(string Ruta, string Genero, ReglaGenero Regla, string PorQue, bool Marcada,
                                          string GeneroActual = "", CambioGenero Cambio = CambioGenero.Rellenar);

/// <summary>Las reglas con las que se propone y a qué canciones.</summary>
/// <param name="Carpetas">Carpeta → género ("" = carpeta excluida de la regla de su madre).</param>
/// <param name="Artistas">Clave del artista principal → género ("" = artista excluido de la regla automática).</param>
public sealed record OpcionesGeneroReglas(IReadOnlyDictionary<string, string> Carpetas, IReadOnlyDictionary<string, string> Artistas,
                                          AlcanceGenero Alcance = AlcanceGenero.Todas);

/// <summary>Lo que sale de proponer.</summary>
/// <param name="Protegidas">Mashups y remixes firmados con otro estilo, que conservan su género.</param>
/// <param name="Listas">Canciones con varios géneros («Rap; Hip Hop; Latino»), que no se tocan: son cosa de «Unificar géneros».</param>
public sealed record CalculoGeneroReglas(List<PropuestaGeneroRegla> Propuestas, int Protegidas, int Listas);

/// <summary>Un género del reparto de un artista y en cuántas de sus canciones va.</summary>
/// <param name="Generico">Es un género genérico, que no cuenta para su sugerencia.</param>
public readonly record struct RepartoGenero(string Genero, int Canciones, bool Generico);

/// <summary>Lo que se sabe del género de un artista, para el mapa artista → género.</summary>
/// <param name="Nombre">Cómo lo escribe la biblioteca (la grafía más repetida).</param>
/// <param name="Canciones">Canciones en las que es el artista principal.</param>
/// <param name="Reparto">Sus géneros, de más a menos canciones, con los genéricos marcados.</param>
/// <param name="Sugerencia">El género dominante sin contar los genéricos, si la regla automática lo propone; "" si no.</param>
public sealed record ResumenArtista(string Clave, string Nombre, int Canciones, int SinGenero, IReadOnlyList<RepartoGenero> Reparto,
                                    string Sugerencia, int Con, int Total, bool Fuerte);

/// <summary>
/// Una carpeta con canciones sin género, para que el usuario decida qué género le corresponde. Las
/// cifras incluyen las subcarpetas, que es a lo que se aplica su regla.
/// </summary>
/// <param name="SinGenero">Canciones de la carpeta y sus subcarpetas que no tienen género.</param>
/// <param name="Total">Canciones de la carpeta y sus subcarpetas.</param>
/// <param name="Dominante">El género más repetido entre las que sí tienen, ya normalizado ("" si ninguna tiene).</param>
/// <param name="ConDominante">Cuántas lo llevan.</param>
/// <param name="ConGenero">Cuántas tienen algún género.</param>
/// <param name="Subcarpetas">Parte de las canciones está en subcarpetas.</param>
public sealed record CarpetaSinGenero(string Ruta, int SinGenero, int Total, string Dominante, int ConDominante, int ConGenero,
                                      bool Subcarpetas = false);

/// <summary>
/// Poner o corregir el género de las canciones con reglas, sin IA (idea P9, ampliada con el género
/// por artista).
///
/// MEDIDO en una biblioteca real: 2.566 de 13.550 archivos no tienen género (19 %), y ni las Colecciones
/// por género ni el filtro de género de rekordbox los ven. Manda, por este orden, la primera regla que
/// tenga género para la canción:
///
///   1. Versión: el estilo que nombra la propia versión («(Bachata Version)», «(Moombahton Remix)»;
///      ver <see cref="EstiloDeVersion"/>). Si la versión dice su estilo, ninguna otra regla la mira.
///   2. Carpeta: un mapa carpeta → género que rellena el usuario («Flamenco Rumbas» → Rumba). Vale para
///      la carpeta y sus subcarpetas, y si hay reglas para varias, manda la más específica (la más
///      profunda). El mapa es explícito a propósito: hay carpetas que mezclan estilos («WarmUp»,
///      «Erasmus») y deducirlo del nombre o de su contenido las estropearía.
///   3. El mapa artista → género que rellena el usuario («Anuel AA» → Reggaeton), por el artista
///      principal. Hace falta porque la regla automática no cubre a los artistas que mezclan estilos:
///      medido, de 729 artistas con 3 o más canciones con género solo 222 llegan al 70 % con uno.
///   4. Artista: el género dominante del artista principal entre sus canciones que ya tienen género,
///      después de normalizarlas con las mismas reglas que «Unificar géneros» y sin contar los
///      genéricos. Solo con un mínimo de canciones y un dominio claro (ver <see cref="MinimoCanciones"/>
///      y <see cref="DominioMinimo"/>).
///
/// Qué sale marcado depende del género que ya tiene la canción (ver <see cref="Calcular"/>): si no
/// tiene o tiene uno genérico, se rellena o se sustituye; si tiene otro estilo, la propuesta sale
/// desmarcada para revisarla, salvo la de la versión, que manda; frente a las reglas de carpeta y de
/// artista, los mashups y los remixes firmados conservan su estilo. No usa la IA:
/// está medido que en lote acierta 1 de cada 20 y que el modelo de 8B confunde estilos («Urban House»
/// → Hip Hop). Tampoco el BPM: medido, por sí solo acierta el estilo un 30,6 %, lo mismo que decir
/// siempre el género más frecuente, porque a 90-100 BPM conviven reggaeton, hip hop, pop y flamenco.
/// </summary>
public static class GeneroPorReglas
{
    /// <summary>
    /// Canciones con género que necesita el artista para que su género cuente. Con dos, que coincidan
    /// puede ser casualidad (un remix, una colaboración con otro estilo); con tres ya es una tendencia.
    /// No depende del tamaño de la biblioteca: es lo que se sabe de UN artista. Los géneros genéricos
    /// no cuentan (ver <see cref="GenerosPorArtista"/>).
    /// </summary>
    public const int MinimoCanciones = 3;

    /// <summary>
    /// Qué parte de sus canciones con género tiene que llevar el dominante: al menos 7 de cada 10, es
    /// decir, más del doble que todos los demás juntos. Por debajo, el artista toca varios estilos y
    /// adivinar cuál es el de una canción concreta es echarlo a suertes.
    /// </summary>
    public const double DominioMinimo = 0.70;

    /// <summary>A partir de cuántas canciones la regla del artista se considera fuerte (sale marcada)…</summary>
    /// <remarks>
    /// Los umbrales salen de lo que significan (una tendencia, más del doble que el resto, casi
    /// unanimidad), no de ajustarlos a una biblioteca. Para comprobar que dicen lo que prometen se
    /// MIDIÓ en una biblioteca real de 13.590 canciones, ocultando el género de cada canción que sí lo
    /// tiene y proponiéndolo con las demás de su artista: con 3 o más y un 70 % salen 2.185 propuestas
    /// y el 74 % coincide exactamente con el género oculto (casi todos los fallos son géneros vecinos,
    /// «Latin» por «Reggaeton»); las fuertes coinciden en el 86 %; por debajo del 70 %, solo entre el
    /// 33 % y el 50 %.
    ///
    /// Sin contar los géneros genéricos («Latin», «Dance», «Electro»…), que no dicen qué estilo es, la
    /// regla cubre un 42 % más (2.830 canciones frente a 1.988) con el mismo acierto (78,1 % frente a
    /// 77,8 %; las fuertes, 87,2 % frente a 87,9 %), y coincide más con las carpetas en las que el
    /// usuario ordena sus estilos (83,0 % frente a 78,3 %; las fuertes, 74,6 % frente a 56,2 %). Los
    /// umbrales no se tocan: por debajo del 70 % entran artistas que de verdad mezclan estilos.
    ///
    /// Que las fuertes coincidan con las carpetas solo un 75 % es lo que impide marcar con ellas una
    /// SUSTITUCIÓN: «fuerte» solo marca el relleno de canciones sin género.
    /// </remarks>
    public const int MinimoFuerte = 5;

    /// <summary>…y con qué dominio: 9 de cada 10.</summary>
    public const double DominioFuerte = 0.90;

    /// <summary>
    /// El género tal como cuenta para estas reglas: con las grafías unificadas como en «Unificar
    /// géneros» (GenerosIa.PorReglas: «Hip-Hop» → Hip Hop, «reguetón» → Reggaeton) y, si es una lista,
    /// el primero, que es el principal (como hace GenreNormalizer.Canonical). Null si no es un género
    /// («@usuario», un pool, «Desconocido»), que para esto es igual que no tener.
    /// </summary>
    public static string? Normalizado(string? genero)
    {
        if (string.IsNullOrWhiteSpace(genero)) return null;
        var p = GenerosIa.PorReglas(genero, 1);
        if (p.Origen == OrigenGenero.NoEsGenero) return null;
        var valor = p.Origen == OrigenGenero.Regla ? p.Propuesto : genero.Trim();
        if (GenerosIa.EsLista(valor))
            valor = valor.Split(new[] { ';', '/', ',', '|' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                         .FirstOrDefault() ?? "";
        return valor.Trim().Length > 0 ? valor.Trim() : null;
    }

    /// <summary>
    /// <see cref="Normalizado"/> con memoria: una biblioteca repite unos pocos cientos de géneros en
    /// miles de canciones, y cada normalización pasa por varias expresiones regulares.
    /// </summary>
    private static Func<string?, string?> Normalizador()
    {
        var vistos = new Dictionary<string, string?>(StringComparer.Ordinal);
        return g =>
        {
            if (g == null) return null;
            if (!vistos.TryGetValue(g, out var n)) vistos[g] = n = Normalizado(g);
            return n;
        };
    }

    /// <summary>
    /// La canción no tiene género (vacío o en blanco). Lo que no es un género («@usuario») no cuenta
    /// aquí: para <see cref="Calcular"/> es un género genérico, que solo se sustituye si el alcance lo
    /// incluye.
    /// </summary>
    public static bool SinGenero(Track t) => string.IsNullOrWhiteSpace(t.Genre);

    /// <summary>Clave para agrupar géneros: «reggaeton» y «Reggaetón» son el mismo.</summary>
    public static string ClaveGenero(string genero) => VocabularioBiblioteca.Clave(genero);

    /// <summary>
    /// El artista principal (el primero del crédito), como clave: «Ozuna, Daddy Yankee Ft Quevedo»
    /// cuenta para Ozuna. Vacío si no hay artista o si es publicidad (un «@usuario», un pool), que no
    /// dice nada del estilo de la canción.
    /// </summary>
    public static string ClaveArtista(string? artista)
    {
        if (LimpiezaPacks.ArtistaEsPublicidad(artista)) return "";
        var primero = IndiceArtistas.Partir(artista).FirstOrDefault();
        return primero == null ? "" : IndiceArtistas.Clave(primero);
    }

    /// <summary>
    /// Los géneros de la biblioteca ya normalizados, de más a menos canciones, escritos como los lleva
    /// la mayoría. Es lo que ofrece el desplegable del mapa de carpetas: elegir un género que ya usas
    /// evita crear otra variante («Reggaeton» y «Reguetón»).
    /// </summary>
    public static IReadOnlyList<(string Genero, int Canciones)> GenerosDeLaBiblioteca(IEnumerable<Track> canciones)
    {
        var grupos = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var normalizado = Normalizador();
        foreach (var t in canciones)
        {
            if (normalizado(t.Genre) is not { } g) continue;
            var k = ClaveGenero(g);
            if (k.Length == 0) continue;
            if (!grupos.TryGetValue(k, out var formas)) grupos[k] = formas = new Dictionary<string, int>(StringComparer.Ordinal);
            formas[g] = formas.GetValueOrDefault(g) + 1;
        }
        return grupos.Values
            .Select(f => (Genero: f.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key, Canciones: f.Values.Sum()))
            .OrderByDescending(x => x.Canciones).ThenBy(x => x.Genero, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// El género que se escribe para lo que el usuario puso en el mapa: si la biblioteca ya usa ese
    /// género (aunque sea con otras mayúsculas o acentos, o con un sinónimo conocido como «reguetón»),
    /// se escribe como lo escribe la biblioteca. Así el mapa no crea otra variante que luego haya que
    /// unificar.
    /// </summary>
    public static string ComoEnLaBiblioteca(string? tecleado, IEnumerable<string> generosBiblioteca)
    {
        var t = (tecleado ?? "").Trim();
        if (t.Length == 0) return "";
        var lista = generosBiblioteca as IReadOnlyCollection<string> ?? generosBiblioteca.ToList();
        string? Existente(string g) { var k = ClaveGenero(g); return lista.FirstOrDefault(x => ClaveGenero(x) == k); }

        if (Existente(t) is { } igual) return igual;
        if (GenerosIa.EsLista(t)) return t;   // «Pop/Rock» escrito a propósito: Canonical se quedaría con «Pop»
        var canonico = GenreNormalizer.Canonical(t);
        if (!GenreNormalizer.Conocidos.Contains(canonico, StringComparer.Ordinal)) return t;
        return Existente(canonico) ?? canonico;
    }

    /// <summary>Lo que se va sumando de una carpeta y todo lo que tiene debajo.</summary>
    private sealed class Cuenta
    {
        public int Total, SinGenero;
        public bool Subcarpetas;
        public readonly List<string?> Generos = new();

        /// <summary>
        /// Por dónde le llegan canciones sin género: «.» las suyas y, si no, el nombre de la subcarpeta
        /// inmediata. Con dos o más, la carpeta agrupa varias.
        /// </summary>
        public readonly HashSet<string> Fuentes = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Las carpetas que tienen canciones sin género, de más a menos. Es por donde conviene empezar el
    /// mapa: unas pocas carpetas grandes suelen cubrir la mayoría. El dominante se enseña como pista,
    /// no se aplica solo.
    ///
    /// Las cifras incluyen las subcarpetas, porque la regla de una carpeta vale también para ellas. Y
    /// salen también las carpetas que AGRUPAN otras con canciones sin género (dentro de las carpetas
    /// de la biblioteca, <paramref name="raices"/>), aunque no tengan canciones propias. POR QUÉ: en
    /// una biblioteca organizada como Género/Pack/… la lista solo enseñaba cientos de carpetas pequeñas
    /// de pack, y la carpeta del género, que es donde tiene sentido la regla, había que buscarla y
    /// añadirla a mano. Una carpeta con una sola subcarpeta no se repite: daría las mismas cifras.
    /// Sin <paramref name="raices"/> no se sube por encima de las carpetas con canciones.
    /// </summary>
    public static List<CarpetaSinGenero> Carpetas(IEnumerable<Track> canciones, IEnumerable<string>? raices = null)
    {
        var listaRaices = (raices ?? Enumerable.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        var dentro = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool EnLaBiblioteca(string carpeta)
        {
            if (!dentro.TryGetValue(carpeta, out var si))
                dentro[carpeta] = si = listaRaices.Any(r => Rutas.MismaCarpeta(carpeta, r) || Rutas.EstaDentroDe(carpeta, r));
            return si;
        }

        var cuentas = new Dictionary<string, Cuenta>(StringComparer.OrdinalIgnoreCase);
        var propias = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // con canciones sin género dentro
        var normalizado = Normalizador();
        foreach (var t in canciones)
        {
            var dir = Path.GetDirectoryName(t.FilePath) ?? "";
            if (dir.Length == 0) continue;
            var sin = SinGenero(t);
            var genero = normalizado(t.Genre);
            if (sin) propias.Add(dir);

            // La carpeta de la canción y, dentro de la biblioteca, todas las que tiene por encima.
            string? carpeta = dir, hija = null;
            while (!string.IsNullOrEmpty(carpeta))
            {
                if (!cuentas.TryGetValue(carpeta, out var c)) cuentas[carpeta] = c = new Cuenta();
                c.Total++;
                c.Generos.Add(genero);
                if (hija != null) c.Subcarpetas = true;
                if (sin)
                {
                    c.SinGenero++;
                    c.Fuentes.Add(hija == null ? "." : Path.GetFileName(hija.TrimEnd('\\', '/')));
                }
                var madre = Path.GetDirectoryName(carpeta.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(madre) || !EnLaBiblioteca(madre)) break;
                hija = carpeta;
                carpeta = madre;
            }
        }

        var res = new List<CarpetaSinGenero>();
        var mezcladas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (carpeta, c) in cuentas)
        {
            if (c.SinGenero == 0 || (!propias.Contains(carpeta) && c.Fuentes.Count < 2)) continue;
            var (dominante, con, total) = Dominante(c.Generos);
            // Le llegan canciones sin género de alguna subcarpeta: su regla se aplicaría también a ellas.
            var agrupa = c.Fuentes.Any(f => f != ".");
            var mezcla = Mezcla(con, total);

            // Una carpeta de la biblioteca que agrupa otras suele ser TODA la música, de muchos estilos:
            // saldría la primera de la lista, y darle un género lo pondría en todo lo que no cubra otra
            // regla. Solo sale si las canciones que tienen género coinciden como lo exige la regla del
            // artista (al menos 3 y 7 de cada 10), como cuando la carpeta añadida a la biblioteca es la
            // de un género. Vale también si tiene canciones sueltas propias: sus cifras siguen siendo
            // las de toda la biblioteca.
            if (agrupa && listaRaices.Any(r => Rutas.MismaCarpeta(carpeta, r)) && !Fuerza(con, total).Propone)
                continue;

            // Lo mismo con una carpeta intermedia que solo agrupa otras («COMERCIAL», con un pack de
            // cada estilo debajo) cuando sus canciones con género muestran varios estilos: también
            // salía la primera, justo debajo de «Empieza por las carpetas de arriba». Si no hay con qué
            // juzgarla (casi nada tiene género), sí sale: es el caso de la carpeta de un género con
            // packs sin etiquetar, para el que existe esta lista. No depende de cómo esté organizada
            // la biblioteca: se mira lo que dicen sus canciones. Se puede añadir a mano igualmente.
            if (agrupa && !propias.Contains(carpeta) && mezcla) continue;

            // Con canciones propias y subcarpetas de varios estilos, sale (sus canciones sueltas
            // necesitan regla), pero detrás de las demás: no es por donde conviene empezar.
            if (agrupa && mezcla) mezcladas.Add(carpeta);
            res.Add(new CarpetaSinGenero(carpeta, c.SinGenero, c.Total, dominante, con, total, c.Subcarpetas));
        }
        return res.OrderBy(c => mezcladas.Contains(c.Ruta))
                  .ThenByDescending(c => c.SinGenero).ThenBy(c => c.Ruta, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Las canciones con género de una carpeta muestran varios estilos: hay bastantes para juzgar
    /// (las mismas que pide la regla del artista) y ninguno llega a dominar como ella exige.
    /// </summary>
    private static bool Mezcla(int con, int total) => total >= MinimoCanciones && !Fuerza(con, total).Propone;

    /// <summary>
    /// Una carpeta elegida a mano (por ejemplo, la que agrupa varias): cuenta también lo que hay en sus
    /// subcarpetas, que es a lo que se aplicará su regla.
    /// </summary>
    public static CarpetaSinGenero ConSubcarpetas(IEnumerable<Track> canciones, string carpeta)
    {
        var dentro = new List<Track>();
        var subcarpetas = false;
        foreach (var t in canciones)
        {
            var dir = Path.GetDirectoryName(t.FilePath) ?? "";
            if (Rutas.MismaCarpeta(dir, carpeta)) dentro.Add(t);
            else if (Rutas.EstaDentroDe(dir, carpeta)) { dentro.Add(t); subcarpetas = true; }
        }
        var (dominante, con, total) = Dominante(dentro.Select(t => t.Genre).Select(Normalizador()));
        return new CarpetaSinGenero(carpeta, dentro.Count(SinGenero), dentro.Count, dominante, con, total, subcarpetas);
    }

    /// <summary>El género más repetido de una lista (null = sin género), cuántas lo llevan y cuántas tienen alguno.</summary>
    private static (string Genero, int Con, int Total) Dominante(IEnumerable<string?> generos)
    {
        var cuenta = new Dictionary<string, (string Nombre, int N)>(StringComparer.Ordinal);
        var total = 0;
        foreach (var g in generos)
        {
            if (g == null) continue;
            var k = ClaveGenero(g);
            if (k.Length == 0) continue;
            total++;
            cuenta[k] = cuenta.TryGetValue(k, out var v) ? (v.Nombre, v.N + 1) : (g, 1);
        }
        if (cuenta.Count == 0) return ("", 0, 0);
        var mejor = cuenta.Values.OrderByDescending(v => v.N).ThenBy(v => v.Nombre, StringComparer.Ordinal).First();
        return (mejor.Nombre, mejor.N, total);
    }

    /// <summary>
    /// La regla de carpeta que corresponde a un archivo: la de su carpeta o la de la carpeta más
    /// cercana por encima. Null si ninguna lo cubre.
    ///
    /// Una regla con el género vacío es una carpeta EXCLUIDA (<see cref="MapaGenerosCarpeta.Excluir"/>):
    /// como es la más cercana, tapa la de su carpeta madre, y sus canciones se quedan sin regla de
    /// carpeta (solo cuenta la del artista). Una subcarpeta suya con regla propia sí la tiene.
    /// </summary>
    public static (string Genero, string Carpeta)? ReglaDeCarpeta(IReadOnlyDictionary<string, string> mapa, string rutaArchivo)
    {
        var dir = Path.GetDirectoryName(rutaArchivo) ?? "";
        (string Genero, string Carpeta)? mejor = null;
        var largo = -1;
        foreach (var (carpeta, genero) in mapa)
        {
            if (!Rutas.MismaCarpeta(dir, carpeta) && !Rutas.EstaDentroDe(dir, carpeta)) continue;
            var l = carpeta.TrimEnd('\\', '/').Length;
            if (l > largo) { largo = l; mejor = ((genero ?? "").Trim(), carpeta); }
        }
        return mejor is { Genero.Length: > 0 } ? mejor : null;
    }

    /// <summary>
    /// <see cref="FamiliasGenero.EsGenerico"/> con memoria, como <see cref="Normalizador"/>: cada
    /// consulta normaliza el nombre con varias expresiones regulares.
    /// </summary>
    private static Func<string, bool> DetectorGenerico()
    {
        var vistos = new Dictionary<string, bool>(StringComparer.Ordinal);
        return g =>
        {
            if (!vistos.TryGetValue(g, out var si)) vistos[g] = si = FamiliasGenero.EsGenerico(g);
            return si;
        };
    }

    /// <summary>
    /// El género de cada artista principal entre sus canciones con género: el dominante, cuántas lo
    /// llevan, cuántas tienen alguno y cuántas se han dejado fuera por tener un género genérico.
    ///
    /// Las canciones con un género genérico («Latin», «Dance», «Electro», «Mashup»…) no cuentan, ni
    /// para el dominante ni para el total: no dicen qué estilo pincha el artista, y contarlas hundía
    /// el dominio de artistas muy claros (Daddy Yankee pasa del 67 % al 77 % sin ellas, Ozuna del 68 %
    /// al 77 %). Así, además, un género genérico no puede salir como dominante.
    /// </summary>
    public static Dictionary<string, (string Genero, int Con, int Total, int Genericos)> GenerosPorArtista(IEnumerable<Track> canciones)
    {
        var porArtista = new Dictionary<string, (List<string?> Generos, int Genericos)>(StringComparer.Ordinal);
        var normalizado = Normalizador();
        var generico = DetectorGenerico();
        foreach (var t in canciones)
        {
            if (normalizado(t.Genre) is not { } g) continue;
            var a = ClaveArtista(t.Artist);
            if (a.Length == 0) continue;
            if (!porArtista.TryGetValue(a, out var l)) l = (new List<string?>(), 0);
            if (generico(g)) l.Genericos++;
            else l.Generos.Add(g);
            porArtista[a] = l;
        }
        return porArtista.ToDictionary(kv => kv.Key, kv =>
        {
            var (genero, con, total) = Dominante(kv.Value.Generos);
            return (genero, con, total, kv.Value.Genericos);
        }, StringComparer.Ordinal);
    }

    /// <summary>
    /// El resumen de cada artista principal, de más a menos canciones: cómo lo escribe la biblioteca,
    /// cuántas canciones tiene y cuántas sin género, su reparto de géneros (genéricos incluidos y
    /// señalados) y lo que propone la regla automática. Es lo que enseña el mapa artista → género para
    /// que el usuario decida. Lineal: va fuera del hilo de la interfaz.
    /// </summary>
    public static List<ResumenArtista> ResumenArtistas(IEnumerable<Track> canciones, CancellationToken ct = default)
    {
        var datos = new Dictionary<string, Acumulado>(StringComparer.Ordinal);
        var normalizado = Normalizador();
        var generico = DetectorGenerico();
        foreach (var t in canciones)
        {
            ct.ThrowIfCancellationRequested();
            var clave = ClaveArtista(t.Artist);
            if (clave.Length == 0) continue;
            if (!datos.TryGetValue(clave, out var a)) datos[clave] = a = new Acumulado();
            a.Canciones++;
            var nombre = IndiceArtistas.Partir(t.Artist).First();
            a.Grafias[nombre] = a.Grafias.GetValueOrDefault(nombre) + 1;
            if (SinGenero(t)) { a.SinGenero++; continue; }
            if (normalizado(t.Genre) is not { } g) continue;
            var k = ClaveGenero(g);
            if (k.Length == 0) continue;
            a.Generos[k] = a.Generos.TryGetValue(k, out var v) ? (v.Nombre, v.N + 1) : (g, 1);
        }

        var res = new List<ResumenArtista>(datos.Count);
        foreach (var (clave, a) in datos)
        {
            var reparto = a.Generos.Values
                .Select(v => new RepartoGenero(v.Nombre, v.N, generico(v.Nombre)))
                .OrderByDescending(r => r.Canciones).ThenBy(r => r.Genero, StringComparer.Ordinal)
                .ToList();
            // El mismo dominante que GenerosPorArtista: el de más canciones sin contar los genéricos.
            var (dominante, con, total) = Dominante(reparto.Where(r => !r.Generico).SelectMany(r => Enumerable.Repeat<string?>(r.Genero, r.Canciones)));
            var (propone, fuerte) = Fuerza(con, total);
            var nombre = a.Grafias.OrderByDescending(f => f.Value)
                                  .ThenBy(f => f.Key.Any(char.IsLetter) && !f.Key.Any(char.IsLower))
                                  .ThenBy(f => f.Key, StringComparer.Ordinal)
                                  .First().Key;
            res.Add(new ResumenArtista(clave, nombre, a.Canciones, a.SinGenero, reparto, propone ? dominante : "", con, total, fuerte));
        }
        return res.OrderByDescending(r => r.Canciones).ThenBy(r => r.Nombre, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Lo que se va sumando de un artista para su resumen.</summary>
    private sealed class Acumulado
    {
        public int Canciones, SinGenero;
        public readonly Dictionary<string, int> Grafias = new(StringComparer.Ordinal);
        /// <summary>Clave del género → cómo se escribe (el primero que se vio) y en cuántas va.</summary>
        public readonly Dictionary<string, (string Nombre, int N)> Generos = new(StringComparer.Ordinal);
    }

    /// <summary>El reparto como se enseña: «Reggaeton 71 · Trap 32 · Latin 10».</summary>
    public static string TextoReparto(IEnumerable<RepartoGenero> reparto, int maximo = 6)
    {
        var lista = reparto.ToList();
        var texto = string.Join(" · ", lista.Take(maximo).Select(r => $"{r.Genero} {r.Canciones:N0}"));
        return lista.Count > maximo ? texto + " · …" : texto;
    }

    /// <summary>¿Basta lo que se sabe del artista para proponer? Y si es así, ¿es lo bastante fuerte para marcarlo?</summary>
    public static (bool Propone, bool Fuerte) Fuerza(int con, int total)
    {
        if (total < MinimoCanciones || con < MinimoCanciones) return (false, false);
        var dominio = (double)con / total;
        if (dominio < DominioMinimo) return (false, false);
        return (true, total >= MinimoFuerte && dominio >= DominioFuerte);
    }

    /// <summary>Los géneros propuestos para las canciones sin género, con el mapa de carpetas y la regla del artista (lo de antes).</summary>
    public static List<PropuestaGeneroRegla> Proponer(IReadOnlyList<Track> canciones, IReadOnlyDictionary<string, string> mapa,
                                                      CancellationToken ct = default)
        => Proponer(canciones, new OpcionesGeneroReglas(mapa, new Dictionary<string, string>(), AlcanceGenero.SinGenero), ct);

    /// <summary>Los géneros propuestos con estas reglas y este alcance (ver <see cref="Calcular"/>).</summary>
    public static List<PropuestaGeneroRegla> Proponer(IReadOnlyList<Track> canciones, OpcionesGeneroReglas op, CancellationToken ct = default)
        => Calcular(canciones, op, ct).Propuestas;

    /// <summary>Tipos de versión que, firmados por alguien, son otra canción con su propio estilo.</summary>
    private static readonly HashSet<string> VersionesFirmadas = new(StringComparer.OrdinalIgnoreCase)
    {
        "Remix", "Bootleg", "Rework", "Refix", "Flip", "VIP", "Dub",
    };

    /// <summary>
    /// Un remix firmado: «Bonita (ADRUKID Remix)». Se lee del título y, si no lo hay, del nombre del
    /// archivo. Un «(Remix)» sin autor no lo es, ni las versiones de utilidad de los pools (Extended,
    /// Intro, Edit, Acapella), que siguen siendo el tema tal cual.
    /// </summary>
    public static bool RemixFirmado(Track t)
    {
        var texto = string.IsNullOrWhiteSpace(t.Title) ? Path.GetFileNameWithoutExtension(t.FilePath) : t.Title;
        var r = RemixParser.Parse(texto);
        return r.IsVersion && VersionesFirmadas.Contains(r.Kind) && RemixParser.Autor(r.Remixer).Length > 0;
    }

    // Un tipo de versión escrito como género: dice que la canción es un mashup o una transición.
    private static readonly HashSet<string> GenerosDeMezcla = new(StringComparer.Ordinal) { "mashup", "mash up", "transition", "transicion" };

    /// <summary>
    /// Un mashup, una transición o un blend: por el nombre del archivo o del título, por estar en una
    /// carpeta de mashups (<see cref="IndiceVersiones.Clasificar"/>) o porque su género lo dice.
    /// </summary>
    public static bool EsMezcla(Track t)
        => IndiceVersiones.Clasificar(t.FilePath) == TipoPieza.Mezcla || Matching.IsMezclaDeVariosTemas(t.Title)
           || GenerosDeMezcla.Contains(FamiliasGenero.Clave(t.Genre));

    /// <summary>
    /// Mashups y remixes firmados: conservan su estilo. MEDIDO en una biblioteca real: sustituir su
    /// género concreto por el del artista estropeó 23 de 28 revisados a mano, porque el estilo es el
    /// de quien lo rehízo o el del otro tema. En un remix firmado el artista principal solo acierta el
    /// estilo de la carpeta del usuario en el 42 % de los casos, frente al 75 % del tema original y el
    /// 81 % de Extended, Intro y Acapella, que por eso no protegen. El remixer tampoco sirve para
    /// deducir: acierta un 53 %.
    /// </summary>
    public static bool Protegida(Track t) => EsMezcla(t) || RemixFirmado(t);

    /// <summary>
    /// Los géneros propuestos, recorriendo la biblioteca una vez (fuera del hilo de la interfaz; con
    /// veinte mil canciones dura poco).
    ///
    /// Para cada canción que entra en el alcance: si su género es una lista, no se toca (es cosa de
    /// «Unificar géneros»). Si la versión dice su estilo, se propone ese y ninguna otra regla la mira.
    /// Si no, la primera regla con género: la carpeta, el mapa del usuario por artista o el dominante
    /// automático del artista. Lo que sale marcado depende del género que ya tiene:
    ///   · Ninguno: marcada si la regla es de carpeta (como siempre, aunque sea un mashup) o del mapa
    ///     del usuario y la canción no está protegida; con el dominante automático, solo si es fuerte y
    ///     no está protegida.
    ///   · Uno genérico: se propone sustituirlo, marcada salvo que esté protegida. MEDIDO: 0
    ///     estropicios en 15 revisadas a mano y en 178 comparadas con las carpetas del usuario.
    ///   · El mismo: nada.
    ///   · Otro estilo concreto: nada si está protegida o es de la misma familia (se perdería detalle:
    ///     Trap bajo Hip Hop); si no, desmarcada para revisar. MEDIDO: un 82 % eran arreglos, pero no
    ///     todos, y el dominante fuerte solo coincide con las carpetas del usuario un 56 %.
    /// La versión sale marcada, también sobre otro estilo concreto (es lo que pide un DJ: si el remix
    /// o la versión dice su estilo, ese manda), salvo que su estilo sea ambiguo, que solo lo diga el
    /// álbum o que la canción tenga otro estilo de la misma familia: entonces se propone desmarcada.
    /// </summary>
    public static CalculoGeneroReglas Calcular(IReadOnlyList<Track> canciones, OpcionesGeneroReglas op, CancellationToken ct = default)
    {
        var artistas = GenerosPorArtista(canciones);
        var normalizado = Normalizador();
        var generico = DetectorGenerico();
        var biblioteca = GenerosDeLaBiblioteca(canciones).Select(g => g.Genero).ToList();
        var comoEnLaBiblioteca = new Dictionary<string, string>(StringComparer.Ordinal);
        string Escrito(string estilo)
        {
            if (!comoEnLaBiblioteca.TryGetValue(estilo, out var e)) comoEnLaBiblioteca[estilo] = e = ComoEnLaBiblioteca(estilo, biblioteca);
            return e;
        }

        var res = new List<PropuestaGeneroRegla>();
        int protegidas = 0, listas = 0;
        foreach (var t in canciones)
        {
            ct.ThrowIfCancellationRequested();

            // La clase del género que ya tiene: vacío, genérico (o algo que no es un género) o concreto.
            var actual = (t.Genre ?? "").Trim();
            var vacio = actual.Length == 0;
            if (!vacio && GenerosIa.EsLista(actual))
            {
                if (op.Alcance != AlcanceGenero.SinGenero) listas++;
                continue;
            }
            var actualNorm = vacio ? null : normalizado(actual);
            var esGenerico = !vacio && (actualNorm == null || generico(actualNorm));
            var cambio = vacio ? CambioGenero.Rellenar : esGenerico ? CambioGenero.SustituirGenerico : CambioGenero.Revisar;
            if (!Admite(op.Alcance, cambio)) continue;
            var claveActual = actualNorm == null ? "" : Compacta(actualNorm);

            // 1. El estilo que dice la propia versión. Si confirma el que tiene, no hay nada que hacer.
            if (EstiloDeVersion.De(t.Title, Path.GetFileNameWithoutExtension(t.FilePath), t.Artist, t.Album) is { } v)
            {
                var estilo = Escrito(v.Estilo);
                if (claveActual.Length > 0 && Compacta(estilo) == claveActual) continue;
                var mismaFamilia = cambio == CambioGenero.Revisar && FamiliasGenero.MismaFamilia(actualNorm, estilo);
                var marcada = !v.Ambiguo && v.Fuente != FuenteEstilo.Album && !mismaFamilia;
                var porQue = v.Fuente == FuenteEstilo.Album
                    ? $"Álbum «{t.Album?.Trim()}» → {estilo}: puede describir el pack, revísalo"
                    : $"Versión «{v.Texto}» → {estilo}";
                if (v.Ambiguo) porQue += $" · «{v.Estilo}» puede nombrar varios estilos: revísalo";
                res.Add(new PropuestaGeneroRegla(t.FilePath, estilo, ReglaGenero.Version, porQue + Sufijo(cambio, actual, marcada, false),
                                                 marcada, t.Genre ?? "", cambio));
                continue;
            }

            // 2. La primera regla con género: carpeta, mapa del usuario o dominante automático.
            string genero, porQueRegla;
            ReglaGenero regla;
            var fuerte = false;
            if (ReglaDeCarpeta(op.Carpetas, t.FilePath) is { } rc)
            {
                var propia = Rutas.MismaCarpeta(Path.GetDirectoryName(t.FilePath) ?? "", rc.Carpeta);
                (genero, regla) = (rc.Genero, ReglaGenero.Carpeta);
                porQueRegla = propia
                    ? $"Carpeta «{NombreCarpeta(rc.Carpeta)}» → {rc.Genero}"
                    : $"Carpeta «{NombreCarpeta(rc.Carpeta)}» (y sus subcarpetas) → {rc.Genero}";
            }
            else
            {
                var clave = ClaveArtista(t.Artist);
                if (clave.Length == 0) continue;
                var artista = IndiceArtistas.Partir(t.Artist).First();
                if (op.Artistas.TryGetValue(clave, out var suyo))
                {
                    // "" es un artista excluido: tampoco le vale la regla automática.
                    if ((suyo ?? "").Trim().Length == 0) continue;
                    (genero, regla) = (suyo!.Trim(), ReglaGenero.MapaArtista);
                    porQueRegla = $"Tu regla: «{artista}» → {genero}";
                }
                else if (artistas.TryGetValue(clave, out var d) && Fuerza(d.Con, d.Total) is { Propone: true } f)
                {
                    (genero, regla, fuerte) = (d.Genero, ReglaGenero.Artista, f.Fuerte);
                    porQueRegla = $"Artista «{artista}»: {d.Con} de {d.Total} canciones con {d.Genero}"
                                  + (d.Genericos > 0 ? " (sin contar géneros genéricos)" : "");
                }
                else continue;
            }

            // 3. La decisión, según el género que ya tiene.
            if (claveActual.Length > 0 && Compacta(genero) == claveActual) continue;
            // Una canción sin género con regla de carpeta sale marcada aunque sea un mashup: no hace falta mirarlo.
            var protegida = (regla != ReglaGenero.Carpeta || cambio != CambioGenero.Rellenar) && Protegida(t);
            bool marcar;
            switch (cambio)
            {
                case CambioGenero.Rellenar:
                    marcar = regla switch
                    {
                        ReglaGenero.Carpeta => true,
                        ReglaGenero.MapaArtista => !protegida,
                        _ => fuerte && !protegida,
                    };
                    break;
                case CambioGenero.SustituirGenerico:
                    marcar = !protegida;
                    break;
                default:
                    if (protegida) { protegidas++; continue; }
                    if (FamiliasGenero.MismaFamilia(actualNorm, genero)) continue;
                    marcar = false;
                    break;
            }
            res.Add(new PropuestaGeneroRegla(t.FilePath, genero, regla, porQueRegla + Sufijo(cambio, actual, marcar, protegida && !marcar),
                                             marcar, t.Genre ?? "", cambio));
        }
        return new CalculoGeneroReglas(res, protegidas, listas);
    }

    /// <summary>
    /// La clave del género sin espacios, para saber si una propuesta deja el mismo género: «Afrohouse»
    /// y «Afro House», o «HipHop» y «Hip Hop», son el mismo. Visto al validar en una biblioteca real:
    /// «(Afro House)» proponía cambiar «Afrohouse» por «Afro House».
    /// </summary>
    private static string Compacta(string genero) => ClaveGenero(genero).Replace(" ", "", StringComparison.Ordinal);

    /// <summary>El alcance elegido incluye canciones con ese género (ninguno, genérico u otro estilo).</summary>
    private static bool Admite(AlcanceGenero alcance, CambioGenero cambio) => alcance switch
    {
        AlcanceGenero.SinGenero => cambio == CambioGenero.Rellenar,
        AlcanceGenero.SinGeneroYGenericos => cambio != CambioGenero.Revisar,
        _ => true,
    };

    /// <summary>Lo que se añade al «Por qué» según lo que hace la propuesta con el género que ya tiene.</summary>
    private static string Sufijo(CambioGenero cambio, string actual, bool marcada, bool protegida)
    {
        var texto = cambio switch
        {
            CambioGenero.SustituirGenerico => GenerosIa.NoEsGenero(actual)
                ? $" · sustituye «{actual}», que no es un género"
                : $" · sustituye «{actual}», un género genérico",
            CambioGenero.Revisar => marcada ? $" · sustituye «{actual}»" : $" · para revisar: la canción tiene {actual}",
            _ => "",
        };
        return protegida ? texto + " · mashup o remix firmado: comprueba a qué tema corresponde" : texto;
    }

    /// <summary>
    /// Cuántos valores de género de la biblioteca se escriben de varias formas o no son un género.
    /// Si hay, conviene pasar antes «Unificar géneros»: estas reglas ya los cuentan juntos, pero la
    /// biblioteca seguiría con las variantes.
    /// </summary>
    public static int VariantesSinUnificar(IEnumerable<Track> canciones)
        => GenerosIa.Contar(canciones).Count(x => GenerosIa.PorReglas(x.Original, x.Canciones).Cambia);

    /// <summary>El nombre de la carpeta para enseñarlo («Flamenco Rumbas»), o la ruta si es una raíz.</summary>
    public static string NombreCarpeta(string ruta)
    {
        var limpia = ruta.TrimEnd('\\', '/');
        var nombre = Path.GetFileName(limpia);
        return nombre.Length > 0 ? nombre : ruta;
    }
}

/// <summary>
/// El mapa carpeta → género que rellena el usuario, guardado en su propio archivo
/// (generos-por-carpeta.json, en la carpeta de datos). Es trabajo hecho a mano: se escribe de forma
/// atómica y un archivo ilegible se aparta en vez de pisarse (<see cref="ArchivoJson"/>).
///
/// Una carpeta con el género vacío ("") está EXCLUIDA: la regla de su carpeta madre no vale para ella
/// (una carpeta «WarmUp» dentro de «Reggaeton»). Una versión anterior que lea el archivo la ignora,
/// que es lo mismo que no tener regla propia.
/// </summary>
public sealed class MapaGenerosCarpeta
{
    /// <summary>El nombre del archivo dentro de la carpeta de datos.</summary>
    public const string NombreArchivo = "generos-por-carpeta.json";

    private readonly string _archivo;
    private readonly Dictionary<string, string> _reglas;

    public string AvisoCarga { get; }

    /// <summary>No se pudo leer ni apartar el archivo al abrirlo: <see cref="Guardar"/> no lo pisa.</summary>
    public bool SoloLectura { get; }

    public MapaGenerosCarpeta(string archivo)
    {
        _archivo = archivo;
        var leido = ArchivoJson.Leer(archivo, () => new Dictionary<string, string>(), out var aviso, out var soloLectura);
        _reglas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (carpeta, genero) in leido)
            if (!string.IsNullOrWhiteSpace(carpeta)) _reglas[carpeta] = (genero ?? "").Trim();
        AvisoCarga = aviso;
        SoloLectura = soloLectura;
    }

    public static string RutaEn(AppPaths paths) => Path.Combine(paths.DataDir, NombreArchivo);

    /// <summary>Las reglas, carpeta → género ("" = carpeta excluida de la regla de su madre).</summary>
    public IReadOnlyDictionary<string, string> Reglas => _reglas;

    /// <summary>Cuántas carpetas tienen un género asignado (sin contar las excluidas).</summary>
    public int ConGenero => _reglas.Values.Count(g => g.Length > 0);

    /// <summary>El género de la regla de esa carpeta, o "" si no tiene (o está excluida).</summary>
    public string De(string carpeta) => _reglas.TryGetValue(carpeta, out var g) ? g : "";

    /// <summary>La carpeta está excluida de la regla de su carpeta madre.</summary>
    public bool Excluida(string carpeta) => _reglas.TryGetValue(carpeta, out var g) && g.Length == 0;

    /// <summary>
    /// Pone la regla de una carpeta, o la quita con un género vacío (vuelve a valer la de su carpeta
    /// madre, si la hay; también deja de estar excluida). Devuelve si cambió algo. No guarda en disco.
    /// </summary>
    public bool Poner(string carpeta, string? genero)
    {
        var g = (genero ?? "").Trim();
        if (g.Length == 0) return _reglas.Remove(carpeta);
        if (_reglas.TryGetValue(carpeta, out var antes) && string.Equals(antes, g, StringComparison.Ordinal)) return false;
        _reglas[carpeta] = g;
        return true;
    }

    /// <summary>
    /// Excluye la carpeta (y sus subcarpetas sin regla propia) de la regla de su carpeta madre.
    /// Devuelve si cambió algo. No guarda en disco.
    /// </summary>
    public bool Excluir(string carpeta)
    {
        if (Excluida(carpeta)) return false;
        _reglas[carpeta] = "";
        return true;
    }

    /// <summary>Devuelve "" si fue bien, o el motivo del fallo. En solo lectura no escribe nada.</summary>
    public string Guardar() => SoloLectura ? ArchivoJson.AvisoSoloLectura(_archivo) : ArchivoJson.Escribir(_archivo, _reglas);
}
