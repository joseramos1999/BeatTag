using Etiquetador.Core.Providers;

namespace Etiquetador.Core.Dj;

/// <summary>Qué clase de lista se sigue.</summary>
public enum TipoListaSeguida
{
    /// <summary>Una playlist o un álbum de Spotify o Deezer, por su enlace.</summary>
    Playlist,

    /// <summary>Un chart de un país (Tendencias), con su fuente y su tamaño.</summary>
    Chart,
}

/// <summary>Una canción de una lista seguida, tal como llegó en una revisión.</summary>
public sealed class PistaLista
{
    public string Artista { get; set; } = "";
    public string Titulo { get; set; } = "";

    /// <summary>
    /// Lo que identifica la pista en su plataforma: el ID de Spotify o el enlace de Deezer. Vacío si la
    /// fuente no lo da.
    /// </summary>
    public string IdPista { get; set; } = "";

    /// <summary>Enlace a la canción, para Pendientes.</summary>
    public string Enlace { get; set; } = "";

    /// <summary>Estaba en la biblioteca cuando se revisó la lista.</summary>
    public bool LaTienes { get; set; }
}

/// <summary>Lo que tenía una lista en un momento: las claves de sus canciones y cuándo se leyó.</summary>
public sealed class InstantaneaLista
{
    public DateTime Fecha { get; set; }
    public int Canciones { get; set; }
    public List<string> Claves { get; set; } = new();
}

/// <summary>
/// Una playlist o un chart que el usuario sigue, con lo necesario para volver a leerlo sin que lo
/// tenga abierto y con lo que tenía las veces anteriores.
/// </summary>
public sealed class ListaSeguida
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public TipoListaSeguida Tipo { get; set; }

    /// <summary>Identifica la lista (ver <see cref="ListasSeguidas.ClavePlaylist"/> y <see cref="ListasSeguidas.ClaveChart"/>).</summary>
    public string Clave { get; set; } = "";
    public string Nombre { get; set; } = "";
    public DateTime Alta { get; set; }

    // --- Playlist ---
    public string Enlace { get; set; } = "";

    // --- Chart ---
    /// <summary>Nombre de <see cref="ChartSource"/>: «Spotify» o «Deezer».</summary>
    public string Fuente { get; set; } = "";
    public string Pais { get; set; } = "";
    public string CodigoPais { get; set; } = "";
    public long ListaPais { get; set; }
    public int Tamano { get; set; }

    // --- Revisiones ---

    /// <summary>
    /// La lectura de la última revisión de un día ANTERIOR al de <see cref="Ultima"/>: contra ella se
    /// decide qué es nuevo. Null hasta que la lista se revisa un día distinto del día en que se empezó
    /// a seguir.
    /// </summary>
    public InstantaneaLista? Referencia { get; set; }

    /// <summary>La lectura más reciente.</summary>
    public InstantaneaLista? Ultima { get; set; }

    /// <summary>Cuándo se revisó por última vez (al abrirla o con «Actualizar todas»).</summary>
    public DateTime? Revisada { get; set; }

    /// <summary>Las que entraron desde <see cref="Referencia"/>, según la última revisión.</summary>
    public List<PistaLista> Nuevas { get; set; } = new();

    /// <summary>
    /// Por qué la última lectura puede no estar completa («Spotify solo deja leer las 100 primeras…»).
    /// Vacío si se leyó entera.
    /// </summary>
    public string Aviso { get; set; } = "";

    /// <summary>Copia profunda: modificarla no cambia la guardada.</summary>
    public ListaSeguida Copia() => new()
    {
        Id = Id, Tipo = Tipo, Clave = Clave, Nombre = Nombre, Alta = Alta, Enlace = Enlace,
        Fuente = Fuente, Pais = Pais, CodigoPais = CodigoPais, ListaPais = ListaPais, Tamano = Tamano,
        Referencia = CopiaDe(Referencia), Ultima = CopiaDe(Ultima), Revisada = Revisada, Aviso = Aviso,
        Nuevas = Nuevas.Select(n => new PistaLista
        {
            Artista = n.Artista, Titulo = n.Titulo, IdPista = n.IdPista, Enlace = n.Enlace, LaTienes = n.LaTienes,
        }).ToList(),
    };

    private static InstantaneaLista? CopiaDe(InstantaneaLista? i)
        => i == null ? null : new InstantaneaLista { Fecha = i.Fecha, Canciones = i.Canciones, Claves = new List<string>(i.Claves) };
}

/// <summary>Qué hay de nuevo en una lectura de una lista seguida.</summary>
/// <param name="Desde">Fecha de la lectura con la que se compara. Null si todavía no hay con qué comparar.</param>
/// <param name="Nuevas">Posiciones (desde 0, en el orden de la lista leída) de las canciones nuevas.</param>
public sealed record NovedadesLista(DateTime? Desde, IReadOnlyList<int> Nuevas)
{
    public static readonly NovedadesLista Ninguna = new(null, Array.Empty<int>());
}

/// <summary>Reglas puras de las listas seguidas: cómo se identifica una lista y una canción, y qué es nuevo.</summary>
public static class ListasSeguidas
{
    /// <summary>
    /// La misma playlist pegada de formas distintas («…/intl-es/playlist/ID?si=…», «spotify:playlist:ID»)
    /// es la misma lista. Un enlace corto que no se puede reconocer sin seguirlo se identifica por sí
    /// mismo: quien lo ha leído ya resuelto debe usar <see cref="ClavePlaylist(PlaylistLink)"/>.
    /// </summary>
    public static string ClavePlaylist(string? enlace)
    {
        var e = (enlace ?? "").Trim();
        return PlaylistLinkParser.Parse(e) is { } l ? ClavePlaylist(l) : "enlace:" + e;
    }

    /// <summary>
    /// La clave de una lista ya reconocida (o resuelta desde un enlace corto). Es la misma que da el
    /// enlace largo: «spotify.link/…» y «open.spotify.com/playlist/ID» son la misma lista seguida.
    /// </summary>
    public static string ClavePlaylist(PlaylistLink l)
        // El ID de Spotify distingue mayúsculas: no se pasa a minúsculas.
        => $"{(l.Plataforma == PlataformaLista.Deezer ? "deezer" : "spotify")}:{l.Tipo}:{l.Id}";

    /// <summary>
    /// El enlace largo de una lista, para guardarlo en vez del corto que se pegó: los enlaces cortos
    /// de «Compartir» caducan, y así «Actualizar todas» no tiene que seguirlos cada vez.
    /// </summary>
    public static string EnlaceCanonico(PlaylistLink l)
        => l.Plataforma == PlataformaLista.Deezer
            ? $"https://www.deezer.com/{l.Tipo}/{l.Id}"
            : $"https://open.spotify.com/{l.Tipo}/{l.Id}";

    /// <summary>
    /// Un chart es su fuente, su país y su tamaño: el Top 100 contra una lectura del Top 50 daría
    /// cincuenta «nuevas» que no lo son.
    /// </summary>
    public static string ClaveChart(ChartSource fuente, string codigoPais, long listaPais, int tamano)
        => fuente == ChartSource.Spotify
            ? $"chart:spotify:{(codigoPais ?? "").Trim().ToLowerInvariant()}:{tamano}"
            : $"chart:deezer:{listaPais}:{tamano}";

    /// <summary>
    /// Las claves de una canción en una instantánea: la de su ID en la plataforma, si lo hay, y la de
    /// artista y título normalizados. Una canción es nueva solo si NINGUNA de las suyas estaba: así no
    /// sale como nueva porque la plataforma le cambió el ID (una reedición) ni porque retocó el título
    /// («(feat. X)» por «(with X)»).
    ///
    /// El título va normalizado pero SIN limpiar versiones ni números: la limpieza de búsqueda quita
    /// los números sueltos, y «Bzrp Music Sessions, Vol. 53» acabaría con la misma clave que la «Vol.
    /// 52». En una lista, la sesión nueva tiene que salir como nueva.
    /// </summary>
    public static IReadOnlyList<string> ClavesPista(PistaLista p)
    {
        var claves = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(p.IdPista)) claves.Add("id:" + p.IdPista.Trim());
        var a = TextUtils.NkClave(p.Artista);
        var t = TextUtils.NkClave(p.Titulo);
        if (a.Length > 0 || t.Length > 0) claves.Add("at:" + a + "|" + t);
        return claves;
    }

    /// <summary>Las posiciones de las canciones de <paramref name="pistas"/> que no estaban en la referencia.</summary>
    public static IReadOnlyList<int> Nuevas(InstantaneaLista? referencia, IReadOnlyList<PistaLista> pistas)
    {
        if (referencia == null) return Array.Empty<int>();
        var antes = new HashSet<string>(referencia.Claves, StringComparer.Ordinal);
        var nuevas = new List<int>();
        for (var i = 0; i < pistas.Count; i++)
        {
            var claves = ClavesPista(pistas[i]);
            if (claves.Count > 0 && !claves.Any(antes.Contains)) nuevas.Add(i);
        }
        return nuevas;
    }

    /// <summary>La instantánea de una lectura.</summary>
    public static InstantaneaLista Instantanea(IReadOnlyList<PistaLista> pistas, DateTime fecha)
        => new()
        {
            Fecha = fecha,
            Canciones = pistas.Count,
            Claves = pistas.SelectMany(ClavesPista).Distinct(StringComparer.Ordinal).ToList(),
        };

    /// <summary>
    /// Apunta una lectura de la lista y dice qué es nuevo. La comparación es con la última lectura de un
    /// DÍA ANTERIOR: abrir dos veces la misma lista en una tarde no borra las novedades de la mañana, y
    /// al día siguiente se comparan con lo último que se vio. Las listas que interesan (charts diarios,
    /// playlists editoriales) cambian como mucho una vez al día.
    ///
    /// <paramref name="fecha"/> es la del contenido: la de la descarga de un chart guardado, no la de
    /// ahora. Una lectura más antigua que la última apuntada (un chart servido de la copia guardada
    /// porque no había red) no avanza nada.
    ///
    /// Con <paramref name="completa"/> a false (Deezer dejó de responder a mitad) se compara pero no se
    /// apunta: la próxima lectura entera daría por nuevas las canciones que faltaron esta vez.
    ///
    /// Una lectura VACÍA nunca se apunta, la marque quien la marque como completa. Visto en la revisión:
    /// sin red y sin copia guardada, Tendencias recibía un chart de 0 canciones y lo apuntaba como la
    /// última lectura; al día siguiente era la referencia, las 50 canciones del chart salían como
    /// nuevas y «Añadir a pendientes las nuevas que faltan» llenaba Pendientes de canciones que no lo eran.
    /// </summary>
    public static NovedadesLista Registrar(ListaSeguida lista, IReadOnlyList<PistaLista> pistas, DateTime fecha, bool completa)
    {
        if (completa && pistas.Count > 0 && (lista.Ultima == null || fecha > lista.Ultima.Fecha))
        {
            if (lista.Ultima != null && lista.Ultima.Fecha.Date < fecha.Date) lista.Referencia = lista.Ultima;
            lista.Ultima = Instantanea(pistas, fecha);
        }
        return Comparar(lista, pistas);
    }

    /// <summary>
    /// Qué canciones de <paramref name="pistas"/> son nuevas según lo que la lista seguida ya tiene
    /// guardado, sin apuntar nada. Sirve para poner al día una lista que se está viendo cuando otra
    /// página la revisa («Actualizar todas» en Pendientes). Sin lista o sin referencia, ninguna.
    /// </summary>
    public static NovedadesLista Comparar(ListaSeguida? lista, IReadOnlyList<PistaLista> pistas)
        => lista?.Referencia is { } referencia
            ? new NovedadesLista(referencia.Fecha, Nuevas(referencia, pistas))
            : NovedadesLista.Ninguna;
}

/// <summary>Las listas que el usuario sigue, en el orden en que empezó a seguirlas.</summary>
public sealed class AlmacenListasSeguidas
{
    /// <summary>Nombre del archivo en la carpeta de datos.</summary>
    public const string NombreArchivo = "listas-seguidas.json";

    private readonly string _archivo;
    private readonly object _cerrojo = new();
    private readonly List<ListaSeguida> _todas;

    public string AvisoCarga { get; }

    /// <summary>No se pudo leer ni apartar el archivo al abrirlo: <see cref="Guardar"/> no lo pisa.</summary>
    public bool SoloLectura { get; }

    public AlmacenListasSeguidas(string archivo)
    {
        _archivo = archivo;
        var leidas = ArchivoJson.Leer(archivo, () => new List<ListaSeguida>(), out var aviso, out var soloLectura);
        _todas = leidas.Where(l => l != null && !string.IsNullOrWhiteSpace(l.Clave)).Select(Sanear).ToList();
        AvisoCarga = aviso;
        SoloLectura = soloLectura;
    }

    private static ListaSeguida Sanear(ListaSeguida l)
    {
        l.Id = string.IsNullOrWhiteSpace(l.Id) ? Guid.NewGuid().ToString("N") : l.Id;
        l.Nombre ??= "";
        l.Enlace ??= "";
        l.Fuente ??= "";
        l.Pais ??= "";
        l.CodigoPais ??= "";
        l.Aviso ??= "";
        l.Nuevas = (l.Nuevas ?? new List<PistaLista>()).Where(n => n != null).ToList();
        if (l.Referencia != null) l.Referencia.Claves ??= new List<string>();
        if (l.Ultima != null) l.Ultima.Claves ??= new List<string>();
        return l;
    }

    public int Count { get { lock (_cerrojo) return _todas.Count; } }

    /// <summary>Copia de todas. Modificarlas no cambia nada.</summary>
    public IReadOnlyList<ListaSeguida> Todas()
    {
        lock (_cerrojo) return _todas.Select(l => l.Copia()).ToList();
    }

    /// <summary>La lista seguida con esa clave, o null si no se sigue. Es una copia.</summary>
    public ListaSeguida? Buscar(string clave)
    {
        lock (_cerrojo) return _todas.FirstOrDefault(l => l.Clave == clave)?.Copia();
    }

    public bool Sigue(string clave)
    {
        lock (_cerrojo) return _todas.Any(l => l.Clave == clave);
    }

    /// <summary>
    /// Empieza a seguir una lista, con lo que tiene ahora como primera lectura. Si ya se seguía, no
    /// cambia nada. Devuelve la guardada (una copia).
    /// </summary>
    public ListaSeguida Seguir(ListaSeguida nueva, IReadOnlyList<PistaLista> pistas, DateTime fecha)
    {
        lock (_cerrojo)
        {
            var ya = _todas.FirstOrDefault(l => l.Clave == nueva.Clave);
            if (ya != null) return ya.Copia();
            var l = nueva.Copia();
            l.Ultima = ListasSeguidas.Instantanea(pistas, fecha);
            l.Referencia = null;
            l.Nuevas = new List<PistaLista>();
            l.Revisada = fecha;
            _todas.Add(l);
            return l.Copia();
        }
    }

    /// <summary>
    /// Pasa una lista seguida de una clave a otra, con su enlace nuevo. Es para las playlists que se
    /// siguieron pegando un enlace corto («spotify.link/…»): antes se identificaban por ese texto y no
    /// se reconocían al pegar el enlace largo de la misma lista. Si ya se sigue con la clave nueva, o
    /// no se sigue con la antigua, no cambia nada. Devuelve si la ha cambiado.
    /// </summary>
    public bool CambiarClave(string antigua, string nueva, string enlace)
    {
        if (string.IsNullOrWhiteSpace(nueva) || antigua == nueva) return false;
        lock (_cerrojo)
        {
            if (_todas.Any(l => l.Clave == nueva)) return false;
            var l = _todas.FirstOrDefault(x => x.Clave == antigua);
            if (l == null) return false;
            l.Clave = nueva;
            if (!string.IsNullOrWhiteSpace(enlace)) l.Enlace = enlace.Trim();
            return true;
        }
    }

    /// <summary>Deja de seguir las indicadas. Devuelve cuántas.</summary>
    public int DejarDeSeguir(IEnumerable<string> ids)
    {
        var quitar = new HashSet<string>(ids, StringComparer.Ordinal);
        lock (_cerrojo) return _todas.RemoveAll(l => quitar.Contains(l.Id));
    }

    /// <summary>
    /// Apunta una lectura de una lista seguida (ver <see cref="ListasSeguidas.Registrar"/>) y guarda las
    /// nuevas para poder mandarlas a pendientes más tarde. Si la lista no se sigue, no hace nada.
    ///
    /// Una lectura VACÍA tampoco toca nada: ni la fecha de revisión, ni el aviso, ni las nuevas
    /// guardadas. Antes ListasSeguidas.Registrar ya no la apuntaba, pero aquí se vaciaban las nuevas
    /// que «Añadir a pendientes las nuevas que faltan» tenía guardadas (y la lista salía revisada hoy).
    /// La protección no puede depender de cada llamador: una playlist de Deezer vacía llega como
    /// lectura correcta de 0 canciones.
    /// </summary>
    /// <param name="nombre">El nombre que trae ahora la lista; vacío para no cambiarlo.</param>
    /// <param name="aviso">Por qué la lectura puede estar incompleta, o vacío.</param>
    public NovedadesLista Registrar(string clave, IReadOnlyList<PistaLista> pistas, DateTime fecha, bool completa,
                                    DateTime ahora, string nombre = "", string aviso = "")
    {
        if (pistas.Count == 0) return NovedadesLista.Ninguna;
        lock (_cerrojo)
        {
            var l = _todas.FirstOrDefault(x => x.Clave == clave);
            if (l == null) return NovedadesLista.Ninguna;
            var novedades = ListasSeguidas.Registrar(l, pistas, fecha, completa);
            l.Revisada = ahora;
            l.Aviso = aviso ?? "";
            if (!string.IsNullOrWhiteSpace(nombre)) l.Nombre = nombre.Trim();
            l.Nuevas = novedades.Nuevas.Select(i => pistas[i]).Select(p => new PistaLista
            {
                Artista = p.Artista, Titulo = p.Titulo, IdPista = p.IdPista, Enlace = p.Enlace, LaTienes = p.LaTienes,
            }).ToList();
            return novedades;
        }
    }

    /// <summary>Devuelve "" si fue bien, o el motivo del fallo. En solo lectura no escribe nada.</summary>
    public string Guardar()
    {
        if (SoloLectura) return ArchivoJson.AvisoSoloLectura(_archivo);
        List<ListaSeguida> copia;
        lock (_cerrojo) copia = _todas.Select(l => l.Copia()).ToList();
        return ArchivoJson.Escribir(_archivo, copia);
    }
}
