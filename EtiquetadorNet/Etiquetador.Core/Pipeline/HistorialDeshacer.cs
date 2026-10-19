using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etiquetador.Core.Providers;

namespace Etiquetador.Core.Pipeline;

/// <summary>Qué operación escribió una ejecución. Se deduce de lo que guarda su manifiesto.</summary>
public enum TipoEjecucion
{
    /// <summary>Aplicar propuestas en Enriquecer o No encontradas (la canción queda «aplicada»).</summary>
    AplicarPropuestas,

    /// <summary>Etiquetas cambiadas sin dar la canción por aplicada: el Editor o Incompletas.</summary>
    EdicionEtiquetas,

    Volumen,
    Generos,
    Renombrado,

    /// <summary>Archivos movidos a otra carpeta (la Bandeja de entrada).</summary>
    Traslado,

    /// <summary>Comentario: las fichas de DJ volcadas al comentario.</summary>
    Comentario,
    Caratula,

    /// <summary>BPM y tonalidad importados de una colección de rekordbox.</summary>
    Rekordbox,
    Bpm,
    Desconocido,
}

/// <summary>Cómo está una ejecución en el historial.</summary>
public enum EstadoEjecucion
{
    /// <summary>Todo sigue por deshacer.</summary>
    Pendiente,

    /// <summary>Una parte se ha deshecho (o dado por perdida) y otra sigue pendiente.</summary>
    Parcial,

    /// <summary>No queda nada pendiente y algo se deshizo.</summary>
    Deshecha,

    /// <summary>No queda nada pendiente y no se deshizo nada: todo se dio por perdido.</summary>
    Perdida,
}

/// <summary>Cómo está un cambio (un archivo) de una ejecución.</summary>
public enum EstadoCambio { Pendiente, Deshecho, Perdido }

/// <summary>Un cambio de una ejecución: un archivo, con lo que se le hizo.</summary>
/// <param name="Huella">Identifica la línea del manifiesto (<see cref="UndoEngine.HuellaLinea"/>).</param>
/// <param name="Antes">Ruta antes de la ejecución.</param>
/// <param name="Despues">Ruta en la que lo dejó la ejecución (la misma si no lo renombró).</param>
/// <param name="Renombra">La ejecución cambió el nombre o la carpeta del archivo.</param>
/// <param name="Campos">Nombres de los campos tocados, tal como se guardan («Title», «Volumen»…).</param>
/// <param name="Legible">false si la línea del manifiesto no se pudo interpretar.</param>
public sealed record CambioDeshacer(string Huella, string Antes, string Despues, bool Renombra,
                                    IReadOnlyList<string> Campos, EstadoCambio Estado, bool Legible = true)
{
    /// <summary>Nombre del archivo tal como lo dejó la ejecución.</summary>
    public string Archivo => Path.GetFileName(Despues.Length > 0 ? Despues : Antes);

    /// <summary>Nombre del archivo antes de la ejecución.</summary>
    public string ArchivoAntes => Path.GetFileName(Antes);

    /// <summary>Lo que se cambió, en claro: «Título, Artista, nombre».</summary>
    public string CamposTexto => !Legible
        ? "(registro ilegible)"
        : string.Join(", ", Campos.Select(HistorialDeshacer.NombreCampo)
                                  .Concat(Renombra ? new[] { "nombre" } : Array.Empty<string>()));
}

/// <summary>Una ejecución del historial: todo lo que escribió una operación de una vez.</summary>
/// <param name="Id">«run_aaaammdd_hhmmss» (con «_N» si hubo dos en el mismo segundo).</param>
/// <param name="Manifiesto">Ruta del manifiesto pendiente, o null si ya no queda nada por deshacer.</param>
/// <param name="UltimoDeshacer">Cuándo se deshizo algo de ella por última vez, si se deshizo.</param>
public sealed record EjecucionDeshacer(string Id, DateTime Fecha, TipoEjecucion Tipo, int Pendientes, int Deshechos,
                                       int Perdidos, int Renombrados, string? Manifiesto, DateTime? UltimoDeshacer)
{
    public int Cambios => Pendientes + Deshechos + Perdidos;

    public EstadoEjecucion Estado =>
        Pendientes > 0 ? (Deshechos + Perdidos > 0 ? EstadoEjecucion.Parcial : EstadoEjecucion.Pendiente)
        : Deshechos > 0 ? EstadoEjecucion.Deshecha
        : EstadoEjecucion.Perdida;

    public string TipoTexto => HistorialDeshacer.Texto(Tipo);

    public string EstadoTexto => Estado switch
    {
        EstadoEjecucion.Pendiente => "Pendiente",
        EstadoEjecucion.Parcial => $"Deshecha en parte ({Deshechos + Perdidos} de {Cambios})"
                                   + (Perdidos > 0 ? $", {Perdidos} dados por perdidos" : ""),
        EstadoEjecucion.Deshecha => Perdidos > 0 ? $"Deshecha ({Perdidos} dados por perdidos)" : "Deshecha",
        _ => "Dada por perdida",
    };

    /// <summary>Se puede deshacer algo de ella.</summary>
    public bool TienePendientes => Pendientes > 0 && Manifiesto != null;
}

/// <summary>Un cambio que no se puede deshacer ahora sin dejar el historial incoherente.</summary>
/// <param name="Posterior">La ejecución posterior que lo impide.</param>
/// <param name="RutaActual">Dónde está ahora el archivo, siguiendo los renombrados posteriores.</param>
/// <param name="OtroArchivo">true si otro archivo ocupa ahora el nombre que dejó esta ejecución.</param>
public sealed record BloqueoDeshacer(CambioDeshacer Cambio, EjecucionDeshacer Posterior, string RutaActual, bool OtroArchivo);

/// <summary>Un cambio cuyo archivo volvieron a tocar ejecuciones posteriores que siguen pendientes.</summary>
/// <param name="CamposCompartidos">Campos que cambiaron esta ejecución y alguna posterior: se queda el valor posterior.</param>
/// <param name="PierdeDeshacer">
/// Esta ejecución lo renombró: al deshacerla vuelve a su nombre original, y las posteriores ya no lo
/// encontrarán donde lo dejaron (sus cambios en él no se podrán deshacer desde la aplicación).
/// </param>
public sealed record SolapeDeshacer(CambioDeshacer Cambio, IReadOnlyList<EjecucionDeshacer> Posteriores,
                                    IReadOnlyList<string> CamposCompartidos, bool PierdeDeshacer);

/// <summary>Lo que pasaría al deshacer unos cambios de una ejecución, antes de hacerlo.</summary>
/// <param name="ADeshacer">Lo que se puede deshacer (todo lo elegido menos lo bloqueado).</param>
/// <param name="NoEncontrados">De lo que se puede deshacer, lo que no está ni donde lo dejó la ejecución ni donde estaba: quedará pendiente.</param>
public sealed record AnalisisDeshacer(EjecucionDeshacer Ejecucion, IReadOnlyList<CambioDeshacer> ADeshacer,
                                      IReadOnlyList<BloqueoDeshacer> Bloqueados, IReadOnlyList<SolapeDeshacer> Solapes,
                                      IReadOnlyList<CambioDeshacer> NoEncontrados)
{
    /// <summary>Huellas de lo que se va a deshacer, para <see cref="UndoEngine.DeshacerCambios"/>.</summary>
    public IReadOnlyCollection<string> Huellas => ADeshacer.Select(c => c.Huella).ToHashSet(StringComparer.Ordinal);

    /// <summary>Las ejecuciones posteriores implicadas, de la más antigua a la más reciente.</summary>
    public IReadOnlyList<EjecucionDeshacer> Posteriores =>
        Bloqueados.Select(b => b.Posterior).Concat(Solapes.SelectMany(s => s.Posteriores))
                  .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                  .OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// El historial de deshacer (E14/M14): todas las ejecuciones anotadas en la carpeta «deshacer»,
/// pendientes (run_X.jsonl), deshechas (run_X.deshecho[-N].jsonl) y dadas por perdidas
/// (run_X.perdidos[-N].jsonl), con su fecha, su tipo y su estado.
///
/// POR QUÉ. Hasta ahora solo existía «Deshacer última», y cualquier operación posterior (una
/// corrección en el Editor, un ajuste de volumen) tapaba el lote de Enriquecer: para deshacerlo había
/// que deshacer antes todo lo que vino después. Visto en una biblioteca real: 177 manifiestos en la
/// carpeta deshacer, y solo el más reciente se podía alcanzar desde la aplicación.
///
/// Deshacer en desorden tiene un riesgo que «Deshacer última» no tenía: una ejecución posterior
/// pendiente puede haber vuelto a tocar el mismo archivo. <see cref="Analizar"/> sigue la cadena de
/// renombrados y dice, antes de tocar nada, qué se puede deshacer, qué se pisaría y qué no se puede
/// deshacer sin deshacer antes la posterior.
///
/// Solo LEE: deshacer lo hace <see cref="UndoEngine.DeshacerCambios"/>. Lee varios MB de texto en
/// una biblioteca grande (MEDIDO en la del usuario: 177 manifiestos, 15 MB, 27.173 líneas), así que
/// hay que llamarlo fuera del hilo de la interfaz.
/// </summary>
public sealed class HistorialDeshacer
{
    private readonly string _dir;

    public HistorialDeshacer(string undoDir) => _dir = undoDir;

    // run_aaaammdd_hhmmss[_N] + sufijos de archivado. Los sufijos se repiten en los archivados que
    // dejó el fallo T1 («run_20260920_230254.deshecho.deshecho.jsonl»).
    private static readonly Regex NombreManifiesto = new(
        @"^(?<id>run_\d{8}_\d{6}(?:_\d+)?)(?<sufijos>(?:\.(?:deshecho|perdidos)(?:-\d+)?)*)\.jsonl$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed class Grupo
    {
        public string Id = "";
        public FileInfo? Pendiente;
        public readonly List<FileInfo> Deshechos = new();
        public readonly List<FileInfo> Perdidos = new();
    }

    private Dictionary<string, Grupo> Agrupar()
    {
        var grupos = new Dictionary<string, Grupo>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_dir)) return grupos;
        foreach (var f in new DirectoryInfo(_dir).EnumerateFiles("run_*.jsonl"))
        {
            var m = NombreManifiesto.Match(f.Name);
            if (!m.Success) continue;
            var id = m.Groups["id"].Value;
            if (!grupos.TryGetValue(id, out var g)) grupos[id] = g = new Grupo { Id = id };
            var sufijos = m.Groups["sufijos"].Value;
            if (sufijos.Length == 0) g.Pendiente = f;
            // Lo dado por perdido nunca se deshizo, aunque más tarde se archivara otra cosa.
            else if (sufijos.Contains(".perdidos", StringComparison.OrdinalIgnoreCase)) g.Perdidos.Add(f);
            else g.Deshechos.Add(f);
        }
        return grupos;
    }

    /// <summary>Las ejecuciones, de la más reciente a la más antigua. Las que no tienen ningún registro no salen.</summary>
    public IReadOnlyList<EjecucionDeshacer> Listar()
    {
        var lista = new List<EjecucionDeshacer>();
        foreach (var g in Agrupar().Values)
        {
            var e = Resumir(g);
            if (e != null && e.Cambios > 0) lista.Add(e);
        }
        return lista.OrderByDescending(e => e.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Una ejecución concreta, leída de nuevo (tras deshacer algo de ella, por ejemplo). null si ya no tiene registros.</summary>
    public EjecucionDeshacer? Leer(string id)
    {
        if (!Agrupar().TryGetValue(id, out var g)) return null;
        var e = Resumir(g);
        return e is { Cambios: > 0 } ? e : null;
    }

    private EjecucionDeshacer? Resumir(Grupo g)
    {
        var nodos = new List<JsonNode?>();
        int pendientes = 0, deshechos = 0, perdidos = 0, renombrados = 0;
        void Contar(FileInfo f, ref int n)
        {
            foreach (var linea in LeerLineas(f.FullName))
            {
                n++;
                JsonNode? nodo = null;
                try { nodo = JsonNode.Parse(linea); } catch { }
                nodos.Add(nodo);
                if (nodo != null && Cambio(nodo, linea, EstadoCambio.Pendiente).Renombra) renombrados++;
            }
        }
        if (g.Pendiente != null) Contar(g.Pendiente, ref pendientes);
        foreach (var f in g.Deshechos) Contar(f, ref deshechos);
        foreach (var f in g.Perdidos) Contar(f, ref perdidos);

        var fecha = FechaDelId(g.Id)
                    ?? g.Pendiente?.LastWriteTime
                    ?? g.Deshechos.Concat(g.Perdidos).Select(f => f.LastWriteTime).DefaultIfEmpty(DateTime.MinValue).Min();
        DateTime? ultimo = g.Deshechos.Count == 0 ? null : g.Deshechos.Max(f => f.LastWriteTime);
        return new EjecucionDeshacer(g.Id, fecha, DeducirTipo(nodos), pendientes, deshechos, perdidos, renombrados,
                                     pendientes > 0 ? g.Pendiente?.FullName : null, ultimo);
    }

    /// <summary>Los cambios de una ejecución: primero los pendientes, después los deshechos y los perdidos.</summary>
    public IReadOnlyList<CambioDeshacer> Cambios(EjecucionDeshacer ejecucion)
    {
        if (!Agrupar().TryGetValue(ejecucion.Id, out var g)) return Array.Empty<CambioDeshacer>();
        var lista = new List<CambioDeshacer>();
        if (g.Pendiente != null) lista.AddRange(LeerCambios(g.Pendiente.FullName, EstadoCambio.Pendiente));
        foreach (var f in g.Deshechos.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            lista.AddRange(LeerCambios(f.FullName, EstadoCambio.Deshecho));
        foreach (var f in g.Perdidos.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            lista.AddRange(LeerCambios(f.FullName, EstadoCambio.Perdido));
        return lista;
    }

    private static IEnumerable<CambioDeshacer> LeerCambios(string archivo, EstadoCambio estado)
    {
        foreach (var linea in LeerLineas(archivo))
        {
            JsonNode? nodo = null;
            try { nodo = JsonNode.Parse(linea); } catch { }
            yield return Cambio(nodo, linea, estado);
        }
    }

    /// <summary>Las líneas con contenido. Un archivo que no se deja leer (bloqueado) cuenta como vacío.</summary>
    private static List<string> LeerLineas(string archivo)
    {
        try { return File.ReadAllLines(archivo, Encoding.UTF8).Where(l => l.Trim().Length > 0).ToList(); }
        catch { return new List<string>(); }
    }

    /// <summary>Un registro, del formato actual (OrigPath/FinalPath/Fields) o del antiguo (new/orig/tags).</summary>
    private static CambioDeshacer Cambio(JsonNode? nodo, string linea, EstadoCambio estado)
    {
        var huella = UndoEngine.HuellaLinea(linea);
        if (nodo is not JsonObject o) return new CambioDeshacer(huella, "", "", false, Array.Empty<string>(), estado, Legible: false);

        string antes, despues;
        bool renamed;
        var campos = new List<string>();
        if (o.ContainsKey("OrigPath"))
        {
            antes = J.S(o["OrigPath"]);
            despues = J.S(o["FinalPath"]);
            renamed = Verdadero(o["Renamed"]);
            if (o["Fields"] is JsonObject f) campos.AddRange(f.Select(kv => kv.Key));
        }
        else
        {
            // Formato antiguo: «new» es la ruta actual y «orig», solo el NOMBRE original.
            despues = J.S(o["new"]);
            var orig = J.S(o["orig"]);
            antes = orig.Length == 0 ? despues : Path.Combine(Path.GetDirectoryName(despues) ?? "", orig);
            renamed = Verdadero(o["renamed"]);
            if (o["tags"] is JsonObject t) campos.AddRange(t.Select(kv => kv.Key));
        }
        if (despues.Length == 0) despues = antes;
        if (antes.Length == 0) antes = despues;
        // Un cambio solo de mayúsculas también es renombrar: deshacerlo cambia el nombre.
        var renombra = renamed && !string.Equals(antes, despues, StringComparison.Ordinal);
        return new CambioDeshacer(huella, antes, despues, renombra, campos, estado);
    }

    private static bool Verdadero(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>Fecha de la ejecución sacada de «run_aaaammdd_hhmmss…», o null.</summary>
    public static DateTime? FechaDelId(string id)
    {
        if (id.Length < 19 || !id.StartsWith("run_", StringComparison.OrdinalIgnoreCase)) return null;
        return DateTime.TryParseExact(id.Substring(4, 15), "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
                                      DateTimeStyles.AssumeLocal, out var f) ? f : null;
    }

    // --- Tipo de ejecución ---

    /// <summary>Campos que solo escribía aplicar una propuesta (o el Editor), como en deshacer.</summary>
    private static readonly HashSet<string> CamposDeAplicar = new(StringComparer.Ordinal)
        { "Title", "Artist", "Album", "Year", "Remixer", UndoEngine.CoverField };

    /// <summary>
    /// Qué operación escribió un manifiesto, por lo que guardan sus registros (todos los de la
    /// ejecución: pendientes y archivados). Si alguno lleva <see cref="UndoRecord.Origen"/>, manda.
    ///
    /// Cada operación anota cosas distintas: Volumen, solo «Volumen»; la importación de rekordbox,
    /// «Bpm» y «Key»; las fichas de DJ, «Comment»; aplicar propuestas marca <see cref="UndoRecord.Aplicada"/>;
    /// Renombrar con IA y la Bandeja, solo el renombrado (la Bandeja además cambia de carpeta). Lo
    /// que no se distingue por los campos (el Editor y las Incompletas) sale como «Edición de
    /// etiquetas». Para los manifiestos anteriores a la marca Aplicada se usa la misma regla que
    /// deshacer (<see cref="UndoEngine.ManifiestoAntiguoDeAplicar"/>).
    /// </summary>
    public static TipoEjecucion DeducirTipo(IReadOnlyList<JsonNode?> registros)
    {
        var objetos = registros.OfType<JsonObject>().ToList();
        if (objetos.Count == 0) return TipoEjecucion.Desconocido;

        foreach (var o in objetos)
            if (o[nameof(UndoRecord.Origen)] is JsonValue v && v.TryGetValue<string>(out var origen)
                && Enum.TryParse<TipoEjecucion>(origen, ignoreCase: true, out var tipo) && Enum.IsDefined(tipo))
                return tipo;

        if (objetos.Any(o => !o.ContainsKey("OrigPath"))) return TipoEjecucion.AplicarPropuestas;   // formato antiguo: solo lo escribía aplicar

        var campos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in objetos)
            if (o["Fields"] is JsonObject f)
                foreach (var (k, _) in f) campos.Add(k);

        if (campos.Contains(UndoEngine.VolumeField)) return TipoEjecucion.Volumen;
        if (campos.Contains("Key")) return TipoEjecucion.Rekordbox;
        if (objetos.Any(o => Verdadero(o[nameof(UndoRecord.Aplicada)]))) return TipoEjecucion.AplicarPropuestas;

        if (campos.Count == 0)
        {
            // Visto en una biblioteca real: un registro con "Renamed":true y la misma ruta antes y
            // después (run_20260925_195430). Sigue siendo un renombrado, aunque no cambiara nada.
            if (!objetos.Any(o => Verdadero(o["Renamed"]))) return TipoEjecucion.Desconocido;
            var cambios = objetos.Select(o => Cambio(o, "", EstadoCambio.Pendiente)).Where(c => c.Renombra);
            return cambios.Any(c => !Rutas.MismaCarpeta(Path.GetDirectoryName(c.Antes) ?? "", Path.GetDirectoryName(c.Despues) ?? ""))
                ? TipoEjecucion.Traslado : TipoEjecucion.Renombrado;
        }

        if (objetos.Any(o => !o.ContainsKey(nameof(UndoRecord.Aplicada))))
        {
            if (UndoEngine.ManifiestoAntiguoDeAplicar(objetos)) return TipoEjecucion.AplicarPropuestas;
            // Deshacer es conservador a propósito (sacar de aplicadas por error no tiene arreglo) y
            // un solo registro sin campos le basta para decir «no». Aquí solo se pone nombre a la
            // ejecución, y basta con una señal clara. Visto en una biblioteca real:
            // run_20260810_115221, 6.883 registros con título, artista, álbum y año y 5.854
            // renombrados, salía como «Edición de etiquetas» porque 3 registros no llevaban campos.
            // Con un solo registro no se puede distinguir del Editor, y decide la regla de deshacer.
            if (objetos.Count > 1 && campos.Overlaps(CamposDeAplicar) && !campos.Contains("Comment"))
                return TipoEjecucion.AplicarPropuestas;
        }

        if (campos.SetEquals(new[] { "Genre" })) return TipoEjecucion.Generos;
        if (campos.SetEquals(new[] { "Comment" })) return TipoEjecucion.Comentario;
        if (campos.SetEquals(new[] { UndoEngine.CoverField })) return TipoEjecucion.Caratula;
        if (campos.SetEquals(new[] { "Bpm" })) return TipoEjecucion.Bpm;
        return TipoEjecucion.EdicionEtiquetas;
    }

    /// <summary>Nombre del tipo para la interfaz.</summary>
    public static string Texto(TipoEjecucion tipo) => tipo switch
    {
        TipoEjecucion.AplicarPropuestas => "Aplicar propuestas",
        TipoEjecucion.EdicionEtiquetas => "Edición de etiquetas",
        TipoEjecucion.Volumen => "Ajuste de volumen",
        TipoEjecucion.Generos => "Géneros",
        TipoEjecucion.Renombrado => "Renombrado",
        TipoEjecucion.Traslado => "Traslado de carpeta",
        TipoEjecucion.Comentario => "Comentario",
        TipoEjecucion.Caratula => "Carátula",
        TipoEjecucion.Rekordbox => "Importación de rekordbox",
        TipoEjecucion.Bpm => "BPM",
        _ => "Sin identificar",
    };

    /// <summary>Nombre de un campo de manifiesto para la interfaz.</summary>
    public static string NombreCampo(string campo) => campo switch
    {
        "Title" => "título",
        "Artist" => "artista",
        "Album" => "álbum",
        "Genre" => "género",
        "Year" => "año",
        "Bpm" => "BPM",
        "Key" => "tonalidad",
        "Comment" => "comentario",
        "Remixer" => "remezclador",
        UndoEngine.CoverField => "carátula",
        UndoEngine.VolumeField => "volumen",
        _ => campo,
    };

    // --- Antes de deshacer: qué tocaron las ejecuciones posteriores ---

    /// <summary>
    /// Qué pasaría al deshacer los cambios elegidos (<paramref name="huellas"/>; null, todos los
    /// pendientes) de <paramref name="ejecucion"/>, mirando las ejecuciones POSTERIORES.
    ///
    /// Se sigue cada archivo desde donde lo dejó esta ejecución a través de los registros posteriores
    /// que siguen vigentes (pendientes, y también los dados por perdidos, que nunca se deshicieron):
    ///   · Si una posterior lo renombró o lo movió, el archivo ya no está con el nombre que dejó esta
    ///     ejecución. Deshacer aquí buscaría en el sitio equivocado -y si en el nombre original hay
    ///     otro archivo, le escribiría a ese-, y la posterior tampoco lo encontraría después. Se
    ///     BLOQUEA: hay que deshacer antes la posterior. Igual si otro archivo ocupa ahora su nombre.
    ///   · Si una posterior lo tocó sin renombrarlo, se puede deshacer, pero se avisa: los campos que
    ///     cambiaron las dos se quedan como los dejó la posterior (deshacer solo devuelve un campo si
    ///     sigue como lo escribió esta ejecución), y si esta ejecución lo había renombrado, al volver
    ///     a su nombre original la posterior ya no podrá deshacer lo suyo en él.
    /// Lo ya deshecho de las posteriores no cuenta: su efecto ya no está.
    /// </summary>
    public AnalisisDeshacer Analizar(EjecucionDeshacer ejecucion, IReadOnlyCollection<string>? huellas,
                                     IReadOnlyList<EjecucionDeshacer>? todas = null)
    {
        var elegidos = Cambios(ejecucion)
            .Where(c => c.Estado == EstadoCambio.Pendiente && c.Legible && (huellas == null || huellas.Contains(c.Huella)))
            .ToList();

        todas ??= Listar();
        var grupos = Agrupar();
        var pasos = new List<(EjecucionDeshacer Ejecucion, CambioDeshacer Cambio)>();
        foreach (var e in todas.Where(e => string.Compare(e.Id, ejecucion.Id, StringComparison.OrdinalIgnoreCase) > 0)
                               .OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (!grupos.TryGetValue(e.Id, out var g)) continue;
            if (g.Pendiente != null)
                pasos.AddRange(LeerCambios(g.Pendiente.FullName, EstadoCambio.Pendiente).Where(c => c.Legible).Select(c => (e, c)));
            foreach (var f in g.Perdidos)
                pasos.AddRange(LeerCambios(f.FullName, EstadoCambio.Perdido).Where(c => c.Legible).Select(c => (e, c)));
        }

        // Índices por ruta de partida y por ruta de llegada (de los renombrados): con decenas de miles
        // de registros posteriores, recorrerlos enteros por cada archivo elegido sería lento.
        var porAntes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var porLlegada = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < pasos.Count; i++)
        {
            var c = pasos[i].Cambio;
            Anotar(porAntes, c.Antes, i);
            if (c.Renombra) Anotar(porLlegada, c.Despues, i);
        }

        var aDeshacer = new List<CambioDeshacer>();
        var bloqueados = new List<BloqueoDeshacer>();
        var solapes = new List<SolapeDeshacer>();
        var noEncontrados = new List<CambioDeshacer>();
        foreach (var cambio in elegidos)
        {
            var actual = cambio.Despues;
            var desde = -1;
            BloqueoDeshacer? bloqueo = null;
            var tocan = new List<EjecucionDeshacer>();
            var compartidos = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                var iParte = Siguiente(porAntes, actual, desde);
                var iLlega = Siguiente(porLlegada, actual, desde);
                if (iParte < 0 && iLlega < 0) break;
                if (iLlega >= 0 && (iParte < 0 || iLlega < iParte))
                {
                    // Otro archivo pasó a llamarse como este: el que hay ahí ya no es el nuestro.
                    bloqueo ??= new BloqueoDeshacer(cambio, pasos[iLlega].Ejecucion, actual, OtroArchivo: true);
                    break;
                }
                var (posterior, suyo) = pasos[iParte];
                desde = iParte;
                if (suyo.Estado == EstadoCambio.Pendiente)
                {
                    if (!tocan.Any(t => t.Id == posterior.Id)) tocan.Add(posterior);
                    foreach (var campo in suyo.Campos.Intersect(cambio.Campos, StringComparer.Ordinal))
                        if (campo != UndoEngine.VolumeField) compartidos.Add(campo);   // el volumen es relativo: se suman
                }
                if (suyo.Renombra)
                {
                    bloqueo ??= new BloqueoDeshacer(cambio, posterior, suyo.Despues, OtroArchivo: false);
                    actual = suyo.Despues;
                }
            }

            if (bloqueo != null)
            {
                bloqueados.Add(bloqueo with { RutaActual = bloqueo.OtroArchivo ? bloqueo.RutaActual : actual });
                continue;
            }
            aDeshacer.Add(cambio);
            if (tocan.Count > 0) solapes.Add(new SolapeDeshacer(cambio, tocan, compartidos.ToList(), cambio.Renombra));
            if (!File.Exists(cambio.Despues) && !File.Exists(cambio.Antes)) noEncontrados.Add(cambio);
        }
        return new AnalisisDeshacer(ejecucion, aDeshacer, bloqueados, solapes, noEncontrados);
    }

    private static void Anotar(Dictionary<string, List<int>> indice, string ruta, int i)
    {
        if (ruta.Length == 0) return;
        if (!indice.TryGetValue(ruta, out var l)) indice[ruta] = l = new List<int>();
        l.Add(i);
    }

    /// <summary>El primer registro posterior a <paramref name="desde"/> con esa ruta, o -1.</summary>
    private static int Siguiente(Dictionary<string, List<int>> indice, string ruta, int desde)
    {
        if (!indice.TryGetValue(ruta, out var l)) return -1;
        var k = l.BinarySearch(desde + 1);
        if (k < 0) k = ~k;
        return k < l.Count ? l[k] : -1;
    }
}
