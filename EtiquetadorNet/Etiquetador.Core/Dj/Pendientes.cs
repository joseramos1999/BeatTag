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

    [JsonIgnore] public bool EstaConseguida => Conseguida != null;

    public Pendiente Copia() => new()
    {
        Id = Id, Artista = Artista, Titulo = Titulo, Origen = Origen, Alta = Alta, Enlace = Enlace,
        Conseguida = Conseguida, Archivo = Archivo, Descartados = new List<string>(Descartados),
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
            if (p.Archivo.Length > 0 && !p.Descartados.Contains(p.Archivo, StringComparer.OrdinalIgnoreCase))
                p.Descartados.Add(p.Archivo);
            p.Conseguida = null;
            p.Archivo = "";
            return true;
        }
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

/// <summary>Qué pendientes tachar, y qué conseguidas cambiaron de archivo, frente a la biblioteca.</summary>
public sealed record CambiosPendientes(IReadOnlyList<(string Id, string Ruta)> Conseguidas,
                                       IReadOnlyList<(string Id, string Ruta)> Movidas)
{
    public bool Vacio => Conseguidas.Count == 0 && Movidas.Count == 0;
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
    /// carpeta) se vuelven a buscar para que la ruta siga al archivo. Si no aparece, la ruta se queda
    /// como estaba: puede ser un disco desconectado, y deshacer la tachadura por eso sería un error.
    /// </summary>
    /// <param name="buscar">Artista y título → ruta de la mejor copia, o "" si no la tienes.</param>
    /// <param name="enBiblioteca">La ruta está en la biblioteca escaneada.</param>
    public static CambiosPendientes Calcular(IReadOnlyList<Pendiente> pendientes, Func<string, string, string> buscar,
                                             Func<string, bool> enBiblioteca)
    {
        var conseguidas = new List<(string, string)>();
        var movidas = new List<(string, string)>();
        foreach (var p in pendientes)
        {
            if (p.EstaConseguida && (p.Archivo.Length == 0 || enBiblioteca(p.Archivo))) continue;
            var ruta = buscar(p.Artista, p.Titulo);
            if (ruta.Length == 0 || p.Descartados.Contains(ruta, StringComparer.OrdinalIgnoreCase)) continue;
            if (p.EstaConseguida) movidas.Add((p.Id, ruta));
            else conseguidas.Add((p.Id, ruta));
        }
        return new CambiosPendientes(conseguidas, movidas);
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
