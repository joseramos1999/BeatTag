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
}

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

        // Al revés: lo último que se hizo es lo primero que se deshace.
        var pendiente = new bool[lineas.Count];
        int rev = 0, err = 0, missing = 0, manual = 0;
        for (var i = lineas.Count - 1; i >= 0; i--)
        {
            var node = lineas[i].Nodo;
            if (node == null) continue;
            Outcome outcome;
            try
            {
                var isNew = node is JsonObject o && o.ContainsKey("OrigPath");
                outcome = isNew ? ProcessNew(node.Deserialize<UndoRecord>(Json)!) : ProcessLegacy(node);
            }
            catch (Exception e)
            {
                outcome = Outcome.Error;
                _log?.Log("  deshacer ERROR: " + e.Message, LogKind.Err);
            }
            switch (outcome)
            {
                case Outcome.Reverted: rev++; break;
                case Outcome.Missing: missing++; pendiente[i] = true; break;
                case Outcome.Manual: manual++; break;
                default: err++; pendiente[i] = true; break;
            }
        }

        var clean = err == 0 && missing == 0;
        var aviso = Archivar(mf, lineas.Select(l => l.Texto).ToList(), pendiente);
        var res = new UndoResult(Path.GetFileName(mf), rev, err, missing, manual, clean, false, aviso);

        // E2: un registro ausente para siempre (archivo enviado a la papelera) o con un error que no
        // se arregla deja el manifiesto como «el último» indefinidamente: cada clic da lo mismo y las
        // ejecuciones anteriores no se alcanzan. Se dice claramente; la salida es DarPorPerdidos.
        if (res.SinAvance && aviso.Length == 0)
        {
            var motivo = missing > 0 && err > 0 ? $"{missing} no aparecen y {err} dan error"
                       : missing > 0 ? (missing == 1 ? "el archivo no aparece" : $"{missing} archivos no aparecen")
                       : (err == 1 ? "da error" : $"{err} dan error");
            var cuales = res.Pendientes == 1
                ? $"el registro pendiente de {Path.GetFileName(mf)}"
                : $"ninguno de los {res.Pendientes} registros pendientes de {Path.GetFileName(mf)}";
            res = res with
            {
                Aviso = $"No se ha podido deshacer {cuales} ({motivo}). " +
                        "Mientras quede pendiente, «Deshacer última» volverá a esta ejecución antes que a las anteriores. " +
                        "Si esos archivos no van a volver, se pueden dar por perdidos.",
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
                _log?.Log($"  deshacer: no se pudo dar por perdido {Path.GetFileName(mf)}: {e.Message}", LogKind.Err);
                return new PendientesDescartados(Path.GetFileName(mf), 0, e.Message);
            }
            try { File.Delete(mf); } catch { }
        }
        _log?.Log($"  deshacer: {lineas.Length} registro(s) de {Path.GetFileName(mf)} dados por perdidos; se guardan en {Path.GetFileName(destino)}.", LogKind.No);
        PodarPortadas();
        return new PendientesDescartados(Path.GetFileName(mf), lineas.Length);
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
            _log?.Log($"  deshacer: quedan {quedan.Count} registro(s) en {Path.GetFileName(mf)}; «Deshacer última» reintentará solo esos.", LogKind.No);
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
    private Outcome ProcessNew(UndoRecord r)
    {
        var path = File.Exists(r.FinalPath) ? r.FinalPath
            : (File.Exists(r.OrigPath) ? r.OrigPath : null);
        if (path == null) { _log?.Log($"  deshacer: NO encontrado [{Path.GetFileName(r.OrigPath)}]", LogKind.Err); return Outcome.Missing; }

        var renamedBack = RenameBack(r.Renamed, ref path, r.FinalPath, r.OrigPath);

        bool restoredAny = false, skippedAny = false, volumenFallido = false, volumenDevuelto = false;

        // El volumen no es un tag: se revierte aplicando la ganancia inversa sobre el propio audio.
        // Va antes que los tags porque reescribe el archivo entero.
        if (r.Fields is { Count: > 0 } && r.Fields.TryGetValue(VolumeField, out var vol))
        {
            var pasos = (int)vol.NewNum - (int)vol.OldNum;
            if (pasos != 0)
            {
                var res = Analysis.Mp3Gain.Apply(path, -pasos);
                if (res.Ok) { restoredAny = volumenDevuelto = true; _log?.Log($"  deshacer: volumen devuelto ({-pasos * Analysis.Mp3Gain.DbPerStep:+0.0;-0.0} dB) [{Path.GetFileName(path)}]", LogKind.Ok); }
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
                using var tf = TagLib.File.Create(path);
                var tocado = false;
                foreach (var (name, fc) in r.Fields)
                {
                    if (name is VolumeField) continue;
                    var (restored, skipped) = name == CoverField ? RestoreCover(tf, fc, path) : RestoreField(tf.Tag, name, fc);
                    restoredAny |= restored; skippedAny |= skipped; tocado |= restored;
                }
                if (tocado) tf.Save();
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
    private Outcome ProcessLegacy(JsonNode r)
    {
        var cur = J.S(J.P(r, "new"));
        var dir = Path.GetDirectoryName(cur) ?? "";
        var origName = J.S(J.P(r, "orig"));
        var origPath = Path.Combine(dir, origName);

        var path = File.Exists(cur) ? cur
            : (!string.Equals(cur, origPath, StringComparison.Ordinal) && File.Exists(origPath) ? origPath : null);
        if (path == null) { _log?.Log($"  deshacer(antiguo): NO encontrado [{origName}]", LogKind.Err); return Outcome.Missing; }

        var wt = J.S(J.P(r, "wtitle"));
        if (wt.Length > 0)
        {
            var tc = TagLib.File.Create(path);
            var curT = tc.Tag.Title ?? ""; tc.Dispose();
            if (TextUtils.Nk(curT) != TextUtils.Nk(wt)) { _log?.Log($"  deshacer(antiguo): editado a mano, se conserva [{origName}]", LogKind.No); return Outcome.Manual; }
        }

        var renamed = J.P(r, "renamed") is JsonValue rv && rv.TryGetValue<bool>(out var rb) && rb;
        RenameBack(renamed, ref path, cur, origPath);

        var tags = J.P(r, "tags");
        if (tags is JsonObject to)
        {
            var tf = TagLib.File.Create(path);
            if (to.ContainsKey("Title")) tf.Tag.Title = J.S(J.P(tags, "Title"));
            if (to.ContainsKey("Album")) tf.Tag.Album = J.S(J.P(tags, "Album"));
            if (to.ContainsKey("Artist")) tf.Tag.Performers = StrArray(J.P(tags, "Artist"));
            if (to.ContainsKey("Genre")) tf.Tag.Genres = StrArray(J.P(tags, "Genre"));
            if (to.ContainsKey("Year")) { try { tf.Tag.Year = (uint)J.I(J.P(tags, "Year")); } catch { } }
            if (to.ContainsKey("Bpm")) { try { tf.Tag.BeatsPerMinute = (uint)J.I(J.P(tags, "Bpm")); } catch { } }
            tf.Save(); tf.Dispose();
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
    private static (bool restored, bool skipped) RestoreField(TagLib.Tag tag, string name, FieldChange fc)
    {
        switch (name)
        {
            case "Title":
                if ((tag.Title ?? "") == (fc.NewStr ?? "")) { tag.Title = Empty(fc.OldStr); return (true, false); }
                return (false, true);
            case "Album":
                if ((tag.Album ?? "") == (fc.NewStr ?? "")) { tag.Album = Empty(fc.OldStr); return (true, false); }
                return (false, true);
            case "Comment":   // ficha de DJ volcada al comentario
                if ((tag.Comment ?? "") == (fc.NewStr ?? "")) { tag.Comment = Empty(fc.OldStr); return (true, false); }
                return (false, true);
            case "Key":   // clave musical (importación de rekordbox)
                if ((tag.InitialKey ?? "") == (fc.NewStr ?? "")) { tag.InitialKey = Empty(fc.OldStr); return (true, false); }
                return (false, true);
            case "Remixer":   // quién firma la versión (TPE4), lo escribe Tagging junto al título
                if ((tag.RemixedBy ?? "") == (fc.NewStr ?? "")) { tag.RemixedBy = Empty(fc.OldStr); return (true, false); }
                return (false, true);
            case "Artist":
                if (SeqEq(tag.Performers, fc.NewArr)) { tag.Performers = (fc.OldArr ?? new()).ToArray(); return (true, false); }
                return (false, true);
            case "Genre":
                if (SeqEq(tag.Genres, fc.NewArr)) { tag.Genres = (fc.OldArr ?? new()).ToArray(); return (true, false); }
                return (false, true);
            case "Year":
                if (tag.Year == fc.NewNum) { tag.Year = fc.OldNum; return (true, false); }
                return (false, true);
            case "Bpm":
                if (tag.BeatsPerMinute == fc.NewNum) { tag.BeatsPerMinute = fc.OldNum; return (true, false); }
                return (false, true);
            default:
                return (false, false);
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
