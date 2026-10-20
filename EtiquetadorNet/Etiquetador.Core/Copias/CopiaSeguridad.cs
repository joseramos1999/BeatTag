using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Etiquetador.Core.Copias;

// ---------------------------------------------------------------------------------------------
// Copia de seguridad y traslado del trabajo manual (equipo «volumen_copias», tanda 4, T15)
// ---------------------------------------------------------------------------------------------
//
// POR QUÉ. El trabajo de un DJ en BeatTag (fichas, colecciones, lo aplicado y descartado en
// Enriquecer, las identificaciones por audio pagadas, pendientes, listas seguidas…) está repartido en
// una docena de archivos de la carpeta de datos. No había forma soportada de llevárselo a otro equipo
// ni de recuperarlo si se perdía: copiar config.net.json a mano a otro sistema dejaba credenciales
// cifradas que allí no se pueden leer, y un archivo dañado o un cierre a la fuerza (T3, T10) se
// llevaba decisiones tomadas a mano.
//
// Qué hace:
//   · «Copia de seguridad…» guarda un ZIP con esos archivos. config.net.json va SIN las claves de API
//     (los campos *Enc): cifradas solo sirven en el equipo y el usuario donde se guardaron, y una
//     copia no debe llevarlas a ningún sitio. Tampoco la posición de la ventana, que es del equipo.
//   · «Restaurar copia…» enseña antes qué se va a sustituir, reescribe las rutas si la música está en
//     otro sitio (MapaRaices) y lo deja PREPARADO: se completa al volver a abrir BeatTag, antes de
//     leer ningún archivo. Con los datos ya en memoria, el siguiente guardado pisaría lo restaurado.
//     Antes de sustituir nada se guarda una copia de lo que había, para poder volver atrás.
//   · Al arrancar, en segundo plano, una copia automática en «copias» (se conservan las 5 últimas),
//     solo si algo ha cambiado desde la anterior: abrir y cerrar BeatTag cinco veces no puede echar
//     fuera la copia buena.
//
// MEDIDO con los archivos de trabajo de una biblioteca real de 13.590 canciones (copiados aparte; la
// configuración, sintética): 10 archivos y 1,8 MB caben en un ZIP de 395 KB que se hace en 114 ms;
// la copia automática sin cambios se descarta en 19 ms; las 11.124 rutas guardadas se agrupan en 2
// raíces y se trasladan de «E:\…» a «/Volumes/…» en 19 ms, con la vuelta idéntica en todas;
// preparar la restauración tarda 137 ms y completarla al arrancar, 82 ms.

/// <summary>Un archivo dentro de la copia.</summary>
public sealed class ArchivoEnCopia
{
    public string Nombre { get; set; } = "";
    public long Bytes { get; set; }
    public DateTime Modificado { get; set; }
}

/// <summary>Lo que dice de sí misma una copia (beattag-copia.json, dentro del ZIP).</summary>
public sealed class ManifiestoCopia
{
    public int Formato { get; set; } = CopiaSeguridad.FormatoActual;
    public string Aplicacion { get; set; } = AppInfo.Name;
    public string Version { get; set; } = AppInfo.Version;
    public DateTime Fecha { get; set; }

    /// <summary>«Windows», «macOS» o «Linux»: el sistema donde se hizo.</summary>
    public string Sistema { get; set; } = "";

    /// <summary>«manual», «automatica» o «antes-de-restaurar».</summary>
    public string Tipo { get; set; } = "";

    public List<ArchivoEnCopia> Archivos { get; set; } = new();

    /// <summary>Resumen del contenido (SHA-256): dos copias con la misma huella guardan lo mismo.</summary>
    public string Huella { get; set; } = "";
}

/// <summary>Resultado de hacer una copia.</summary>
public sealed record ResultadoCopia(string Ruta, int Archivos, long Bytes, string Error, IReadOnlyList<string> Avisos)
{
    public bool Ok => Error.Length == 0;
}

/// <summary>Una copia leída del disco, lista para enseñarla y restaurarla.</summary>
public sealed class ContenidoCopia
{
    public string Origen { get; init; } = "";
    public ManifiestoCopia Manifiesto { get; init; } = new();

    /// <summary>Archivo → contenido. Solo lo que se acepta restaurar (ver <see cref="CopiaSeguridad.Leer"/>).</summary>
    public IReadOnlyDictionary<string, byte[]> Archivos { get; init; } = new Dictionary<string, byte[]>();

    /// <summary>Lo que había en el ZIP y no se restaura, con el motivo.</summary>
    public IReadOnlyList<string> Descartados { get; init; } = Array.Empty<string>();
}

/// <summary>Una fila de la vista previa: qué pasa con cada archivo al restaurar.</summary>
public sealed record FilaRestauracion(string Nombre, string Descripcion, long BytesCopia, DateTime? FechaCopia,
                                      long? BytesActual, DateTime? FechaActual, bool EnLaCopia)
{
    public string Accion => !EnLaCopia ? "No está en la copia: se conserva el actual"
                          : BytesActual == null ? "Se añade"
                          : "Sustituye al actual";

    /// <summary>«64,2 KB · 17/10/2026 15:56», o «—» si no está en la copia.</summary>
    public string CopiaTexto => EnLaCopia ? TextosCopia.TamanoYFecha(BytesCopia, FechaCopia) : "—";

    /// <summary>Lo mismo del archivo que hay ahora en este equipo, o «No existe».</summary>
    public string ActualTexto => BytesActual is { } b ? TextosCopia.TamanoYFecha(b, FechaActual) : "No existe";
}

/// <summary>Una raíz de la música en la copia, con su ubicación propuesta en este equipo.</summary>
/// <param name="Origen">La carpeta en el equipo donde se hizo la copia.</param>
/// <param name="Rutas">Cuántas rutas de los archivos de datos cuelgan de ella.</param>
/// <param name="Propuesta">Dónde parece estar ahora ("" si no se ha encontrado).</param>
/// <param name="Motivo">Por qué se propone eso, para que el usuario lo pueda juzgar.</param>
public sealed record RaizCopia(string Origen, int Rutas, string Propuesta, string Motivo);

/// <summary>Resultado de preparar una restauración.</summary>
/// <param name="Resellados">Entradas cuya fecha guardada se ha puesto al día con la del archivo en su sitio (ver <see cref="RutasJson.Resellar"/>).</param>
public sealed record ResultadoPreparacion(int Archivos, int RutasReescritas, IReadOnlyList<string> Avisos, string Error, int Resellados = 0)
{
    public bool Ok => Error.Length == 0;
}

/// <summary>La restauración preparada que espera al próximo arranque (restauracion.json).</summary>
public sealed class RestauracionPreparada
{
    public DateTime Preparada { get; set; }
    public DateTime FechaCopia { get; set; }
    public string Origen { get; set; } = "";
    public List<string> Archivos { get; set; } = new();
    public int RutasReescritas { get; set; }
}

/// <summary>Resultado de completar una restauración al arrancar.</summary>
public sealed record ResultadoRestauracion(DateTime FechaCopia, int Restaurados, IReadOnlyList<string> Fallidos,
                                           string CopiaPrevia, string Error)
{
    public bool Ok => Error.Length == 0 && Fallidos.Count == 0;

    /// <summary>Lo que se dice en la ventana al abrirse.</summary>
    public string Texto
    {
        get
        {
            if (Error.Length > 0) return Error;
            var cuantos = Restaurados == 1 ? "1 archivo" : $"{Restaurados} archivos";
            var texto = $"Se ha restaurado la copia de seguridad del {FechaCopia:dd/MM/yyyy HH:mm} ({cuantos}). "
                      + (CopiaPrevia.Length > 0
                          ? $"Lo que había antes se ha guardado en «{Path.GetFileName(CopiaPrevia)}», en la carpeta «copias» de la carpeta de datos, por si necesitas volver atrás. "
                          : "")
                      + "Las claves de API de este equipo se conservan.";
            if (Fallidos.Count > 0)
                texto += (Fallidos.Count == 1 ? " No se pudo restaurar " : " No se pudieron restaurar: ") + string.Join("; ", Fallidos) + ".";
            return texto;
        }
    }
}

/// <summary>Copia de seguridad del trabajo del usuario: hacerla, leerla, prepararla y completarla.</summary>
public static class CopiaSeguridad
{
    /// <summary>Versión del formato del ZIP. Una copia de un formato posterior no se restaura.</summary>
    public const int FormatoActual = 1;

    /// <summary>El manifiesto dentro del ZIP.</summary>
    public const string NombreManifiesto = "beattag-copia.json";

    /// <summary>Lo que marca una restauración preparada como completa.</summary>
    public const string NombreRestauracion = "restauracion.json";

    /// <summary>Cuántas copias automáticas (y cuántas «antes de restaurar») se conservan.</summary>
    public const int CopiasQueSeConservan = 5;

    public const string PrefijoAutomatica = "auto_";
    public const string PrefijoAntesDeRestaurar = "antes-de-restaurar_";

    /// <summary>
    /// Tope para un archivo de datos que no está en la lista conocida: los de trabajo pesan KB (el
    /// mayor medido, identificacion.json, 1,7 MB); algo mucho mayor sería una caché.
    /// </summary>
    public const long TopeOtros = 20L * 1024 * 1024;

    /// <summary>Topes al leer un ZIP, para que uno dañado o ajeno no agote la memoria.</summary>
    internal const long TopePorArchivo = 64L * 1024 * 1024;
    internal const long TopeTotal = 256L * 1024 * 1024;
    internal const int TopeEntradas = 200;

    /// <summary>Campos de config.net.json que son de ESTE equipo y no viajan en la copia.</summary>
    internal static bool EsDelEquipo(string campo)
        => campo.EndsWith("Enc", StringComparison.Ordinal) || campo.StartsWith("Window", StringComparison.Ordinal);

    public static string CarpetaCopias(AppPaths p) => Path.Combine(p.DataDir, "copias");

    public static string CarpetaRestauracion(AppPaths p) => Path.Combine(p.DataDir, "restauracion-pendiente");

    public static string Sistema()
        => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    // =============================================================================================
    // Qué entra
    // =============================================================================================

    /// <summary>
    /// Los archivos que entran en la copia y existen: los de trabajo conocidos y cualquier otro .json
    /// de la raíz de la carpeta de datos que no sea una caché conocida (así un archivo de datos que
    /// añada una pestaña nueva no se queda fuera por olvido), hasta <see cref="TopeOtros"/>.
    /// </summary>
    public static List<string> ArchivosAIncluir(AppPaths p)
    {
        var res = p.ArchivosDeTrabajo().Where(File.Exists).ToList();
        var vistos = new HashSet<string>(res.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
        var fuera = new HashSet<string>(p.FueraDeLaCopia().Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(p.DataDir))
                foreach (var f in Directory.EnumerateFiles(p.DataDir, "*.json", SearchOption.TopDirectoryOnly)
                                           .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var n = Path.GetFileName(f);
                    if (vistos.Contains(n) || fuera.Contains(n)) continue;
                    try { if (new FileInfo(f).Length > TopeOtros) continue; } catch { continue; }
                    vistos.Add(n);
                    res.Add(f);
                }
        }
        catch { /* sin poder listar la carpeta, al menos los conocidos */ }
        return res;
    }

    /// <summary>Qué es cada archivo, en la vista previa.</summary>
    public static string Descripcion(string nombre, AppPaths p)
    {
        string N(string ruta) => Path.GetFileName(ruta);
        return nombre switch
        {
            _ when Igual(nombre, N(p.ConfigPath)) => "Ajustes (sin las claves de API)",
            _ when Igual(nombre, N(p.FichasDjPath)) => "Fichas de DJ",
            _ when Igual(nombre, N(p.ColeccionesPath)) => "Colecciones",
            _ when Igual(nombre, N(p.BandejaPath)) => "Estado de la Bandeja de entrada",
            _ when Igual(nombre, N(p.IgnoredPath)) => "Canciones descartadas en Enriquecer",
            _ when Igual(nombre, N(p.AppliedPath)) => "Canciones ya aplicadas en Enriquecer",
            _ when Igual(nombre, N(p.ApplyMarksPath)) => "Casillas «Aplicar» de Enriquecer",
            _ when Igual(nombre, N(p.AudioAceptadasPath)) => "Avisos aceptados de Comprobar audio",
            _ when Igual(nombre, N(p.ArtistAliasesPath)) => "Alias de artista",
            _ when Igual(nombre, N(p.ArtistExceptionsPath)) => "Grafías de artista",
            _ when Igual(nombre, N(p.IdentificacionCachePath)) => "Identificaciones por audio (consultas ya hechas)",
            _ when Igual(nombre, Dj.AlmacenPendientes.NombreArchivo) => "Lista de pendientes",
            _ when Igual(nombre, Dj.AlmacenListasSeguidas.NombreArchivo) => "Listas seguidas",
            _ when Igual(nombre, Dj.MapaGenerosCarpeta.NombreArchivo) => "Reglas de género por carpeta",
            _ when Igual(nombre, N(Dj.AlmacenSustituciones.RutaEn(p.DataDir))) => "Sustituciones de la Bandeja que se pueden deshacer",
            _ when Igual(nombre, Providers.AlmacenArtistasDeezer.NombreArchivo) => "Artistas de Novedades: correcciones y exclusiones",
            _ => "Otros datos de BeatTag",
        };
    }

    private static bool Igual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // =============================================================================================
    // Hacer la copia
    // =============================================================================================

    /// <summary>Lo recogido para una copia: nombre, contenido y fecha de cada archivo.</summary>
    internal sealed record Recogido(string Nombre, byte[] Bytes, DateTime Modificado);

    /// <summary>
    /// Lee los archivos que entran. Se leen compartiendo escritura y borrado: la copia automática va en
    /// segundo plano mientras el motor arranca, y no puede impedir que este guarde (los guardados
    /// sustituyen el archivo de golpe; quien lee se queda con el anterior entero).
    /// </summary>
    internal static List<Recogido> Recoger(AppPaths p, List<string> avisos) => Recoger(p, avisos, out _);

    /// <param name="noLeidos">Archivos que existen y no se han podido leer (bloqueados, sin permiso).</param>
    internal static List<Recogido> Recoger(AppPaths p, List<string> avisos, out int noLeidos)
    {
        var res = new List<Recogido>();
        noLeidos = 0;
        foreach (var ruta in ArchivosAIncluir(p))
        {
            var nombre = Path.GetFileName(ruta);
            byte[] bytes;
            DateTime fecha;
            try
            {
                bytes = LeerCompartido(ruta);
                fecha = File.GetLastWriteTime(ruta);
            }
            catch (FileNotFoundException) { continue; }   // se ha borrado entre listar y leer: no estaba
            catch (Exception e) { avisos.Add($"{nombre}: no se pudo leer ({e.Message})"); noLeidos++; continue; }

            if (Igual(nombre, Path.GetFileName(p.ConfigPath)))
            {
                var limpio = SinDatosDelEquipo(bytes);
                if (limpio == null)
                {
                    // Sin entenderla no se pueden quitar las claves con seguridad: no viaja.
                    avisos.Add($"{nombre}: no se pudo leer como JSON y no se incluye (podría llevar claves)");
                    continue;
                }
                bytes = limpio;
            }
            res.Add(new Recogido(nombre, bytes, fecha));
        }
        return res;
    }

    internal static byte[] LeerCompartido(string ruta)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                using var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                return ms.ToArray();
            }
            catch (IOException) when (intento < 3 && File.Exists(ruta))
            {
                Thread.Sleep(250);   // otro programa lo tiene abierto sin compartir: suele ser un momento
            }
        }
    }

    /// <summary>
    /// config.net.json sin lo que es de este equipo: las claves cifradas (*Enc) y la ventana. null si
    /// no se entiende como JSON.
    /// </summary>
    public static byte[]? SinDatosDelEquipo(byte[] config)
    {
        if (RutasJson.Leer(RutasJson.Texto(config)) is not JsonObject o) return null;
        foreach (var k in o.Select(x => x.Key).Where(EsDelEquipo).ToList()) o.Remove(k);
        return Encoding.UTF8.GetBytes(o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Huella del contenido: igual huella, mismos archivos con lo mismo dentro.</summary>
    internal static string Huella(IEnumerable<Recogido> recogidos)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var r in recogidos.OrderBy(x => x.Nombre, StringComparer.OrdinalIgnoreCase))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(r.Nombre.ToLowerInvariant() + "\0"));
            sha.AppendData(BitConverter.GetBytes(r.Bytes.LongLength));
            sha.AppendData(r.Bytes);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>
    /// Hace una copia en <paramref name="destinoZip"/>. Se escribe a un temporal y se coloca de una
    /// vez: una copia a medias nunca pasa por buena.
    /// </summary>
    public static ResultadoCopia Crear(AppPaths p, string destinoZip, string tipo, DateTime fecha)
    {
        var avisos = new List<string>();
        var recogidos = Recoger(p, avisos);
        return Escribir(recogidos, destinoZip, tipo, fecha, avisos);
    }

    private static ResultadoCopia Escribir(List<Recogido> recogidos, string destinoZip, string tipo, DateTime fecha, List<string> avisos)
    {
        if (recogidos.Count == 0)
            return new ResultadoCopia(destinoZip, 0, 0, "No hay ningún archivo de trabajo que copiar.", avisos);

        var manifiesto = new ManifiestoCopia
        {
            Fecha = fecha,
            Sistema = Sistema(),
            Tipo = tipo,
            Huella = Huella(recogidos),
            Archivos = recogidos.Select(r => new ArchivoEnCopia { Nombre = r.Nombre, Bytes = r.Bytes.LongLength, Modificado = r.Modificado }).ToList(),
        };

        var tmp = destinoZip + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(destinoZip);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var m = zip.CreateEntry(NombreManifiesto, CompressionLevel.Optimal);
                using (var s = m.Open())
                    JsonSerializer.Serialize(s, manifiesto, new JsonSerializerOptions { WriteIndented = true });
                foreach (var r in recogidos)
                {
                    var e = zip.CreateEntry(r.Nombre, CompressionLevel.Optimal);
                    // El ZIP solo admite fechas de 1980 a 2107: una fuera de rango (un reloj mal puesto)
                    // haría fallar la copia entera por un dato que no importa.
                    if (r.Modificado.Year is >= 1980 and <= 2107) e.LastWriteTime = new DateTimeOffset(r.Modificado);
                    using var s = e.Open();
                    s.Write(r.Bytes);
                }
            }
            File.Move(tmp, destinoZip, overwrite: true);
            return new ResultadoCopia(destinoZip, recogidos.Count, new FileInfo(destinoZip).Length, "", avisos);
        }
        catch (Exception e)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return new ResultadoCopia(destinoZip, 0, 0, e.Message, avisos);
        }
    }

    /// <summary>Un nombre libre en <paramref name="carpeta"/>: «prefijo_aaaammdd_hhmmss.zip», o con «_2»… si ya existe.</summary>
    internal static string NombreLibre(string carpeta, string prefijo, DateTime fecha)
    {
        var raiz = $"{prefijo}{fecha:yyyyMMdd_HHmmss}";
        var ruta = Path.Combine(carpeta, raiz + ".zip");
        for (var n = 2; File.Exists(ruta); n++) ruta = Path.Combine(carpeta, $"{raiz}_{n}.zip");
        return ruta;
    }

    /// <summary>
    /// La copia automática del arranque. null si no hacía falta: no hay datos todavía, o nada ha
    /// cambiado desde la última (misma huella). Conserva las <see cref="CopiasQueSeConservan"/> últimas.
    /// </summary>
    public static ResultadoCopia? CopiaAutomatica(AppPaths p, DateTime ahora)
    {
        var avisos = new List<string>();
        var recogidos = Recoger(p, avisos);
        if (recogidos.Count == 0) return null;

        var carpeta = CarpetaCopias(p);
        LimpiarTemporales(carpeta, ahora);
        var ultima = Copias(carpeta, PrefijoAutomatica).LastOrDefault();
        if (ultima != null && LeerManifiesto(ultima)?.Huella == Huella(recogidos)) return null;

        var res = Escribir(recogidos, NombreLibre(carpeta, PrefijoAutomatica, ahora), "automatica", ahora, avisos);
        if (res.Ok) Rotar(carpeta, PrefijoAutomatica, CopiasQueSeConservan);
        return res;
    }

    /// <summary>
    /// Borra los «.zip.tmp» que dejó una copia cortada a medias (la automática va en un hilo de fondo
    /// que muere si se cierra BeatTag mientras escribe). Solo los de hace más de 10 minutos: uno más
    /// reciente puede ser de una copia que se está haciendo ahora.
    /// </summary>
    internal static void LimpiarTemporales(string carpeta, DateTime ahora)
    {
        try
        {
            if (!Directory.Exists(carpeta)) return;
            foreach (var f in Directory.EnumerateFiles(carpeta, "*.zip.tmp"))
                try { if (ahora - File.GetLastWriteTime(f) > TimeSpan.FromMinutes(10)) File.Delete(f); } catch { }
        }
        catch { }
    }

    /// <summary>Las copias de un tipo, de la más antigua a la más reciente (el nombre lleva la fecha).</summary>
    public static List<string> Copias(string carpeta, string prefijo)
    {
        try
        {
            if (!Directory.Exists(carpeta)) return new List<string>();
            // Una copia a medias («.zip.tmp») no es una copia. MEDIDO con .NET 9 en Windows: «*.zip» ya
            // no casa con «.zip.tmp»; el filtro queda por si un sistema aplica la regla antigua de las
            // extensiones de tres letras, con la que sí casaría.
            return Directory.EnumerateFiles(carpeta, prefijo + "*.zip")
                            .Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                            .ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>Borra las copias más antiguas de un tipo hasta dejar <paramref name="conservar"/>.</summary>
    public static int Rotar(string carpeta, string prefijo, int conservar)
    {
        var todas = Copias(carpeta, prefijo);
        var borradas = 0;
        foreach (var f in todas.Take(Math.Max(0, todas.Count - conservar)))
            try { File.Delete(f); borradas++; } catch { /* se intentará en la siguiente */ }
        return borradas;
    }

    // =============================================================================================
    // Leer una copia
    // =============================================================================================

    /// <summary>El manifiesto de un ZIP, o null si no lo tiene o no se puede leer.</summary>
    public static ManifiestoCopia? LeerManifiesto(string zip)
    {
        try
        {
            using var a = ZipFile.OpenRead(zip);
            var e = a.GetEntry(NombreManifiesto);
            if (e == null || e.Length > 1024 * 1024) return null;
            using var s = e.Open();
            return JsonSerializer.Deserialize<ManifiestoCopia>(s);
        }
        catch { return null; }
    }

    /// <summary>
    /// Lee una copia para restaurarla. Solo se aceptan archivos .json sueltos (sin carpetas, con un
    /// nombre válido), que no sean cachés y dentro de unos topes de tamaño: una copia dañada o un ZIP
    /// cualquiera no pueden escribir fuera de la carpeta de datos ni agotar la memoria. Devuelve null
    /// con el motivo en <paramref name="error"/> si no es una copia de BeatTag que se pueda usar.
    /// </summary>
    public static ContenidoCopia? Leer(string zip, AppPaths p, out string error)
    {
        error = "";
        try
        {
            using var a = ZipFile.OpenRead(zip);
            var m = a.GetEntry(NombreManifiesto);
            ManifiestoCopia? manifiesto = null;
            if (m != null && m.Length <= 1024 * 1024)
                using (var s = m.Open())
                    try { manifiesto = JsonSerializer.Deserialize<ManifiestoCopia>(s); } catch (JsonException) { }
            if (manifiesto == null || !Igual(manifiesto.Aplicacion, AppInfo.Name))
            {
                error = "El archivo no es una copia de seguridad de BeatTag.";
                return null;
            }
            if (manifiesto.Formato > FormatoActual)
            {
                error = $"La copia se hizo con una versión más reciente de BeatTag ({manifiesto.Version}). Actualiza BeatTag para restaurarla.";
                return null;
            }

            var fuera = new HashSet<string>(p.FueraDeLaCopia().Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
            var archivos = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var descartados = new List<string>();
            long total = 0;
            foreach (var e in a.Entries)
            {
                if (Igual(e.FullName, NombreManifiesto)) continue;
                var nombre = e.FullName;
                if (archivos.Count >= TopeEntradas) { descartados.Add($"{nombre}: la copia tiene demasiados archivos"); continue; }
                if (nombre.Length == 0 || nombre.Length > 120 || nombre.IndexOfAny(new[] { '/', '\\' }) >= 0
                    || nombre.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || nombre.StartsWith('.')
                    || !nombre.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    descartados.Add($"{nombre}: no es un archivo de datos de BeatTag");
                    continue;
                }
                if (fuera.Contains(nombre)) { descartados.Add($"{nombre}: es una caché o de otra aplicación"); continue; }
                if (e.Length > TopePorArchivo || total + e.Length > TopeTotal) { descartados.Add($"{nombre}: demasiado grande"); continue; }
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                if (ms.Length > TopePorArchivo) { descartados.Add($"{nombre}: demasiado grande"); continue; }
                total += ms.Length;
                archivos[nombre] = ms.ToArray();
            }
            if (archivos.Count == 0)
            {
                error = "La copia no contiene ningún archivo de datos que se pueda restaurar.";
                return null;
            }
            return new ContenidoCopia { Origen = zip, Manifiesto = manifiesto, Archivos = archivos, Descartados = descartados };
        }
        catch (InvalidDataException) { error = "El archivo está dañado o no es un ZIP."; return null; }
        catch (Exception e) { error = "No se pudo leer la copia: " + e.Message; return null; }
    }

    /// <summary>Qué pasará con cada archivo: los de la copia y los de trabajo de aquí que no están en ella.</summary>
    public static List<FilaRestauracion> Vista(ContenidoCopia c, AppPaths p)
    {
        var filas = new List<FilaRestauracion>();
        foreach (var (nombre, bytes) in c.Archivos.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var actual = new FileInfo(Path.Combine(p.DataDir, nombre));
            var enManifiesto = c.Manifiesto.Archivos.FirstOrDefault(a => Igual(a.Nombre, nombre));
            filas.Add(new FilaRestauracion(nombre, Descripcion(nombre, p), bytes.LongLength, enManifiesto?.Modificado,
                                           actual.Exists ? actual.Length : null, actual.Exists ? actual.LastWriteTime : null, EnLaCopia: true));
        }
        foreach (var ruta in ArchivosAIncluir(p))
        {
            var nombre = Path.GetFileName(ruta);
            if (c.Archivos.ContainsKey(nombre)) continue;
            var actual = new FileInfo(ruta);
            filas.Add(new FilaRestauracion(nombre, Descripcion(nombre, p), 0, null, actual.Length, actual.LastWriteTime, EnLaCopia: false));
        }
        return filas;
    }

    // =============================================================================================
    // Rutas de la copia y raíces de la música
    // =============================================================================================

    /// <summary>Las rutas de cada archivo de la copia (las veces que aparezcan), para contar y proponer raíces.</summary>
    public static Dictionary<string, List<string>> Rutas(ContenidoCopia c)
    {
        var res = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (nombre, bytes) in c.Archivos)
            res[nombre] = RutasJson.Rutas(RutasJson.Leer(RutasJson.Texto(bytes)));
        return res;
    }

    /// <summary>
    /// Las carpetas de la música según los ajustes de la copia: las de la biblioteca (marcadas o no) y
    /// las de la Bandeja. Son el punto de partida de las raíces que se proponen.
    /// </summary>
    public static List<string> CarpetasDeLaCopia(ContenidoCopia c, AppPaths p)
    {
        var res = new List<string>();
        if (!c.Archivos.TryGetValue(Path.GetFileName(p.ConfigPath), out var cfg)) return res;
        if (RutasJson.Leer(RutasJson.Texto(cfg)) is not JsonObject o) return res;
        foreach (var campo in new[] { nameof(AppConfig.Folders), nameof(AppConfig.DisabledFolders) })
            if (o[campo] is JsonArray a)
                foreach (var v in a)
                    if (v is JsonValue jv && jv.GetValueKind() == JsonValueKind.String) res.Add(jv.GetValue<string>());
        foreach (var campo in new[] { nameof(AppConfig.BandejaCarpeta), nameof(AppConfig.BandejaDestino) })
            if (o[campo] is JsonValue jv && jv.GetValueKind() == JsonValueKind.String) res.Add(jv.GetValue<string>());
        return res.Where(r => RutaPartida.De(r) != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Cuántos tramos de una ruta son «el disco» y no carpetas de música: en macOS «/Volumes/USB» o
    /// «/Users/dj» cuentan como el volumen; en Windows la letra ya lo es.
    /// </summary>
    private static int TramosDeVolumen(RutaPartida r)
    {
        if (r.Estilo == EstiloRuta.Windows || r.Tramos.Count == 0) return 0;
        var t0 = r.Tramos[0];
        if ((Igual(t0, "Volumes") || Igual(t0, "Users") || Igual(t0, "home") || Igual(t0, "mnt")) && r.Tramos.Count >= 2) return 2;
        if (Igual(t0, "media") && r.Tramos.Count >= 3) return 3;
        return 0;
    }

    private static RutaPartida Prefijo(RutaPartida r, int tramos) => r with { Tramos = r.Tramos.Take(tramos).ToList() };

    /// <summary>
    /// Agrupa las carpetas de la copia en pocas raíces para que el usuario no tenga que confirmar una
    /// por carpeta: las que cuelgan de la misma carpeta de primer nivel de un disco se resumen en su
    /// antepasado común. MEDIDO en una biblioteca real: 43 carpetas de «E:\Musica\COMERCIAL\…» y la de
    /// la Bandeja quedan en 2 raíces. Las rutas que no cuelgan de ninguna carpeta conocida aportan su
    /// propia carpeta.
    ///
    /// Esas rutas sueltas se agrupan aparte y nunca ensanchan una raíz de las carpetas de la música:
    /// una ruta de los ajustes que no es música (la colección de rekordbox en Documentos, por ejemplo)
    /// se juntaba con la Bandeja de Descargas por compartir «C:\Users», la raíz pasaba a ser la carpeta
    /// del usuario y en otro equipo ya no se encontraba (MEDIDO: de 2 raíces encontradas a 1 perdida).
    /// </summary>
    public static List<RutaPartida> AgruparRaices(IEnumerable<string> carpetas, IEnumerable<string> rutas)
    {
        var deCarpetas = carpetas.Select(RutaPartida.De).Where(r => r != null).Cast<RutaPartida>().ToList();
        var principales = SinAnidadas(Agrupar(deCarpetas));

        var sueltas = new List<RutaPartida>();
        foreach (var ruta in rutas.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var r = RutaPartida.De(ruta);
            if (r == null || r.Tramos.Count == 0 || deCarpetas.Any(a => r.EmpiezaPor(a)) || principales.Any(a => r.EmpiezaPor(a))) continue;
            sueltas.Add(Prefijo(r, r.Tramos.Count - 1));   // su carpeta
        }
        // Una raíz de rutas sueltas que queda dentro de una de las carpetas sobra; una que contiene a una
        // de ellas se queda junto a ella (la más concreta manda al traducir, ver MapaRaices).
        var deSueltas = SinAnidadas(Agrupar(sueltas)).Where(g => !principales.Any(p => g.EmpiezaPor(p)));
        return principales.Concat(deSueltas).ToList();
    }

    /// <summary>Sin las raíces que quedan dentro de otra (un grupo de primer nivel dentro de una unidad entera).</summary>
    private static List<RutaPartida> SinAnidadas(List<RutaPartida> grupos)
        => grupos.Where(g => !grupos.Any(o => !ReferenceEquals(o, g) && g.EmpiezaPor(o) && !o.EmpiezaPor(g))).ToList();

    /// <summary>
    /// Agrupa carpetas por disco y carpeta de primer nivel y resume cada grupo en su antepasado común.
    /// </summary>
    private static List<RutaPartida> Agrupar(List<RutaPartida> anclas)
    {
        var grupos = new List<RutaPartida>();
        foreach (var g in anclas.GroupBy(a =>
                 {
                     var v = TramosDeVolumen(a);
                     var clave = a.Estilo + "|" + a.Volumen.ToUpperInvariant() + "|" + string.Join("/", a.Tramos.Take(Math.Min(a.Tramos.Count, v + 1)).Select(t => RutaPartida.Nfc(t).ToUpperInvariant()));
                     return clave;
                 }))
        {
            var lista = g.ToList();
            var comun = lista[0].Tramos.Count;
            foreach (var a in lista.Skip(1))
            {
                var n = 0;
                while (n < comun && n < a.Tramos.Count && RutaPartida.MismoTramo(a.Tramos[n], lista[0].Tramos[n])) n++;
                comun = n;
            }
            var raiz = Prefijo(lista[0], comun);
            if (raiz.Estilo == EstiloRuta.Unix && raiz.Tramos.Count == 0) continue;   // «/» no es una raíz
            if (!grupos.Any(x => x.EmpiezaPor(raiz) && raiz.EmpiezaPor(x))) grupos.Add(raiz);
        }
        return grupos;
    }

    /// <summary>
    /// Las raíces de la música de la copia, con cuántas rutas cuelgan de cada una y dónde parecen
    /// estar en este equipo:
    ///   1. si la carpeta existe aquí, ahí (misma unidad, mismo equipo o reinstalación);
    ///   2. si coincide por el final con una carpeta de la biblioteca de este equipo
    ///      («E:\Musica\COMERCIAL\House» ↔ «/Volumes/USB/COMERCIAL/House» → «/Volumes/USB/COMERCIAL»);
    ///   3. si en algún disco de este equipo hay una carpeta con el mismo camino (o con su final):
    ///      «D:\Musica» → «/Volumes/USB/Musica», o «E:» que ahora es «F:»;
    ///   4. si no, sin propuesta: el usuario la indica o la deja como está.
    /// </summary>
    public static List<RaizCopia> ProponerRaices(IReadOnlyList<string> carpetasCopia, IReadOnlyCollection<string> rutas,
                                                 Func<string, bool> existeCarpeta, IReadOnlyList<string> carpetasActuales,
                                                 IReadOnlyList<string> volumenes)
        => ProponerRaices(carpetasCopia, rutas, existeCarpeta, carpetasActuales, volumenes,
                          OperatingSystem.IsWindows() ? EstiloRuta.Windows : EstiloRuta.Unix);

    /// <param name="estiloAqui">El estilo de las rutas de este sistema. Una ruta del otro estilo no puede
    /// «existir aquí» aunque el sistema la resuelva: Windows lee «/Users/dj/Music» como
    /// «C:\Users\dj\Music», y si el usuario se llama igual en los dos equipos la daba por encontrada y
    /// dejaba las rutas en formato macOS.</param>
    internal static List<RaizCopia> ProponerRaices(IReadOnlyList<string> carpetasCopia, IReadOnlyCollection<string> rutas,
                                                   Func<string, bool> existeCarpeta, IReadOnlyList<string> carpetasActuales,
                                                   IReadOnlyList<string> volumenes, EstiloRuta estiloAqui)
    {
        var raices = AgruparRaices(carpetasCopia, rutas);
        var partidas = rutas.Select(RutaPartida.De).Where(r => r != null).Cast<RutaPartida>().ToList();
        var actuales = carpetasActuales.Select(RutaPartida.De).Where(r => r != null).Cast<RutaPartida>().ToList();
        var anclas = carpetasCopia.Select(RutaPartida.De).Where(r => r != null).Cast<RutaPartida>().ToList();
        // Cada ruta cuenta en la raíz más concreta que la contiene (una raíz de rutas sueltas puede
        // contener a otra de las carpetas de la música, y al traducir manda la más concreta).
        var cuenta = new Dictionary<RutaPartida, int>(ReferenceEqualityComparer.Instance);
        foreach (var r in partidas)
            if (raices.Where(x => r.EmpiezaPor(x)).OrderByDescending(x => x.Tramos.Count).FirstOrDefault() is { } suya)
                cuenta[suya] = cuenta.TryGetValue(suya, out var n) ? n + 1 : 1;

        var res = new List<RaizCopia>();
        foreach (var raiz in raices)
        {
            var cuantas = cuenta.TryGetValue(raiz, out var c) ? c : 0;
            var texto = raiz.Texto();
            string propuesta = "", motivo;
            var porBib = PorLaBiblioteca(raiz, anclas, actuales);
            if (raiz.Estilo == estiloAqui && SinFallo(() => existeCarpeta(texto)))
            {
                propuesta = texto;
                motivo = "Existe en este equipo.";
            }
            else if (porBib is { } pb)
            {
                propuesta = pb.Propuesta;
                motivo = $"Coincide con la carpeta «{pb.Carpeta}» de la biblioteca de este equipo.";
            }
            else if (PorLosDiscos(raiz, existeCarpeta, volumenes) is { } enDisco)
            {
                propuesta = enDisco;
                motivo = "Se ha encontrado una carpeta con el mismo camino en otro disco.";
            }
            else motivo = "No se ha encontrado en este equipo. Indica dónde está o déjala como está.";
            res.Add(new RaizCopia(texto, cuantas, propuesta, motivo));
        }
        return res.OrderByDescending(r => r.Rutas).ThenBy(r => r.Origen, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool SinFallo(Func<bool> f)
    {
        try { return f(); } catch { return false; }
    }

    /// <summary>Paso 2 de <see cref="ProponerRaices"/>: por coincidencia con el final de las carpetas de la biblioteca de aquí.</summary>
    private static (string Propuesta, string Carpeta)? PorLaBiblioteca(RutaPartida raiz, List<RutaPartida> anclas, List<RutaPartida> actuales)
    {
        var votos = new Dictionary<string, (int Votos, string Carpeta)>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in anclas.Where(a => a.EmpiezaPor(raiz)))
        {
            // Lo que hay que encontrar al final de una carpeta de aquí: el último tramo de la raíz
            // (si lo tiene) y lo que va de la raíz a la carpeta.
            var bajo = a.Tramos.Skip(raiz.Tramos.Count).ToList();
            var buscar = (raiz.Tramos.Count > 0 ? new[] { raiz.Tramos[^1] } : Array.Empty<string>()).Concat(bajo).ToList();
            if (buscar.Count == 0) continue;
            foreach (var f in actuales)
            {
                if (f.Tramos.Count < buscar.Count) continue;
                var desde = f.Tramos.Count - buscar.Count;
                var casa = true;
                for (var i = 0; i < buscar.Count && casa; i++) casa = RutaPartida.MismoTramo(f.Tramos[desde + i], buscar[i]);
                if (!casa) continue;
                var propuesta = Prefijo(f, f.Tramos.Count - bajo.Count).Texto();
                votos[propuesta] = (votos.TryGetValue(propuesta, out var v) ? v.Votos + 1 : 1, f.Texto());
            }
        }
        if (votos.Count == 0) return null;
        var mejor = votos.OrderByDescending(v => v.Value.Votos).First();
        return (mejor.Key, mejor.Value.Carpeta);
    }

    /// <summary>Paso 3 de <see cref="ProponerRaices"/>: el mismo camino (o su final) en otro disco.</summary>
    private static string? PorLosDiscos(RutaPartida raiz, Func<string, bool> existeCarpeta, IReadOnlyList<string> volumenes)
    {
        var resto = raiz.Tramos.Skip(TramosDeVolumen(raiz)).ToList();
        if (resto.Count == 0) return null;
        for (var desde = 0; desde < resto.Count; desde++)   // primero el camino entero, luego su final
        {
            var cola = resto.Skip(desde).ToList();
            foreach (var v in volumenes)
            {
                var vol = RutaPartida.De(v);
                if (vol == null) continue;
                var candidata = (vol with { Tramos = vol.Tramos.Concat(cola).ToList() }).Texto();
                if (raiz.EmpiezaPor(RutaPartida.De(candidata)!) && RutaPartida.De(candidata)!.EmpiezaPor(raiz)) continue;
                if (SinFallo(() => existeCarpeta(candidata))) return candidata;
            }
        }
        return null;
    }

    /// <summary>
    /// Los discos de este equipo donde buscar la música: en Windows las unidades fijas y extraíbles
    /// (las de red pueden tardar en responder si no están); en macOS lo montado en /Volumes, y la
    /// carpeta del usuario y su carpeta de música en los dos.
    /// </summary>
    public static List<string> VolumenesDelEquipo()
    {
        var res = new List<string>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var d in DriveInfo.GetDrives())
                    try { if (d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady) res.Add(d.RootDirectory.FullName); }
                    catch { }
            }
            else
            {
                foreach (var raiz in new[] { "/Volumes", "/media", "/mnt" })
                    if (Directory.Exists(raiz)) res.AddRange(Directory.EnumerateDirectories(raiz));
            }
        }
        catch { }
        foreach (var carpeta in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.MyMusic })
        {
            var c = Environment.GetFolderPath(carpeta);
            if (c.Length > 0 && !res.Contains(c, StringComparer.OrdinalIgnoreCase)) res.Add(c);
        }
        return res;
    }

    // =============================================================================================
    // Preparar la restauración (se completa al arrancar)
    // =============================================================================================

    /// <summary>
    /// Deja la restauración lista en «restauracion-pendiente»: los archivos de la copia, con las rutas
    /// ya reescritas según <paramref name="mapa"/>, y el aviso de que está completa (restauracion.json),
    /// que se escribe lo último. Sustituye a otra preparada antes. No toca los archivos en uso.
    ///
    /// <paramref name="existe"/> sirve para elegir la forma de los acentos que tiene cada archivo en
    /// este equipo (ver <see cref="MapaRaices.Traducir"/>); va fuera del hilo de la interfaz.
    /// </summary>
    public static ResultadoPreparacion PrepararRestauracion(ContenidoCopia c, AppPaths p, MapaRaices mapa,
                                                            Func<string, bool>? existe, DateTime ahora,
                                                            Func<string, (long Ticks, long Bytes)?>? sello = null)
    {
        var dir = CarpetaRestauracion(p);
        var avisos = new List<string>();
        var archivos = new List<string>();
        int reescritas = 0, resellados = 0;
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            foreach (var (nombre, original) in c.Archivos.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var bytes = original;
                var esConfig = Igual(nombre, Path.GetFileName(p.ConfigPath));
                var nodo = RutasJson.Leer(RutasJson.Texto(bytes));
                if (nodo == null)
                {
                    // Uno dañado en la copia: restaurarlo solo serviría para que se apartara al abrir.
                    avisos.Add($"{nombre} está dañado en la copia y no se restaura");
                    continue;
                }
                var cambia = false;
                if (!mapa.Vacio)
                {
                    var r = RutasJson.Reescribir(nodo, s => mapa.Traducir(s, existe));
                    nodo = r.Raiz;
                    reescritas += r.Cambiadas;
                    cambia = r.Cambiadas > 0;
                    if (r.Repetidas > 0)
                        avisos.Add($"{nombre}: {r.Repetidas} {(r.Repetidas == 1 ? "entrada coincidía" : "entradas coincidían")} con otra al cambiar de ubicación y se ha conservado la primera");
                }
                if (sello != null && !esConfig)
                {
                    // Ya con las rutas de este equipo: la fecha redondeada por el disco nuevo no
                    // puede dejar caducadas las identificaciones pagadas (ver RutasJson.Resellar).
                    var n = RutasJson.Resellar(nodo, sello);
                    resellados += n;
                    cambia |= n > 0;
                }
                if (esConfig && nodo is JsonObject cfg)
                {
                    // Por si la copia no la hizo BeatTag: unas claves de otro sitio nunca se restauran.
                    foreach (var k in cfg.Select(x => x.Key).Where(EsDelEquipo).ToList()) { cfg.Remove(k); cambia = true; }
                }
                if (cambia) bytes = Encoding.UTF8.GetBytes(nodo!.ToJsonString(new JsonSerializerOptions { WriteIndented = esConfig }));
                File.WriteAllBytes(Path.Combine(dir, nombre), bytes);
                archivos.Add(nombre);
            }
            if (archivos.Count == 0) throw new InvalidOperationException("ningún archivo de la copia se puede restaurar");

            var marca = new RestauracionPreparada
            {
                Preparada = ahora, FechaCopia = c.Manifiesto.Fecha, Origen = Path.GetFileName(c.Origen),
                Archivos = archivos, RutasReescritas = reescritas,
            };
            File.WriteAllText(Path.Combine(dir, NombreRestauracion), JsonSerializer.Serialize(marca, new JsonSerializerOptions { WriteIndented = true }));
            return new ResultadoPreparacion(archivos.Count, reescritas, avisos, "", resellados);
        }
        catch (Exception e)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
            return new ResultadoPreparacion(0, 0, avisos, "No se pudo preparar la restauración: " + e.Message);
        }
    }

    /// <summary>La restauración preparada que espera al próximo arranque, o null.</summary>
    public static RestauracionPreparada? Pendiente(AppPaths p)
    {
        try
        {
            var marca = Path.Combine(CarpetaRestauracion(p), NombreRestauracion);
            return File.Exists(marca) ? JsonSerializer.Deserialize<RestauracionPreparada>(File.ReadAllText(marca)) : null;
        }
        catch { return null; }
    }

    /// <summary>Descarta la restauración preparada. Devuelve si había una.</summary>
    public static bool CancelarPendiente(AppPaths p)
    {
        var dir = CarpetaRestauracion(p);
        if (!Directory.Exists(dir)) return false;
        Directory.Delete(dir, recursive: true);
        return true;
    }

    /// <summary>
    /// Completa la restauración preparada, si la hay. Se llama al arrancar, ANTES de leer ningún
    /// archivo de datos. null si no había ninguna.
    ///
    /// Primero guarda una copia de lo que hay («antes-de-restaurar_…zip»): sin ella no se sustituye
    /// nada. Luego coloca cada archivo de golpe (temporal + intercambio). De config.net.json se
    /// conservan las claves de API y la ventana de este equipo, que la copia no lleva.
    /// </summary>
    public static ResultadoRestauracion? AplicarRestauracionPendiente(AppPaths p, DateTime ahora)
    {
        var dir = CarpetaRestauracion(p);
        if (!Directory.Exists(dir)) return null;
        var marca = Pendiente(p);
        if (marca == null)
        {
            // Sin la marca, la preparación no terminó (se cerró a medias): no se usa.
            try { Directory.Delete(dir, recursive: true); } catch { }
            return new ResultadoRestauracion(default, 0, Array.Empty<string>(), "",
                "Había una restauración de copia de seguridad a medio preparar y se ha descartado. Si la necesitas, vuelve a restaurarla desde Ajustes.");
        }

        // La copia de lo actual. Un archivo que no se ha podido leer (bloqueado por otro programa) no
        // estaría en ella: entonces no se sustituye nada.
        var copias = CarpetaCopias(p);
        var avisosPrevia = new List<string>();
        var recogidos = Recoger(p, avisosPrevia, out var noLeidos);
        // La restauración preparada se conserva: no se ha tocado nada, y el motivo suele ser pasajero (un
        // antivirus que tiene abierto un archivo). Borrarla obligaba a elegir otra vez el ZIP y a volver a
        // confirmar las ubicaciones de la música; si el problema sigue, se descarta desde Ajustes.
        string Abortar(string motivo)
            => $"No se ha restaurado la copia de seguridad: antes de sustituir nada se guarda una copia de lo actual, y no se pudo ({motivo}). "
             + "No se ha cambiado nada. Se volverá a intentar la próxima vez que se abra BeatTag; para descartarla, usa «Cancelar la restauración» en Ajustes.";
        if (noLeidos > 0)
            return new ResultadoRestauracion(marca.FechaCopia, 0, Array.Empty<string>(), "", Abortar(string.Join("; ", avisosPrevia)));
        // Sin nada que guardar (un equipo recién instalado) no hay copia previa, y no se dice que la haya.
        var rutaPrevia = "";
        if (recogidos.Count > 0)
        {
            var previa = Escribir(recogidos, NombreLibre(copias, PrefijoAntesDeRestaurar, ahora), "antes-de-restaurar", ahora, avisosPrevia);
            if (!previa.Ok)
                return new ResultadoRestauracion(marca.FechaCopia, 0, Array.Empty<string>(), "", Abortar(previa.Error));
            rutaPrevia = previa.Ruta;
            Rotar(copias, PrefijoAntesDeRestaurar, CopiasQueSeConservan);
        }
        var enLaPrevia = new HashSet<string>(recogidos.Select(r => r.Nombre), StringComparer.OrdinalIgnoreCase);

        var fallidos = new List<string>();
        var hechos = 0;
        foreach (var nombre in marca.Archivos)
        {
            if (nombre.IndexOfAny(new[] { '/', '\\' }) >= 0 || nombre.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
            var origen = Path.Combine(dir, nombre);
            var destino = Path.Combine(p.DataDir, nombre);
            try
            {
                // Lo que existe y no está en la copia previa (una configuración que no se entiende
                // como JSON no se copia, por si lleva claves) se aparta como «.ilegible_fecha»,
                // igual que haría el motor al leerlo: sustituir no puede perder nada sin rastro.
                if (File.Exists(destino) && !enLaPrevia.Contains(nombre)) ArchivoDatos.CopiarApartado(destino);
                var bytes = File.ReadAllBytes(origen);
                if (Igual(nombre, Path.GetFileName(p.ConfigPath))) bytes = ConDatosDelEquipo(bytes, destino);
                // Como texto SIN la marca de orden de bytes que traen algunos (las listas de
                // IgnoreList): Escribir pone la suya, y con dos el JSON ya no se podría leer.
                var error = ArchivoDatos.Escribir(destino, RutasJson.Texto(bytes));
                if (error.Length > 0) { fallidos.Add($"{nombre} ({error})"); continue; }
                hechos++;
            }
            catch (Exception e) { fallidos.Add($"{nombre} ({e.Message})"); }
        }
        try { Directory.Delete(dir, recursive: true); } catch { }
        return new ResultadoRestauracion(marca.FechaCopia, hechos, fallidos, rutaPrevia, "");
    }

    /// <summary>
    /// La configuración restaurada, con las claves cifradas y la ventana de la configuración que había
    /// en este equipo. Si la de aquí no se puede leer, va la restaurada tal cual (sin claves).
    ///
    /// El Client ID de Spotify no es secreto y viaja en la copia, pero va en pareja con su Client
    /// Secret, que no viaja: si este equipo conserva su secreto, se conserva también su ID. Con el ID
    /// de la copia y el secreto de aquí, Spotify rechazaría las dos.
    /// </summary>
    internal static byte[] ConDatosDelEquipo(byte[] restaurada, string actual)
    {
        if (RutasJson.Leer(RutasJson.Texto(restaurada)) is not JsonObject nueva) return restaurada;
        foreach (var k in nueva.Select(x => x.Key).Where(EsDelEquipo).ToList()) nueva.Remove(k);
        try
        {
            if (File.Exists(actual) && RutasJson.Leer(RutasJson.Texto(File.ReadAllBytes(actual))) is JsonObject vieja)
            {
                foreach (var (k, v) in vieja.Where(x => EsDelEquipo(x.Key)).ToList())
                    nueva[k] = v?.DeepClone();
                // MEDIDO al restaurar sobre una configuración con secreto y sin ID: se ponía SpotifyId a
                // null, y la configuración lo lee como texto. Sin ID aquí, queda vacío.
                if (vieja[nameof(AppConfig.SpotifySecretEnc)] is JsonValue secreto
                    && secreto.GetValueKind() == JsonValueKind.String && secreto.GetValue<string>().Length > 0)
                    nueva[nameof(AppConfig.SpotifyId)] = vieja[nameof(AppConfig.SpotifyId)] is JsonValue id && id.GetValueKind() == JsonValueKind.String
                        ? id.DeepClone()
                        : JsonValue.Create("");
            }
        }
        catch { /* sin la de aquí, la restaurada sin claves */ }
        return Encoding.UTF8.GetBytes(nueva.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
