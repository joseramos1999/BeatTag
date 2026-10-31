using System.Globalization;
using System.Text;
using Etiquetador.Core.Ai;

namespace Etiquetador.Core.Dj;

/// <summary>
/// Lo que el planificador de <see cref="OrganizarPorGenero"/> necesita saber del disco. Va aparte para
/// poder probarlo sin archivos (las pruebas usan uno en memoria) y para que la vista previa solo lea.
/// </summary>
public interface IAccesoDisco
{
    /// <summary>
    /// Hay un archivo (o una carpeta) con ese nombre, sin distinguir mayúsculas ni la forma Unicode:
    /// en Windows y en macOS «Tití.mp3» y «TITÍ.mp3» son el mismo nombre.
    /// </summary>
    bool ExisteArchivo(string ruta);

    /// <summary>
    /// El nombre con que está en el disco la subcarpeta <paramref name="nombre"/> de
    /// <paramref name="padre"/>, sin distinguir mayúsculas ni forma Unicode; null si no existe.
    /// </summary>
    string? NombreRealDeCarpeta(string padre, string nombre);

    /// <summary>
    /// La carpeta existe y se puede ver ahora. Una carpeta de la biblioteca en un disco externo
    /// desconectado no existe, aunque la biblioteca conserve sus canciones (ver LibraryStore).
    /// </summary>
    bool ExisteCarpeta(string carpeta);

    /// <summary>
    /// Si hay un ARCHIVO con ese nombre (sin distinguir mayúsculas ni la forma Unicode), su ruta con el
    /// nombre que tiene en el disco; si no, null.
    /// </summary>
    string? ArchivoReal(string ruta);

    /// <summary>
    /// El disco donde está de verdad la ruta: entre dos rutas con la misma unidad, mover es renombrar (ver
    /// <see cref="DiscoReal.UnidadDe"/>, que sigue uniones, enlaces y carpetas donde está montado otro disco).
    /// </summary>
    string UnidadDe(string ruta);
}

/// <summary>
/// El disco de verdad, solo para leer. Recuerda lo que ya ha mirado de cada carpeta: el plan pregunta
/// por miles de archivos de unas pocas carpetas de destino, y se lista cada una una sola vez.
/// Una instancia para cada plan (lo que recuerda es de ese momento) y para un solo hilo.
/// </summary>
public sealed class DiscoReal : IAccesoDisco
{
    private readonly IReadOnlyList<string> _puntos;
    private readonly Func<string, string?> _destinoDeEnlace;
    private readonly Func<string, string?> _volumen;
    // Por carpeta: sus entradas (archivos y carpetas) por clave, con el nombre real. Vacío si la
    // carpeta no existe; null si no se ha podido listar (entonces se pregunta archivo a archivo).
    private readonly Dictionary<string, Dictionary<string, string>?> _entradas = new(ComparadorRutas.Instancia);
    private readonly Dictionary<string, Dictionary<string, string>?> _subcarpetas = new(ComparadorRutas.Instancia);
    private readonly Dictionary<string, string> _unidades = new(ComparadorRutas.Instancia);

    public DiscoReal() : this(Rutas.PuntosDeMontaje()) { }

    /// <param name="puntosDeMontaje">
    /// Los de <see cref="Rutas.PuntosDeMontaje"/>, que se pueden calcular una vez por vista previa y
    /// reutilizar: con una unidad de red caída, preguntar por ellos puede tardar segundos.
    /// </param>
    public DiscoReal(IReadOnlyList<string> puntosDeMontaje) : this(puntosDeMontaje, null, null) { }

    /// <summary>Para las pruebas: cómo se resuelve un enlace y cuál es el volumen de una carpeta.</summary>
    internal DiscoReal(IReadOnlyList<string> puntosDeMontaje, Func<string, string?>? destinoDeEnlace, Func<string, string?>? volumen)
    {
        _puntos = puntosDeMontaje;
        _destinoDeEnlace = destinoDeEnlace ?? Rutas.DestinoDeEnlace;
        _volumen = volumen ?? Rutas.VolumenDe;
    }

    public bool ExisteArchivo(string ruta)
    {
        var dir = Path.GetDirectoryName(ruta);
        if (string.IsNullOrEmpty(dir)) return false;
        var entradas = Listar(_entradas, dir, Directory.EnumerateFileSystemEntries);
        return entradas != null
            ? entradas.ContainsKey(ComparadorRutas.Clave(Path.GetFileName(ruta)))
            : File.Exists(ruta) || Directory.Exists(ruta);
    }

    public string? NombreRealDeCarpeta(string padre, string nombre)
    {
        var subcarpetas = Listar(_subcarpetas, padre, Directory.EnumerateDirectories);
        if (subcarpetas != null)
            return subcarpetas.TryGetValue(ComparadorRutas.Clave(nombre), out var real) ? real : null;
        return Directory.Exists(Path.Combine(padre, nombre)) ? nombre : null;
    }

    public bool ExisteCarpeta(string carpeta)
    {
        if (!_existe.TryGetValue(carpeta, out var si))
        {
            try { si = Directory.Exists(carpeta); }
            catch { si = false; }
            _existe[carpeta] = si;
        }
        return si;
    }

    private readonly Dictionary<string, bool> _existe = new(ComparadorRutas.Instancia);

    public string? ArchivoReal(string ruta)
    {
        var dir = Path.GetDirectoryName(ruta);
        if (string.IsNullOrEmpty(dir)) return null;
        // Solo los de su extensión: se pregunta por los asociados (.asd, .cue, .lrc) de miles de
        // canciones, y listar cada carpeta entera para eso costaba más que el resto del plan.
        var ext = Path.GetExtension(ruta);
        var archivos = Listar(_archivos, dir, d => ext.Length == 0 ? Directory.EnumerateFiles(d) : Directory.EnumerateFiles(d, "*" + ext, PorExtension),
                              clave: dir + "\0" + ext);
        if (archivos != null)
            return archivos.TryGetValue(ComparadorRutas.Clave(Path.GetFileName(ruta)), out var real) ? Path.Combine(dir, real) : null;
        return File.Exists(ruta) ? ruta : null;
    }

    private readonly Dictionary<string, Dictionary<string, string>?> _archivos = new(ComparadorRutas.Instancia);

    // Sin distinguir mayúsculas también en macOS («Tema.mp3.ASD»), y sin saltarse los ocultos, como al listar la carpeta entera.
    private static readonly EnumerationOptions PorExtension = new() { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0 };

    /// <summary>
    /// El disco donde está de verdad la ruta, aunque una carpeta del camino sea una unión, un enlace o
    /// el punto de montaje de otro disco: en Windows, el nombre del volumen de la carpeta existente más
    /// cercana («volumen:\\?\Volume{…}\», ver <see cref="Rutas.VolumenDe"/>); si no se tiene (fuera de
    /// Windows, una carpeta compartida en red), el punto de montaje de la ruta con los enlaces
    /// resueltos. Ver el comentario de unidades en <see cref="Rutas"/>.
    /// </summary>
    public string UnidadDe(string ruta)
    {
        if (_unidades.TryGetValue(ruta, out var ya)) return ya;
        string unidad;
        var existente = ruta;
        while (!string.IsNullOrEmpty(existente) && !ExisteCarpeta(existente)) existente = Path.GetDirectoryName(existente);
        if (!string.IsNullOrEmpty(existente) && _volumen(existente) is { Length: > 0 } volumen)
            unidad = "volumen:" + volumen;
        else
            unidad = Rutas.UnidadDe(Rutas.RutaReal(ruta, _destinoDeEnlace), _puntos);
        _unidades[ruta] = unidad;
        return unidad;
    }

    private static Dictionary<string, string>? Listar(Dictionary<string, Dictionary<string, string>?> cache, string dir,
                                                      Func<string, IEnumerable<string>> enumerar, string? clave = null)
    {
        clave ??= dir;
        if (cache.TryGetValue(clave, out var ya)) return ya;
        Dictionary<string, string>? res;
        try
        {
            res = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Directory.Exists(dir))
                foreach (var e in enumerar(dir))
                {
                    var n = Path.GetFileName(e);
                    res.TryAdd(ComparadorRutas.Clave(n), n);
                }
        }
        catch { res = null; }
        cache[clave] = res;
        return res;
    }
}

/// <summary>Dónde se crean las carpetas de los géneros.</summary>
public enum DestinoOrganizar
{
    /// <summary>En una carpeta «Por género» dentro de cada carpeta de la biblioteca (la opción recomendada).</summary>
    PorGeneroEnCadaRaiz,

    /// <summary>Directamente en cada carpeta de la biblioteca, junto a las carpetas que ya tenga.</summary>
    EnLaRaiz,

    /// <summary>En una carpeta elegida, que tiene que estar dentro de la biblioteca (ver <see cref="OrganizarPorGenero.ValidarDestino"/>).</summary>
    Carpeta,
}

/// <summary>Cómo se organiza. Los valores por defecto son los recomendados.</summary>
public sealed record OpcionesOrganizar
{
    public DestinoOrganizar Destino { get; init; } = DestinoOrganizar.PorGeneroEnCadaRaiz;

    /// <summary>Con <see cref="DestinoOrganizar.Carpeta"/>, la carpeta elegida.</summary>
    public string CarpetaDestino { get; init; } = "";

    /// <summary>Dentro de cada género, las subcarpetas de origen («Reggaeton/Pack Enero/…»).</summary>
    public bool ConservarEstructura { get; init; }

    /// <summary>Las canciones sin género van a «Sin género». Sin marcar, se quedan donde están.</summary>
    public bool MoverSinGenero { get; init; }

    /// <summary>Los mashups, las transiciones, las acapellas y los instrumentales se quedan donde están.</summary>
    public bool DejarHerramientas { get; init; } = true;

    /// <summary>
    /// Los géneros con menos canciones que esto van a «Otros géneros». Cuentan solo las canciones que
    /// se organizan (también las que ya están en la carpeta de su género), no las que se quedan donde
    /// están, tampoco las que se quedarían por su destino (otra unidad, una ruta demasiado larga, un
    /// nombre ocupado…). 0, desactivado; al activarlo, <see cref="OrganizarPorGenero.AgruparPorDefecto"/>.
    /// </summary>
    public int AgruparMenosDe { get; init; }

    /// <summary>
    /// Carpetas con ajustes propios (las de Duplicados y las excluidas del ajuste de Volumen): sus
    /// canciones, también las de sus subcarpetas, se quedan donde están.
    /// </summary>
    public IReadOnlyCollection<string> CarpetasConAjustes { get; init; } = Array.Empty<string>();

    /// <summary>Carpetas de origen desmarcadas en la vista previa: sus canciones (solo las de esa carpeta) se quedan.</summary>
    public IReadOnlyCollection<string> OrigenesExcluidos { get; init; } = Array.Empty<string>();
}

/// <summary>Por qué una canción no se mueve.</summary>
public enum MotivoOmision
{
    SinGenero,
    YaEnSuSitio,
    /// <summary>Mashup, transición, acapella o instrumental, con «Dejar en su sitio…» marcado.</summary>
    HerramientaDj,
    /// <summary>Está en una carpeta con ajustes propios de Duplicados o de Volumen.</summary>
    CarpetaConAjustes,
    /// <summary>Iría a parar dentro de una carpeta con ajustes propios de Duplicados o de Volumen, y ganaría esos ajustes.</summary>
    DestinoConAjustes,
    /// <summary>Su carpeta de origen está desmarcada en la vista previa.</summary>
    OrigenDesmarcado,
    /// <summary>Su carpeta no existe ahora (un disco externo desconectado, una unidad de red caída).</summary>
    NoDisponible,
    OtraUnidad,
    /// <summary>La ruta de destino tendría <see cref="OrganizarPorGenero.LargoMaximoRuta"/> caracteres o más.</summary>
    RutaLarga,
    /// <summary>Un tramo de la ruta de destino pasaría de <see cref="OrganizarPorGenero.BytesMaximoTramo"/> bytes.</summary>
    TramoLargo,
    /// <summary>En el destino ya hay un archivo con ese nombre, o otra canción del plan va al mismo sitio.</summary>
    Colision,
    /// <summary>Moverla cambiaría lo que BeatTag deduce de su carpeta: dejaría de contar como mashup, acapella, instrumental o edición de DJ, o pasaría a contar como tal.</summary>
    TipoPorCarpeta,
}

/// <summary>Una canción que se mueve.</summary>
/// <param name="Genero">El género con que se ha decidido, escrito como en la biblioteca («» si va a «Sin género»).</param>
/// <param name="CarpetaGenero">La carpeta del género tal como queda en el disco («Reggaeton», «Otros géneros»…).</param>
public sealed record MovimientoOrganizar(string Origen, string Destino, string Genero, string CarpetaGenero)
{
    /// <summary>
    /// Los archivos que acompañan a la canción por su nombre y van con ella a la misma carpeta, con el
    /// nombre que tienen en el disco: «Tema.mp3.asd» (Ableton Live), «Tema.cue» y «Tema.lrc». Ver
    /// <see cref="OrganizarPorGenero.ExtensionesAsociadas"/>.
    /// </summary>
    public IReadOnlyList<string> Asociados { get; init; } = Array.Empty<string>();

    /// <summary>Adónde va cada asociado: a la carpeta de la canción, con su mismo nombre.</summary>
    public string DestinoDe(string asociado) => Path.Combine(Path.GetDirectoryName(Destino) ?? "", Path.GetFileName(asociado));
}

/// <summary>Una canción que no se mueve, con el motivo.</summary>
public sealed record OmisionOrganizar(string Ruta, MotivoOmision Motivo, string Texto)
{
    public string Archivo => Path.GetFileName(Ruta);
    public string Carpeta => Path.GetDirectoryName(Ruta) ?? "";
}

/// <summary>Una carpeta de destino.</summary>
/// <param name="Carpeta">La ruta completa.</param>
/// <param name="Nombre">Desde la carpeta de destino («Reggaeton», «Reggaeton/Pack Enero»).</param>
/// <param name="YaExiste">Ya está en el disco (no se crea).</param>
public sealed record CarpetaDestinoPlan(string Carpeta, string Nombre, int Canciones, bool YaExiste)
{
    public bool Nueva => !YaExiste;
}

/// <summary>Una carpeta de origen y cómo se reparten sus canciones.</summary>
/// <param name="Total">Canciones de la carpeta (sin contar las subcarpetas, que son origen aparte).</param>
/// <param name="Reparto">Los tres destinos con más canciones: «301 a Reggaeton · 96 a Latin · 78 a Hip Hop».</param>
/// <param name="Excluida">Desmarcada en la vista previa.</param>
public sealed record CarpetaOrigenPlan(string Carpeta, int Total, int SeMueven, int SeQuedan, string Reparto, bool Excluida);

/// <summary>Lo que haría Organizar por género, sin haber tocado nada.</summary>
public sealed class PlanOrganizar
{
    public static readonly PlanOrganizar Vacio = new();

    public IReadOnlyList<MovimientoOrganizar> Movimientos { get; init; } = Array.Empty<MovimientoOrganizar>();
    public IReadOnlyList<OmisionOrganizar> Omitidas { get; init; } = Array.Empty<OmisionOrganizar>();

    /// <summary>Las carpetas de destino, con cuántas canciones llegan a cada una.</summary>
    public IReadOnlyList<CarpetaDestinoPlan> PorDestino { get; init; } = Array.Empty<CarpetaDestinoPlan>();

    public IReadOnlyList<CarpetaOrigenPlan> PorOrigen { get; init; } = Array.Empty<CarpetaOrigenPlan>();

    /// <summary>De media, entre cuántas carpetas de género se reparte cada carpeta de origen que mueve algo.</summary>
    public double MediaGenerosPorOrigen { get; init; }

    /// <summary>Las canciones de la biblioteca que se han mirado (las de las carpetas marcadas).</summary>
    public int Canciones { get; init; }

    public int Carpetas => PorDestino.Count;
    public int CarpetasNuevas => PorDestino.Count(d => d.Nueva);

    /// <summary>Cuántos archivos asociados (.asd, .cue, .lrc) se mueven con sus canciones.</summary>
    public int Asociados => Movimientos.Sum(m => m.Asociados.Count);

    /// <summary>Cuántas se omiten por ese motivo.</summary>
    public int CuantasOmitidas(MotivoOmision motivo) => Omitidas.Count(o => o.Motivo == motivo);
}

/// <summary>
/// «Organizar por género»: mueve cada canción a una carpeta con el nombre de su género, sin tocar las
/// etiquetas. Aquí está SOLO el plan, que no toca nada: lo ejecuta OrganizadorPorGenero (en la
/// aplicación), con su historial para deshacer.
///
/// QUÉ SE DECIDIÓ Y POR QUÉ. MEDIDO en una biblioteca real de 13.352 canciones (una raíz con 42
/// carpetas planas):
///   · Por defecto, Género/archivo dentro de una carpeta «Por género» de cada carpeta de la
///     biblioteca: 10.896 traslados (91,1 GB), todos renombrados dentro de la misma unidad, a 181
///     carpetas, sin ninguna ruta de 260 caracteres. Directamente en la raíz se mezclarían con las 5
///     carpetas que ya se llaman como un género; Género/Origen daba 975 carpetas y solo 6 caracteres de
///     margen de ruta; Género/Artista, un 68 % de carpetas con una sola canción.
///   · Las canciones sin género se quedan: son el 18 % y moverlas a «Sin género» vaciaría las carpetas
///     de origen sin poner orden. Es una opción.
///   · Un archivo nunca se sobrescribe ni se renombra: en 63 de los 65 grupos de nombres repetidos los
///     archivos tenían tamaños distintos (eran versiones distintas), y conservar el nombre es lo que
///     permite a rekordbox, Serato, Traktor y Engine reubicar el archivo.
///   · Los mashups, las transiciones, las acapellas y los instrumentales se quedan por defecto: un
///     mashup son dos temas, a menudo de dos estilos, y su género es el que menos se puede deducir. Los
///     DJ los guardan por packs.
///   · Las carpetas con ajustes propios (Duplicados, Volumen) se quedan: fuera de ellas, la canción
///     perdería la exclusión o la prioridad que el DJ le dio a esa carpeta. Y nada entra en ellas, que
///     la ganaría (revisión 1).
///   · Una pieza que cambiaría de tipo al moverla (una acapella que lo es solo por estar en
///     «Acapellas», o lo que entraría en una carpeta «Instrumental») se queda: ver LoQueDiceLaRuta.
///   · La etiqueta solo coincidía con el género que nombra su carpeta en el 37 % de las canciones, y
///     cada carpeta de origen se repartiría de media entre 24 géneros: por eso hay vista previa y se
///     puede desmarcar cada carpeta de origen.
/// </summary>
public static class OrganizarPorGenero
{
    /// <summary>La carpeta que se crea dentro de cada carpeta de la biblioteca con la opción por defecto.</summary>
    public const string CarpetaPorGenero = "Por género";

    /// <summary>Adonde van los géneros pequeños si se agrupan.</summary>
    public const string CarpetaOtros = "Otros géneros";

    /// <summary>Adonde van las canciones sin género si se mueven.</summary>
    public const string CarpetaSinGenero = "Sin género";

    /// <summary>La carpeta de un género cuyo nombre se queda vacío al quitarle lo que no vale en un nombre.</summary>
    public const string GeneroSinNombre = "Género sin nombre";

    /// <summary>
    /// Una ruta de destino con estos caracteres o más se omite. Es el límite clásico de Windows
    /// (MAX_PATH), que siguen teniendo muchos programas, también de DJ, aunque el sistema admita más; la
    /// misma biblioteca en un disco que se lleva de un equipo a otro tiene que seguir abriéndose.
    /// </summary>
    public const int LargoMaximoRuta = 260;

    /// <summary>Cada tramo de la ruta, como mucho estos bytes en UTF-8: el límite de APFS y de ext4 (NTFS admite 255 caracteres, que es más).</summary>
    public const int BytesMaximoTramo = 255;

    /// <summary>
    /// Los archivos que acompañan a una canción por su nombre y se mueven con ella (revisión 1: antes se
    /// quedaban en la carpeta de origen, separados de su canción):
    ///   · «Tema.mp3.asd»: el análisis de Ableton Live, con los marcadores de warp hechos a mano; lleva
    ///     el nombre ENTERO del audio, así que es siempre de esa canción.
    ///   · «Tema.cue» y «Tema.lrc»: una hoja de pistas y una letra sincronizada; llevan el nombre sin la
    ///     extensión, así que solo se mueven si ninguna otra canción de la carpeta se llama igual («Tema.mp3»
    ///     y «Tema.flac»): si no, no se sabe de cuál son y se quedan.
    /// </summary>
    public static readonly IReadOnlyList<string> ExtensionesAsociadas = new[] { ".asd", ".cue", ".lrc" };

    /// <summary>Con «Agrupar en Otros géneros» recién marcado, los géneros de menos de estas canciones (una tendencia, como en GeneroPorReglas).</summary>
    public const int AgruparPorDefecto = 3;

    // --- El nombre de la carpeta de un género ---

    private static readonly char[] Prohibidos = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

    // Nombres de dispositivo de Windows: una carpeta «CON» (o «CON.algo») no se puede crear.
    private static readonly HashSet<string> Reservados = new(
        new[] { "CON", "PRN", "AUX", "NUL" }
            .Concat(Enumerable.Range(1, 9).Select(i => "COM" + i))
            .Concat(Enumerable.Range(1, 9).Select(i => "LPT" + i)),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// El nombre de la carpeta de un género, válido en Windows, macOS y Linux: los caracteres que no
    /// admite un nombre (/ \ : * ? " &lt; &gt; |) pasan a «-» y los de control se quitan; fuera los
    /// espacios y puntos de los extremos (un punto al principio la ocultaría en macOS, y Windows quita
    /// el del final); un nombre reservado de Windows («CON», «LPT1») lleva « (género)» detrás; en
    /// forma NFC y con <see cref="BytesMaximoTramo"/> bytes como mucho, cortando entre caracteres. Si
    /// no queda nada, <see cref="GeneroSinNombre"/>.
    /// </summary>
    public static string NombreCarpetaGenero(string? genero)
    {
        var texto = genero ?? "";
        if (!texto.IsNormalized(NormalizationForm.FormC)) texto = texto.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(texto.Length);
        foreach (var c in texto)
        {
            if (Array.IndexOf(Prohibidos, c) >= 0) sb.Append('-');
            else if (!char.IsControl(c)) sb.Append(c);
        }
        var s = SinExtremos(sb.ToString());

        var punto = s.IndexOf('.');
        var nucleo = (punto < 0 ? s : s[..punto]).TrimEnd();
        if (Reservados.Contains(nucleo)) s = nucleo + " (género)" + (punto < 0 ? "" : s[punto..]);

        s = SinExtremos(Recortar(s, BytesMaximoTramo));
        return s.Length == 0 ? GeneroSinNombre : s;
    }

    /// <summary>Sin espacios ni puntos en los extremos, aunque se alternen («. Trance .»).</summary>
    private static string SinExtremos(string s)
    {
        string antes;
        do
        {
            antes = s;
            s = s.Trim().Trim('.');
        } while (s != antes);
        return s;
    }

    /// <summary>Como mucho <paramref name="bytes"/> bytes en UTF-8, sin partir un carácter (ni un emoji con su modificador).</summary>
    private static string Recortar(string s, int bytes)
    {
        if (Encoding.UTF8.GetByteCount(s) <= bytes) return s;
        var sb = new StringBuilder();
        var usados = 0;
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            var el = e.GetTextElement();
            var n = Encoding.UTF8.GetByteCount(el);
            if (usados + n > bytes) break;
            sb.Append(el);
            usados += n;
        }
        return sb.ToString();
    }

    // --- La carpeta de destino ---

    /// <summary>
    /// Por qué no vale <paramref name="carpeta"/> como destino, o "" si vale. Es el revés de
    /// <see cref="RevisionBandeja.ValidarCarpeta"/>: aquí la carpeta tiene que ser de la biblioteca
    /// (una de sus carpetas o estar dentro de una), porque si no las canciones saldrían de ella; y no
    /// puede estar dentro de una carpeta desmarcada, donde dejarían de verse en todas las pestañas, ni
    /// dentro de una carpeta con ajustes propios (<paramref name="carpetasConAjustes"/>), donde las
    /// canciones ganarían la exclusión o la prioridad de Duplicados o de Volumen sin que nadie la pida.
    /// </summary>
    public static string ValidarDestino(string carpeta, IEnumerable<string> raicesHabilitadas, IEnumerable<string> carpetasDesmarcadas,
                                        IEnumerable<string>? carpetasConAjustes = null)
    {
        if (string.IsNullOrWhiteSpace(carpeta)) return "Elige la carpeta de destino.";
        foreach (var d in carpetasDesmarcadas)
            if (Rutas.MismaCarpeta(carpeta, d) || Rutas.EstaDentroDe(carpeta, d))
                return $"Esa carpeta está dentro de «{GeneroPorReglas.NombreCarpeta(d)}», que está desmarcada en la biblioteca: "
                     + "las canciones que se movieran ahí dejarían de verse en todas las pestañas.";
        if (!raicesHabilitadas.Any(r => Rutas.MismaCarpeta(carpeta, r) || Rutas.EstaDentroDe(carpeta, r)))
            return "La carpeta de destino tiene que ser una carpeta de la biblioteca o estar dentro de una: si no, las canciones saldrían de la biblioteca.";
        foreach (var c in carpetasConAjustes ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(c) && (Rutas.MismaCarpeta(carpeta, c) || Rutas.EstaDentroDe(carpeta, c)))
                return $"Esa carpeta está dentro de «{GeneroPorReglas.NombreCarpeta(c)}», que tiene ajustes propios en Duplicados o en Volumen: "
                     + "las canciones que se movieran ahí ganarían esos ajustes.";
        return "";
    }

    // --- El plan ---

    /// <summary>
    /// Qué se movería y adónde, y qué no y por qué. Solo LEE (el disco, a través de
    /// <paramref name="disco"/>). Recorre la biblioteca una vez: hay que llamarlo fuera del hilo de la
    /// interfaz, y se puede cancelar.
    ///
    /// El género de cada canción es el de <see cref="GeneroPorReglas.Normalizado"/> (con una lista, el
    /// primero), escrito como lo escribe la mayoría de la biblioteca: «Reggaeton», «Reggaetón» y
    /// «Reguetón» acaban en una sola carpeta. Las canciones se recorren ordenadas por ruta para que el
    /// plan sea siempre el mismo: si dos van al mismo destino, se mueve la primera y la otra se lista.
    /// </summary>
    /// <param name="canciones">La biblioteca (las canciones de las carpetas marcadas).</param>
    /// <param name="raicesHabilitadas">Las carpetas marcadas de la biblioteca.</param>
    public static PlanOrganizar Planear(IReadOnlyList<Track> canciones, IReadOnlyList<string> raicesHabilitadas,
                                        OpcionesOrganizar op, IAccesoDisco disco, CancellationToken ct = default)
    {
        if (op.Destino == DestinoOrganizar.Carpeta && string.IsNullOrWhiteSpace(op.CarpetaDestino))
            throw new ArgumentException("Falta la carpeta de destino.", nameof(op));

        // El género de cada canción, escrito como la mayoría, y cuántas canciones tiene cada uno.
        var generos = GeneroPorReglas.GenerosDeLaBiblioteca(canciones);
        var forma = new Dictionary<string, (string Genero, int Canciones)>(StringComparer.Ordinal);
        foreach (var g in generos) forma[GeneroPorReglas.ClaveGenero(g.Genero)] = g;
        var normalizados = new Dictionary<string, string?>(StringComparer.Ordinal);
        (string Genero, int Canciones)? GeneroDe(Track t)
        {
            if (string.IsNullOrWhiteSpace(t.Genre)) return null;
            if (!normalizados.TryGetValue(t.Genre, out var n)) normalizados[t.Genre] = n = GeneroPorReglas.Normalizado(t.Genre);
            if (n == null) return null;
            var k = GeneroPorReglas.ClaveGenero(n);
            return k.Length > 0 && forma.TryGetValue(k, out var f) ? f : null;
        }

        // Las carpetas que son de un género: una canción que ya está en una de ellas, dentro del destino,
        // cuenta desde ahí (volver a organizar no anida «Pop/Pop»). Son «Otros géneros», «Sin género», las
        // que se llaman como un género de la biblioteca y las que lo escriben de otra forma, contada igual
        // que al agrupar los géneros: una carpeta «Breaks», «Rap» o «Electro-Latino» es la de Breakbeat,
        // Hip Hop o Electro Latino, y no tiene que acabar dentro de la nueva («Breakbeat/Breaks»).
        var nombresDeGenero = new HashSet<string>(ComparadorRutas.Instancia) { CarpetaOtros, CarpetaSinGenero };
        foreach (var g in generos) nombresDeGenero.Add(NombreCarpetaGenero(g.Genero));
        var esCarpetaDeGenero = new Dictionary<string, bool>(ComparadorRutas.Instancia);
        bool EsCarpetaDeGenero(string nombre)
        {
            if (!esCarpetaDeGenero.TryGetValue(nombre, out var es))
                esCarpetaDeGenero[nombre] = es = nombresDeGenero.Contains(nombre) || OtraFormaDeUnGenero(nombre);
            return es;
        }
        // Una lista no es otra forma de escribir su primer género: «Salsa, Vol. 1» o «Reggaeton, Dembow» son
        // carpetas tuyas. Tampoco un nombre con letras o símbolos que la clave no conserva («邦楽 Pop», «Pop
        // 🇯🇵», «🔥 Reggaeton»): fuera de lo ASCII solo se admiten espacios y signos de puntuación
        // («Electro–Latino»). Ni una carpeta que por su nombre marca el tipo de pieza («Mash Up» con el
        // género «Mash-Up»): quitarla de la ruta cambiaría lo que es la canción, que se quedaría en su sitio
        // en vez de moverse con su carpeta.
        var neutra = LoQueDiceLaRuta.De("x");
        bool OtraFormaDeUnGenero(string nombre)
            => !GenerosIa.EsLista(nombre)
               && TextUtils.RemoveDiacritics(nombre).All(c => char.IsAscii(c) || EsEspacioOPuntuacion(c))
               && LoQueDiceLaRuta.De(Path.Combine(nombre, "x")) == neutra
               && GeneroPorReglas.Normalizado(nombre) is { } n && forma.ContainsKey(GeneroPorReglas.ClaveGenero(n));

        var raices = raicesHabilitadas.Where(r => !string.IsNullOrWhiteSpace(r)).OrderByDescending(r => r.Length).ToList();
        var raizDe = new Dictionary<string, string?>(ComparadorRutas.Instancia);
        // Las carpetas con ajustes propios protegen en los dos sentidos: lo que hay dentro no sale (perdería
        // la exclusión de Duplicados o de Volumen, o la prioridad) y lo de fuera no entra (la ganaría).
        var conAjustes = new Dictionary<string, bool>(ComparadorRutas.Instancia);
        var carpetasConAjustes = op.CarpetasConAjustes.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        bool ConAjustes(string carpeta)
        {
            if (!conAjustes.TryGetValue(carpeta, out var si))
                conAjustes[carpeta] = si = carpetasConAjustes.Any(c => Rutas.MismaCarpeta(carpeta, c) || Rutas.EstaDentroDe(carpeta, c));
            return si;
        }
        var excluidos = new HashSet<string>(op.OrigenesExcluidos.Where(c => !string.IsNullOrWhiteSpace(c))
                                                                .Select(c => c.TrimEnd('\\', '/')), ComparadorRutas.Instancia);
        var unidades = new Dictionary<string, string>(ComparadorRutas.Instancia);
        string Unidad(string ruta)
        {
            if (!unidades.TryGetValue(ruta, out var u)) unidades[ruta] = u = disco.UnidadDe(ruta);
            return u;
        }

        // El nombre de cada carpeta como está en el disco o, si aún no existe, como lo escribió la
        // primera canción que la pidió: dos grafías del mismo nombre no pueden ser dos carpetas.
        var nombres = new Dictionary<string, string>(StringComparer.Ordinal);
        string Real(string padre, string nombre)
        {
            var k = ComparadorRutas.Clave(padre).TrimEnd('\\', '/') + "\0" + ComparadorRutas.Clave(nombre);
            if (!nombres.TryGetValue(k, out var real)) nombres[k] = real = disco.NombreRealDeCarpeta(padre, nombre) ?? nombre;
            return real;
        }

        // Lo que la carpeta dice de una pieza, mirado con un nombre neutro, una vez por carpeta: si
        // coincide en el origen y en el destino, moverla no cambia nada (ver LoQueDiceLaRuta).
        var firmas = new Dictionary<string, LoQueDiceLaRuta>(ComparadorRutas.Instancia);
        LoQueDiceLaRuta Firma(string carpeta)
        {
            if (!firmas.TryGetValue(carpeta, out var f)) firmas[carpeta] = f = LoQueDiceLaRuta.De(Path.Combine(carpeta, "x"));
            return f;
        }
        // Mirar una pieza es leer su nombre: se guarda, porque la segunda pasada puede repetirse (ver más abajo).
        var cambios = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? CambioPorCarpeta(string origen, string destino, string nombreDestino)
        {
            if (Firma(Path.GetDirectoryName(origen) ?? "") == Firma(Path.GetDirectoryName(destino) ?? "")) return null;
            var clave = origen + "\0" + destino;
            if (cambios.TryGetValue(clave, out var ya)) return ya;
            var antes = LoQueDiceLaRuta.De(origen);
            var despues = LoQueDiceLaRuta.De(destino);
            var cambio = antes == despues ? null   // lo dice ya su nombre, esté donde esté
                       : antes.LoQueNoDice(despues) is { } pierde ? $"Su carpeta la marca como {pierde}: fuera de ella dejaría de contar como tal"
                       : despues.LoQueNoDice(antes) is { } gana ? $"En «{nombreDestino}» pasaría a contar como {gana}"
                       : TextoMotivo(MotivoOmision.TipoPorCarpeta);
            cambios[clave] = cambio;
            return cambio;
        }

        string Base(string raiz) => op.Destino switch
        {
            DestinoOrganizar.PorGeneroEnCadaRaiz => Path.Combine(raiz, Real(raiz, CarpetaPorGenero)),
            DestinoOrganizar.EnLaRaiz => raiz,
            _ => op.CarpetaDestino,
        };

        var omitidas = new List<OmisionOrganizar>();
        var totales = new Dictionary<string, int>(ComparadorRutas.Instancia);   // canciones de cada carpeta de origen
        var miradas = 0;

        // PRIMERA PASADA: qué canciones se pueden organizar (las que no se quedan por lo que son o por
        // dónde están) y cuántas hay de cada género. Es el máximo que puede tener cada género para
        // «Agrupar en Otros géneros»: la segunda pasada solo puede dejar fuera más.
        var candidatas = new List<(Track T, string Dir, string Raiz, (string Genero, int Canciones)? Genero)>();
        var porGenero = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordenadas = canciones.OrderBy(t => t.FilePath, StringComparer.Ordinal).ToList();

        // Los archivos asociados (ver ExtensionesAsociadas): «Tema.cue» y «Tema.lrc» solo son de
        // «Tema.mp3» si ninguna otra canción de su carpeta se llama «Tema».
        var mismoNombre = new Dictionary<string, int>(StringComparer.Ordinal);
        string ClaveNombre(string dir, string ruta) => ComparadorRutas.Clave(dir) + "\0" + ComparadorRutas.Clave(Path.GetFileNameWithoutExtension(ruta));
        foreach (var t in ordenadas)
        {
            var k = ClaveNombre(Path.GetDirectoryName(t.FilePath) ?? "", t.FilePath);
            mismoNombre[k] = mismoNombre.GetValueOrDefault(k) + 1;
        }
        var asociadosDe = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string> Asociados(string ruta, string dir)
        {
            if (asociadosDe.TryGetValue(ruta, out var ya)) return ya;
            var res = new List<string>();
            asociadosDe[ruta] = res;
            if (disco.ArchivoReal(ruta + ".asd") is { } asd) res.Add(asd);
            if (mismoNombre.GetValueOrDefault(ClaveNombre(dir, ruta)) > 1) return res;
            var sinExtension = Path.Combine(dir, Path.GetFileNameWithoutExtension(ruta));
            foreach (var ext in new[] { ".cue", ".lrc" })
                if (disco.ArchivoReal(sinExtension + ext) is { } a) res.Add(a);
            return res;
        }
        for (var i = 0; i < ordenadas.Count; i++)
        {
            if (i % 256 == 0) ct.ThrowIfCancellationRequested();
            var t = ordenadas[i];
            var dir = Path.GetDirectoryName(t.FilePath) ?? "";
            if (dir.Length == 0) continue;
            if (!raizDe.TryGetValue(dir, out var raiz))
                raizDe[dir] = raiz = raices.FirstOrDefault(r => Rutas.MismaCarpeta(dir, r) || Rutas.EstaDentroDe(dir, r));
            if (raiz == null) continue;   // fuera de las carpetas marcadas: no es de la biblioteca

            miradas++;
            totales[dir] = totales.GetValueOrDefault(dir) + 1;

            void Omitir(MotivoOmision m, string texto) => omitidas.Add(new OmisionOrganizar(t.FilePath, m, texto));

            // Un disco desconectado no ha borrado su música: la biblioteca conserva sus canciones, pero
            // no se pueden mover, y contarlas en la vista previa solo daría fallos al organizar.
            if (!disco.ExisteCarpeta(dir)) { Omitir(MotivoOmision.NoDisponible, TextoMotivo(MotivoOmision.NoDisponible)); continue; }
            if (ConAjustes(dir)) { Omitir(MotivoOmision.CarpetaConAjustes, TextoMotivo(MotivoOmision.CarpetaConAjustes)); continue; }
            if (excluidos.Contains(dir.TrimEnd('\\', '/'))) { Omitir(MotivoOmision.OrigenDesmarcado, TextoMotivo(MotivoOmision.OrigenDesmarcado)); continue; }

            var genero = GeneroDe(t);
            if (genero == null && !op.MoverSinGenero) { Omitir(MotivoOmision.SinGenero, TextoMotivo(MotivoOmision.SinGenero)); continue; }
            if (op.DejarHerramientas && Herramienta(t.FilePath) is { } herramienta)
            {
                Omitir(MotivoOmision.HerramientaDj, herramienta);
                continue;
            }

            candidatas.Add((t, dir, raiz, genero));
            if (genero is { } g) porGenero[g.Genero] = porGenero.GetValueOrDefault(g.Genero) + 1;
        }

        // SEGUNDA PASADA: adónde va cada una, en el mismo orden. Aquí también se quedan algunas, por su
        // destino: otra unidad, una pieza que cambiaría de tipo, una ruta larga, una colisión… «Agrupar en
        // Otros géneros» cuenta solo las que se organizan: las que se mueven y las que ya están en la
        // carpeta de su género (así volver a organizar decide igual). Un género con 4 intros que se
        // quedan y 1 canción que se mueve tiene 1, no 5 (revisiones 1 y 2). Como eso solo se sabe al
        // terminar la pasada, el género que se queda por debajo del mínimo pasa a «Otros géneros» y se
        // vuelve a pasar. Los géneros que van a «Otros» solo aumentan, así que termina; casi siempre
        // basta una vuelta más, y lo que se mira del disco ya está guardado de la primera.
        Pasada SegundaPasada(HashSet<string> haciaOtros)
        {
            var p = new Pasada();
            var ocupados = new HashSet<string>(ComparadorRutas.Instancia);
            for (var i = 0; i < candidatas.Count; i++)
            {
                if (i % 256 == 0) ct.ThrowIfCancellationRequested();
                var (t, dir, raiz, genero) = candidatas[i];

                void Omitir(MotivoOmision m, string texto) => p.Omitidas.Add(new OmisionOrganizar(t.FilePath, m, texto));
                void Organizada() { if (genero is { } g) p.Organizadas[g.Genero] = p.Organizadas.GetValueOrDefault(g.Genero) + 1; }

                var nombreGenero = genero == null ? CarpetaSinGenero
                                 : haciaOtros.Contains(genero.Value.Genero) ? CarpetaOtros
                                 : NombreCarpetaGenero(genero.Value.Genero);
                var baseDir = Base(raiz);
                var carpetaGenero = Real(baseDir, nombreGenero);
                var relativa = op.ConservarEstructura
                    ? Relativa(dir, raiz, baseDir, op.Destino == DestinoOrganizar.PorGeneroEnCadaRaiz ? null : EsCarpetaDeGenero)
                    : "";
                var destinoDir = Path.Combine(baseDir, carpetaGenero);
                foreach (var tramo in Tramos(relativa)) destinoDir = Path.Combine(destinoDir, Real(destinoDir, tramo));
                var destino = Path.Combine(destinoDir, Path.GetFileName(t.FilePath));
                var nombreDestino = relativa.Length == 0 ? carpetaGenero : Path.Combine(carpetaGenero, relativa);

                if (ComparadorRutas.Instancia.Equals(destino, t.FilePath))
                {
                    Omitir(MotivoOmision.YaEnSuSitio, TextoMotivo(MotivoOmision.YaEnSuSitio));
                    Organizada();
                    continue;
                }
                // La unidad de la carpeta a la que va de verdad, no la de la base (revisión 3): una carpeta de
                // género que ya existe, o una subcarpeta conservada, puede ser una unión, un enlace o el punto
                // de montaje de otro disco. Si aún no existe, UnidadDe sube hasta la carpeta existente más
                // cercana, que es lo mismo que mirar la base.
                if (!ComparadorRutas.Instancia.Equals(Unidad(dir), Unidad(destinoDir))) { Omitir(MotivoOmision.OtraUnidad, TextoMotivo(MotivoOmision.OtraUnidad)); continue; }
                if (ConAjustes(destinoDir)) { Omitir(MotivoOmision.DestinoConAjustes, TextoDestinoConAjustes(nombreDestino)); continue; }
                if (CambioPorCarpeta(t.FilePath, destino, nombreDestino) is { } cambio) { Omitir(MotivoOmision.TipoPorCarpeta, cambio); continue; }
                // Los archivos que la acompañan por su nombre van con ella: valen para ellos los mismos límites
                // y tampoco pueden pisar nada.
                var asociados = Asociados(t.FilePath, dir);
                var destinosAsociados = asociados.Select(a => Path.Combine(destinoDir, Path.GetFileName(a))).ToList();
                if (destino.Length >= LargoMaximoRuta || destinosAsociados.Any(a => a.Length >= LargoMaximoRuta))
                {
                    Omitir(MotivoOmision.RutaLarga, TextoMotivo(MotivoOmision.RutaLarga));
                    continue;
                }
                if (Tramos(destino).Any(s => Encoding.UTF8.GetByteCount(s) > BytesMaximoTramo)
                    || destinosAsociados.Any(a => Encoding.UTF8.GetByteCount(Path.GetFileName(a)) > BytesMaximoTramo))
                {
                    Omitir(MotivoOmision.TramoLargo, TextoMotivo(MotivoOmision.TramoLargo));
                    continue;
                }
                if (ocupados.Contains(destino) || disco.ExisteArchivo(destino))
                {
                    Omitir(MotivoOmision.Colision, TextoColision(nombreDestino));
                    continue;
                }
                if (destinosAsociados.FirstOrDefault(a => ocupados.Contains(a) || disco.ExisteArchivo(a)) is { } ocupado)
                {
                    Omitir(MotivoOmision.Colision, TextoColisionAsociado(Path.GetFileName(ocupado), nombreDestino));
                    continue;
                }

                ocupados.Add(destino);
                ocupados.UnionWith(destinosAsociados);
                p.Movimientos.Add(new MovimientoOrganizar(t.FilePath, destino, genero?.Genero ?? "", carpetaGenero) { Asociados = asociados });
                Organizada();
                p.Destinos[destinoDir] = p.Destinos.TryGetValue(destinoDir, out var d) ? (d.Nombre, d.Canciones + 1) : (nombreDestino, 1);
                if (!p.Repartos.TryGetValue(dir, out var reparto)) p.Repartos[dir] = reparto = new Dictionary<string, int>(StringComparer.Ordinal);
                reparto[carpetaGenero] = reparto.GetValueOrDefault(carpetaGenero) + 1;
            }
            return p;
        }

        var aOtros = new HashSet<string>(StringComparer.Ordinal);
        if (op.AgruparMenosDe > 0) aOtros.UnionWith(porGenero.Where(g => g.Value < op.AgruparMenosDe).Select(g => g.Key));
        var pasada = SegundaPasada(aOtros);
        while (op.AgruparMenosDe > 0)
        {
            var pocos = porGenero.Keys.Where(g => !aOtros.Contains(g) && pasada.Organizadas.GetValueOrDefault(g) < op.AgruparMenosDe).ToList();
            if (pocos.Count == 0) break;
            aOtros.UnionWith(pocos);
            pasada = SegundaPasada(aOtros);
        }
        omitidas.AddRange(pasada.Omitidas);

        var porDestino = pasada.Destinos
            .Select(kv => new CarpetaDestinoPlan(kv.Key, kv.Value.Nombre, kv.Value.Canciones, ExisteCarpeta(disco, kv.Key)))
            .OrderBy(c => c.Nombre, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Carpeta, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var sinReparto = new Dictionary<string, int>(StringComparer.Ordinal);
        var porOrigen = totales
            .Select(kv =>
            {
                var repartoDe = pasada.Repartos.GetValueOrDefault(kv.Key) ?? sinReparto;
                var mueven = repartoDe.Values.Sum();
                var reparto = string.Join(" · ", repartoDe.OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.CurrentCultureIgnoreCase)
                                                          .Take(3).Select(r => $"{N(r.Value)} a {r.Key}"));
                return new CarpetaOrigenPlan(kv.Key, kv.Value, mueven, kv.Value - mueven, reparto, excluidos.Contains(kv.Key.TrimEnd('\\', '/')));
            })
            .OrderBy(c => c.Carpeta, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new PlanOrganizar
        {
            Movimientos = pasada.Movimientos,
            Omitidas = omitidas.OrderBy(o => o.Ruta, StringComparer.Ordinal).ToList(),   // en el orden de la biblioteca, como antes de las dos pasadas
            PorDestino = porDestino,
            PorOrigen = porOrigen,
            MediaGenerosPorOrigen = pasada.Repartos.Count == 0 ? 0 : pasada.Repartos.Values.Average(r => r.Count),
            Canciones = miradas,
        };
    }

    /// <summary>Lo que da una vuelta de la segunda pasada de <see cref="Planear"/>.</summary>
    private sealed class Pasada
    {
        public List<MovimientoOrganizar> Movimientos { get; } = new();

        /// <summary>Las que se quedan por su destino (las de la primera pasada van aparte).</summary>
        public List<OmisionOrganizar> Omitidas { get; } = new();

        /// <summary>Por carpeta de destino, su nombre desde el destino y cuántas canciones llegan.</summary>
        public Dictionary<string, (string Nombre, int Canciones)> Destinos { get; } = new(ComparadorRutas.Instancia);

        /// <summary>Por carpeta de origen, cuántas de sus canciones van a cada carpeta de género (solo las que mueven algo).</summary>
        public Dictionary<string, Dictionary<string, int>> Repartos { get; } = new(ComparadorRutas.Instancia);

        /// <summary>Por género, las canciones que se organizan: las que se mueven y las que ya están en la carpeta de su género.</summary>
        public Dictionary<string, int> Organizadas { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Un espacio o un signo de puntuación de cualquier alfabeto («–», «’», «¡»), no un símbolo ni un emoji.</summary>
    private static bool EsEspacioOPuntuacion(char c) => CharUnicodeInfo.GetUnicodeCategory(c)
        is UnicodeCategory.SpaceSeparator or UnicodeCategory.DashPunctuation or UnicodeCategory.ConnectorPunctuation
        or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
        or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;

    /// <summary>
    /// Las subcarpetas de origen que se conservan dentro del género. Si la canción ya está dentro del
    /// destino, se cuenta desde la carpeta de su género: al volver a organizar, una canción de «Por
    /// género/Latin/Pack» que pasa a Reggaeton va a «Por género/Reggaeton/Pack», no a «Por
    /// género/Reggaeton/Latin/Pack». En «Por género» todo lo que cuelga directamente es la carpeta de un
    /// género (<paramref name="esCarpetaDeGenero"/> null), aunque ese género ya no esté en la biblioteca;
    /// en otro destino, que puede tener carpetas del usuario, solo las que lo son para
    /// <paramref name="esCarpetaDeGenero"/>.
    /// </summary>
    private static string Relativa(string dir, string raiz, string baseDir, Func<string, bool>? esCarpetaDeGenero)
    {
        if (Rutas.MismaCarpeta(dir, baseDir)) return "";
        if (Rutas.EstaDentroDe(dir, baseDir))
        {
            var tramos = Tramos(Debajo(dir, baseDir)).ToList();
            if (tramos.Count > 0 && (esCarpetaDeGenero == null || esCarpetaDeGenero(tramos[0]))) tramos.RemoveAt(0);
            return tramos.Count == 0 ? "" : Path.Combine(tramos.ToArray());
        }
        if (Rutas.MismaCarpeta(dir, raiz)) return "";
        if (Rutas.EstaDentroDe(dir, raiz)) return Debajo(dir, raiz);
        return "";
    }

    /// <summary>Lo que hay de <paramref name="ruta"/> por debajo de <paramref name="carpeta"/>, que la contiene.</summary>
    private static string Debajo(string ruta, string carpeta)
        => ruta.Substring(carpeta.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
               .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static IEnumerable<string> Tramos(string ruta)
        => ruta.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

    private static bool ExisteCarpeta(IAccesoDisco disco, string carpeta)
    {
        var limpia = carpeta.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var padre = Path.GetDirectoryName(limpia);
        return !string.IsNullOrEmpty(padre) && disco.NombreRealDeCarpeta(padre, Path.GetFileName(limpia)) != null;
    }

    /// <summary>
    /// Lo que BeatTag deduce de la RUTA de una pieza (no de sus etiquetas), tal como lo usa cada página:
    /// el tipo de pieza de Duplicados, Pendientes y Tendencias (<see cref="IndiceVersiones.Clasificar"/>),
    /// si es una edición de DJ para Duplicados (<see cref="Analysis.EdicionDj.Es"/>), si es una acapella
    /// (Compatibles), si Comprobar audio la deja fuera (<see cref="Analysis.Identificacion.SeExcluye"/>), si
    /// Enriquecer la salta (carpeta de mashups o nombre de mezcla) y si una lista la da por no tenida.
    /// Varias cosas salen de la carpeta: «Mashups» (en toda la ruta), «Acapellas» (en toda la ruta),
    /// «Instrumentales» o «Hype Intros» (la carpeta que la contiene).
    ///
    /// POR QUÉ (revisión 1). Al llevar a «Por género/Reggaeton» una acapella que lo era solo por estar en
    /// «Acapellas Pack», dejaba de serlo para todas esas páginas: Comprobar audio podía mandarla al servicio
    /// de pago y Duplicados emparejarla con el original. Al revés, el género «Instrumental» crea una
    /// carpeta «Instrumental» que marcaría como instrumental todo lo que entrara. Una pieza que cambiaría
    /// así se queda donde está, con su motivo.
    ///
    /// Con la misma carpeta, el mismo nombre da lo mismo: las reglas solo miran el nombre y la carpeta, y
    /// el nombre nunca cambia al organizar. Por eso basta comparar una vez por carpeta con un nombre
    /// neutro («x»), y solo si las carpetas difieren se mira cada pieza.
    /// </summary>
    private readonly record struct LoQueDiceLaRuta(TipoPieza Tipo, bool EdicionDj, bool Acapella, bool FueraDeComprobar,
                                                   bool FueraDeEnriquecer, bool NoCuentaEnListas)
    {
        public static LoQueDiceLaRuta De(string ruta)
        {
            var carpetaDeMashups = Matching.IsMashupFolder(Path.GetDirectoryName(ruta));
            var acapella = Analysis.Identificacion.EsAcapella(ruta);
            var p = Pipeline.FileNameParser.Parse(Path.GetFileName(ruta));
            return new LoQueDiceLaRuta(
                IndiceVersiones.Clasificar(ruta),
                Analysis.EdicionDj.Es(ruta),
                acapella,
                Analysis.Identificacion.SeExcluye(ruta, out _),
                carpetaDeMashups || Matching.IsSkipMix(p.Base, p.FnTitle, p.FnArtist),
                acapella || carpetaDeMashups || Matching.IsMezclaDeVariosTemas(Path.GetFileNameWithoutExtension(ruta)));
        }

        /// <summary>Lo que esta lectura dice y <paramref name="otra"/> no («acapella», «mashup»…), o null.</summary>
        public string? LoQueNoDice(LoQueDiceLaRuta otra)
        {
            if (Tipo != otra.Tipo)
                switch (Tipo)
                {
                    case TipoPieza.Mezcla: return "mashup o transición";
                    case TipoPieza.Acapella: return "acapella";
                    case TipoPieza.Instrumental: return "instrumental";
                    case TipoPieza.EdicionDj: return "edición de DJ";
                }
            if (Acapella && !otra.Acapella) return "acapella";
            if ((FueraDeComprobar && !otra.FueraDeComprobar) || (FueraDeEnriquecer && !otra.FueraDeEnriquecer)
                || (NoCuentaEnListas && !otra.NoCuentaEnListas)) return "mashup";
            if (EdicionDj && !otra.EdicionDj) return "edición de DJ";
            return null;
        }
    }

    /// <summary>
    /// Si es una herramienta de DJ que se deja en su sitio (mashup, transición, acapella o
    /// instrumental, con el criterio de <see cref="IndiceVersiones.Clasificar"/>), el motivo; si no, null.
    /// </summary>
    private static string? Herramienta(string ruta) => IndiceVersiones.Clasificar(ruta) switch
    {
        TipoPieza.Mezcla => "Herramienta de DJ: mashup o transición",
        TipoPieza.Acapella => "Herramienta de DJ: acapella",
        TipoPieza.Instrumental => "Herramienta de DJ: instrumental",
        _ => null,
    };

    // --- Textos ---

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    /// <summary>Un número con separador de miles («10.896»).</summary>
    private static string N(int n) => n.ToString("N0", Es);

    /// <summary>El motivo de una omisión, para la tabla «Se omiten».</summary>
    public static string TextoMotivo(MotivoOmision motivo) => motivo switch
    {
        MotivoOmision.SinGenero => "No tiene género",
        MotivoOmision.YaEnSuSitio => "Ya está en la carpeta de su género",
        MotivoOmision.HerramientaDj => "Herramienta de DJ",
        MotivoOmision.CarpetaConAjustes => "Su carpeta tiene ajustes propios en Duplicados o en Volumen",
        MotivoOmision.DestinoConAjustes => "Su carpeta de destino tiene ajustes propios en Duplicados o en Volumen",
        MotivoOmision.OrigenDesmarcado => "Su carpeta de origen está desmarcada",
        MotivoOmision.NoDisponible => "Su carpeta no está disponible",
        MotivoOmision.TipoPorCarpeta => "Moverla cambiaría lo que su carpeta indica de ella (mashup, acapella, instrumental o edición de DJ)",
        MotivoOmision.OtraUnidad => "Está en otra unidad",
        MotivoOmision.RutaLarga => $"La ruta de destino tendría {LargoMaximoRuta} caracteres o más",
        MotivoOmision.TramoLargo => $"Un tramo de la ruta de destino pasaría de {BytesMaximoTramo} bytes",
        _ => "Ya hay un archivo con ese nombre en el destino",
    };

    /// <summary>«Ya hay un archivo con ese nombre en «Latin»».</summary>
    public static string TextoColision(string carpeta) => $"Ya hay un archivo con ese nombre en «{carpeta}»";

    /// <summary>«Su archivo asociado «Tema.mp3.asd» ya existe en «Pop»».</summary>
    public static string TextoColisionAsociado(string archivo, string carpeta) => $"Su archivo asociado «{archivo}» ya existe en «{carpeta}»";

    /// <summary>«Su carpeta de destino, «House», tiene ajustes propios en Duplicados o en Volumen».</summary>
    public static string TextoDestinoConAjustes(string carpeta)
        => $"Su carpeta de destino, «{carpeta}», tiene ajustes propios en Duplicados o en Volumen";

    /// <summary>«Se moverá 1 canción a 1 carpeta» / «Se moverán 10.896 canciones a 181 carpetas».</summary>
    public static string TextoMovimientos(int canciones, int carpetas)
    {
        if (canciones <= 0) return "No hay ninguna canción que mover";
        var verbo = canciones == 1 ? "Se moverá 1 canción" : $"Se moverán {N(canciones)} canciones";
        return verbo + (carpetas == 1 ? " a 1 carpeta" : $" a {N(carpetas)} carpetas");
    }

    /// <summary>Cuántas no se mueven por un motivo, con el número bien concordado («1 se omite…», «66 se omiten…»). "" si ninguna.</summary>
    public static string TextoOmision(MotivoOmision motivo, int n)
    {
        if (n <= 0) return "";
        var uno = n == 1;
        var cuantas = N(n);
        return motivo switch
        {
            MotivoOmision.SinGenero => uno ? "1 se queda por no tener género" : $"{cuantas} se quedan por no tener género",
            MotivoOmision.YaEnSuSitio => uno ? "1 ya está en la carpeta de su género" : $"{cuantas} ya están en la carpeta de su género",
            MotivoOmision.HerramientaDj => uno
                ? "1 se queda por ser una herramienta de DJ (mashup, transición, acapella o instrumental)"
                : $"{cuantas} se quedan por ser herramientas de DJ (mashups, transiciones, acapellas o instrumentales)",
            MotivoOmision.CarpetaConAjustes => uno
                ? "1 se queda porque su carpeta tiene ajustes propios en Duplicados o en Volumen"
                : $"{cuantas} se quedan porque su carpeta tiene ajustes propios en Duplicados o en Volumen",
            MotivoOmision.DestinoConAjustes => uno
                ? "1 se queda porque su carpeta de destino tiene ajustes propios en Duplicados o en Volumen"
                : $"{cuantas} se quedan porque su carpeta de destino tiene ajustes propios en Duplicados o en Volumen",
            MotivoOmision.OrigenDesmarcado => uno ? "1 se queda porque su carpeta está desmarcada" : $"{cuantas} se quedan porque su carpeta está desmarcada",
            MotivoOmision.NoDisponible => uno ? "1 se omite porque su carpeta no está disponible" : $"{cuantas} se omiten porque su carpeta no está disponible",
            MotivoOmision.TipoPorCarpeta => uno
                ? "1 se queda porque moverla cambiaría lo que su carpeta indica de ella (mashup, acapella, instrumental o edición de DJ)"
                : $"{cuantas} se quedan porque moverlas cambiaría lo que su carpeta indica de ellas (mashups, acapellas, instrumentales o ediciones de DJ)",
            MotivoOmision.OtraUnidad => uno ? "1 se omite porque está en otra unidad" : $"{cuantas} se omiten porque están en otra unidad",
            MotivoOmision.RutaLarga => uno
                ? $"1 se omite porque su ruta de destino tendría {LargoMaximoRuta} caracteres o más"
                : $"{cuantas} se omiten porque su ruta de destino tendría {LargoMaximoRuta} caracteres o más",
            MotivoOmision.TramoLargo => uno
                ? $"1 se omite porque un tramo de su ruta pasaría de {BytesMaximoTramo} bytes"
                : $"{cuantas} se omiten porque un tramo de su ruta pasaría de {BytesMaximoTramo} bytes",
            _ => uno ? "1 se omite porque ya hay un archivo con ese nombre" : $"{cuantas} se omiten porque ya hay un archivo con ese nombre",
        };
    }

    /// <summary>El orden en que el resumen cuenta lo que no se mueve.</summary>
    private static readonly MotivoOmision[] OrdenResumen =
    {
        MotivoOmision.SinGenero, MotivoOmision.HerramientaDj, MotivoOmision.TipoPorCarpeta, MotivoOmision.CarpetaConAjustes,
        MotivoOmision.DestinoConAjustes, MotivoOmision.OrigenDesmarcado, MotivoOmision.YaEnSuSitio, MotivoOmision.Colision,
        MotivoOmision.NoDisponible, MotivoOmision.OtraUnidad, MotivoOmision.RutaLarga, MotivoOmision.TramoLargo,
    };

    /// <summary>
    /// El resumen de la vista previa: «Se moverán 10.896 canciones a 181 carpetas · 2.456 se quedan por
    /// no tener género · 66 se omiten porque ya hay un archivo con ese nombre · De media, cada carpeta
    /// de origen se repartirá entre 24 géneros».
    /// </summary>
    public static string Resumen(PlanOrganizar plan)
    {
        var partes = new List<string> { TextoMovimientos(plan.Movimientos.Count, plan.Carpetas) };
        var porMotivo = plan.Omitidas.GroupBy(o => o.Motivo).ToDictionary(g => g.Key, g => g.Count());
        foreach (var m in OrdenResumen)
            if (porMotivo.TryGetValue(m, out var n)) partes.Add(TextoOmision(m, n));
        var media = (int)Math.Round(plan.MediaGenerosPorOrigen, MidpointRounding.AwayFromZero);
        if (media >= 2) partes.Add($"De media, cada carpeta de origen se repartirá entre {N(media)} géneros");
        return string.Join(" · ", partes);
    }

    /// <summary>La nota de la tabla de destinos: «1 canción», «12 canciones».</summary>
    public static string TextoCanciones(int n) => n == 1 ? "1 canción" : $"{N(n)} canciones";
}
