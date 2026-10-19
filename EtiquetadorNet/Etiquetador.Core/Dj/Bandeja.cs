using Etiquetador.Core.Analysis;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Core.Dj;

/// <summary>
/// Por dónde va una canción recién llegada, antes de entrar en la biblioteca.
///
/// Las dos primeras las pone la aplicación y las dos últimas el usuario. La distinción importa: que
/// una canción esté analizada solo dice que BeatTag ya la ha mirado; que esté preparada dice que
/// alguien ha decidido que está lista, y solo esas pasan a la biblioteca.
/// </summary>
public enum EstadoBandeja
{
    /// <summary>Acaba de aparecer en la carpeta y todavía no se ha revisado.</summary>
    Recibida,
    /// <summary>BeatTag ya ha buscado duplicados, calidad baja y etiquetas que faltan.</summary>
    Analizada,
    /// <summary>El usuario ha visto los avisos.</summary>
    Revisada,
    /// <summary>El usuario la da por lista para entrar en la biblioteca.</summary>
    Preparada,
}

public enum TipoAviso
{
    /// <summary>La misma canción, con el mismo artista y título, ya está en la biblioteca.</summary>
    YaEnBiblioteca,
    /// <summary>En la biblioteca hay otra versión de la canción (extended, remix…).</summary>
    OtraVersion,
    /// <summary>Hay otra copia igual dentro de la propia bandeja.</summary>
    RepetidaEnBandeja,
    CalidadBaja,
    Incompleta,
    /// <summary>En la carpeta de destino ya hay un archivo con ese nombre.</summary>
    NombreOcupado,
}

public sealed record AvisoBandeja(TipoAviso Tipo, string Texto);

/// <summary>Lo que se descarta de una selección de la bandeja y lo que se conserva para no perder la canción (X15).</summary>
public sealed record PlanDescarte<T>(IReadOnlyList<T> Descartar, IReadOnlyList<T> Conservar);

/// <summary>Canciones agrupadas por clave, para buscar coincidencias sin recorrer la biblioteca entera cada vez.</summary>
public sealed class IndiceCanciones
{
    private readonly Dictionary<string, List<Track>> _exacta = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Track>> _cancion = new(StringComparer.Ordinal);

    public static readonly IndiceCanciones Vacio = new(Array.Empty<Track>());

    public IndiceCanciones(IEnumerable<Track> canciones)
    {
        foreach (var t in canciones)
        {
            Anotar(_exacta, RevisionBandeja.ClaveExacta(t), t);
            Anotar(_cancion, RevisionBandeja.ClaveCancion(t), t);
        }
    }

    private static void Anotar(Dictionary<string, List<Track>> d, string clave, Track t)
    {
        if (clave.Length == 0) return;
        if (!d.TryGetValue(clave, out var l)) d[clave] = l = new List<Track>();
        l.Add(t);
    }

    /// <summary>Las que tienen la misma clave exacta, sin contar el propio archivo.</summary>
    public IReadOnlyList<Track> MismaExacta(Track t) => Otras(_exacta, RevisionBandeja.ClaveExacta(t), t);

    /// <summary>Las que son la misma canción en cualquier versión, sin contar el propio archivo.</summary>
    public IReadOnlyList<Track> MismaCancion(Track t) => Otras(_cancion, RevisionBandeja.ClaveCancion(t), t);

    private static IReadOnlyList<Track> Otras(Dictionary<string, List<Track>> d, string clave, Track t)
        => clave.Length > 0 && d.TryGetValue(clave, out var l)
            ? l.Where(x => !string.Equals(x.FilePath, t.FilePath, StringComparison.OrdinalIgnoreCase)).ToList()
            : Array.Empty<Track>();
}

/// <summary>Lo que se comprueba de una canción antes de dejarla entrar en la biblioteca.</summary>
public static class RevisionBandeja
{
    /// <summary>
    /// Artista y título tal cual, normalizados. Es el mismo criterio que usa la pestaña Duplicados,
    /// para que una canción que aquí sale como repetida salga también allí.
    ///
    /// Hacen falta los dos: con solo el título, cualquier «Intro» coincidiría con todas las demás.
    /// </summary>
    public static string ClaveExacta(Track t)
    {
        var a = TextUtils.Nk(t.Artist);
        var ti = TextUtils.Nk(t.Title);
        return a.Length == 0 || ti.Length == 0 ? "" : a + "|" + ti;
    }

    /// <summary>
    /// La canción sin su versión: sin «Extended», «Remix», invitados ni BPM. Sirve para avisar de que
    /// ya existe OTRA versión, que para un DJ es tan útil de saber como un duplicado exacto.
    /// </summary>
    public static string ClaveCancion(Track t)
    {
        var a = TextUtils.Nk(Descriptors.CleanKeywords(t.Artist));
        var ti = TextUtils.Nk(Descriptors.CleanTitle(t.Title));
        return a.Length == 0 || ti.Length == 0 ? "" : a + "|" + ti;
    }

    /// <summary>
    /// Los avisos de una canción de la bandeja. Lista vacía si no hay nada que objetar.
    /// </summary>
    /// <param name="biblioteca">Índice de la biblioteca.</param>
    /// <param name="bandeja">Índice de la propia bandeja, para las copias repetidas dentro de ella.</param>
    /// <param name="carpetaDestino">Donde irá al pasar a la biblioteca. Vacío si aún no se ha elegido.</param>
    /// <param name="existe">Si existe un archivo. Se pasa aparte para poder probarlo sin disco.</param>
    public static IReadOnlyList<AvisoBandeja> Revisar(Track t, IndiceCanciones biblioteca, IndiceCanciones bandeja,
                                                     string carpetaDestino, Func<string, bool> existe)
    {
        var avisos = new List<AvisoBandeja>();

        var exactas = biblioteca.MismaExacta(t);
        if (exactas.Count > 0)
            avisos.Add(new AvisoBandeja(TipoAviso.YaEnBiblioteca, "Ya está en la biblioteca: " + Enumerar(exactas)));
        else
        {
            // Solo si no hay una exacta: avisar de las dos cosas a la vez sería repetir lo mismo.
            var versiones = biblioteca.MismaCancion(t);
            if (versiones.Count > 0)
                avisos.Add(new AvisoBandeja(TipoAviso.OtraVersion,
                    "Otra versión en la biblioteca: " + Enumerar(versiones, conVersion: true)));
        }

        var repetidas = bandeja.MismaExacta(t);
        if (repetidas.Count > 0)
            avisos.Add(new AvisoBandeja(TipoAviso.RepetidaEnBandeja, "Repetida en la bandeja: " + Enumerar(repetidas)));

        if (AudioQuality.IsPoor(t))
            avisos.Add(new AvisoBandeja(TipoAviso.CalidadBaja, $"Calidad baja: {t.Bitrate} kbps"));

        var faltan = t.CamposQueFaltan();
        if (faltan.Count > 0)
            avisos.Add(new AvisoBandeja(TipoAviso.Incompleta, "Faltan etiquetas: " + string.Join(", ", faltan)));

        if (carpetaDestino.Trim().Length > 0 && existe(Path.Combine(carpetaDestino, t.FileName)))
            avisos.Add(new AvisoBandeja(TipoAviso.NombreOcupado,
                $"Ya hay un archivo llamado «{t.FileName}» en la carpeta de destino"));

        return avisos;
    }

    /// <summary>
    /// El aviso en dos o tres palabras, para la columna de la tabla. El texto completo nombra el
    /// archivo con el que coincide y es largo: con dos avisos, el segundo quedaba cortado y no se veía.
    /// </summary>
    public static string NombreCorto(TipoAviso tipo) => tipo switch
    {
        TipoAviso.YaEnBiblioteca => "Ya en la biblioteca",
        TipoAviso.OtraVersion => "Otra versión",
        TipoAviso.RepetidaEnBandeja => "Repetida",
        TipoAviso.CalidadBaja => "Calidad baja",
        TipoAviso.Incompleta => "Faltan etiquetas",
        _ => "Nombre ocupado",
    };

    /// <summary>
    /// ¿Dice este aviso que la canción ya la tienes? Solo la copia exacta de la biblioteca y la
    /// repetida en la propia bandeja (X15). «Otra versión» no cuenta: una extended o un remix de algo
    /// que ya tienes es justo lo que un DJ suele querer conservar, y descartarla por el filtro de
    /// duplicados sería perder música.
    /// </summary>
    public static bool EsAvisoDeDuplicado(TipoAviso tipo) => tipo is TipoAviso.YaEnBiblioteca or TipoAviso.RepetidaEnBandeja;

    /// <summary>¿Alguno de estos avisos dice que la canción ya la tienes? Ver <see cref="EsAvisoDeDuplicado"/>.</summary>
    public static bool TieneAvisoDeDuplicado(IEnumerable<AvisoBandeja> avisos) => avisos.Any(a => EsAvisoDeDuplicado(a.Tipo));

    /// <summary>
    /// El cuerpo del diálogo antes de descartar descargas a la papelera (X15). Lo que conviene no
    /// pasar por alto es que alguna seleccionada NO tenga aviso de duplicado: con la tabla filtrada y
    /// Ctrl+A es fácil llevarse una canción que todavía no tienes. Y que alguna sea MEJOR que la copia
    /// de la biblioteca (I14): esa conviene usarla para sustituir, no tirarla.
    /// </summary>
    /// <param name="total">Las que se van a descartar de verdad (ya sin las que se conservan; ver <see cref="PlanearDescarte{T}"/>).</param>
    /// <param name="conAvisoDeDuplicado">Cuántas de ellas tienen aviso de duplicado.</param>
    /// <param name="mejores">Cuántas mejoran su copia de la biblioteca (I14).</param>
    /// <param name="conservadas">
    /// Canciones repetidas en la bandeja, sin copia en la biblioteca, de las que se conserva una copia
    /// porque estaban seleccionadas todas (ver <see cref="PlanearDescarte{T}"/>).
    /// </param>
    public static string CuerpoDescartar(int total, int conAvisoDeDuplicado, int mejores = 0, int conservadas = 0)
    {
        if (total <= 0) return "";
        var sinAviso = total - conAvisoDeDuplicado;
        string texto;
        if (sinAviso <= 0)
            texto = total == 1
                ? "La descarga ya está en la biblioteca o repetida en la bandeja."
                : $"Las {total} ya están en la biblioteca o repetidas en la bandeja.";
        else if (conAvisoDeDuplicado <= 0)
            texto = total == 1
                ? "⚠ Esta descarga no tiene aviso de duplicado: no consta que ya la tengas."
                : $"⚠ Ninguna de las {total} tiene aviso de duplicado: no consta que ya las tengas.";
        else
            texto = $"{conAvisoDeDuplicado} ya están en la biblioteca o repetidas en la bandeja. "
                  + (sinAviso == 1
                      ? "⚠ 1 no tiene aviso de duplicado: no consta que ya la tengas."
                      : $"⚠ {sinAviso} no tienen aviso de duplicado: no consta que ya las tengas.");
        if (mejores > 0)
            texto += mejores == 1
                ? " ⚠ 1 es de mejor calidad que su copia de la biblioteca: puedes sustituir esa copia por ella en lugar de descartarla."
                : $" ⚠ {mejores} son de mejor calidad que su copia de la biblioteca: puedes sustituir esas copias por ellas en lugar de descartarlas.";
        if (conservadas > 0) texto += " " + TextoConservadas(conservadas);
        return texto;
    }

    // ---- Descartar sin quedarse sin ninguna copia (X15, revisión) ----
    //
    // Visto en revisión: «Tema.mp3» y «Tema (1).mp3» (la descarga doble del navegador) salen las dos
    // con «Repetida en la bandeja», porque cada una ve a la otra. Con el filtro «Con aviso de
    // duplicado» y Ctrl+A entraban las dos en la selección, el diálogo decía «Las 2 ya están en la
    // biblioteca o repetidas en la bandeja» y las dos iban a la papelera: el DJ se quedaba sin
    // ninguna copia de una canción que no tenía. Ahora se hace como «Marcar sobrantes» en
    // Duplicados: de cada canción repetida que no está en la biblioteca se conserva una copia.

    /// <summary>
    /// Qué se descarta de verdad de una selección de la bandeja y qué se conserva.
    ///
    /// Una canción repetida en la bandeja (misma clave exacta que <see cref="ClaveExacta"/>, dos copias
    /// o más) que NO está en la biblioteca, y con TODAS sus copias de la bandeja seleccionadas,
    /// conserva la mejor (ver <see cref="MejorCopia"/>); el resto se descarta. Si alguna de sus copias
    /// se queda fuera de la selección, la canción sigue en la bandeja y no hace falta conservar nada.
    /// Una canción que ya está en la biblioteca no conserva ninguna: ahí ya hay una copia.
    /// </summary>
    /// <param name="seleccion">Lo que el usuario ha seleccionado para descartar.</param>
    /// <param name="bandeja">Todo lo que hay en la bandeja (seleccionado o no).</param>
    /// <param name="cancion">La canción de cada elemento.</param>
    /// <param name="enBiblioteca">Si ese elemento ya tiene copia exacta en la biblioteca.</param>
    public static PlanDescarte<T> PlanearDescarte<T>(IReadOnlyList<T> seleccion, IEnumerable<T> bandeja,
                                                     Func<T, Track> cancion, Func<T, bool> enBiblioteca)
    {
        var elegidas = new HashSet<string>(seleccion.Select(x => cancion(x).FilePath), StringComparer.OrdinalIgnoreCase);
        var conservar = new List<T>();
        foreach (var grupo in bandeja.GroupBy(x => ClaveExacta(cancion(x)), StringComparer.Ordinal))
        {
            if (grupo.Key.Length == 0) continue;
            var copias = grupo.ToList();
            if (copias.Count < 2 || copias.Any(enBiblioteca)) continue;
            if (!copias.All(x => elegidas.Contains(cancion(x).FilePath))) continue;
            var mejor = MejorCopia(copias.Select(cancion).ToList());
            conservar.Add(copias.First(x => ReferenceEquals(cancion(x), mejor)));
        }
        if (conservar.Count == 0) return new PlanDescarte<T>(seleccion.ToList(), conservar);

        var quedan = new HashSet<string>(conservar.Select(x => cancion(x).FilePath), StringComparer.OrdinalIgnoreCase);
        return new PlanDescarte<T>(seleccion.Where(x => !quedan.Contains(cancion(x).FilePath)).ToList(), conservar);
    }

    /// <summary>
    /// La copia que se conserva de una canción repetida: la de más calidad (sin pérdida antes que con
    /// pérdida, y después más kbps, el mismo criterio que <see cref="SustitucionBandeja.EsMejor"/>). Con la misma calidad, la
    /// de nombre más corto: la descarga doble del navegador añade « (1)» y el sistema « - copia», así
    /// que el nombre más corto suele ser el original. Si aún empatan, por orden alfabético, para que
    /// la elección no dependa del orden de la tabla.
    /// </summary>
    public static Track MejorCopia(IReadOnlyList<Track> copias)
        => copias.OrderByDescending(t => AudioQuality.Rate(t) == QualityTier.Lossless)
                 .ThenByDescending(t => t.Bitrate)
                 .ThenBy(t => t.FileName.Length)
                 .ThenBy(t => t.FileName, StringComparer.Ordinal)
                 .First();

    /// <summary>La frase que dice qué se conserva, para el diálogo y para el estado al terminar.</summary>
    public static string TextoConservadas(int conservadas) => conservadas switch
    {
        <= 0 => "",
        1 => "De 1 canción repetida en la bandeja que no está en la biblioteca estaban seleccionadas todas las copias: "
           + "la de mejor calidad se queda en la bandeja y se descartan las demás.",
        _ => $"De {conservadas} canciones repetidas en la bandeja que no están en la biblioteca estaban seleccionadas todas las copias: "
           + "de cada una se queda en la bandeja la de mejor calidad y se descartan las demás.",
    };

    /// <summary>Lo mismo para el estado, cuando ya se ha descartado.</summary>
    public static string TextoConservadasTrasDescartar(int conservadas) => conservadas switch
    {
        <= 0 => "",
        1 => "Se ha conservado en la bandeja la copia de mejor calidad de 1 canción repetida que no está en la biblioteca.",
        _ => $"Se ha conservado en la bandeja la copia de mejor calidad de {conservadas} canciones repetidas que no están en la biblioteca.",
    };

    private static string Enumerar(IReadOnlyList<Track> canciones, bool conVersion = false)
    {
        string Uno(Track x) => conVersion
            ? $"{x.FileName} ({PrioridadVersion.Nombre(PrioridadVersion.De(x.FileName))})"
            : x.FileName;
        return canciones.Count == 1 ? Uno(canciones[0]) : $"{Uno(canciones[0])} y {canciones.Count - 1} más";
    }

    /// <summary>
    /// Por qué no vale una carpeta como bandeja, o "" si vale.
    ///
    /// No puede solaparse con la biblioteca. Si la bandeja estuviera dentro de una carpeta de la
    /// biblioteca, sus canciones ya contarían como tuyas antes de revisarlas y cada una se
    /// encontraría a sí misma como duplicada; y pasar una canción «a la biblioteca» no la movería de
    /// ningún sitio a ninguna parte.
    /// </summary>
    public static string ValidarCarpeta(string carpeta, IEnumerable<string> carpetasBiblioteca, Func<string, bool> existeCarpeta)
    {
        if (string.IsNullOrWhiteSpace(carpeta)) return "Elige la carpeta donde dejas la música nueva.";
        if (!existeCarpeta(carpeta)) return "La carpeta no existe.";

        foreach (var raiz in carpetasBiblioteca)
        {
            if (!Rutas.Solapan(carpeta, raiz)) continue;
            var nombre = Path.GetFileName(raiz.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return $"Esa carpeta se solapa con «{nombre}», que forma parte de la biblioteca. La bandeja tiene que estar fuera de ella.";
        }
        return "";
    }
}

/// <summary>Lo que se recuerda de cada canción de la bandeja entre sesiones.</summary>
public sealed class EntradaBandeja
{
    public EstadoBandeja Estado { get; set; } = EstadoBandeja.Recibida;

    /// <summary>Cuándo se vio por primera vez. Sirve para ordenar por llegada.</summary>
    public DateTime Llegada { get; set; }
}

/// <summary>El estado de cada canción de la bandeja, por ruta.</summary>
public sealed class AlmacenBandeja
{
    private readonly string _archivo;
    private readonly Dictionary<string, EntradaBandeja> _entradas;

    public string AvisoCarga { get; }

    /// <summary>No se pudo leer ni apartar el archivo al abrirlo: <see cref="Guardar"/> no lo pisa.</summary>
    public bool SoloLectura { get; }

    public AlmacenBandeja(string archivo)
    {
        _archivo = archivo;
        var leidas = ArchivoJson.Leer(archivo, () => new Dictionary<string, EntradaBandeja>(), out var aviso, out var soloLectura);
        _entradas = new Dictionary<string, EntradaBandeja>(leidas, StringComparer.OrdinalIgnoreCase);
        AvisoCarga = aviso;
        SoloLectura = soloLectura;
    }

    /// <summary>La entrada de un archivo; si es la primera vez que se ve, la crea como recibida.</summary>
    public EntradaBandeja Registrar(string ruta, DateTime ahora)
    {
        if (!_entradas.TryGetValue(ruta, out var e))
            _entradas[ruta] = e = new EntradaBandeja { Estado = EstadoBandeja.Recibida, Llegada = ahora };
        return e;
    }

    public EntradaBandeja? Obtener(string ruta) => _entradas.TryGetValue(ruta, out var e) ? e : null;

    public void Marcar(string ruta, EstadoBandeja estado)
    {
        if (_entradas.TryGetValue(ruta, out var e)) e.Estado = estado;
    }

    public void Olvidar(string ruta) => _entradas.Remove(ruta);

    /// <summary>Olvida las que ya no están en la carpeta (se movieron o se borraron). Devuelve cuántas.</summary>
    public int Podar(IEnumerable<string> presentes)
    {
        var vivos = new HashSet<string>(presentes, StringComparer.OrdinalIgnoreCase);
        var fuera = _entradas.Keys.Where(k => !vivos.Contains(k)).ToList();
        foreach (var k in fuera) _entradas.Remove(k);
        return fuera.Count;
    }

    /// <summary>
    /// Analizar solo hace avanzar a las recién llegadas. Una canción que el usuario ya dio por
    /// revisada o preparada no puede volver atrás porque se pulse Analizar otra vez.
    /// </summary>
    public static EstadoBandeja TrasAnalizar(EstadoBandeja actual)
        => actual == EstadoBandeja.Recibida ? EstadoBandeja.Analizada : actual;

    public static string Nombre(EstadoBandeja e) => e switch
    {
        EstadoBandeja.Recibida => "Recibida",
        EstadoBandeja.Analizada => "Analizada",
        EstadoBandeja.Revisada => "Revisada",
        _ => "Preparada",
    };

    /// <summary>Devuelve "" si fue bien, o el motivo del fallo. En solo lectura no escribe nada.</summary>
    public string Guardar() => SoloLectura ? ArchivoJson.AvisoSoloLectura(_archivo) : ArchivoJson.Escribir(_archivo, _entradas);
}

/// <summary>Resultado de pasar un archivo de la bandeja a la biblioteca.</summary>
public sealed record Traslado(string Origen, string Destino, string Error)
{
    public bool Ok => Error.Length == 0;
}

public static class TrasladoBandeja
{
    /// <summary>
    /// Mueve un archivo a una carpeta de la biblioteca conservando su nombre. NUNCA sobrescribe: si
    /// en el destino ya hay un archivo con ese nombre, no se mueve y se dice por qué. Machacar una
    /// canción de la biblioteca al ordenar la bandeja sería perder música.
    /// </summary>
    public static Traslado Mover(string archivo, string carpetaDestino)
    {
        var destino = Path.Combine(carpetaDestino, Path.GetFileName(archivo));
        try
        {
            if (!File.Exists(archivo)) return new Traslado(archivo, destino, "El archivo ya no está en la bandeja.");
            if (!Directory.Exists(carpetaDestino)) return new Traslado(archivo, destino, "La carpeta de destino no existe.");
            if (File.Exists(destino)) return new Traslado(archivo, destino, "Ya hay un archivo con ese nombre en el destino; no se sobrescribe.");

            File.Move(archivo, destino, overwrite: false);
            return new Traslado(archivo, destino, "");
        }
        catch (Exception e) { return new Traslado(archivo, destino, e.Message); }
    }
}

// ---------------------------------------------------------------------------------------------
// Sustituir la copia de la biblioteca por una descarga mejor (I14)
// ---------------------------------------------------------------------------------------------

/// <summary>La copia de la biblioteca que una descarga puede sustituir, y por qué es mejor («320 kbps frente a 128 kbps»).</summary>
public sealed record PropuestaSustitucion(Track Vieja, string Motivo);

/// <summary>Cómo fue sustituir una copia de la biblioteca.</summary>
/// <param name="Destino">Dónde ha quedado la descarga (la carpeta y el nombre de la copia antigua).</param>
/// <param name="Error">"" si fue bien; si no, qué pasó y en qué estado han quedado los dos archivos.</param>
/// <param name="ViejaEnPapelera">La copia antigua ya está en la papelera (también cuando algo falló después).</param>
public sealed record ResultadoSustitucion(string Destino, string Error, bool ViejaEnPapelera)
{
    public bool Ok => Error.Length == 0;
}

/// <summary>
/// «Sustituir»: la descarga ocupa el lugar de la copia que ya estaba en la biblioteca.
///
/// POR QUÉ. Descargar en 320 kbps un tema que tienes a 128 era un camino de cinco pasos: la bandeja
/// decía «Ya en la biblioteca» y nada más; había que ir a Duplicados, mandar la vieja a la papelera,
/// volver, mover la nueva y renombrarla, y la ficha de DJ de la vieja se quedaba huérfana. Aquí se
/// hace de una vez y con las mismas garantías que el resto: la copia antigua va a la papelera (y si
/// su unidad no tiene, no se sustituye nada), la nueva toma el nombre y la carpeta de la antigua, la
/// ficha la acompaña y el traslado queda anotado para deshacer.
/// </summary>
public static class SustitucionBandeja
{
    /// <summary>
    /// Cuántos kbps más tiene que tener la descarga para contar como mejor. Es el salto más pequeño de
    /// la escala de MP3 en la zona que importa (128→160, 192→224, 224→256). Con menos, dos
    /// codificaciones de la misma calidad saldrían como «mejores» la una de la otra: un VBR V0 ronda
    /// los 245 kbps de media según el tema, y un CBR 256 no es mejor que él.
    /// </summary>
    public const int MargenKbps = 32;

    /// <summary>
    /// Diferencia de duración, en segundos, por encima de la cual ya no se da por la misma grabación.
    /// Es la misma tolerancia con la que Duplicados («Artista + título + duración») da dos copias por
    /// iguales: el silencio inicial o final cambia de una tienda a otra, pero una intro de DJ o una
    /// extended alargan el tema mucho más.
    /// </summary>
    public const int ToleranciaDuracionSeg = 5;

    /// <summary>
    /// La copia de la biblioteca que <paramref name="nueva"/> puede sustituir, o null. Hace falta todo:
    ///   · una ÚNICA coincidencia exacta (artista y título) en la biblioteca; con dos o más no hay forma
    ///     de saber cuál sustituir, y eso es trabajo para Duplicados;
    ///   · la misma duración (± <see cref="ToleranciaDuracionSeg"/> s) y las dos conocidas: si no, puede
    ///     ser otra edición con las mismas etiquetas;
    ///   · la misma marca de letra en el nombre («Clean», «Dirty» o ninguna): los pools publican las
    ///     dos versiones con el mismo artista y título, y no son intercambiables;
    ///   · que la descarga sea de mejor calidad (ver <see cref="EsMejor"/>).
    /// </summary>
    public static PropuestaSustitucion? Proponer(Track nueva, IndiceCanciones biblioteca)
    {
        var exactas = biblioteca.MismaExacta(nueva);
        if (exactas.Count != 1) return null;
        var vieja = exactas[0];
        if (nueva.DurationSeconds <= 0 || vieja.DurationSeconds <= 0) return null;
        if (Math.Abs(nueva.DurationSeconds - vieja.DurationSeconds) > ToleranciaDuracionSeg) return null;
        // Por el nombre: lo que diga el catálogo de la vieja no dice nada de qué versión es la descarga.
        if (ExplicitDetector.DelNombre(nueva) != ExplicitDetector.DelNombre(vieja)) return null;
        if (!EsMejor(nueva, vieja)) return null;
        return new PropuestaSustitucion(vieja, $"{Calidad(nueva)} frente a {Calidad(vieja)}");
    }

    /// <summary>
    /// ¿Es <paramref name="nueva"/> de mejor calidad que <paramref name="vieja"/>? Sin pérdida frente
    /// a con pérdida, sí. Entre dos con pérdida, si tiene al menos <see cref="MargenKbps"/> kbps más.
    /// Entre dos sin pérdida, no: un FLAC no suena mejor que un WAV del mismo máster. Si no se conoce
    /// el bitrate de alguna de las dos con pérdida, no se puede afirmar.
    /// </summary>
    public static bool EsMejor(Track nueva, Track vieja)
    {
        var n = AudioQuality.Rate(nueva);
        var v = AudioQuality.Rate(vieja);
        if (n == QualityTier.Lossless) return v != QualityTier.Lossless;
        if (v == QualityTier.Lossless) return false;
        if (nueva.Bitrate <= 0 || vieja.Bitrate <= 0) return false;
        return nueva.Bitrate >= vieja.Bitrate + MargenKbps;
    }

    /// <summary>La calidad en pocas palabras: «320 kbps», «sin pérdida (AIFF)».</summary>
    public static string Calidad(Track t)
    {
        if (AudioQuality.Rate(t) == QualityTier.Lossless)
            return $"sin pérdida ({Path.GetExtension(t.FilePath).TrimStart('.').ToUpperInvariant()})";
        return t.Bitrate > 0 ? $"{t.Bitrate} kbps" : "calidad desconocida";
    }

    /// <summary>
    /// Dónde queda la descarga: en la carpeta de la copia antigua, con su nombre y la extensión de la
    /// descarga. Si las dos extensiones son la misma (salvo mayúsculas), se conserva la de la antigua y
    /// la ruta no cambia: así rekordbox, que guarda los cue points por ruta, la sigue encontrando.
    /// </summary>
    public static string Destino(string vieja, string nueva)
    {
        var extNueva = Path.GetExtension(nueva);
        var extVieja = Path.GetExtension(vieja);
        var ext = string.Equals(extNueva, extVieja, StringComparison.OrdinalIgnoreCase) ? extVieja : extNueva;
        return Path.Combine(Path.GetDirectoryName(vieja) ?? "", Path.GetFileNameWithoutExtension(vieja) + ext);
    }

    /// <summary>
    /// Lo que conviene saber sobre rekordbox, que guarda los cue points y la cuadrícula ligados a la
    /// ruta del archivo.
    /// </summary>
    public static string TextoRekordbox(string vieja, string destino)
    {
        if (string.Equals(vieja, destino, StringComparison.OrdinalIgnoreCase))
            return "rekordbox guarda los cue points y la cuadrícula por ruta. La ruta no cambia, así que la pista los conserva, "
                 + "pero el audio es otro archivo: conviene volver a analizarla en rekordbox y comprobar que los cue points caen donde deben.";
        var de = Path.GetExtension(vieja).TrimStart('.').ToUpperInvariant();
        var a = Path.GetExtension(destino).TrimStart('.').ToUpperInvariant();
        return $"rekordbox guarda los cue points y la cuadrícula por ruta, y la extensión cambia ({de} → {a}): rekordbox dará la pista "
             + "por perdida. Reubícala desde rekordbox apuntando al archivo nuevo, o vuelve a importarla.";
    }

    /// <summary>
    /// Lo que conviene saber sobre Serato y Traktor (revisión de I14). Serato no guarda los cue points
    /// por ruta como rekordbox, sino DENTRO del propio archivo, en sus etiquetas: se quedan en la copia
    /// antigua, que va a la papelera. Traktor los guarda en su colección, ligados a la ruta.
    /// </summary>
    public const string TextoSeratoTraktor =
        "Serato guarda los cue points y la cuadrícula dentro del propio archivo, así que se quedan en la copia antigua: "
        + "en la descarga habrá que volver a ponerlos. Traktor los guarda en su colección, ligados a la ruta, igual que rekordbox.";

    /// <summary>
    /// El cuerpo y la nota del diálogo de confirmación, con los dos archivos y su calidad. Dice también
    /// lo que NO pasa a la descarga: las etiquetas del archivo antiguo (comentario, tonalidad,
    /// puntuación, color…). Copiarlas sería decidir por el DJ qué etiquetas valen para un archivo
    /// nuevo; la descarga conserva las suyas y Enriquecer la vuelve a proponer.
    ///
    /// Quien llama comprueba ANTES que la unidad de la copia antigua tiene papelera (si no, no se abre
    /// el diálogo), así que la nota puede prometer que se recupera desde ahí.
    /// </summary>
    public static (string Cuerpo, string Nota) TextosConfirmacion(Track nueva, Track vieja)
    {
        var destino = Destino(vieja.FilePath, nueva.FilePath);
        var carpeta = Path.GetDirectoryName(vieja.FilePath) ?? "";
        var cuerpo = $"La descarga «{nueva.FileName}» ({Calidad(nueva)}) sustituirá a «{vieja.FileName}» ({Calidad(vieja)}), "
                   + $"en «{carpeta}». La copia de la biblioteca va a la papelera y la descarga ocupa su lugar como "
                   + $"«{Path.GetFileName(destino)}». Su ficha de DJ pasa a la descarga. Las etiquetas del archivo antiguo "
                   + "(comentario, tonalidad, puntuación, color…) no pasan: la descarga conserva las suyas y Enriquecer la volverá a proponer.";
        var nota = TextoRekordbox(vieja.FilePath, destino) + " " + TextoSeratoTraktor
                 + " La copia antigua se puede recuperar desde la papelera del sistema, y el traslado se puede deshacer desde Enriquecer.";
        return (cuerpo, nota);
    }

    /// <summary>
    /// La ficha que queda en la descarga: la de la copia antigua (lo que el DJ ha ido apuntando con el
    /// tiempo) con lo que se haya rellenado ya en la bandeja por encima. Cada dato de la nueva manda si
    /// lo tiene; los momentos, el ambiente y las etiquetas se suman; «arma secreta» se conserva si
    /// cualquiera de las dos lo era.
    ///
    /// La letra «Limpia» de la copia antigua NO se hereda: es una decisión que se toma escuchando, y
    /// los pools publican la versión clean y la explícita con el mismo artista, título y duración.
    /// Heredarla podría meter una letra explícita en una colección «solo limpias» para una boda, que es
    /// el fallo que más daño hace. «Explícita» sí se hereda: equivocarse en ese sentido solo aparta una
    /// canción de más. Devuelve null si no queda nada que guardar.
    /// </summary>
    public static FichaDj? FusionarFichas(FichaDj? vieja, FichaDj? nueva)
    {
        if (vieja == null && nueva == null) return null;
        var v = vieja ?? new FichaDj();
        var n = nueva ?? new FichaDj();
        var f = new FichaDj
        {
            Energia = n.Energia > 0 ? n.Energia : v.Energia,
            Momentos = v.Momentos | n.Momentos,
            Voz = n.Voz != TipoVoz.SinIndicar ? n.Voz : v.Voz,
            Letra = n.Letra != TipoLetra.SinIndicar ? n.Letra
                  : v.Letra == TipoLetra.Explicita ? TipoLetra.Explicita : TipoLetra.SinIndicar,
            Idioma = n.Idioma.Trim().Length > 0 ? n.Idioma : v.Idioma,
            Ambiente = Unir(v.Ambiente, n.Ambiente),
            Etiquetas = Unir(v.Etiquetas, n.Etiquetas),
            ArmaSecreta = v.ArmaSecreta || n.ArmaSecreta,
        };
        return f.EstaVacia ? null : f;
    }

    private static List<string> Unir(IEnumerable<string> a, IEnumerable<string> b)
    {
        var res = new List<string>();
        var vistos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in a.Concat(b))
            if (Fichas.Clave(e).Length > 0 && vistos.Add(Fichas.Clave(e))) res.Add(e);
        return res;
    }

    /// <summary>
    /// Lleva a <paramref name="destino"/> la ficha fusionada de la copia antigua y de la descarga, y
    /// quita las de las rutas que ya no existen. Se llama DESPUÉS de sustituir. No guarda en disco.
    /// </summary>
    public static void LlevarFicha(AlmacenFichas fichas, string vieja, string nueva, string destino)
    {
        var f = FusionarFichas(fichas.Obtener(vieja), fichas.Obtener(nueva));
        fichas.Quitar(nueva);
        if (!string.Equals(vieja, destino, StringComparison.OrdinalIgnoreCase)) fichas.Quitar(vieja);
        if (f != null) fichas.Poner(destino, f);
        else fichas.Quitar(destino);
    }

    /// <summary>
    /// Sustituye en disco la copia <paramref name="vieja"/> de la biblioteca por la descarga
    /// <paramref name="nueva"/>. Hay que llamarlo fuera del hilo de la interfaz, con el reproductor
    /// parado y con el historial para deshacer YA abierto: si no se puede abrir, no se llama.
    ///
    /// El orden está pensado para que cualquier fallo antes de mandar la vieja a la papelera deje todo
    /// como estaba:
    ///   1. La descarga se mueve a un temporal en la carpeta de la vieja. Si la bandeja está en otro
    ///      disco, aquí se copia el archivo entero, que es lo que puede fallar a medias (disco lleno).
    ///   2. La vieja va a la papelera. Si se niega, la descarga vuelve a la bandeja.
    ///   3. El temporal toma el nombre de la vieja: un renombrado en la misma carpeta.
    ///   4. Se anota para deshacer (bandeja → destino). Se anota al final y no antes a propósito: con
    ///      la misma extensión el destino ES la ruta de la vieja, y un registro escrito antes de que la
    ///      vieja se fuera a la papelera, si la papelera se negaba, haría que deshacer moviera la copia
    ///      de la biblioteca a la bandeja. Si anotar falla, la descarga vuelve a la bandeja y se dice
    ///      que la vieja está en la papelera.
    /// La vieja no se anota: no hay forma de devolverla desde el programa, se recupera desde la papelera.
    ///
    /// Con <paramref name="carpetaDiario"/>, antes del paso 1 se apunta ahí qué se va a hacer, y se
    /// borra al terminar. Si la aplicación se cierra entre el paso 1 y el 3, la descarga se queda con
    /// el nombre temporal (oculto para el escáner) en la carpeta de la biblioteca, y la entrada de la
    /// bandeja se poda en el siguiente análisis: no se pierde, pero nadie la encontraría. Con lo
    /// apuntado, <see cref="Recuperar"/> la devuelve a la bandeja. Si no se puede apuntar, no se empieza.
    /// </summary>
    /// <param name="enviarPapelera">Envía a la papelera (Services.Papelera.Enviar, que se niega en las unidades sin papelera).</param>
    /// <param name="anotar">Anota un registro en el historial para deshacer; devuelve "" o el motivo del fallo.</param>
    /// <param name="carpetaDiario">Dónde se apunta la sustitución en curso (ver <see cref="CarpetaDiario"/>); null, sin apuntar.</param>
    public static ResultadoSustitucion Ejecutar(string nueva, string vieja, Func<string, (bool Ok, string Error)> enviarPapelera,
                                                Func<UndoRecord, string> anotar, string? carpetaDiario = null)
    {
        var destino = Destino(vieja, nueva);
        if (!File.Exists(nueva)) return Fallo("La descarga ya no está en la bandeja. No se ha cambiado nada.");
        if (!File.Exists(vieja)) return Fallo($"La copia de la biblioteca ya no está en «{vieja}». No se ha cambiado nada.");
        var mismaRuta = string.Equals(destino, vieja, StringComparison.OrdinalIgnoreCase);
        if (!mismaRuta && (File.Exists(destino) || Directory.Exists(destino)))
            return Fallo($"En la carpeta de la biblioteca ya hay un archivo llamado «{Path.GetFileName(destino)}»; no se sobrescribe. No se ha cambiado nada.");

        // Extensión que el escáner no reconoce como audio: si se escanea mientras tanto, no entra.
        var id = Guid.NewGuid().ToString("N");
        var temporal = Path.Combine(Path.GetDirectoryName(vieja) ?? "", $".beattag-sustituir-{id}.tmp");

        string? diario = null;
        if (carpetaDiario != null)
        {
            diario = Path.Combine(carpetaDiario, id + ".json");
            var errDiario = ArchivoDatos.EscribirJson(diario, new DiarioSustitucion { Temporal = temporal, Bandeja = nueva, Vieja = vieja });
            if (errDiario.Length > 0)
                return Fallo($"No se pudo apuntar la sustitución antes de empezar ({errDiario}). No se ha cambiado nada.");
        }
        try { return Pasos(); }
        finally
        {
            // Mientras el temporal exista, lo apuntado sigue haciendo falta para recuperarlo.
            if (diario != null && !File.Exists(temporal))
                try { File.Delete(diario); } catch { /* se recoge en el siguiente análisis de la bandeja */ }
        }

        ResultadoSustitucion Pasos()
        {
            // 1. A la carpeta de la biblioteca, todavía con un nombre temporal.
            try { File.Move(nueva, temporal); }
            catch (Exception e)
            {
                // Una copia entre discos que falló a medias puede dejar el temporal: el original sigue en la bandeja.
                if (File.Exists(nueva)) try { if (File.Exists(temporal)) File.Delete(temporal); } catch { }
                return Fallo($"No se pudo llevar la descarga a la carpeta de la biblioteca ({e.Message}). No se ha cambiado nada.");
            }

            // 2. La copia antigua, a la papelera.
            (bool Ok, string Error) papelera;
            try { papelera = enviarPapelera(vieja); }
            catch (Exception e) { papelera = (false, e.Message); }
            if (!papelera.Ok)
            {
                var motivo = string.IsNullOrWhiteSpace(papelera.Error) ? "motivo desconocido." : papelera.Error.Trim();
                return Fallo($"No se sustituye: la copia de la biblioteca no se pudo enviar a la papelera. {motivo} "
                             + Devolver(temporal, nueva, "No se ha cambiado nada."));
            }

            // 3. Ocupa el lugar de la antigua.
            try { File.Move(temporal, destino); }
            catch (Exception e)
            {
                return Fallo($"La copia antigua está en la papelera, pero la descarga no pudo ocupar su lugar ({e.Message}). "
                             + Devolver(temporal, nueva, "La descarga sigue en la bandeja.")
                             + " Recupera la copia antigua desde la papelera si la necesitas.", viejaEnPapelera: true);
            }

            // 4. Queda anotado para deshacer.
            string errAnotar;
            try { errAnotar = anotar(new UndoRecord { OrigPath = nueva, FinalPath = destino, Renamed = true }) ?? ""; }
            catch (Exception e) { errAnotar = e.Message; }
            if (errAnotar.Length > 0)
                return Fallo($"El cambio no se pudo anotar para deshacer ({errAnotar}). "
                             + Devolver(destino, nueva, "La descarga vuelve a la bandeja.")
                             + " La copia antigua está en la papelera, desde donde puedes recuperarla.", viejaEnPapelera: true);

            return new ResultadoSustitucion(destino, "", true);
        }

        ResultadoSustitucion Fallo(string error, bool viejaEnPapelera = false) => new(destino, error, viejaEnPapelera);
    }

    /// <summary>La carpeta donde se apuntan las sustituciones en curso, dentro de la de datos de la aplicación.</summary>
    public static string CarpetaDiario(string carpetaDatos) => Path.Combine(carpetaDatos, "bandeja-sustituciones");

    /// <summary>Lo que se apunta antes de sustituir (ver <see cref="Ejecutar"/>).</summary>
    public sealed class DiarioSustitucion
    {
        /// <summary>El nombre temporal que lleva la descarga en la carpeta de la biblioteca entre los pasos 1 y 3.</summary>
        public string Temporal { get; set; } = "";
        /// <summary>Dónde estaba la descarga en la bandeja.</summary>
        public string Bandeja { get; set; } = "";
        /// <summary>La copia de la biblioteca que se iba a sustituir.</summary>
        public string Vieja { get; set; } = "";
    }

    /// <summary>
    /// Recoge las sustituciones que se quedaron a medias porque la aplicación se cerró (ver
    /// <see cref="Ejecutar"/>): la descarga que se quedó con el nombre temporal vuelve a la bandeja
    /// con su nombre. Devuelve una frase por cada una que ha hecho falta recoger, para el estado y el
    /// registro. No lanza: lo que no se pueda recoger ahora se intenta en el siguiente análisis.
    /// </summary>
    public static IReadOnlyList<string> Recuperar(string carpetaDiario)
    {
        var avisos = new List<string>();
        string[] diarios;
        try { diarios = Directory.Exists(carpetaDiario) ? Directory.GetFiles(carpetaDiario, "*.json") : Array.Empty<string>(); }
        catch (Exception e) { return new[] { $"No se pudo revisar si quedó alguna sustitución a medias ({e.Message})." }; }

        foreach (var diario in diarios)
        {
            var leido = ArchivoDatos.LeerJson<DiarioSustitucion>(diario);
            var d = leido.Datos;
            if (d == null || d.Temporal.Length == 0)
            {
                // Ilegible: ArchivoDatos ya lo ha apartado como «.ilegible_<fecha>» y lo dice.
                if (leido.Aviso.Length > 0) avisos.Add(leido.Aviso);
                continue;
            }
            var aviso = RecuperarUna(d);
            if (aviso.Length > 0) avisos.Add(aviso);
            if (!File.Exists(d.Temporal))
                try { File.Delete(diario); } catch { /* se vuelve a intentar la próxima vez */ }
        }
        return avisos;
    }

    private static string RecuperarUna(DiarioSustitucion d)
    {
        // Terminó (el temporal ya ocupa el lugar de la antigua) o no llegó a empezar: no hay nada que recoger.
        if (!File.Exists(d.Temporal)) return "";
        var nombre = Path.GetFileName(d.Bandeja);
        try
        {
            // El original sigue en la bandeja: el temporal es una copia que no llegó a completarse
            // entre dos discos (File.Move solo borra el original cuando la copia está entera).
            if (File.Exists(d.Bandeja))
            {
                File.Delete(d.Temporal);
                return "";
            }

            var carpetaBandeja = Path.GetDirectoryName(d.Bandeja) ?? "";
            string destino;
            if (carpetaBandeja.Length > 0 && Directory.Exists(carpetaBandeja)) destino = d.Bandeja;
            else
            {
                // La carpeta de la bandeja ya no existe: se le devuelve su nombre donde está, a la vista.
                destino = Path.Combine(Path.GetDirectoryName(d.Temporal) ?? "", nombre);
                if (File.Exists(destino) || Directory.Exists(destino))
                    destino = Path.Combine(Path.GetDirectoryName(d.Temporal) ?? "",
                                           $"{Path.GetFileNameWithoutExtension(nombre)} (recuperada){Path.GetExtension(nombre)}");
            }
            File.Move(d.Temporal, destino);
            var texto = string.Equals(destino, d.Bandeja, StringComparison.OrdinalIgnoreCase)
                ? $"Una sustitución se interrumpió al cerrarse la aplicación: la descarga «{nombre}» ha vuelto a la bandeja."
                : $"Una sustitución se interrumpió al cerrarse la aplicación y la carpeta de la bandeja ya no existe: la descarga está en «{destino}».";
            if (!File.Exists(d.Vieja))
                texto += $" La copia de la biblioteca («{Path.GetFileName(d.Vieja)}») ya estaba en la papelera, desde donde puedes recuperarla.";
            return texto;
        }
        catch (Exception e)
        {
            return $"Una sustitución se interrumpió al cerrarse la aplicación y la descarga «{nombre}» sigue en «{d.Temporal}»: "
                 + $"no se pudo devolver a la bandeja ({e.Message}). Se volverá a intentar en el siguiente análisis.";
        }
    }

    /// <summary>Devuelve un archivo a la bandeja tras un fallo y dice cómo ha quedado.</summary>
    private static string Devolver(string desde, string bandeja, string siSale)
    {
        try
        {
            File.Move(desde, bandeja);
            return siSale;
        }
        catch (Exception e)
        {
            return $"Tampoco se pudo devolver la descarga a la bandeja ({e.Message}): está en «{desde}».";
        }
    }
}
