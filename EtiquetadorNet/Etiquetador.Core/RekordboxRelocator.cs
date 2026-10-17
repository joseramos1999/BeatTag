using System.Xml.Linq;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Core;

/// <summary>Resultado de reparar una colección de rekordbox.</summary>
public sealed record RelocateResult(int Reparadas, int SinCambio, int NoEncontradas, string Error = "")
{
    public bool Ok => Error.Length == 0;
}

/// <summary>
/// Repara las rutas de una colección de rekordbox después de que BeatTag haya renombrado archivos.
///
/// POR QUÉ HACE FALTA. rekordbox no guarda los cue points ni el beatgrid dentro del archivo: los
/// tiene en su base de datos, ligados a la RUTA. Al renombrar un archivo, rekordbox lo da por
/// perdido y con él se van los puntos y la rejilla, que para un DJ son horas de trabajo. Es una
/// queja recurrente en sus foros, y BeatTag la provoca cada vez que renombra.
///
/// Lo que se necesita para arreglarlo ya lo escribimos: el manifiesto para deshacer guarda la ruta
/// vieja y la nueva de cada archivo. Con eso se reescriben las Location del XML exportado de
/// rekordbox, y al reimportarlo la colección vuelve a apuntar a los archivos correctos, con sus
/// cue points intactos.
/// </summary>
public static class RekordboxRelocator
{

    /// <summary>
    /// Todos los renombrados hechos hasta ahora, de ruta ANTIGUA a ruta ACTUAL, leídos de los
    /// manifiestos para deshacer.
    ///
    /// Se recorren de la más vieja a la más reciente y se encadenan: si un archivo se renombró dos
    /// veces (A->B y luego B->C), lo que necesita rekordbox es A->C, no dos saltos sueltos.
    ///
    /// Los manifiestos archivados («.deshecho») son renombrados que se DESHICIERON: el archivo
    /// volvió a su nombre. Visto en una biblioteca real: run_20260920_230254.deshecho.jsonl tiene
    /// 12 registros con "Renamed":true que se deshicieron esa misma noche, y se seguían leyendo
    /// como A->B vigentes; la reparación cambiaba la Location de esos 12 temas, que estaban bien en
    /// A, por B, que ya no existía, y rekordbox los perdía con sus cue points. Ahora, tras cada A->B
    /// deshecho va su vuelta B->A, colocada en el momento del deshacer: la cadena se anula (A no se
    /// toca) y una Location que hubiera llegado a apuntar a B vuelve a A. AppEngine.ReubicarFichas
    /// usa este mismo mapa y sigue devolviendo las fichas de B a A.
    /// </summary>
    public static Dictionary<string, string> ReadRenames(string undoDir)
    {
        var mapa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(undoDir)) return mapa;

        var pasos = new List<(DateTime Cuando, int Fase, string Manifiesto, int Orden, string De, string A)>();
        foreach (var mf in new DirectoryInfo(undoDir).EnumerateFiles("run_*.jsonl"))
        {
            var renombrados = new List<UndoRecord>();
            foreach (var linea in File.ReadLines(mf.FullName))
            {
                if (string.IsNullOrWhiteSpace(linea)) continue;
                var r = LeerRenombrado(linea);
                if (r == null || !r.Renamed) continue;
                if (string.IsNullOrEmpty(r.OrigPath) || string.IsNullOrEmpty(r.FinalPath)) continue;
                if (string.Equals(r.OrigPath, r.FinalPath, StringComparison.OrdinalIgnoreCase)) continue;
                renombrados.Add(r);
            }
            if (renombrados.Count == 0) continue;

            // El nombre lleva la fecha de la ejecución; la fecha de modificación del archivado es la
            // del deshacer (o, en los archivados antiguos, la de la propia ejecución).
            var hecho = FechaDelNombre(mf.Name) ?? mf.LastWriteTime;
            for (var i = 0; i < renombrados.Count; i++)
                pasos.Add((hecho, 0, mf.Name, i, renombrados[i].OrigPath, renombrados[i].FinalPath));

            if (mf.Name.Contains(".deshecho", StringComparison.OrdinalIgnoreCase))
            {
                // Deshacer recorre el manifiesto al revés, así que las vueltas van en orden inverso.
                var deshecho = mf.LastWriteTime > hecho ? mf.LastWriteTime : hecho;
                for (int i = renombrados.Count - 1, k = 0; i >= 0; i--, k++)
                    pasos.Add((deshecho, 1, mf.Name, k, renombrados[i].FinalPath, renombrados[i].OrigPath));
            }
        }

        foreach (var p in pasos.OrderBy(p => p.Cuando).ThenBy(p => p.Fase)
                               .ThenBy(p => p.Manifiesto, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Orden))
        {
            // Si algo ya apuntaba a la ruta que este cambio deja atrás, se reencamina al destino
            // nuevo para que la cadena quede resuelta de una vez.
            foreach (var k in mapa.Where(kv => string.Equals(kv.Value, p.De, StringComparison.OrdinalIgnoreCase))
                                  .Select(kv => kv.Key).ToList())
                mapa[k] = p.A;

            mapa[p.De] = p.A;
        }

        // Un archivo que acabó donde empezó no necesita reparación.
        foreach (var k in mapa.Where(kv => string.Equals(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase))
                              .Select(kv => kv.Key).ToList())
            mapa.Remove(k);

        return mapa;
    }

    /// <summary>Fecha de la ejecución sacada del nombre «run_aaaammdd_hhmmss…», o null.</summary>
    private static DateTime? FechaDelNombre(string nombre)
    {
        if (nombre.Length < 19 || !nombre.StartsWith("run_", StringComparison.OrdinalIgnoreCase)) return null;
        return DateTime.TryParseExact(nombre.Substring(4, 15), "yyyyMMdd_HHmmss",
                                      System.Globalization.CultureInfo.InvariantCulture,
                                      System.Globalization.DateTimeStyles.AssumeLocal, out var f)
            ? f : null;
    }

    /// <summary>
    /// Una línea de manifiesto, venga del formato actual o del antiguo.
    ///
    /// Deshacer siempre entendió los dos, pero esto solo leía el nuevo, así que los renombrados más
    /// viejos -justo los que llevan más tiempo rotos en rekordbox- no se reparaban. El formato
    /// antiguo guarda la ruta ACTUAL en "new" y solo el NOMBRE original en "orig", de modo que la
    /// ruta de partida se reconstruye con la carpeta de la actual.
    /// </summary>
    private static UndoRecord? LeerRenombrado(string linea)
    {
        try
        {
            var nodo = System.Text.Json.Nodes.JsonNode.Parse(linea);
            if (nodo is not System.Text.Json.Nodes.JsonObject o) return null;

            if (o.ContainsKey("OrigPath"))
                return System.Text.Json.JsonSerializer.Deserialize<UndoRecord>(linea);

            var actual = o["new"]?.GetValue<string>() ?? "";
            var nombreOriginal = o["orig"]?.GetValue<string>() ?? "";
            if (actual.Length == 0 || nombreOriginal.Length == 0) return null;

            var renombrado = o["renamed"] is System.Text.Json.Nodes.JsonValue v
                             && v.TryGetValue<bool>(out var b) && b;

            return new UndoRecord
            {
                OrigPath = Path.Combine(Path.GetDirectoryName(actual) ?? "", nombreOriginal),
                FinalPath = actual,
                Renamed = renombrado,
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Convierte una ruta local en la Location que escribe rekordbox:
    ///   Windows  D:\Musica\x.mp3          → file://localhost/D:/Musica/x.mp3
    ///   macOS    /Users/dj/Music/x.mp3    → file://localhost/Users/dj/Music/x.mp3
    ///   red      \\servidor\musica\x.mp3  → file://servidor/musica/x.mp3
    ///
    /// Con una ruta de macOS antes salía «file://localhost//Users/…»: el primer tramo, vacío, se
    /// unía con otra barra. Y en macOS la barra invertida NO separa carpetas -es un carácter más del
    /// nombre-, así que solo se convierte en las rutas de Windows.
    /// </summary>
    public static string PathToLocation(string path)
    {
        var windows = OperatingSystem.IsWindows() || EsRutaDeUnidad(path) || path.StartsWith(@"\\", StringComparison.Ordinal);
        var s = windows ? path.Replace('\\', '/') : path;

        // Ruta de red: el servidor va en el lugar del host, como en cualquier URI file://.
        if (s.StartsWith("//", StringComparison.Ordinal))
            return "file://" + Escapar(s[2..], dejarUnidad: false);

        // Ruta absoluta de macOS/Linux: la barra inicial ya es la que separa el host de la ruta.
        if (s.StartsWith('/'))
            return "file://localhost/" + Escapar(s[1..], dejarUnidad: false);

        // Windows: los dos puntos de la unidad («D:») NO se escapan, rekordbox espera «D:/».
        return "file://localhost/" + Escapar(s, dejarUnidad: true);
    }

    /// <summary>Escapa tramo a tramo (las barras separan, no se escapan).</summary>
    private static string Escapar(string ruta, bool dejarUnidad)
    {
        var partes = ruta.Split('/');
        for (var i = 0; i < partes.Length; i++)
        {
            if (dejarUnidad && i == 0 && partes[i].EndsWith(':')) continue;
            partes[i] = Uri.EscapeDataString(partes[i]);
        }
        return string.Join("/", partes);
    }

    /// <summary>«D:\…» o «D:/…».</summary>
    internal static bool EsRutaDeUnidad(string ruta)
        => ruta.Length >= 3 && char.IsAsciiLetter(ruta[0]) && ruta[1] == ':' && ruta[2] is '\\' or '/';

    /// <summary>
    /// Reescribe las rutas del XML. <paramref name="mapa"/> va de ruta ANTIGUA a ruta NUEVA.
    /// Escribe el resultado en <paramref name="destino"/> sin tocar el original: el usuario decide
    /// luego si lo reimporta, y conserva el de partida por si algo saliera mal.
    /// </summary>
    public static RelocateResult Repair(string xmlOrigen, string destino, IReadOnlyDictionary<string, string> mapa)
    {
        if (mapa.Count == 0) return new RelocateResult(0, 0, 0, "No hay ningún renombrado que reparar.");

        try
        {
            var doc = XDocument.Load(xmlOrigen);
            int reparadas = 0, sinCambio = 0, noEncontradas = 0;

            foreach (var tr in doc.Descendants("TRACK"))
            {
                var loc = (string?)tr.Attribute("Location");
                if (string.IsNullOrEmpty(loc)) continue;   // los TRACK de PLAYLISTS son referencias

                var actual = RekordboxImporter.LocationToPath(loc);
                if (mapa.TryGetValue(actual, out var nueva))
                {
                    if (string.Equals(actual, nueva, StringComparison.OrdinalIgnoreCase)) { sinCambio++; continue; }

                    // Solo se toca lo que rekordbox tiene PERDIDO, y solo para mandarlo a un archivo
                    // que existe. Una pista cuyo archivo sigue en su sitio está bien como está (y con
                    // ella, sus cue points); cambiarla a una ruta que no existe es justo perderla.
                    if (File.Exists(actual)) { sinCambio++; continue; }
                    if (!File.Exists(nueva)) { noEncontradas++; continue; }

                    tr.SetAttributeValue("Location", PathToLocation(nueva));

                    // El nombre visible también cambia al renombrar; si no se actualiza, la
                    // colección apunta bien pero se sigue leyendo el nombre viejo.
                    var nombre = Path.GetFileNameWithoutExtension(nueva);
                    if (!string.IsNullOrEmpty(nombre) && tr.Attribute("Name") != null)
                        tr.SetAttributeValue("Name", nombre);

                    reparadas++;
                }
                else if (!File.Exists(actual)) noEncontradas++;
                else sinCambio++;
            }

            var dir = Path.GetDirectoryName(destino);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            doc.Save(destino);

            return new RelocateResult(reparadas, sinCambio, noEncontradas);
        }
        catch (Exception e) { return new RelocateResult(0, 0, 0, e.Message); }
    }
}
