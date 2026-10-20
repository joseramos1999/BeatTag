using System.Globalization;
using System.Text;
using System.Xml;

namespace Etiquetador.Core;

/// <summary>Una lista que se lleva a rekordbox, dentro de la carpeta «BeatTag».</summary>
/// <param name="Carpeta">Subcarpeta dentro de «BeatTag» («Colecciones»…); "" para dejarla directamente en «BeatTag».</param>
/// <param name="Nombre">Nombre de la lista en rekordbox.</param>
/// <param name="Rutas">Los archivos, en el orden de la lista.</param>
public sealed record ListaParaRekordbox(string Carpeta, string Nombre, IReadOnlyList<string> Rutas);

/// <summary>Lo que se escribe de cada pista: el título y el artista que se ven en rekordbox si aún no la tiene.</summary>
public readonly record struct DatosPistaRekordbox(string Nombre, string Artista);

/// <summary>Cómo fue la exportación.</summary>
/// <param name="Listas">Listas escritas (las vacías no se escriben).</param>
/// <param name="Pistas">Archivos distintos en la colección del XML.</param>
/// <param name="Vacias">Listas que no se escribieron por no tener ninguna canción.</param>
public sealed record ResultadoExportacionRekordbox(int Listas, int Pistas, int Vacias, string Error = "")
{
    public bool Ok => Error.Length == 0;
}

/// <summary>Lo que hay en el archivo donde se va a exportar (ver <see cref="ExportadorRekordbox.QueHayEn"/>).</summary>
public enum DestinoExportacion { NoExiste, DeBeatTag, Otro }

/// <summary>
/// Escribe un XML de rekordbox (DJ_PLAYLISTS) con una carpeta «BeatTag» de listas, para importarlas
/// todas de una vez en lugar de un M3U8 tras otro.
///
/// QUÉ SE ESCRIBE DE CADA PISTA, Y POR QUÉ TAN POCO. Solo Location, Name (el título) y Artist.
/// COMPROBADO en la especificación que publica rekordbox (xml_format_list.pdf, «XML file format for
/// playlists sharing»): de cada TRACK solo la Location es imprescindible, la colección solo necesita
/// las pistas que están en alguna lista, y una lista puede nombrar sus pistas por TrackID (KeyType 0).
/// La especificación no dice qué pasa con una pista que rekordbox ya tiene; lo documentado por
/// terceros es que importar una lista NO la toca (se queda con sus cue points, su beatgrid, su
/// valoración y sus comentarios) y que «Importar a la colección» sobre pistas elegidas SÍ la sustituye
/// por lo del XML. Por eso no se escriben BPM, tonalidad, valoración, comentarios, cues ni beatgrid:
/// lo que no va en el XML no se puede pisar con nada inventado aquí, y la página explica que hay que
/// importar la carpeta, no las pistas. Name y Artist son las etiquetas del propio archivo y sirven
/// para que una pista que rekordbox aún no tiene no entre sin nombre. La Location se escribe
/// exactamente como la escribe rekordbox (<see cref="RekordboxRelocator.PathToLocation"/>) o, si se
/// conoce la que ya tiene la pista en su colección, esa misma: es lo que la identifica.
/// </summary>
public static class ExportadorRekordbox
{
    /// <summary>Carpeta de rekordbox donde va todo.</summary>
    public const string CarpetaRaiz = "BeatTag";

    /// <param name="datos">Título y artista de cada ruta (de la biblioteca).</param>
    /// <param name="locations">Location exacta que ya tiene cada ruta en rekordbox, si se conoce (de su XML).</param>
    public static ResultadoExportacionRekordbox Escribir(string destino, IReadOnlyList<ListaParaRekordbox> listas,
        Func<string, DatosPistaRekordbox> datos, IReadOnlyDictionary<string, string>? locations = null)
    {
        var tmp = destino + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(destino);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            ResultadoExportacionRekordbox r;
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                r = Escribir(fs, listas, datos, locations);
            if (r.Listas == 0) return r with { Error = "No hay ninguna lista con canciones de la biblioteca que exportar." };
            File.Move(tmp, destino, overwrite: true);
            return r;
        }
        catch (Exception e) { return new ResultadoExportacionRekordbox(0, 0, 0, e.Message); }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public static ResultadoExportacionRekordbox Escribir(Stream salida, IReadOnlyList<ListaParaRekordbox> listas,
        Func<string, DatosPistaRekordbox> datos, IReadOnlyDictionary<string, string>? locations = null)
    {
        // Las listas, sin repetir archivo dentro de cada una (dos filas de un chart pueden casar con
        // el mismo archivo) y sin las vacías.
        var limpias = new List<(string Carpeta, string Nombre, List<string> Rutas)>();
        var vacias = 0;
        foreach (var l in listas)
        {
            var vistas = new HashSet<string>(ComparadorRutas.Instancia);
            var rutas = l.Rutas.Where(r => !string.IsNullOrWhiteSpace(r) && vistas.Add(r)).ToList();
            if (rutas.Count == 0) { vacias++; continue; }
            limpias.Add((Limpio(l.Carpeta, ""), Limpio(l.Nombre, "Lista"), rutas));
        }

        // La colección: cada archivo una vez, con un TrackID propio de este XML.
        var ids = new Dictionary<string, int>(ComparadorRutas.Instancia);
        var orden = new List<string>();
        foreach (var l in limpias)
            foreach (var r in l.Rutas)
                if (!ids.ContainsKey(r)) { ids[r] = ids.Count + 1; orden.Add(r); }

        var ajustes = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), Indent = true, IndentChars = "  ", CheckCharacters = false,
        };
        using (var w = XmlWriter.Create(salida, ajustes))
        {
            w.WriteStartDocument();
            w.WriteStartElement("DJ_PLAYLISTS");
            w.WriteAttributeString("Version", "1.0.0");

            w.WriteStartElement("PRODUCT");
            w.WriteAttributeString("Name", "BeatTag");
            w.WriteAttributeString("Version", AppInfo.Version);
            w.WriteAttributeString("Company", "BeatTag");
            w.WriteEndElement();

            w.WriteStartElement("COLLECTION");
            w.WriteAttributeString("Entries", Num(orden.Count));
            foreach (var ruta in orden)
            {
                var d = datos(ruta);
                w.WriteStartElement("TRACK");
                w.WriteAttributeString("TrackID", Num(ids[ruta]));
                w.WriteAttributeString("Name", Texto(d.Nombre.Length > 0 ? d.Nombre : Path.GetFileNameWithoutExtension(ruta)));
                w.WriteAttributeString("Artist", Texto(d.Artista));
                w.WriteAttributeString("Location",
                    locations != null && locations.TryGetValue(ruta, out var loc) && loc.Length > 0 ? loc : RekordboxRelocator.PathToLocation(ruta));
                w.WriteEndElement();
            }
            w.WriteEndElement();

            // ROOT → BeatTag → (subcarpetas en el orden en que aparecen) → listas.
            var grupos = limpias.GroupBy(l => l.Carpeta, StringComparer.CurrentCultureIgnoreCase).ToList();
            var sueltas = grupos.FirstOrDefault(g => g.Key.Length == 0)?.ToList() ?? new();
            var carpetas = grupos.Where(g => g.Key.Length > 0).ToList();

            w.WriteStartElement("PLAYLISTS");
            Carpeta(w, "ROOT", 1, raiz: true);
            Carpeta(w, CarpetaRaiz, carpetas.Count + sueltas.Count);
            foreach (var g in carpetas)
            {
                var deEsta = g.ToList();
                Carpeta(w, g.Key, deEsta.Count);
                Listas(w, deEsta, ids);
                w.WriteEndElement();
            }
            Listas(w, sueltas, ids);
            w.WriteEndElement();   // BeatTag
            w.WriteEndElement();   // ROOT
            w.WriteEndElement();   // PLAYLISTS

            w.WriteEndElement();   // DJ_PLAYLISTS
            w.WriteEndDocument();
        }
        return new ResultadoExportacionRekordbox(limpias.Count, orden.Count, vacias);
    }

    /// <summary>Una carpeta (Type 0) con lo que lleva dentro; se cierra fuera.</summary>
    private static void Carpeta(XmlWriter w, string nombre, int dentro, bool raiz = false)
    {
        w.WriteStartElement("NODE");
        // ROOT va como lo exporta rekordbox («Type» primero); las demás, con «Name» primero.
        if (raiz) { w.WriteAttributeString("Type", "0"); w.WriteAttributeString("Name", nombre); }
        else { w.WriteAttributeString("Name", nombre); w.WriteAttributeString("Type", "0"); }
        w.WriteAttributeString("Count", Num(dentro));
    }

    /// <summary>Las listas de una carpeta, con nombres sin repetir: «Top 50» y «Top 50 (2)».</summary>
    private static void Listas(XmlWriter w, IEnumerable<(string Carpeta, string Nombre, List<string> Rutas)> listas, Dictionary<string, int> ids)
    {
        var usados = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var l in listas)
        {
            var nombre = l.Nombre;
            for (var n = 2; !usados.Add(nombre); n++) nombre = $"{l.Nombre} ({n})";
            w.WriteStartElement("NODE");
            w.WriteAttributeString("Name", nombre);
            w.WriteAttributeString("Type", "1");
            w.WriteAttributeString("KeyType", "0");   // las pistas se nombran por su TrackID
            w.WriteAttributeString("Entries", Num(l.Rutas.Count));
            foreach (var r in l.Rutas)
            {
                w.WriteStartElement("TRACK");
                w.WriteAttributeString("Key", Num(ids[r]));
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
    }

    /// <summary>
    /// Qué hay en un archivo donde se va a exportar: no existe, lo escribió BeatTag (una exportación
    /// anterior, que se puede sustituir sin más) u OTRA cosa, casi siempre la colección exportada de
    /// rekordbox. El cuadro de guardar solo pregunta «¿reemplazar?», y elegir por error la colección
    /// exportada la sustituiría por unas pocas listas: en ese caso se pide confirmación aparte.
    /// Solo lee el principio del archivo.
    /// </summary>
    public static DestinoExportacion QueHayEn(string ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta) || !File.Exists(ruta)) return DestinoExportacion.NoExiste;
        try
        {
            using var r = XmlReader.Create(new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                                           new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = true });
            if (r.MoveToContent() != XmlNodeType.Element || r.LocalName != "DJ_PLAYLISTS") return DestinoExportacion.Otro;
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element) continue;
                // PRODUCT va lo primero; si lo que llega es la colección, no lo escribió BeatTag.
                return r.LocalName == "PRODUCT" && r.GetAttribute("Name") == CarpetaRaiz
                    ? DestinoExportacion.DeBeatTag : DestinoExportacion.Otro;
            }
            return DestinoExportacion.Otro;
        }
        catch { return DestinoExportacion.Otro; }
    }

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Sin saltos de línea ni caracteres de control, que el XML no admite y rekordbox enseña mal.</summary>
    internal static string Texto(string? s)
    {
        var t = new string((s ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return System.Text.RegularExpressions.Regex.Replace(t, @"\s{2,}", " ").Trim();
    }

    private static string Limpio(string? s, string siVacio)
    {
        var t = Texto(s);
        return t.Length > 0 ? t : siVacio;
    }
}
