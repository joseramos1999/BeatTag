using Etiquetador.Core.Ai;

namespace Etiquetador.Core.Dj;

/// <summary>Qué regla propone el género.</summary>
public enum ReglaGenero
{
    /// <summary>El mapa carpeta → género que rellena el usuario. Es una decisión suya: manda.</summary>
    Carpeta,
    /// <summary>El género dominante del artista entre sus canciones que ya tienen género.</summary>
    Artista,
}

/// <summary>Un género propuesto para una canción que no tiene, con la regla y los datos que lo justifican.</summary>
/// <param name="Marcada">Sale marcada solo si la regla es fuerte.</param>
public sealed record PropuestaGeneroRegla(string Ruta, string Genero, ReglaGenero Regla, string PorQue, bool Marcada);

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
/// Poner género a las canciones que no tienen, sin IA (idea P9).
///
/// MEDIDO en una biblioteca real: 2.566 de 13.550 archivos no tienen género (19 %), y ni las Colecciones
/// por género ni el filtro de género de rekordbox los ven. Dos reglas, en este orden:
///
///   1. Carpeta: un mapa carpeta → género que rellena el usuario («Flamenco Rumbas» → Rumba). Vale para
///      la carpeta y sus subcarpetas, y si hay reglas para varias, manda la más específica (la más
///      profunda). El mapa es explícito a propósito: hay carpetas que mezclan estilos («WarmUp»,
///      «Erasmus») y deducirlo del nombre o de su contenido las estropearía.
///   2. Artista: el género dominante del artista principal entre sus canciones que ya tienen género,
///      después de normalizarlas con las mismas reglas que «Unificar géneros». Solo con un mínimo de
///      canciones y un dominio claro (ver <see cref="MinimoCanciones"/> y <see cref="DominioMinimo"/>).
///
/// Nunca pisa un género existente, y no usa la IA: está medido que en lote acierta 1 de cada 20 y que
/// el modelo de 8B confunde estilos («Urban House» → Hip Hop).
/// </summary>
public static class GeneroPorReglas
{
    /// <summary>
    /// Canciones con género que necesita el artista para que su género cuente. Con dos, que coincidan
    /// puede ser casualidad (un remix, una colaboración con otro estilo); con tres ya es una tendencia.
    /// No depende del tamaño de la biblioteca: es lo que se sabe de UN artista.
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
    /// 33 % y el 50 %. En las 2.597 canciones sin género, el artista propone 280 (7 fuertes): la
    /// regla de carpeta es la que más cubre, y por eso va primero.
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

    /// <summary>La canción no tiene género (vacío o en blanco). Lo que no es un género tampoco se pisa: eso lo arregla «Unificar géneros».</summary>
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
    /// El género de cada artista principal entre sus canciones con género: el dominante, cuántas lo
    /// llevan y cuántas tienen alguno.
    /// </summary>
    public static Dictionary<string, (string Genero, int Con, int Total)> GenerosPorArtista(IEnumerable<Track> canciones)
    {
        var porArtista = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        var normalizado = Normalizador();
        foreach (var t in canciones)
        {
            if (normalizado(t.Genre) is not { } g) continue;
            var a = ClaveArtista(t.Artist);
            if (a.Length == 0) continue;
            if (!porArtista.TryGetValue(a, out var l)) porArtista[a] = l = new List<string?>();
            l.Add(g);
        }
        return porArtista.ToDictionary(kv => kv.Key, kv => Dominante(kv.Value), StringComparer.Ordinal);
    }

    /// <summary>¿Basta lo que se sabe del artista para proponer? Y si es así, ¿es lo bastante fuerte para marcarlo?</summary>
    public static (bool Propone, bool Fuerte) Fuerza(int con, int total)
    {
        if (total < MinimoCanciones || con < MinimoCanciones) return (false, false);
        var dominio = (double)con / total;
        if (dominio < DominioMinimo) return (false, false);
        return (true, total >= MinimoFuerte && dominio >= DominioFuerte);
    }

    /// <summary>
    /// Los géneros propuestos para las canciones sin género. La regla de carpeta va primero (es una
    /// decisión del usuario) y sale marcada; la del artista solo se mira si ninguna carpeta cubre la
    /// canción, y sale marcada solo si es fuerte. El recorrido es lineal: va fuera del hilo de la
    /// interfaz, pero con veinte mil canciones dura poco.
    /// </summary>
    public static List<PropuestaGeneroRegla> Proponer(IReadOnlyList<Track> canciones, IReadOnlyDictionary<string, string> mapa,
                                                      CancellationToken ct = default)
    {
        var artistas = GenerosPorArtista(canciones);
        var res = new List<PropuestaGeneroRegla>();
        foreach (var t in canciones)
        {
            ct.ThrowIfCancellationRequested();
            if (!SinGenero(t)) continue;

            if (ReglaDeCarpeta(mapa, t.FilePath) is { } regla)
            {
                var propia = Rutas.MismaCarpeta(Path.GetDirectoryName(t.FilePath) ?? "", regla.Carpeta);
                var porQue = propia
                    ? $"Carpeta «{NombreCarpeta(regla.Carpeta)}» → {regla.Genero}"
                    : $"Carpeta «{NombreCarpeta(regla.Carpeta)}» (y sus subcarpetas) → {regla.Genero}";
                res.Add(new PropuestaGeneroRegla(t.FilePath, regla.Genero, ReglaGenero.Carpeta, porQue, true));
                continue;
            }

            var clave = ClaveArtista(t.Artist);
            if (clave.Length == 0 || !artistas.TryGetValue(clave, out var d)) continue;
            var (propone, fuerte) = Fuerza(d.Con, d.Total);
            if (!propone) continue;
            var artista = IndiceArtistas.Partir(t.Artist).First();
            res.Add(new PropuestaGeneroRegla(t.FilePath, d.Genero, ReglaGenero.Artista,
                $"Artista «{artista}»: {d.Con} de {d.Total} canciones con {d.Genero}", fuerte));
        }
        return res;
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
