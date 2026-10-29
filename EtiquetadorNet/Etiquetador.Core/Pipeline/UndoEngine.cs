using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etiquetador.Core.Providers;

namespace Etiquetador.Core.Pipeline;

/// <summary>Resultado de un deshacer.</summary>
/// <param name="Aviso">"" si todo fue normal; si no, algo que el usuario debe saber (p. ej. que el manifiesto no se pudo actualizar).</param>
public sealed record UndoResult(string File, int Reverted, int Errors, int Missing, int Manual, bool Clean, bool None, string Aviso = "")
{
    /// <summary>
    /// Registros que siguen en el manifiesto: el próximo «Deshacer última» reintentará SOLO esos.
    /// </summary>
    public int Pendientes => Errors + Missing;

    /// <summary>
    /// Esta pasada no ha deshecho NADA y siguen quedando pendientes: el manifiesto está atascado
    /// (un archivo enviado a la papelera, uno que falla siempre). Es cuando la interfaz debe ofrecer
    /// <see cref="UndoEngine.DarPorPerdidos"/>; si no, las ejecuciones anteriores no se alcanzan.
    /// </summary>
    public bool SinAvance => !None && Pendientes > 0 && Reverted == 0 && Manual == 0;

    /// <summary>
    /// Rutas de lo revertido cuyo nombre o etiquetas han vuelto atrás (la de antes y la de después,
    /// de cada registro), venga de donde venga: lo que se calculó sobre ellas (una propuesta en
    /// pantalla) ya no vale. Un registro que solo devuelve el volumen no entra: el audio cambia, pero
    /// el nombre y las etiquetas, que es de lo que sale la propuesta, siguen igual.
    /// </summary>
    public IReadOnlyList<string> Revertidas { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Rutas de lo revertido que había dado la canción por aplicada (<see cref="UndoRecord.Aplicada"/>),
    /// para que quien llama la olvide como «ya aplicada». Sin esto, una canción que solo cambió tags
    /// seguía en aplicadas.json después de deshacerla y Analizar no la volvía a proponer nunca.
    /// Solo estas: deshacer un ajuste de volumen no puede devolver a pendientes las aplicadas en
    /// Enriquecer (ver <see cref="UndoEngine.DioPorAplicada"/>).
    /// </summary>
    public IReadOnlyList<string> AplicadasRevertidas { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Las dos rutas de CADA registro que se ha intentado deshacer, haya ido como haya ido: es lo que
    /// hay que releer en la biblioteca (<c>LibraryStore.RefrescarAsync</c>) en lugar de recorrer
    /// todas las carpetas. <see cref="Revertidas"/> no basta: un registro que vuelve al nombre
    /// original y después falla al escribir las etiquetas cuenta como error, pero el archivo ya ha
    /// cambiado de nombre.
    /// </summary>
    public IReadOnlyList<string> Tocadas { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Archivos que este deshacer ha devuelto a su nombre anterior y ahí se quedan: dónde estaban y
    /// dónde quedan. Lo que quien llama guarda por ruta para ESE archivo (que una ejecución posterior lo
    /// dio por aplicado) tiene que seguirlo, igual que lo siguen los registros de esas posteriores
    /// (hallazgo datos-1). Sin esto, tras deshacer una sustitución de la Bandeja la marca de «aplicada»
    /// que Enriquecer puso a la descarga se quedaba en la ruta de la biblioteca, y la copia antigua
    /// que se recuperase allí de la papelera ya no se volvía a proponer.
    /// </summary>
    public IReadOnlyList<(string Desde, string Hasta)> Vueltas { get; init; } = Array.Empty<(string, string)>();
}

/// <summary>
/// Lo que un deshacer acaba de hacer con un archivo, para que quien guarda cachés por ruta, fecha y
/// tamaño (huellas, «Comprobar audio», sonoridad) las ponga al día en lugar de darlas por caducadas.
///
/// POR QUÉ (hallazgo datos-3). Al escribir, Enriquecer, Unificar géneros o las herramientas del
/// Asistente avisan a esas cachés; al deshacer no se avisaba a nadie: la fecha del archivo cambiaba,
/// las huellas se volvían a calcular y «Comprobar audio» volvía a mandar al servicio de pago canciones
/// que ya había identificado. Desde el historial se deshacen miles de canciones de una vez.
/// </summary>
/// <param name="Desde">Dónde estaba el archivo al empezar a deshacer.</param>
/// <param name="Hasta">Dónde ha quedado (la misma ruta si no ha vuelto a su nombre anterior).</param>
/// <param name="Etiquetas">Se han reescrito etiquetas o la carátula: el audio es el mismo.</param>
/// <param name="Volumen">Se ha devuelto el volumen: cambia el audio, pero no la canción ni su huella.</param>
/// <param name="FechaAntes">Fecha de modificación (ticks UTC) justo antes de tocarlo.</param>
/// <param name="TamanoAntes">Tamaño en bytes justo antes de tocarlo.</param>
/// <param name="ExistiaAntes">Se pudieron leer la fecha y el tamaño antes de tocarlo.</param>
/// <param name="PasosVolumen">
/// Pasos de global_gain que ha aplicado el deshacer (negativo si ha bajado el volumen; 0 si no lo ha
/// tocado). Con ellos, la medida de sonoridad se pone al día sin volver a medir el archivo.
/// </param>
/// <param name="PasosAnotados">
/// La parte de <paramref name="PasosVolumen"/> que deshace ajustes anotados en la ganancia aplicada
/// (<see cref="UndoRecord.GananciaAnotada"/>): solo esos pasos se descuentan de ella. Los de un ajuste
/// de BeatTag 1.10, que no la anotaba, solo desplazan la medida.
/// </param>
public readonly record struct ArchivoDeshecho(string Desde, string Hasta, bool Etiquetas, bool Volumen,
                                              long FechaAntes, long TamanoAntes, bool ExistiaAntes, int PasosVolumen = 0,
                                              int PasosAnotados = 0);

/// <summary>Resultado de <see cref="UndoEngine.DarPorPerdidos"/>.</summary>
/// <param name="Archivo">El manifiesto que se ha sacado de la cola ("" si no había ninguno).</param>
/// <param name="Registros">Cuántos registros pendientes se han dado por perdidos.</param>
/// <param name="Error">"" si fue bien; si no, por qué el manifiesto sigue pendiente.</param>
public sealed record PendientesDescartados(string Archivo, int Registros, string Error = "")
{
    public bool Ok => Error.Length == 0;
}

/// <summary>
/// Deshace la ÚLTIMA ejecución (o un manifiesto dado): renombra a la ruta ABSOLUTA original y
/// restaura CADA campo solo si su valor actual sigue siendo el que escribió la app. Lee el formato
/// nuevo (OrigPath/FinalPath/Fields) y también migra el formato antiguo (new/orig/tags/wtitle).
///
/// Tras cada pasada, el manifiesto se queda SOLO con lo que no se pudo revertir, y lo revertido se
/// archiva aparte («run_X.deshecho.jsonl»). Así deshacer se puede repetir sin efectos dobles.
/// </summary>
public sealed class UndoEngine
{
    private enum Outcome { Reverted, Missing, Manual, Error }

    private static readonly JsonSerializerOptions Json = new();
    private readonly AppPaths _paths;
    private readonly Logger? _log;

    public UndoEngine(AppPaths paths, Logger? log = null) { _paths = paths; _log = log; }

    /// <summary>
    /// Se llama tras cada archivo que un deshacer ha renombrado o reescrito, en el hilo que deshace
    /// (ver <see cref="ArchivoDeshecho"/>). Un fallo de quien escucha no detiene el deshacer.
    /// </summary>
    public Action<ArchivoDeshecho>? AlDeshacer { get; set; }

    /// <summary>
    /// Cómo se reescribe el manifiesto de una ejecución posterior para que siga a un archivo que ha
    /// vuelto a su nombre anterior. Es <see cref="ArchivoDatos.Escribir"/>; las pruebas lo sustituyen
    /// para simular un manifiesto bloqueado (antivirus, OneDrive) justo en ese momento.
    /// </summary>
    internal Func<string, string, string> EscribirPosterior { get; set; } = ArchivoDatos.Escribir;

    // Solo cuentan como «por deshacer» los manifiestos de ejecución puros: run_aaaammdd_hhmmss.jsonl
    // (con un _N opcional por si dos ejecuciones caen en el mismo segundo).
    private static readonly Regex ManifiestoPendiente =
        new(@"^run_\d{8}_\d{6}(_\d+)?\.jsonl$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// ¿Es un manifiesto de ejecución que aún se puede deshacer? Los archivados («.deshecho») no.
    ///
    /// Visto en una biblioteca real: el patrón «run_*.jsonl» también casaba con
    /// run_20260920_230254.deshecho.jsonl, y como renombrar no cambia la fecha de modificación,
    /// el manifiesto YA deshecho seguía siendo «el último». Un segundo clic lo volvía a procesar:
    /// con los 29 MP3 ajustados a −3/−4,5 dB el 09-10, los habría dejado 3-4,5 dB por ENCIMA del
    /// original, y ninguna ejecución anterior se podía alcanzar desde la interfaz.
    /// </summary>
    public static bool EsManifiestoPendiente(string nombreArchivo)
        => ManifiestoPendiente.IsMatch(Path.GetFileName(nombreArchivo));

    /// <summary>
    /// Manifiestos que aún se pueden deshacer, del más reciente al más antiguo. Se ordenan por el
    /// NOMBRE, que lleva la fecha de la ejecución, y no por la fecha de modificación: reescribir un
    /// manifiesto a medio deshacer no lo convierte en más reciente que la ejecución siguiente.
    /// </summary>
    public IReadOnlyList<string> ManifiestosPendientes()
    {
        if (!Directory.Exists(_paths.UndoDir)) return Array.Empty<string>();
        return new DirectoryInfo(_paths.UndoDir).EnumerateFiles("run_*.jsonl")
            .Where(f => EsManifiestoPendiente(f.Name))
            .OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.FullName)
            .ToList();
    }

    public UndoResult UndoLastRun(string? manifest = null)
    {
        string? mf;
        if (!string.IsNullOrEmpty(manifest)) mf = File.Exists(manifest) ? manifest : null;
        else mf = PrimeroConRegistros();
        if (mf == null) return new UndoResult("", 0, 0, 0, 0, false, true);
        return Deshacer(mf, null);
    }

    /// <summary>
    /// Deshace SOLO los registros elegidos de una ejecución pendiente (E14), identificados por la
    /// huella de su línea (<see cref="HuellaLinea"/>); con <paramref name="huellas"/> null, todos,
    /// igual que <see cref="UndoLastRun"/> con ese manifiesto. Lo que no se elige sigue pendiente en
    /// el manifiesto, que se reescribe de forma atómica; lo revertido se archiva como en un deshacer
    /// normal («.deshecho»), así que RekordboxRelocator y «Deshacer última» lo ven igual.
    ///
    /// Se identifica por la huella y no por el número de línea a propósito: entre que el historial
    /// lee el manifiesto y el usuario confirma, una pasada anterior puede haberlo reescrito (las
    /// líneas pendientes se conservan tal cual, pero cambian de posición).
    ///
    /// Solo acepta manifiestos pendientes: un archivado ya está deshecho, y volver a procesarlo es
    /// justo el fallo T1 (el volumen se aplicaba otra vez).
    /// </summary>
    public UndoResult DeshacerCambios(string manifiesto, IReadOnlyCollection<string>? huellas)
    {
        if (string.IsNullOrEmpty(manifiesto) || !EsManifiestoPendiente(manifiesto) || !File.Exists(manifiesto)
            || huellas is { Count: 0 })
            return new UndoResult("", 0, 0, 0, 0, false, true);
        return Deshacer(manifiesto, huellas == null ? null : new HashSet<string>(huellas, StringComparer.Ordinal));
    }

    /// <summary>
    /// Identifica una línea de manifiesto: los 16 primeros caracteres hexadecimales del SHA-1 de su
    /// texto. Archivar copia las líneas tal cual, así que la huella no cambia mientras siga pendiente.
    /// Dos líneas idénticas tienen la misma, y se deshacen juntas: describen el mismo cambio.
    /// </summary>
    public static string HuellaLinea(string linea)
        => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(linea)), 0, 8).ToLowerInvariant();

    // --- Lo que hizo el deshacer con cada archivo ---

    /// <summary>Lo que el deshacer de un registro ha hecho con su archivo (ver <see cref="ArchivoDeshecho"/>).</summary>
    private sealed class Efecto
    {
        public string Desde = "", Hasta = "";
        public bool Movido, Etiquetas, Volumen;
        public int PasosVolumen, PasosAnotados;
        public long M, S;
        public bool Existia;

        /// <summary>Dónde está el archivo y su fecha y tamaño, ANTES de tocarlo.</summary>
        public void Sellar(string ruta)
        {
            Desde = Hasta = ruta;
            try
            {
                var fi = new FileInfo(ruta);
                if (fi.Exists) { M = fi.LastWriteTimeUtc.Ticks; S = fi.Length; Existia = true; }
            }
            catch { /* sin sello, las cachés de ese archivo solo se mueven */ }
        }
    }

    private void Avisar(Efecto e)
    {
        if (AlDeshacer == null || !(e.Movido || e.Etiquetas || e.Volumen)) return;
        try { AlDeshacer(new ArchivoDeshecho(e.Desde, e.Hasta, e.Etiquetas, e.Volumen, e.M, e.S, e.Existia, e.PasosVolumen, e.PasosAnotados)); }
        catch (Exception x) { _log?.Log($"  deshacer: no se pudieron poner al día las cachés de audio de «{Path.GetFileName(e.Hasta)}»: {x.Message}", LogKind.Err); }
    }

    // --- Las ejecuciones posteriores siguen al archivo que vuelve a su nombre (hallazgo datos-1) ---
    //
    // Desde el historial (E14) se puede deshacer un renombrado aunque una ejecución POSTERIOR siga
    // pendiente sobre el mismo archivo sin renombrarlo (un ajuste de volumen, el Editor, Enriquecer
    // sobre una descarga que «Sustituir» llevó a la biblioteca). Al volver el archivo a su nombre
    // anterior, el registro de la posterior seguía apuntando al nombre que le había dejado esta. Visto
    // en revisión: si después aparecía OTRO archivo con ese nombre -la copia antigua recuperada de la
    // papelera, como aconseja la Bandeja al sustituir-, «Deshacer última» le devolvía a ese otro
    // archivo las etiquetas de la descarga, o le aplicaba la ganancia inversa de un volumen que nunca
    // se le subió. Y si no aparecía nada, la posterior se quedaba atascada como «no encontrada».
    //
    // Ahora, al devolver un archivo a su nombre anterior, los registros PENDIENTES de las ejecuciones
    // posteriores que lo tocaron sin renombrarlo pasan a apuntar a ese nombre: siguen deshaciéndose
    // sobre el archivo correcto. Si el historial de una posterior no se deja escribir, ese archivo
    // vuelve a donde lo dejó la ejecución y se queda pendiente: nunca se deja una posterior
    // apuntando a un nombre que ya no es el de su archivo.

    /// <summary>Un archivo que este deshacer ha devuelto a su nombre anterior.</summary>
    /// <param name="Linea">Su registro en el manifiesto que se deshace.</param>
    /// <param name="Desde">Dónde lo dejó la ejecución, que es donde lo esperan las posteriores.</param>
    /// <param name="Hasta">Su nombre anterior, donde queda.</param>
    internal sealed record Vuelta(int Linea, string Desde, string Hasta);

    /// <summary>El manifiesto pendiente de una ejecución posterior, con sus líneas puestas al día.</summary>
    private sealed class Posterior
    {
        public required string Ruta { get; init; }

        /// <summary>Las líneas con contenido, tal como se leyeron; null si no se pudo leer.</summary>
        public required List<string>? Lineas { get; init; }

        /// <summary>Las líneas puestas al día, o null si ninguna cambia.</summary>
        public string[]? Nuevas;

        /// <summary>De cada línea puesta al día, la ruta seguida que cambió.</summary>
        public readonly Dictionary<int, string> Claves = new();

        public bool Escrita;
    }

    /// <summary>Antes, después y si renombra, de un registro de formato nuevo o antiguo; null si no se entiende.</summary>
    private static (string Antes, string Despues, bool Renombra)? Rutas3(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        if (o.ContainsKey("OrigPath"))
        {
            var antes = J.S(o["OrigPath"]);
            var despues = J.S(o["FinalPath"]);
            if (despues.Length == 0) despues = antes;
            return (antes, despues, Verdadero(o["Renamed"]) && !string.Equals(antes, despues, StringComparison.Ordinal));
        }
        var actual = J.S(o["new"]);
        if (actual.Length == 0) return null;
        var orig = J.S(o["orig"]);
        var previa = orig.Length == 0 ? actual : Path.Combine(Path.GetDirectoryName(actual) ?? "", orig);
        return (previa, actual, Verdadero(o["renamed"]) && !string.Equals(previa, actual, StringComparison.Ordinal));
    }

    private static bool Verdadero(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>
    /// La línea <paramref name="linea"/> de una ejecución posterior puesta al día, o null si no cambia.
    /// <paramref name="seguir"/> lleva cada ruta seguida (donde la posterior esperaba el archivo) a la
    /// ruta en que queda. Solo cambia un registro que tocó el archivo SIN renombrarlo; uno que lo
    /// renombra, o que lleva otro archivo a esa ruta, hace que desde ahí esa ruta sea de otro
    /// archivo, y se deja de seguir. Hay que llamarla con las líneas en orden, de la más antigua a la
    /// más reciente. <paramref name="clave"/> dice qué ruta seguida cambió.
    /// </summary>
    internal static string? Reubicar(string linea, Dictionary<string, string> seguir, out string? clave)
    {
        clave = null;
        if (seguir.Count == 0) return null;
        JsonObject? o;
        try { o = JsonNode.Parse(linea) as JsonObject; } catch { return null; }
        if (Rutas3(o) is not { } r) return null;
        if (r.Renombra)
        {
            seguir.Remove(r.Antes);
            seguir.Remove(r.Despues);
            return null;
        }
        if (!seguir.TryGetValue(r.Antes, out var nueva)) return null;
        clave = r.Antes;
        if (o!.ContainsKey("OrigPath"))
        {
            o["OrigPath"] = nueva;
            if (J.S(o["FinalPath"]).Length > 0) o["FinalPath"] = nueva;
        }
        else
        {
            o["new"] = nueva;
            if (J.S(o["orig"]).Length > 0) o["orig"] = Path.GetFileName(nueva);
        }
        return o.ToJsonString();
    }

    /// <summary>
    /// Los manifiestos pendientes de las ejecuciones POSTERIORES a <paramref name="mf"/>, de la más
    /// antigua a la más reciente. Se leen dejando que otro los lea a la vez (el historial puede estar
    /// abierto). <paramref name="ilegible"/>: alguno no se pudo leer.
    /// </summary>
    private List<Posterior> LeerPosteriores(string mf, out string ilegible)
    {
        ilegible = "";
        var nombre = Path.GetFileName(mf);
        var lista = new List<Posterior>();
        foreach (var p in ManifiestosPendientes().Where(p => string.Compare(Path.GetFileName(p), nombre, StringComparison.OrdinalIgnoreCase) > 0)
                                                 .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            List<string>? lineas = null;
            try
            {
                using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var lector = new StreamReader(fs, Encoding.UTF8);
                lineas = new List<string>();
                for (var l = lector.ReadLine(); l != null; l = lector.ReadLine())
                    if (l.Trim().Length > 0) lineas.Add(l);
            }
            catch (Exception e)
            {
                if (ilegible.Length == 0) ilegible = $"{Path.GetFileName(p)}: {e.Message}";
            }
            lista.Add(new Posterior { Ruta = p, Lineas = lineas });
        }
        return lista;
    }

    /// <summary>
    /// De <paramref name="rutas"/> (donde las ejecuciones que se deshacen dejaron sus archivos
    /// renombrados), las que no se podrían seguir ahora: alguna posterior que tendría que ponerse al
    /// día no se deja escribir. Se comprueba ANTES de tocar ningún archivo, abriendo cada manifiesto
    /// en exclusiva un instante, para no renombrar algo que luego haya que devolver. Si alguna
    /// posterior no se pudo ni leer, no se sabe qué toca: no se sigue ninguna.
    /// </summary>
    private static HashSet<string> PosterioresQueNoSeDejanEscribir(List<Posterior> posteriores, IEnumerable<string> rutas,
                                                                  string ilegible, out string motivo)
    {
        motivo = ilegible;
        var todas = new HashSet<string>(rutas, StringComparer.OrdinalIgnoreCase);
        if (ilegible.Length > 0) return todas;

        // Las mismas reglas que al seguir de verdad, con las rutas apuntando a sí mismas: solo
        // interesa qué manifiestos habría que reescribir y por qué rutas.
        var simulacion = todas.ToDictionary(r => r, r => r, StringComparer.OrdinalIgnoreCase);
        var bloqueadas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in posteriores)
        {
            if (simulacion.Count == 0) break;
            var claves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in p.Lineas!)
                if (Reubicar(l, simulacion, out var c) != null && c != null) claves.Add(c);
            if (claves.Count == 0) continue;
            try { using (new FileStream(p.Ruta, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
            catch (Exception e)
            {
                if (motivo.Length == 0) motivo = $"{Path.GetFileName(p.Ruta)}: {e.Message}";
                bloqueadas.UnionWith(claves);
            }
        }
        return bloqueadas;
    }

    /// <summary>
    /// El aviso de los renombrados que no se deshacen todavía porque una posterior no se puede poner
    /// al día (ver <see cref="PosterioresQueNoSeDejanEscribir"/>).
    ///
    /// POR QUÉ DOS TEXTOS (revisión de datos-1): si una posterior no se pudo ni leer, se aplazan TODOS
    /// los renombrados elegidos, también los de archivos que ninguna posterior tocó, porque no se sabe
    /// qué toca. Decir entonces que «el historial de una ejecución posterior que también los cambió
    /// está en uso» era falso para esos archivos.
    /// </summary>
    /// <param name="n">Cuántos archivos se aplazan.</param>
    /// <param name="motivo">El manifiesto y el error, tal como los dio el sistema.</param>
    /// <param name="ilegible">Alguna posterior no se pudo leer (y no solo escribir).</param>
    internal static string TextoAplazados(int n, string motivo, bool ilegible)
    {
        var cuantos = n == 1 ? "1 archivo no se ha deshecho todavía" : $"{n} archivos no se han deshecho todavía";
        var porque = ilegible
            ? $"no se ha podido leer el historial de una ejecución posterior ({motivo}), y sin él no se sabe si "
              + $"{(n == 1 ? "lo cambió" : "los cambió")} también"
            : $"el historial de una ejecución posterior que también {(n == 1 ? "lo" : "los")} cambió está en uso ({motivo})";
        return $"{cuantos}: {porque}. Vuelve a intentarlo dentro de unos segundos.";
    }

    /// <summary>
    /// Pone al día las posteriores para que sigan a los archivos de <paramref name="vueltas"/>. Lo que
    /// no se pueda poner al día en TODAS sus posteriores vuelve a donde lo dejó la ejecución, y su
    /// registro se queda pendiente (con un aviso). Devuelve las rutas de esos archivos.
    /// </summary>
    private HashSet<string> SeguirEnPosteriores(List<Posterior> posteriores, List<Vuelta> vueltas,
                                                Outcome?[] resultado, bool[] pendiente, List<string> avisos)
    {
        var noSiguen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seguir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in vueltas) seguir.TryAdd(v.Desde, v.Hasta);

        // 1) En memoria y en orden: una posterior puede haber vuelto a llevar otro archivo a ese nombre.
        foreach (var p in posteriores)
        {
            if (seguir.Count == 0) break;
            if (p.Lineas == null) continue;
            for (var k = 0; k < p.Lineas.Count; k++)
            {
                var nueva = Reubicar(p.Lineas[k], seguir, out var clave);
                if (nueva == null || clave == null) continue;
                p.Nuevas ??= p.Lineas.ToArray();
                p.Nuevas[k] = nueva;
                p.Claves[k] = clave;
            }
        }

        // 2) A disco. Unos pocos reintentos: un antivirus o OneDrive sueltan el archivo enseguida.
        var errores = new List<string>();
        foreach (var p in posteriores.Where(p => p.Nuevas != null))
        {
            var error = EscribirConReintentos(p.Ruta, p.Nuevas!);
            if (error.Length == 0)
            {
                p.Escrita = true;
                _log?.Log($"  deshacer: {p.Claves.Count} registro(s) de {Path.GetFileName(p.Ruta)} siguen a su archivo hasta su nombre anterior.", LogKind.No);
                continue;
            }
            errores.Add($"{Path.GetFileName(p.Ruta)}: {error}");
            noSiguen.UnionWith(p.Claves.Values);
        }
        if (noSiguen.Count == 0) return noSiguen;
        _log?.Log("  deshacer: no se pudieron poner al día ejecuciones posteriores: " + string.Join(" · ", errores), LogKind.Err);

        // 3) Cada archivo que alguna posterior no sigue vuelve a donde lo dejó la ejecución, y las
        //    posteriores que sí se pusieron al día vuelven a esperarlo ahí.
        var devueltas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sinDevolver = new List<string>();
        foreach (var v in vueltas.Where(v => noSiguen.Contains(v.Desde)))
        {
            try
            {
                Mover(v.Hasta, v.Desde);
                devueltas.Add(v.Desde);
                resultado[v.Linea] = Outcome.Error;
                pendiente[v.Linea] = true;
                Avisar(new Efecto { Desde = v.Hasta, Hasta = v.Desde, Movido = true });
            }
            catch (Exception e)
            {
                sinDevolver.Add(Path.GetFileName(v.Hasta));
                _log?.Log($"  deshacer: «{Path.GetFileName(v.Hasta)}» no se pudo devolver a «{Path.GetFileName(v.Desde)}»: {e.Message}", LogKind.Err);
            }
        }
        foreach (var p in posteriores.Where(p => p.Escrita))
        {
            var volver = p.Claves.Where(kv => devueltas.Contains(kv.Value)).Select(kv => kv.Key).ToList();
            if (volver.Count == 0) continue;
            foreach (var k in volver) p.Nuevas![k] = p.Lineas![k];
            var error = EscribirConReintentos(p.Ruta, p.Nuevas!);
            if (error.Length > 0)
            {
                avisos.Add($"No se ha podido corregir el historial de {Path.GetFileName(p.Ruta)} ({error}): al deshacer esa ejecución, "
                         + (volver.Count == 1 ? "1 archivo puede no encontrarse." : $"{volver.Count} archivos pueden no encontrarse."));
                _log?.Log($"  deshacer: {Path.GetFileName(p.Ruta)} se quedó apuntando a {volver.Count} nombre(s) anterior(es): {error}", LogKind.Err);
            }
        }

        var n = devueltas.Count;
        if (n > 0)
            avisos.Add($"{(n == 1 ? "1 archivo se ha dejado con el nombre que le dio la ejecución" : $"{n} archivos se han dejado con el nombre que les dio la ejecución")}: "
                     + $"no se pudo poner al día el historial de una ejecución posterior que también {(n == 1 ? "lo" : "los")} cambió ({errores[0]}). "
                     + $"{(n == 1 ? "Sigue pendiente" : "Siguen pendientes")}; vuelve a intentarlo dentro de unos segundos.");
        if (sinDevolver.Count > 0)
            avisos.Add($"No se ha podido poner al día el historial de una ejecución posterior ({errores[0]}) ni devolver "
                     + (sinDevolver.Count == 1 ? $"«{sinDevolver[0]}» a donde lo dejó" : $"{sinDevolver.Count} archivos a donde los dejó")
                     + " la ejecución: al deshacer esa ejecución posterior, comprueba antes que sus archivos siguen en su sitio.");
        noSiguen.IntersectWith(devueltas);
        return noSiguen;
    }

    /// <summary>
    /// Lo mismo dentro del manifiesto que se deshace: sus registros POSTERIORES (en él) que siguen
    /// pendientes -no elegidos, o que no se pudieron deshacer- y tocaron el archivo sin renombrarlo.
    ///
    /// En una sola pasada hacia delante, con un único diccionario en el que cada vuelta entra al
    /// pasar por su línea. POR QUÉ (revisión de datos-1): antes cada vuelta recorría por su cuenta
    /// todas las líneas pendientes que la seguían y las volvía a leer como JSON; deshacer desde el
    /// historial la mitad de una ejecución de miles de renombrados tardaba el triple (MEDIDO: 6.000
    /// registros, 13,7 s frente a 2,7 s), y con ejecuciones de 15.000 pasaba del minuto. Así cada
    /// línea pendiente se lee una vez. Y si dos vueltas dejaron su archivo en la misma ruta, manda
    /// la más reciente, que es la que ocupaba esa ruta cuando se escribieron las líneas de después.
    /// </summary>
    internal static void SeguirEnEsteManifiesto(List<string> textos, bool[] pendiente, IEnumerable<Vuelta> vueltas)
    {
        var porLinea = vueltas.GroupBy(v => v.Linea).ToDictionary(g => g.Key, g => g.ToList());
        if (porLinea.Count == 0) return;
        var desde = porLinea.Keys.Min();
        var seguir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var j = desde; j < textos.Count; j++)
        {
            // Primero la línea, con lo que venía de antes (si es un renombrado pendiente, deja de
            // seguirse esa ruta), y después la vuelta que empieza en ella: nunca se sigue a sí misma.
            if (seguir.Count > 0 && pendiente[j])
            {
                var nueva = Reubicar(textos[j], seguir, out _);
                if (nueva != null) textos[j] = nueva;
            }
            if (porLinea.TryGetValue(j, out var empiezan))
                foreach (var v in empiezan) seguir[v.Desde] = v.Hasta;
        }
    }

    /// <summary>Escribe un manifiesto posterior, con unos pocos reintentos. "" si fue bien, o el motivo.</summary>
    private string EscribirConReintentos(string ruta, IEnumerable<string> lineas)
    {
        var contenido = string.Join("\n", lineas) + "\n";
        var error = "";
        for (var intento = 0; intento < 3; intento++)
        {
            if (intento > 0) System.Threading.Thread.Sleep(150);
            error = EscribirPosterior(ruta, contenido);
            if (error.Length == 0) return "";
        }
        return error;
    }

    /// <summary>Mueve un archivo; si solo cambian mayúsculas, en dos pasos (Windows y macOS no distinguen).</summary>
    private static void Mover(string de, string a)
    {
        if (string.Equals(de, a, StringComparison.OrdinalIgnoreCase))
        {
            var tmp = Path.Combine(Path.GetDirectoryName(a) ?? "",
                                   Path.GetFileNameWithoutExtension(a) + "_" + Guid.NewGuid().ToString("N")[..6] + Path.GetExtension(a));
            File.Move(de, tmp);
            File.Move(tmp, a);
        }
        else File.Move(de, a);
    }

    private UndoResult Deshacer(string mf, IReadOnlySet<string>? seleccion)
    {
        // Cada línea con su texto original: lo que no se pueda revertir vuelve al manifiesto tal cual.
        var lineas = new List<(string Texto, JsonNode? Nodo)>();
        foreach (var ln in File.ReadAllLines(mf, Encoding.UTF8))
        {
            if (ln.Trim().Length == 0) continue;
            JsonNode? n = null;
            try { n = JsonNode.Parse(ln); } catch { }
            if (n == null) _log?.Log("  deshacer: línea ilegible en el manifiesto; se archiva sin tocar ningún archivo", LogKind.Err);
            lineas.Add((ln, n));
        }
        var textos = lineas.Select(l => l.Texto).ToList();
        bool Elegida(int i) => seleccion == null || seleccion.Contains(HuellaLinea(lineas[i].Texto));

        // Antes de tocar nada: las ejecuciones posteriores que tendrán que seguir a los archivos que
        // vuelvan a su nombre anterior, y lo que no se puede deshacer ahora porque su historial no se
        // deja escribir (ver SeguirEnPosteriores).
        var posteriores = new List<Posterior>();
        var avisos = new List<string>();
        var aplazadas = new HashSet<int>();
        var renombran = new Dictionary<int, string>();
        for (var i = 0; i < lineas.Count; i++)
            if (Elegida(i) && Rutas3(lineas[i].Nodo) is { Renombra: true } r) renombran[i] = r.Despues;
        if (renombran.Count > 0)
        {
            posteriores = LeerPosteriores(mf, out var ilegible);
            var bloqueadas = PosterioresQueNoSeDejanEscribir(posteriores, renombran.Values, ilegible, out var motivo);
            foreach (var (i, ruta) in renombran)
                if (bloqueadas.Contains(ruta)) aplazadas.Add(i);
            if (aplazadas.Count > 0)
            {
                var n = aplazadas.Count;
                avisos.Add(TextoAplazados(n, motivo, ilegible.Length > 0));
                _log?.Log(ilegible.Length > 0
                    ? $"  deshacer: {n} renombrado(s) aplazados; no se pudo leer una ejecución posterior ({motivo})."
                    : $"  deshacer: {n} renombrado(s) aplazados; una ejecución posterior no se deja poner al día ({motivo}).", LogKind.Err);
            }
        }

        // Al revés: lo último que se hizo es lo primero que se deshace.
        var pendiente = new bool[lineas.Count];
        var resultado = new Outcome?[lineas.Count];
        var vueltas = new List<Vuelta>();
        var tocadas = new List<string>();
        var legadoDeAplicar = ManifiestoAntiguoDeAplicar(lineas.Select(l => l.Nodo));
        for (var i = lineas.Count - 1; i >= 0; i--)
        {
            // Lo que no se ha elegido (E14) se queda pendiente tal cual, sin mirar el archivo.
            if (!Elegida(i)) { pendiente[i] = true; continue; }
            if (aplazadas.Contains(i)) { pendiente[i] = true; resultado[i] = Outcome.Error; continue; }
            var node = lineas[i].Nodo;
            if (node == null) continue;
            // Primero la de después y luego la de antes: al deshacer, el archivo acaba en la de antes,
            // y la biblioteca relee la lista del final al principio. Con un cambio solo de mayúsculas
            // («tema» → «Tema»), las dos rutas valen para el mismo archivo y debe entrar con «tema».
            tocadas.AddRange(RutasDe(node).Reverse());
            var efecto = new Efecto();
            Outcome outcome;
            try
            {
                var isNew = node is JsonObject o && o.ContainsKey("OrigPath");
                outcome = isNew ? ProcessNew(node.Deserialize<UndoRecord>(Json)!, efecto) : ProcessLegacy(node, efecto);
            }
            catch (Exception e)
            {
                outcome = Outcome.Error;
                _log?.Log("  deshacer ERROR: " + e.Message, LogKind.Err);
            }
            resultado[i] = outcome;
            if (outcome is Outcome.Missing or Outcome.Error) pendiente[i] = true;
            if (efecto.Movido) vueltas.Add(new Vuelta(i, efecto.Desde, efecto.Hasta));
            Avisar(efecto);
        }

        // Las ejecuciones posteriores siguen a los archivos que han vuelto a su nombre anterior.
        var quedan = new List<(string Desde, string Hasta)>();
        if (vueltas.Count > 0)
        {
            var devueltas = SeguirEnPosteriores(posteriores, vueltas, resultado, pendiente, avisos);
            var siguen = vueltas.Where(v => !devueltas.Contains(v.Desde)).ToList();
            SeguirEnEsteManifiesto(textos, pendiente, siguen);
            quedan.AddRange(siguen.Select(v => (v.Desde, v.Hasta)));
        }

        // Las cuentas, al final: devolver un archivo a donde lo dejó la ejecución (arriba) cambia el
        // resultado de su registro.
        var revertidas = new List<string>();
        var aplicadasRevertidas = new List<string>();
        int rev = 0, err = 0, missing = 0, manual = 0;
        for (var i = lineas.Count - 1; i >= 0; i--)
        {
            switch (resultado[i])
            {
                case Outcome.Reverted:
                    rev++;
                    var rutas = RutasDe(lineas[i].Nodo!).ToList();
                    if (!SoloVolumen(lineas[i].Nodo!)) revertidas.AddRange(rutas);
                    if (DioPorAplicada(lineas[i].Nodo, legadoDeAplicar)) aplicadasRevertidas.AddRange(rutas);
                    break;
                case Outcome.Missing: missing++; break;
                case Outcome.Manual: manual++; break;
                case Outcome.Error: err++; break;
            }
        }

        var clean = err == 0 && missing == 0;
        var avisoArchivar = Archivar(mf, textos, pendiente);
        var aviso = string.Join(" ", new[] { avisoArchivar }.Concat(avisos).Where(t => t.Length > 0));
        var res = new UndoResult(Path.GetFileName(mf), rev, err, missing, manual, clean, false, aviso)
        {
            Revertidas = revertidas,
            AplicadasRevertidas = aplicadasRevertidas,
            Tocadas = tocadas,
            Vueltas = quedan,
        };

        // E2: un registro ausente para siempre (archivo enviado a la papelera) o con un error que no
        // se arregla deja el manifiesto como «el último» indefinidamente: cada clic da lo mismo y las
        // ejecuciones anteriores no se alcanzan. Se dice claramente; la salida es DarPorPerdidos.
        // Lo aplazado por un historial en uso ya lleva su propio aviso, que dice cómo seguir.
        if (res.SinAvance && aviso.Length == 0)
        {
            var motivo = missing > 0 && err > 0
                         ? $"{(missing == 1 ? "1 no aparece" : $"{missing} no aparecen")} y {(err == 1 ? "1 da error" : $"{err} dan error")}"
                       : missing > 0 ? (missing == 1 ? "el archivo no aparece" : $"{missing} archivos no aparecen")
                       : (err == 1 ? "da error" : $"{err} dan error");
            var cuales = res.Pendientes == 1
                ? $"el registro pendiente de {Path.GetFileName(mf)}"
                : $"ninguno de los {res.Pendientes} registros pendientes de {Path.GetFileName(mf)}";
            res = res with
            {
                Aviso = $"No se ha podido deshacer {cuales} ({motivo}). " +
                        "Mientras quede pendiente, «Deshacer última» volverá a esta ejecución antes que a las anteriores. " +
                        "Si esos archivos no van a volver, se pueden dar por perdidos en «Historial de cambios» (Enriquecer).",
            };
        }

        // Lo que se acaba de archivar ya no necesita sus copias de carátula.
        if (rev + manual > 0) PodarPortadas();
        return res;
    }

    /// <summary>
    /// El manifiesto pendiente más reciente que tenga algún registro. Un manifiesto vacío es el resto
    /// de un archivado que no pudo terminar (ver <see cref="Archivar"/>): no hay nada que deshacer en
    /// él y no puede tapar a los anteriores, así que se borra (o, si no se puede, se salta).
    /// </summary>
    private string? PrimeroConRegistros()
    {
        foreach (var mf in ManifiestosPendientes())
        {
            bool vacio;
            try { vacio = File.ReadLines(mf, Encoding.UTF8).All(l => l.Trim().Length == 0); }
            catch { return mf; }   // no se puede mirar: que lo intente UndoLastRun y lo cuente
            if (!vacio) return mf;
            try { File.Delete(mf); } catch { }
        }
        return null;
    }

    /// <summary>
    /// Saca de la cola un manifiesto cuyos pendientes no se van a poder deshacer (por defecto, el
    /// más reciente): se archiva como «run_X.perdidos.jsonl» con los registros tal cual, sin tocar
    /// ningún archivo.
    ///
    /// POR QUÉ. Visto en la revisión de E2: un registro ausente para siempre o con un error
    /// permanente deja su manifiesto como «el último» y «Deshacer última» ya no llega nunca a las
    /// ejecuciones anteriores; antes la única salida era borrar el manifiesto a mano. Es una acción
    /// EXPLÍCITA porque un archivo ausente puede volver (un disco externo sin conectar).
    ///
    /// No lleva «.deshecho» a propósito: esos renombrados NO se deshicieron, y RekordboxRelocator
    /// debe seguir leyéndolos como vigentes (A→B), no como vueltas al nombre original.
    /// </summary>
    public PendientesDescartados DarPorPerdidos(string? manifest = null)
    {
        var mf = !string.IsNullOrEmpty(manifest)
            ? (File.Exists(manifest) && EsManifiestoPendiente(manifest) ? manifest : null)
            : PrimeroConRegistros();
        if (mf == null) return new PendientesDescartados("", 0);

        string[] lineas;
        try { lineas = File.ReadAllLines(mf, Encoding.UTF8).Where(l => l.Trim().Length > 0).ToArray(); }
        catch (Exception e) { return new PendientesDescartados(Path.GetFileName(mf), 0, e.Message); }

        var destino = RutaArchivado(mf, ".perdidos");
        try
        {
            File.Move(mf, destino);
        }
        catch (Exception e)
        {
            // Bloqueado (antivirus, OneDrive): se copia lo pendiente y se vacía el manifiesto, que
            // es lo que de verdad lo saca de la cola. Si ni eso se puede, sigue pendiente.
            var errCopia = ArchivoDatos.Escribir(destino, string.Join("\n", lineas) + "\n");
            var errVaciar = errCopia.Length == 0 ? ArchivoDatos.Escribir(mf, "") : errCopia;
            if (errVaciar.Length > 0)
            {
                // La copia sobra si el manifiesto sigue pendiente: visto en revisión, se quedaba al
                // lado, el historial contaba esas líneas dos veces («Deshecha en parte…» con todo
                // pendiente), la reparación de rekordbox daba por vigentes renombrados que luego se
                // podían deshacer, y un reintento creaba «.perdidos-2».
                if (errCopia.Length == 0)
                    try { File.Delete(destino); }
                    catch (Exception b) { _log?.Log($"  deshacer: no se pudo borrar la copia {Path.GetFileName(destino)}: {b.Message}", LogKind.Err); }
                _log?.Log($"  deshacer: no se pudo dar por perdido {Path.GetFileName(mf)}: {e.Message}", LogKind.Err);
                return new PendientesDescartados(Path.GetFileName(mf), 0, e.Message);
            }
            try { File.Delete(mf); } catch { }
        }
        _log?.Log($"  deshacer: {lineas.Length} registro(s) de {Path.GetFileName(mf)} dados por perdidos; se guardan en {Path.GetFileName(destino)}.", LogKind.No);
        PodarPortadas();
        return new PendientesDescartados(Path.GetFileName(mf), lineas.Length);
    }

    // --- ¿Dio este registro la canción por aplicada? ---
    //
    // «Deshacer última» deshace el último manifiesto pendiente, sea de lo que sea. Visto en revisión:
    // con 640 canciones aplicadas en Enriquecer y un ajuste de volumen posterior de la biblioteca,
    // deshacer el volumen las sacaba todas de aplicadas.json, sin forma de recuperarlas, y el estado
    // decía «640 canciones vuelven a estar pendientes».

    /// <summary>
    /// Campos que la aplicación de propuestas (Tagging.ApplyTags) no escribe nunca: el volumen, la
    /// clave (importación de rekordbox) y el comentario (fichas de DJ al comentario).
    /// </summary>
    private static readonly HashSet<string> CamposAjenosAAplicar = new(StringComparer.Ordinal) { VolumeField, "Key", "Comment" };

    /// <summary>
    /// Campos que, en un manifiesto antiguo, solo escribía aplicar una propuesta (o el Editor):
    /// Unificar géneros solo toca el género, y la importación de rekordbox, BPM y clave.
    /// </summary>
    private static readonly HashSet<string> CamposDeAplicar = new(StringComparer.Ordinal)
        { "Title", "Artist", "Album", "Year", "Remixer", CoverField };

    /// <summary>
    /// ¿Dio este registro la canción por aplicada? Los de esta versión lo dicen
    /// (<see cref="UndoRecord.Aplicada"/>, que se escribe siempre). Los anteriores no lo llevan, y por
    /// ellos decide <paramref name="manifiestoAntiguoDeAplicar"/>
    /// (<see cref="ManifiestoAntiguoDeAplicar"/>).
    /// </summary>
    public static bool DioPorAplicada(JsonNode? registro, bool manifiestoAntiguoDeAplicar)
    {
        if (registro is not JsonObject o) return false;
        if (o.TryGetPropertyValue(nameof(UndoRecord.Aplicada), out var marca))
            return marca is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        return manifiestoAntiguoDeAplicar;
    }

    /// <summary>
    /// Para los registros sin marca (manifiestos anteriores a ella): ¿es el manifiesto, entero, de
    /// aplicar propuestas? Ante la duda, NO. Dejar una canción como aplicada es lo que ya pasaba y
    /// tiene arreglo; sacarla de aplicadas.json por error no lo tiene.
    ///
    /// Un manifiesto es de una sola operación, así que se mira entero:
    ///   · el formato antiguo (new/orig) solo lo escribía aplicar propuestas: sí;
    ///   · un registro sin campos (Renombrar con IA, Bandeja) o con volumen, clave o comentario lo
    ///     delata como de otra cosa: no;
    ///   · si no, hace falta alguna señal de aplicar: título, artista, álbum, año, remixer, carátula,
    ///     o un renombrado con campos. Un manifiesto que solo cambia géneros (Unificar géneros) o BPM
    ///     (rekordbox) no la tiene: no.
    /// Lo que se escapa es el Editor, cuyos registros son como los de aplicar; solo pasa con un
    /// manifiesto de antes de esta versión.
    /// </summary>
    public static bool ManifiestoAntiguoDeAplicar(IEnumerable<JsonNode?> registros)
    {
        var senal = false;
        foreach (var n in registros)
        {
            if (n is not JsonObject o || o.ContainsKey(nameof(UndoRecord.Aplicada))) continue;
            if (!o.ContainsKey("OrigPath")) { senal = true; continue; }   // formato antiguo new/orig
            if (o["Fields"] is not JsonObject campos) return false;        // solo renombrar: otra herramienta
            foreach (var (campo, _) in campos)
            {
                if (CamposAjenosAAplicar.Contains(campo)) return false;
                if (CamposDeAplicar.Contains(campo)) senal = true;
            }
            if (campos.Count == 0 || (o["Renamed"] is JsonValue r && r.TryGetValue<bool>(out var ren) && ren)) senal = true;
        }
        return senal;
    }

    /// <summary>¿Es un registro de la pestaña Volumen (sin renombrado y sin más campo que el volumen)?</summary>
    private static bool SoloVolumen(JsonNode node)
        => node is JsonObject o
           && o["Fields"] is JsonObject campos && campos.Count == 1 && campos.ContainsKey(VolumeField)
           && !(o["Renamed"] is JsonValue r && r.TryGetValue<bool>(out var ren) && ren);

    /// <summary>Las dos rutas de un registro (formato nuevo o antiguo); vacías las que no consten.</summary>
    private static IEnumerable<string> RutasDe(JsonNode node)
    {
        var nuevo = node is JsonObject o && o.ContainsKey("OrigPath");
        var despues = J.S(J.P(node, nuevo ? "FinalPath" : "new"));
        var orig = J.S(J.P(node, nuevo ? "OrigPath" : "orig"));
        var antes = nuevo || orig.Length == 0 ? orig : Path.Combine(Path.GetDirectoryName(despues) ?? "", orig);
        return new[] { antes, despues }.Where(r => r.Length > 0);
    }

    /// <summary>
    /// Deja en el manifiesto SOLO los registros que no se pudieron revertir y archiva el resto.
    ///
    /// POR QUÉ. Antes, con un solo error o un solo ausente, el manifiesto se conservaba ENTERO para
    /// reintentar. Los tags aguantaban el reintento -solo se restauran si siguen como los dejó la
    /// aplicación-, pero el volumen no: se revierte aplicando la ganancia inversa, que es relativa y
    /// no deja marca. Deshacer un ajuste de 300 MP3 con uno abierto en rekordbox daba 299 revertidos
    /// y 1 error; al cerrar rekordbox y reintentar, los 299 bajaban OTROS 3 dB.
    ///
    /// El orden importa: primero se reescribe el manifiesto y después se escribe el archivado. Si
    /// algo fallase entre los dos pasos, se pierde el archivado -el historial, que la reparación de
    /// rekordbox usa para saber qué renombrados se deshicieron- pero nunca queda un registro ya
    /// revertido esperando a revertirse otra vez.
    ///
    /// Devuelve "" si fue bien, o un aviso para el usuario.
    /// </summary>
    private string Archivar(string mf, List<string> lineas, bool[] pendiente)
    {
        var quedan = lineas.Where((_, i) => pendiente[i]).ToList();
        var hechas = lineas.Where((_, i) => !pendiente[i]).ToList();
        var archivado = RutaArchivado(mf);
        try
        {
            if (quedan.Count == 0)
            {
                // Todo deshecho: el manifiesto entero pasa a archivado de una vez.
                try
                {
                    File.Move(mf, archivado);
                    // Con la fecha del DESHACER: renombrar conserva la de la ejecución, y
                    // RekordboxRelocator la usa para colocar en el tiempo la vuelta al nombre original.
                    try { File.SetLastWriteTime(archivado, DateTime.Now); } catch { }
                    return "";
                }
                catch (Exception e)
                {
                    // M3: el manifiesto bloqueado (antivirus, OneDrive) hacía fallar el Move y se
                    // quedaba ENTERO como pendiente con todo ya revertido: un reintento habría vuelto
                    // a aplicar la ganancia inversa a todos los MP3. Se sigue por el camino de la
                    // rama parcial: primero se VACÍA el manifiesto (lo que impide repetir nada) y
                    // después se escribe el archivado. Un manifiesto vacío ya no tapa a los
                    // anteriores (PrimeroConRegistros lo salta).
                    _log?.Log($"  deshacer: no se pudo mover {Path.GetFileName(mf)} al archivo ({e.Message}); se vacía y se archiva aparte.", LogKind.Err);
                }
            }
            if (hechas.Count == 0) return "";   // no se pudo nada: el manifiesto se queda como estaba

            var error = ArchivoDatos.Escribir(mf, quedan.Count == 0 ? "" : string.Join("\n", quedan) + "\n");
            if (error.Length > 0)
            {
                var aviso = $"No se pudo actualizar el manifiesto {Path.GetFileName(mf)} ({error}). " +
                            "NO vuelvas a pulsar «Deshacer última» hasta revisarlo: repetiría también lo ya revertido.";
                _log?.Log("  deshacer: " + aviso, LogKind.Err);
                return aviso;
            }
            var errArch = ArchivoDatos.Escribir(archivado, string.Join("\n", hechas) + "\n");
            if (errArch.Length > 0)
                _log?.Log($"  deshacer: no se pudo archivar lo revertido en {Path.GetFileName(archivado)}: {errArch}", LogKind.Err);
            if (quedan.Count == 0)
            {
                // Ya vacío, no repite nada; si tampoco se deja borrar, PrimeroConRegistros lo salta.
                try { File.Delete(mf); } catch { }
                return "";
            }
            _log?.Log($"  deshacer: quedan {quedan.Count} registro(s) pendientes en {Path.GetFileName(mf)}; lo revertido se archiva aparte.", LogKind.No);
            return "";
        }
        catch (Exception e)
        {
            var aviso = $"No se pudo archivar el manifiesto {Path.GetFileName(mf)} ({e.Message}). " +
                        "NO vuelvas a pulsar «Deshacer última» hasta revisarlo: repetiría también lo ya revertido.";
            _log?.Log("  deshacer: " + aviso, LogKind.Err);
            return aviso;
        }
    }

    /// <summary>
    /// Nombre libre para archivar lo revertido: «run_X.deshecho.jsonl» la primera vez y
    /// «run_X.deshecho-2.jsonl», «-3»… si un deshacer se completa en varias pasadas. Ninguno casa
    /// con <see cref="EsManifiestoPendiente"/>, y todos llevan «.deshecho», que es lo que mira
    /// RekordboxRelocator para saber que esos renombrados se deshicieron.
    /// </summary>
    private static string RutaArchivado(string mf, string sufijo = ".deshecho")
    {
        var dir = Path.GetDirectoryName(mf) ?? "";
        var nombre = Path.GetFileName(mf);
        var jsonl = nombre.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
        var raiz = jsonl ? nombre[..^6] : nombre;
        var ext = jsonl ? ".jsonl" : "";
        for (var n = 1; ; n++)
        {
            var candidato = Path.Combine(dir, raiz + sufijo + (n == 1 ? "" : "-" + n) + ext);
            if (!File.Exists(candidato)) return candidato;
        }
    }

    /// <summary>
    /// Antigüedad mínima de una copia de carátula para poder borrarla. Tagging guarda la copia
    /// ANTES de que ApplyEngine escriba la línea del manifiesto que la cita: sin este margen, una
    /// poda que coincidiera con una aplicación en curso borraría la copia recién hecha.
    /// </summary>
    public static readonly TimeSpan MargenPortadas = TimeSpan.FromDays(2);

    // Los nombres de las copias: SHA-1 de los bytes + extensión (Tagging.GuardarCaratula).
    private static readonly Regex NombreCopiaPortada =
        new(@"[0-9a-f]{40}\.(?:jpg|png|gif|bmp|webp)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Política de retención de las copias de carátula (<see cref="AppPaths.PortadasDir"/>): se borra
    /// una copia SOLO si ningún manifiesto pendiente la cita y tiene más de
    /// <see cref="MargenPortadas"/>. Los manifiestos (pendientes y archivados) no se borran nunca:
    /// son texto, pesan poco y RekordboxRelocator los usa como historial de renombrados.
    ///
    /// POR QUÉ. Las copias solo sirven para deshacer: una vez deshecha (o dada por perdida) la
    /// ejecución que las cita, ya no las necesita nadie, y antes se quedaban para siempre. Si no se
    /// puede leer algún manifiesto pendiente no se borra nada: no se sabría qué cita.
    ///
    /// Devuelve cuántas copias se han borrado.
    /// </summary>
    public int PodarPortadas()
    {
        try
        {
            if (!Directory.Exists(_paths.PortadasDir)) return 0;
            var citadas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mf in ManifiestosPendientes())
            {
                // Se busca el NOMBRE en el texto en bruto, no se interpreta el JSON: así también
                // cuenta una línea que no se deja leer, y una ruta absoluta de otra carpeta de datos.
                foreach (Match m in NombreCopiaPortada.Matches(File.ReadAllText(mf, Encoding.UTF8)))
                    citadas.Add(m.Value);
            }

            var limite = DateTime.Now - MargenPortadas;
            var borradas = 0;
            foreach (var f in new DirectoryInfo(_paths.PortadasDir).EnumerateFiles())
            {
                if (citadas.Contains(f.Name) || f.LastWriteTime > limite) continue;
                try { f.Delete(); borradas++; } catch { }
            }
            if (borradas > 0) _log?.Log($"  deshacer: {borradas} copia(s) de carátula que ya no cita ninguna ejecución pendiente, borradas.", LogKind.No);
            return borradas;
        }
        catch (Exception e)
        {
            _log?.Log("  deshacer: no se pudieron revisar las copias de carátula: " + e.Message, LogKind.Err);
            return 0;
        }
    }

    /// <summary>Nombre del campo de manifiesto que guarda un cambio de volumen (no es un tag).</summary>
    public const string VolumeField = "Volumen";

    /// <summary>
    /// Campo de manifiesto de la carátula. Contrato compartido con quien sustituye la carátula:
    /// ANTES de sustituirla se guarda la frontal anterior en <see cref="AppPaths.PortadasDir"/> y se
    /// anota un FieldChange de texto con OldStr = ruta absoluta de esa copia ("" si el archivo no
    /// tenía carátula) y NewStr = <see cref="MarcaCover"/> de la carátula puesta
    /// («sustituida:&lt;sha1&gt;»; ver <see cref="FieldChange.Portada"/>).
    /// </summary>
    public const string CoverField = "Cover";

    /// <summary>
    /// Marca de NewStr que dice que OldStr guarda la carátula anterior. Es la ÚNICA definición (antes
    /// había otra igual en Tagging). Los registros escritos por la 1.9.x llevan la marca sola; los
    /// nuevos, «sustituida:&lt;sha1 de la carátula puesta&gt;». Los antiguos de verdad llevan
    /// "changed" y ninguna copia, y esos no se pueden devolver.
    /// </summary>
    public const string CoverSustituida = "sustituida";

    /// <summary>
    /// NewStr de una sustitución de carátula: la marca y el SHA-1 de la carátula que se PUSO, para
    /// que deshacer sepa si la frontal sigue siendo la de la aplicación (ver RestoreCover).
    /// </summary>
    public static string MarcaCover(byte[]? puesta)
        => puesta is { Length: > 0 } ? CoverSustituida + ":" + Sha1(puesta) : CoverSustituida;

    private static string Sha1(byte[] datos)
        => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(datos)).ToLowerInvariant();

    // ---------- Formato nuevo ----------
    private Outcome ProcessNew(UndoRecord r, Efecto ef)
    {
        var path = File.Exists(r.FinalPath) ? r.FinalPath
            : (File.Exists(r.OrigPath) ? r.OrigPath : null);
        if (path == null) { _log?.Log($"  deshacer: NO encontrado [{Path.GetFileName(r.OrigPath)}]", LogKind.Err); return Outcome.Missing; }

        ef.Sellar(path);
        var renamedBack = RenameBack(r.Renamed, ref path, r.FinalPath, r.OrigPath);
        if (renamedBack) { ef.Movido = true; ef.Hasta = path; }

        bool restoredAny = false, skippedAny = false, volumenFallido = false, volumenDevuelto = false;

        // El volumen no es un tag: se revierte aplicando la ganancia inversa sobre el propio audio.
        // Va antes que los tags porque reescribe el archivo entero.
        if (r.Fields is { Count: > 0 } && r.Fields.TryGetValue(VolumeField, out var vol))
        {
            var pasos = (int)vol.NewNum - (int)vol.OldNum;
            if (pasos != 0)
            {
                var res = Analysis.Mp3Gain.Apply(path, -pasos);
                if (res.Ok)
                {
                    restoredAny = volumenDevuelto = ef.Volumen = true;
                    ef.PasosVolumen += res.Steps;
                    if (r.GananciaAnotada == true) ef.PasosAnotados += res.Steps;   // los de 1.10 no se anotaron
                    _log?.Log($"  deshacer: volumen devuelto ({-pasos * Analysis.Mp3Gain.DbPerStep:+0.0;-0.0} dB) [{Path.GetFileName(path)}]", LogKind.Ok);
                }
                else
                {
                    // El audio sigue con el volumen cambiado. Antes esto solo se anotaba y el
                    // registro se contaba como revertido: el manifiesto se archivaba y ya no había
                    // forma de reintentarlo. Marcándolo como error, el manifiesto se conserva.
                    volumenFallido = true;
                    _log?.Log($"  deshacer: NO se pudo devolver el volumen [{Path.GetFileName(path)}]: {res.Error}", LogKind.Err);
                }
            }
        }

        // Los tags solo se abren si hay alguno que restaurar. Un registro de volumen no lleva más
        // que el volumen, y abrir con TagLib un MP3 que no sabe leer fallaba DESPUÉS de haber
        // devuelto el audio: el registro quedaba como error y el reintento lo bajaba otra vez.
        if (r.Fields is { Count: > 0 } && r.Fields.Keys.Any(k => k != VolumeField))
        {
            try
            {
                using var tf = Tagging.AbrirParaEscribir(path);   // TagLib no escribe AIFF-C: así el motivo sale en español
                var tocado = false;
                foreach (var (name, fc) in r.Fields)
                {
                    if (name is VolumeField) continue;
                    var (restored, skipped) = name == CoverField ? RestoreCover(tf, fc, path) : RestoreField(tf.Tag, name, fc);
                    restoredAny |= restored; skippedAny |= skipped; tocado |= restored;
                }
                if (tocado) { tf.Save(); ef.Etiquetas = true; }
            }
            catch (Exception e) when (volumenDevuelto)
            {
                // El volumen ya está en su sitio y no puede quedar pendiente: se da el registro por
                // cerrado y se dice qué no se pudo hacer.
                _log?.Log($"  deshacer: volumen devuelto, pero los tags no se pudieron restaurar [{Path.GetFileName(path)}]: {e.Message}", LogKind.Err);
                return Outcome.Manual;
            }
        }

        // El volumen manda sobre el resto: si el audio no se pudo devolver a su sitio, este registro
        // NO está deshecho, aunque el nombre y los tags sí hayan vuelto.
        if (volumenFallido) return Outcome.Error;
        if (renamedBack || restoredAny) return Outcome.Reverted;
        if (skippedAny) { _log?.Log($"  deshacer: cambios manuales conservados [{Path.GetFileName(r.OrigPath)}]", LogKind.No); return Outcome.Manual; }
        return Outcome.Reverted;
    }

    // ---------- Formato antiguo (new/orig/renamed/tags/wtitle) ----------
    private Outcome ProcessLegacy(JsonNode r, Efecto ef)
    {
        var cur = J.S(J.P(r, "new"));
        var dir = Path.GetDirectoryName(cur) ?? "";
        var origName = J.S(J.P(r, "orig"));
        var origPath = Path.Combine(dir, origName);

        var path = File.Exists(cur) ? cur
            : (!string.Equals(cur, origPath, StringComparison.Ordinal) && File.Exists(origPath) ? origPath : null);
        if (path == null) { _log?.Log($"  deshacer(antiguo): NO encontrado [{origName}]", LogKind.Err); return Outcome.Missing; }
        ef.Sellar(path);

        var wt = J.S(J.P(r, "wtitle"));
        if (wt.Length > 0)
        {
            var tc = TagLib.File.Create(path);
            var curT = tc.Tag.Title ?? ""; tc.Dispose();
            if (TextUtils.Nk(curT) != TextUtils.Nk(wt)) { _log?.Log($"  deshacer(antiguo): editado a mano, se conserva [{origName}]", LogKind.No); return Outcome.Manual; }
        }

        var renamed = J.P(r, "renamed") is JsonValue rv && rv.TryGetValue<bool>(out var rb) && rb;
        if (RenameBack(renamed, ref path, cur, origPath)) { ef.Movido = true; ef.Hasta = path; }

        var tags = J.P(r, "tags");
        if (tags is JsonObject to)
        {
            var tf = Tagging.AbrirParaEscribir(path);
            if (to.ContainsKey("Title")) tf.Tag.Title = J.S(J.P(tags, "Title"));
            if (to.ContainsKey("Album")) tf.Tag.Album = J.S(J.P(tags, "Album"));
            if (to.ContainsKey("Artist")) tf.Tag.Performers = StrArray(J.P(tags, "Artist"));
            if (to.ContainsKey("Genre")) tf.Tag.Genres = StrArray(J.P(tags, "Genre"));
            if (to.ContainsKey("Year")) { try { tf.Tag.Year = (uint)J.I(J.P(tags, "Year")); } catch { } }
            if (to.ContainsKey("Bpm")) { try { tf.Tag.BeatsPerMinute = (uint)J.I(J.P(tags, "Bpm")); } catch { } }
            tf.Save(); tf.Dispose();
            ef.Etiquetas = true;
        }
        return Outcome.Reverted;
    }

    // Renombra de vuelta a la ruta original si procede (incluye caso solo-mayús/minús). Devuelve si renombró.
    private static bool RenameBack(bool renamed, ref string path, string finalPath, string origPath)
    {
        if (!renamed || !string.Equals(path, finalPath, StringComparison.Ordinal) ||
            string.Equals(finalPath, origPath, StringComparison.Ordinal)) return false;

        var dir = Path.GetDirectoryName(finalPath) ?? "";
        if (string.Equals(finalPath, origPath, StringComparison.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(origPath);
            var tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(origPath) + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ext);
            File.Move(finalPath, tmp);
            File.Move(tmp, origPath);
        }
        else File.Move(finalPath, origPath);
        path = origPath;
        return true;
    }

    // Restaura un campo solo si el valor actual coincide con el que escribió la app. (restaurado, omitido).
    //
    // Si el valor actual YA es el anterior, no se omite: ya está devuelto (ni restaurado ni omitido).
    // POR QUÉ (revisión de interfaz-9): un registro con su registro corrector (un género con barra en
    // ID3v2.3, que se relee partido) se deshace del más reciente al más antiguo; el corrector devuelve
    // el campo y el original ya no coincide con lo que escribió. Contado como omitido, el resumen
    // decía «1 revertido · 1 conservado» y el registro, «cambios manuales conservados», aunque el
    // archivo había vuelto entero. Lo mismo si el usuario lo había devuelto a mano a como estaba.
    private static (bool restored, bool skipped) RestoreField(TagLib.Tag tag, string name, FieldChange fc)
    {
        switch (name)
        {
            case "Title": return Texto(tag.Title, fc, v => tag.Title = v);
            case "Album": return Texto(tag.Album, fc, v => tag.Album = v);
            case "Comment": return Texto(tag.Comment, fc, v => tag.Comment = v);           // ficha de DJ volcada al comentario
            case "Key": return Texto(tag.InitialKey, fc, v => tag.InitialKey = v);         // clave musical (importación de rekordbox)
            case "Remixer": return Texto(tag.RemixedBy, fc, v => tag.RemixedBy = v);       // quién firma la versión (TPE4), lo escribe Tagging junto al título
            case Tagging.IsrcField: return Texto(tag.ISRC, fc, v => tag.ISRC = v);         // ISRC del catálogo (C14), solo si el usuario lo activó en Ajustes
            case "Artist": return Lista(tag.Performers, fc, v => tag.Performers = v);
            case "Genre": return Lista(tag.Genres, fc, v => tag.Genres = v);
            case "Year": return Numero(tag.Year, fc, v => tag.Year = v);
            case "Bpm": return Numero(tag.BeatsPerMinute, fc, v => tag.BeatsPerMinute = v);
            default:
                return (false, false);
        }

        static (bool, bool) Texto(string? actual, FieldChange fc, Action<string?> poner)
        {
            if ((actual ?? "") == (fc.NewStr ?? "")) { poner(Empty(fc.OldStr)); return (true, false); }
            return (false, (actual ?? "") != (fc.OldStr ?? ""));
        }

        static (bool, bool) Lista(string[]? actual, FieldChange fc, Action<string[]> poner)
        {
            if (SeqEq(actual, fc.NewArr)) { poner((fc.OldArr ?? new()).ToArray()); return (true, false); }
            return (false, !SeqEq(actual, fc.OldArr));
        }

        static (bool, bool) Numero(uint actual, FieldChange fc, Action<uint> poner)
        {
            if (actual == fc.NewNum) { poner(fc.OldNum); return (true, false); }
            return (false, actual != fc.OldNum);
        }
    }

    /// <summary>
    /// Devuelve la carátula frontal que tenía el archivo antes de sustituirla (contrato
    /// <see cref="CoverField"/>). Solo se toca la FRONTAL: el resto de imágenes (contraportada,
    /// artista…) se quedan como estén. (restaurado, omitido).
    /// </summary>
    private (bool restored, bool skipped) RestoreCover(TagLib.File tf, FieldChange fc, string path)
    {
        // Registro antiguo ("changed"): no se guardó la carátula anterior y no hay nada que devolver.
        var marca = fc.NewStr ?? "";
        string? huellaPuesta;
        if (string.Equals(marca, CoverSustituida, StringComparison.Ordinal)) huellaPuesta = null;   // 1.9.x: sin huella
        else if (marca.StartsWith(CoverSustituida + ":", StringComparison.Ordinal)) huellaPuesta = marca[(CoverSustituida.Length + 1)..];
        else return (false, false);

        // Como con los demás campos: solo se devuelve si la frontal sigue siendo la que puso la
        // aplicación. Antes se devolvía siempre, y una carátula cambiada a mano DESPUÉS de la
        // ejecución (otra portada elegida en Mp3tag, por ejemplo) se pisaba al deshacer.
        if (huellaPuesta != null)
        {
            var frontal = tf.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover);
            var datos = frontal?.Data?.Data;
            if (datos is not { Length: > 0 } || !string.Equals(Sha1(datos), huellaPuesta, StringComparison.OrdinalIgnoreCase))
            {
                _log?.Log($"  deshacer: la carátula se cambió después de aplicar; se conserva la actual [{Path.GetFileName(path)}]", LogKind.No);
                return (false, true);
            }
        }

        var otras = tf.Tag.Pictures.Where(p => p.Type != TagLib.PictureType.FrontCover).ToList();
        var copia = fc.OldStr ?? "";
        if (copia.Length == 0)
        {
            // No tenía carátula: se quita la que se puso.
            tf.Tag.Pictures = otras.ToArray();
            return (true, false);
        }

        // Si la carpeta de datos cambió de sitio, la ruta absoluta anotada ya no vale, pero la copia
        // sigue en la carpeta de portadas actual con el mismo nombre.
        if (!File.Exists(copia))
        {
            var aqui = Path.Combine(_paths.PortadasDir, Path.GetFileName(copia));
            if (!File.Exists(aqui))
            {
                _log?.Log($"  deshacer: falta la copia de la carátula anterior ({Path.GetFileName(copia)}); se conserva la actual [{Path.GetFileName(path)}]", LogKind.Err);
                return (false, true);
            }
            copia = aqui;
        }

        var anterior = new TagLib.Picture(copia) { Type = TagLib.PictureType.FrontCover, Description = "" };
        otras.Insert(0, anterior);
        tf.Tag.Pictures = otras.ToArray();
        return (true, false);
    }

    private static string? Empty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static bool SeqEq(string[]? a, List<string>? b)
    {
        a ??= Array.Empty<string>();
        b ??= new List<string>();
        if (a.Length != b.Count) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static string[] StrArray(JsonNode? n)
    {
        var list = new List<string>();
        if (n is JsonArray arr) foreach (var e in arr) if (e != null) list.Add(J.S(e));
        return list.ToArray();
    }
}
