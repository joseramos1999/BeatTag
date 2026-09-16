using Etiquetador.Core.Analysis;

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

    public AlmacenBandeja(string archivo)
    {
        _archivo = archivo;
        var leidas = ArchivoJson.Leer(archivo, () => new Dictionary<string, EntradaBandeja>(), out var aviso);
        _entradas = new Dictionary<string, EntradaBandeja>(leidas, StringComparer.OrdinalIgnoreCase);
        AvisoCarga = aviso;
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

    /// <summary>Devuelve "" si fue bien, o el motivo del fallo.</summary>
    public string Guardar() => ArchivoJson.Escribir(_archivo, _entradas);
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
