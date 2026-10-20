using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Etiquetador.Core.Dj;

/// <summary>
/// Una canción que te falta y quieres conseguir: sale de una lista de fuera (un chart, una playlist)
/// y se tacha sola cuando aparece en la biblioteca.
///
/// El almacén es genérico a propósito: no sabe de Tendencias ni de Playlists. El origen es un texto
/// libre («Top 50 de Spain (Spotify)», el nombre de una playlist) para que cualquier otra fuente que
/// proponga canciones que faltan pueda alimentarlo sin cambiar el formato del archivo.
/// </summary>
public sealed class Pendiente
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Artista { get; set; } = "";
    public string Titulo { get; set; } = "";

    /// <summary>De dónde salió: el nombre de la lista o del chart.</summary>
    public string Origen { get; set; } = "";

    /// <summary>Cuándo se añadió a la lista.</summary>
    public DateTime Alta { get; set; }

    /// <summary>Enlace a la canción en la plataforma de la que salió (Spotify, Deezer), si lo hay.</summary>
    public string Enlace { get; set; } = "";

    /// <summary>Cuándo apareció en la biblioteca. Null mientras siga pendiente.</summary>
    public DateTime? Conseguida { get; set; }

    /// <summary>El archivo de la biblioteca con el que se tachó. Vacío mientras siga pendiente.</summary>
    public string Archivo { get; set; } = "";

    /// <summary>
    /// Archivos que el usuario ha dicho que NO son esta canción («Volver a pendiente»). El cruce es el
    /// de Tendencias, por artista y título limpio, y un remix o una versión con otro título parecido
    /// pueden darla por conseguida. Sin esta lista, el siguiente escaneo la volvería a tachar con el
    /// mismo archivo y la corrección del usuario no serviría de nada.
    /// </summary>
    public List<string> Descartados { get; set; } = new();

    /// <summary>
    /// La fecha en que se había tachado con el último archivo descartado. Con ella, «Volver a contar el
    /// archivo descartado» deshace un «Volver a pendiente» pulsado por error y la deja como estaba, con
    /// su fecha de entonces y no con la de hoy. Null si no hay nada que deshacer.
    /// </summary>
    public DateTime? ConseguidaAntes { get; set; }

    [JsonIgnore] public bool EstaConseguida => Conseguida != null;

    public Pendiente Copia() => new()
    {
        Id = Id, Artista = Artista, Titulo = Titulo, Origen = Origen, Alta = Alta, Enlace = Enlace,
        Conseguida = Conseguida, Archivo = Archivo, Descartados = new List<string>(Descartados),
        ConseguidaAntes = ConseguidaAntes,
    };
}

/// <summary>Una canción que se quiere añadir a pendientes.</summary>
public sealed record NuevaPendiente(string Artista, string Titulo, string Origen, string Enlace = "");

/// <summary>Lo que dio añadir a pendientes.</summary>
/// <param name="Anadidas">Las que entraron.</param>
/// <param name="YaEstaban">Las que ya estaban pendientes (o venían repetidas en la misma tanda).</param>
public sealed record AltaPendientes(int Anadidas, int YaEstaban);

/// <summary>
/// La lista de pendientes, en el orden en que se añadieron.
///
/// Igual que las fichas y las colecciones, es trabajo del usuario y no se regenera: se escribe de
/// forma atómica y un archivo ilegible se aparta en vez de pisarse (ver <see cref="ArchivoJson"/>).
/// </summary>
public sealed class AlmacenPendientes
{
    /// <summary>Nombre del archivo en la carpeta de datos.</summary>
    public const string NombreArchivo = "pendientes.json";

    private readonly string _archivo;
    private readonly object _cerrojo = new();
    private readonly List<Pendiente> _todas;
    private readonly Func<string?, string?, IReadOnlyList<string>> _claves;

    public string AvisoCarga { get; }

    /// <summary>No se pudo leer ni apartar el archivo al abrirlo: <see cref="Guardar"/> no lo pisa.</summary>
    public bool SoloLectura { get; }

    /// <param name="archivo">Ruta del JSON.</param>
    /// <param name="claves">
    /// Cómo se reconoce que dos pendientes son la misma canción: las claves «artista|título» con las que
    /// Tendencias cruza la lista con la biblioteca (una por artista del crédito, con los alias). Así
    /// «Bizarrap, Shakira» y «Shakira, Bizarrap» no entran dos veces. Sin ella, artista y título
    /// normalizados tal cual.
    /// </param>
    public AlmacenPendientes(string archivo, Func<string?, string?, IReadOnlyList<string>>? claves = null)
    {
        _archivo = archivo;
        _claves = claves ?? ClaveSimple;
        var leidas = ArchivoJson.Leer(archivo, () => new List<Pendiente>(), out var aviso, out var soloLectura);
        // Un archivo editado a mano puede traer nulos donde se esperan listas o textos.
        _todas = leidas.Where(p => p != null).Select(Sanear).ToList();
        AvisoCarga = aviso;
        SoloLectura = soloLectura;
    }

    private static Pendiente Sanear(Pendiente p)
    {
        p.Id = string.IsNullOrWhiteSpace(p.Id) ? Guid.NewGuid().ToString("N") : p.Id;
        p.Artista ??= "";
        p.Titulo ??= "";
        p.Origen ??= "";
        p.Enlace ??= "";
        p.Archivo ??= "";
        p.Descartados = (p.Descartados ?? new List<string>()).Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
        return p;
    }

    private static IReadOnlyList<string> ClaveSimple(string? artista, string? titulo)
    {
        var a = TextUtils.NkClave(artista);
        var t = TextUtils.NkClave(titulo);
        return a.Length == 0 || t.Length == 0 ? Array.Empty<string>() : new[] { a + "|" + t };
    }

    public int Count { get { lock (_cerrojo) return _todas.Count; } }

    /// <summary>Copia de todas, en el orden en que se añadieron. Modificarlas no cambia nada.</summary>
    public IReadOnlyList<Pendiente> Todas()
    {
        lock (_cerrojo) return _todas.Select(p => p.Copia()).ToList();
    }

    /// <summary>
    /// Añade las que no estén ya PENDIENTES. Una que se consiguió en su día y vuelve a faltar (se borró
    /// el archivo, por ejemplo) entra de nuevo: la conseguida se queda como historial.
    /// </summary>
    public AltaPendientes Anadir(IEnumerable<NuevaPendiente> nuevas, DateTime ahora)
    {
        int anadidas = 0, yaEstaban = 0;
        lock (_cerrojo)
        {
            var ocupadas = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in _todas.Where(p => !p.EstaConseguida))
                ocupadas.UnionWith(ClavesDe(p.Artista, p.Titulo));

            foreach (var n in nuevas)
            {
                var artista = (n.Artista ?? "").Trim();
                var titulo = (n.Titulo ?? "").Trim();
                if (titulo.Length == 0) continue;   // sin título no hay nada que buscar ni que tachar
                var claves = ClavesDe(artista, titulo);
                if (claves.Any(ocupadas.Contains)) { yaEstaban++; continue; }
                ocupadas.UnionWith(claves);
                _todas.Add(new Pendiente
                {
                    Artista = artista, Titulo = titulo, Origen = (n.Origen ?? "").Trim(),
                    Enlace = (n.Enlace ?? "").Trim(), Alta = ahora,
                });
                anadidas++;
            }
        }
        return new AltaPendientes(anadidas, yaEstaban);
    }

    /// <summary>
    /// Las claves de una canción. Si el título o el artista se quedan en nada al normalizar, se usa
    /// el texto entero: así dos pendientes iguales sin clave tampoco se duplican.
    /// </summary>
    private IReadOnlyList<string> ClavesDe(string artista, string titulo)
    {
        var c = _claves(artista, titulo);
        return c.Count > 0 ? c : new[] { "~" + artista.ToLowerInvariant() + "|" + titulo.ToLowerInvariant() };
    }

    /// <summary>Quita las indicadas. Devuelve cuántas.</summary>
    public int Quitar(IEnumerable<string> ids)
    {
        var quitar = new HashSet<string>(ids, StringComparer.Ordinal);
        lock (_cerrojo) return _todas.RemoveAll(p => quitar.Contains(p.Id));
    }

    /// <summary>Quita todas las conseguidas. Devuelve cuántas.</summary>
    public int QuitarConseguidas()
    {
        lock (_cerrojo) return _todas.RemoveAll(p => p.EstaConseguida);
    }

    /// <summary>
    /// Tacha las indicadas con su archivo, si siguen pendientes (pudo quitarlas el usuario mientras se
    /// cruzaba con la biblioteca). Devuelve cuántas.
    /// </summary>
    public int MarcarConseguidas(IEnumerable<(string Id, string Ruta)> conseguidas, DateTime cuando)
    {
        var n = 0;
        lock (_cerrojo)
        {
            foreach (var (id, ruta) in conseguidas)
            {
                var p = _todas.FirstOrDefault(x => x.Id == id);
                if (p == null || p.EstaConseguida || string.IsNullOrWhiteSpace(ruta)) continue;
                p.Conseguida = cuando;
                p.Archivo = ruta;
                n++;
            }
        }
        return n;
    }

    /// <summary>
    /// Pone al día la ruta de las conseguidas cuyo archivo se ha renombrado o movido. La fecha en que
    /// se consiguió no cambia. Devuelve cuántas.
    /// </summary>
    public int CambiarArchivos(IEnumerable<(string Id, string Ruta)> movidas)
    {
        var n = 0;
        lock (_cerrojo)
        {
            foreach (var (id, ruta) in movidas)
            {
                var p = _todas.FirstOrDefault(x => x.Id == id);
                if (p == null || !p.EstaConseguida || string.IsNullOrWhiteSpace(ruta)
                    || string.Equals(p.Archivo, ruta, StringComparison.Ordinal)) continue;
                p.Archivo = ruta;
                n++;
            }
        }
        return n;
    }

    /// <summary>
    /// El usuario dice que el archivo con el que se tachó no es esta canción: vuelve a pendiente y ese
    /// archivo ya no la tachará. Devuelve si había algo que deshacer.
    /// </summary>
    public bool VolverAPendiente(string id)
    {
        lock (_cerrojo)
        {
            var p = _todas.FirstOrDefault(x => x.Id == id);
            if (p == null || !p.EstaConseguida) return false;
            if (p.Archivo.Length > 0)
            {
                // Al final de la lista: «Volver a contar el archivo descartado» deshace el último.
                p.Descartados.RemoveAll(d => string.Equals(d, p.Archivo, StringComparison.OrdinalIgnoreCase));
                p.Descartados.Add(p.Archivo);
                p.ConseguidaAntes = p.Conseguida;
            }
            p.Conseguida = null;
            p.Archivo = "";
            return true;
        }
    }

    /// <summary>
    /// Deshace el último «Volver a pendiente» de la canción: el último archivo descartado vuelve a
    /// contar. Si sigue pendiente y ese archivo está en la biblioteca (<paramref name="enBiblioteca"/>),
    /// se tacha otra vez con él y con la fecha de entonces. Si no está, sigue pendiente y el próximo
    /// cruce con la biblioteca decide. Devuelve el archivo que vuelve a contar ("" si no había ninguno
    /// descartado) y si la canción se ha vuelto a tachar con él.
    ///
    /// Sin esto, un «Volver a pendiente» pulsado por error sobre una conseguida correcta la dejaba
    /// pendiente para siempre: ese archivo ya no la tachaba y no había forma de sacarlo de la lista.
    /// </summary>
    public (string Archivo, bool Tachada) RecuperarDescartado(string id, Func<string, bool> enBiblioteca)
    {
        lock (_cerrojo)
        {
            var p = _todas.FirstOrDefault(x => x.Id == id);
            if (p == null || p.Descartados.Count == 0) return ("", false);
            var ultimo = p.Descartados[^1];
            p.Descartados.RemoveAt(p.Descartados.Count - 1);
            var tachada = !p.EstaConseguida && enBiblioteca(ultimo);
            if (tachada)
            {
                p.Conseguida = p.ConseguidaAntes ?? DateTime.Now;
                p.Archivo = ultimo;
            }
            // La fecha guardada era la del último descartado: para los anteriores no se sabe.
            p.ConseguidaAntes = null;
            return (ultimo, tachada);
        }
    }

    /// <summary>
    /// Devuelve a pendiente las conseguidas cuyo archivo ya no existe (se envió a la papelera o se
    /// borró), si siguen tachadas con ese mismo archivo. No lo apunta como descartado: el archivo no
    /// era una copia equivocada, simplemente ya no está, y si vuelve (se recupera de la papelera) la
    /// tachará otra vez. Devuelve cuántas.
    /// </summary>
    public int Reabrir(IEnumerable<(string Id, string Ruta)> sinArchivo)
    {
        var n = 0;
        lock (_cerrojo)
        {
            foreach (var (id, ruta) in sinArchivo)
            {
                var p = _todas.FirstOrDefault(x => x.Id == id);
                if (p == null || !p.EstaConseguida || !string.Equals(p.Archivo, ruta, StringComparison.Ordinal)) continue;
                p.Conseguida = null;
                p.Archivo = "";
                n++;
            }
        }
        return n;
    }

    /// <summary>Devuelve "" si fue bien, o el motivo del fallo. En solo lectura no escribe nada.</summary>
    public string Guardar()
    {
        if (SoloLectura) return ArchivoJson.AvisoSoloLectura(_archivo);
        List<Pendiente> copia;
        lock (_cerrojo) copia = _todas.Select(p => p.Copia()).ToList();
        return ArchivoJson.Escribir(_archivo, copia);
    }
}

/// <summary>
/// Qué pendientes tachar, qué conseguidas cambiaron de archivo y cuáles se quedaron sin él, frente a
/// la biblioteca.
/// </summary>
/// <param name="SinArchivo">Conseguidas cuyo archivo ya no existe y no tienen otra copia: vuelven a pendiente.</param>
public sealed record CambiosPendientes(IReadOnlyList<(string Id, string Ruta)> Conseguidas,
                                       IReadOnlyList<(string Id, string Ruta)> Movidas,
                                       IReadOnlyList<(string Id, string Ruta)> SinArchivo)
{
    public bool Vacio => Conseguidas.Count == 0 && Movidas.Count == 0 && SinArchivo.Count == 0;
}

/// <summary>El cruce de las pendientes con la biblioteca. Puro: se ejecuta fuera del hilo de la interfaz.</summary>
public static class CrucePendientes
{
    /// <summary>
    /// Para cada pendiente, la copia de la biblioteca con la que casa (la misma búsqueda que marca «En
    /// la biblioteca» en Tendencias: alias, varios artistas por pista, y sin contar acapellas ni
    /// mashups). Un archivo que el usuario descartó para esa canción no la tacha.
    ///
    /// Las conseguidas cuyo archivo ya no está en la biblioteca (BeatTag lo renombró, o se movió de
    /// carpeta) se vuelven a buscar para que la ruta siga al archivo. Si no aparece otra copia y
    /// <paramref name="sinArchivo"/> confirma que el archivo ya no existe (se envió a la papelera o se
    /// borró), vuelve a pendiente: seguir diciendo «Conseguida» con un archivo que ya no está engaña.
    /// Si existe (se desmarcó su carpeta en Biblioteca) o no se puede saber, se queda como estaba.
    /// </summary>
    /// <param name="buscar">Artista y título → ruta de la mejor copia, o "" si no está en la biblioteca.</param>
    /// <param name="enBiblioteca">La ruta está en la biblioteca escaneada.</param>
    /// <param name="sinArchivo">
    /// El archivo ya no existe con seguridad (ver <see cref="ArchivoDesaparecido"/>). Toca el disco: solo
    /// se pregunta por las conseguidas que han salido de la biblioteca. Sin él, nunca se destacha.
    /// </param>
    public static CambiosPendientes Calcular(IReadOnlyList<Pendiente> pendientes, Func<string, string, string> buscar,
                                             Func<string, bool> enBiblioteca, Func<string, bool>? sinArchivo = null)
    {
        var conseguidas = new List<(string, string)>();
        var movidas = new List<(string, string)>();
        var perdidas = new List<(string, string)>();
        foreach (var p in pendientes)
        {
            if (p.EstaConseguida && (p.Archivo.Length == 0 || enBiblioteca(p.Archivo))) continue;
            var ruta = buscar(p.Artista, p.Titulo);
            if (ruta.Length == 0 || p.Descartados.Contains(ruta, StringComparer.OrdinalIgnoreCase))
            {
                if (p.EstaConseguida && sinArchivo != null && sinArchivo(p.Archivo)) perdidas.Add((p.Id, p.Archivo));
                continue;
            }
            if (p.EstaConseguida) movidas.Add((p.Id, ruta));
            else conseguidas.Add((p.Id, ruta));
        }
        return new CambiosPendientes(conseguidas, movidas, perdidas);
    }

    /// <summary>
    /// El archivo ya no existe Y su carpeta sí: se envió a la papelera o se borró. Si tampoco está la
    /// carpeta, puede ser un disco externo desconectado o una unidad de red sin conexión, y eso no
    /// quiere decir que la canción se haya perdido: no se da por desaparecido. Toca el disco: fuera del
    /// hilo de la interfaz.
    /// </summary>
    public static bool ArchivoDesaparecido(string ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta)) return false;
        try
        {
            var carpeta = Path.GetDirectoryName(ruta);
            return !string.IsNullOrEmpty(carpeta) && Directory.Exists(carpeta) && !File.Exists(ruta);
        }
        catch { return false; }   // una ruta ilegible no es motivo para destachar nada
    }
}

/// <summary>Las pendientes en texto plano o en CSV, para llevárselas a otro sitio.</summary>
public static class ExportacionPendientes
{
    /// <summary>
    /// Una línea «Artista - Título» por canción: es lo que se pega en el buscador de un pool o se le
    /// pasa a otra persona. Las conseguidas lo dicen al final de la línea.
    /// </summary>
    public static string Texto(IEnumerable<Pendiente> pendientes)
    {
        var sb = new StringBuilder();
        foreach (var p in pendientes)
        {
            sb.Append(p.Artista.Length > 0 ? $"{p.Artista} - {p.Titulo}" : p.Titulo);
            if (p.Conseguida is { } f) sb.Append("  · conseguida el ").Append(f.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
            sb.Append(Environment.NewLine);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Todas las columnas, separadas por «;» y entrecomilladas, como los demás informes de la
    /// aplicación: es lo que espera Excel en español. Las fechas, en formato ISO para que cualquier hoja
    /// de cálculo las ordene bien.
    /// </summary>
    public static string Csv(IEnumerable<Pendiente> pendientes)
    {
        var sb = new StringBuilder();
        sb.Append("estado;artista;titulo;origen;anadida;conseguida;archivo;enlace").Append("\r\n");
        foreach (var p in pendientes)
        {
            sb.Append(string.Join(";", new[]
            {
                TextUtils.CsvField(p.EstaConseguida ? "conseguida" : "pendiente"),
                TextUtils.CsvField(p.Artista), TextUtils.CsvField(p.Titulo), TextUtils.CsvField(p.Origen),
                TextUtils.CsvField(p.Alta.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                TextUtils.CsvField(p.Conseguida?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""),
                TextUtils.CsvField(p.Archivo), TextUtils.CsvField(p.Enlace),
            })).Append("\r\n");
        }
        return sb.ToString();
    }
}
