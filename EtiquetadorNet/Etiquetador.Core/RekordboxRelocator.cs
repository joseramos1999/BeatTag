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
    /// </summary>
    public static Dictionary<string, string> ReadRenames(string undoDir)
    {
        var mapa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(undoDir)) return mapa;

        var manifiestos = new DirectoryInfo(undoDir).EnumerateFiles("run_*.jsonl")
                                                    .OrderBy(f => f.Name)   // el nombre lleva la fecha
                                                    .ToList();
        foreach (var mf in manifiestos)
        {
            foreach (var linea in File.ReadLines(mf.FullName))
            {
                if (string.IsNullOrWhiteSpace(linea)) continue;
                var r = LeerRenombrado(linea);
                if (r == null || !r.Renamed) continue;
                if (string.IsNullOrEmpty(r.OrigPath) || string.IsNullOrEmpty(r.FinalPath)) continue;
                if (string.Equals(r.OrigPath, r.FinalPath, StringComparison.OrdinalIgnoreCase)) continue;

                // Si algo ya apuntaba a la ruta que este cambio deja atrás, se reencamina al destino
                // nuevo para que la cadena quede resuelta de una vez.
                foreach (var k in mapa.Where(kv => string.Equals(kv.Value, r.OrigPath, StringComparison.OrdinalIgnoreCase))
                                      .Select(kv => kv.Key).ToList())
                    mapa[k] = r.FinalPath;

                mapa[r.OrigPath] = r.FinalPath;
            }
        }

        // Un archivo que acabó donde empezó no necesita reparación.
        foreach (var k in mapa.Where(kv => string.Equals(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase))
                              .Select(kv => kv.Key).ToList())
            mapa.Remove(k);

        return mapa;
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

    /// <summary>Convierte una ruta local en la Location que escribe rekordbox.</summary>
    public static string PathToLocation(string path)
    {
        // rekordbox usa "file://localhost/D:/Musica/x.mp3" con los caracteres especiales escapados.
        // Las barras invertidas de Windows pasan a normales; los dos puntos de la unidad NO se
        // escapan, así que se escapa segmento a segmento.
        var s = path.Replace('\\', '/');
        var partes = s.Split('/');
        for (var i = 0; i < partes.Length; i++)
        {
            // El primer segmento es la unidad ("D:"), que va tal cual.
            if (i == 0 && partes[i].EndsWith(':')) continue;
            partes[i] = Uri.EscapeDataString(partes[i]);
        }
        return "file://localhost/" + string.Join("/", partes);
    }

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
