using System.Xml;
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
    public static Dictionary<string, string> ReadRenames(string undoDir) => LeerRenombrados(undoDir, conDeshechos: true);

    /// <summary>
    /// Solo los renombrados que siguen VIGENTES: los A→B de ejecuciones que no se han deshecho,
    /// encadenados igual que en <see cref="ReadRenames"/>. Es lo que hay que contar como
    /// «renombrados anotados».
    ///
    /// Visto en una biblioteca real: ReadRenames incluye, para cada renombrado deshecho, su vuelta
    /// B→A -necesaria para reparar una Location que llegó a apuntar a B-, así que el contador de
    /// Ajustes contaba de más los 12 de run_20260920_230254.deshecho.jsonl, que ya estaban en su
    /// nombre original.
    /// </summary>
    public static Dictionary<string, string> RenombradosVigentes(string undoDir) => LeerRenombrados(undoDir, conDeshechos: false);

    private static Dictionary<string, string> LeerRenombrados(string undoDir, bool conDeshechos)
    {
        var mapa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(undoDir)) return mapa;

        var pasos = new List<(DateTime Cuando, int Fase, string Manifiesto, int Orden, string De, string A)>();
        foreach (var mf in new DirectoryInfo(undoDir).EnumerateFiles("run_*.jsonl"))
        {
            if (!conDeshechos && mf.Name.Contains(".deshecho", StringComparison.OrdinalIgnoreCase)) continue;
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

        // Quién apunta a cada ruta (de destino a orígenes), para encadenar sin recorrer el mapa entero
        // en cada paso. MEDIDO en un historial real (7.529 renombrados anotados): recorriéndolo entero
        // por paso, leerlo tardaba 1,4 s, y AppEngine.ReubicarFichas lo lee al cambiar la biblioteca.
        var apuntan = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Apuntar(string origen, string destino)
        {
            if (mapa.TryGetValue(origen, out var antes) && apuntan.TryGetValue(antes, out var deAntes)) deAntes.Remove(origen);
            mapa[origen] = destino;
            if (!apuntan.TryGetValue(destino, out var deDestino)) apuntan[destino] = deDestino = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            deDestino.Add(origen);
        }

        foreach (var p in pasos.OrderBy(p => p.Cuando).ThenBy(p => p.Fase)
                               .ThenBy(p => p.Manifiesto, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Orden))
        {
            // Si algo ya apuntaba a la ruta que este cambio deja atrás, se reencamina al destino
            // nuevo para que la cadena quede resuelta de una vez.
            if (apuntan.TryGetValue(p.De, out var previos))
                foreach (var k in previos.ToList()) Apuntar(k, p.A);

            Apuntar(p.De, p.A);
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
    ///
    /// Se escapa COMO rekordbox, no como un URI de manual (ver <see cref="Escapar"/>): la Location es
    /// lo que identifica la pista al importar un XML, y lo más seguro es escribirla carácter a
    /// carácter igual que la escribe él.
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

    /// <summary>
    /// Signos que rekordbox deja sin escapar. MEDIDO en una colección real exportada de rekordbox 7.2
    /// (19.690 pistas): deja tal cual «( ) , ! # $ + @ ; =» y escapa el espacio, «&amp; [ ] ' ^ ` %» y
    /// todo lo que no es ASCII, con las cifras hexadecimales en minúscula («%c3%b3»). Con el escapado
    /// de manual (Uri.EscapeDataString) 15.018 de esas 19.690 Location salían distintas de las suyas
    /// -los paréntesis y las comas de «(Extended Mix)» o «Artista, Artista»- aunque nombraran el
    /// mismo archivo; ahora salen idénticas las 19.690.
    /// </summary>
    private const string SinEscapar = "-._~()!,#$+@;=";

    /// <summary>Escapa tramo a tramo (las barras separan, no se escapan).</summary>
    private static string Escapar(string ruta, bool dejarUnidad)
    {
        var partes = ruta.Split('/');
        for (var i = 0; i < partes.Length; i++)
        {
            if (dejarUnidad && i == 0 && partes[i].EndsWith(':')) continue;
            partes[i] = EscaparTramo(partes[i]);
        }
        return string.Join("/", partes);
    }

    private static string EscaparTramo(string tramo)
    {
        var sb = new System.Text.StringBuilder(tramo.Length + 16);
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(tramo))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || SinEscapar.Contains(c))) sb.Append(c);
            else sb.Append('%').Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>«D:\…» o «D:/…».</summary>
    internal static bool EsRutaDeUnidad(string ruta)
        => ruta.Length >= 3 && char.IsAsciiLetter(ruta[0]) && ruta[1] == ':' && ruta[2] is '\\' or '/';

    /// <summary>Nombre de la lista que se añade a la colección reparada con las pistas reparadas.</summary>
    public const string ListaReparadas = "Rutas reparadas por BeatTag";

    /// <summary>
    /// Reescribe las rutas del XML. <paramref name="mapa"/> va de ruta ANTIGUA a ruta NUEVA.
    /// Escribe el resultado en <paramref name="destino"/> sin tocar el original: el usuario decide
    /// luego si lo reimporta, y conserva el de partida por si algo saliera mal.
    ///
    /// Se copia el XML de principio a fin sin cargarlo en memoria (antes, XDocument.Load con
    /// colecciones de decenas de MB; ver <see cref="LectorRekordbox"/>), y se escribe primero en un
    /// temporal: un fallo a mitad no deja en el destino un XML cortado que rekordbox intentaría leer.
    ///
    /// Con <paramref name="conLista"/>, la copia lleva además una lista «Rutas reparadas por BeatTag»
    /// con las pistas reparadas: en rekordbox se importan desde ella, sin tener que buscarlas una a una
    /// entre todas las de la colección.
    ///
    /// <paramref name="tituloDe"/> da el título de la etiqueta del archivo nuevo, si se conoce (de la
    /// biblioteca). Name, en rekordbox, es el TÍTULO de la pista, no el nombre del archivo: al reparar
    /// un renombrado se ponía el nombre del archivo («Artista - Título (Extended Mix)») y la pista
    /// entraba en rekordbox con el artista repetido en el título. Sin título conocido se sigue usando
    /// el nombre del archivo, como antes.
    /// </summary>
    public static RelocateResult Repair(string xmlOrigen, string destino, IReadOnlyDictionary<string, string> mapa,
                                        bool conLista = false, CancellationToken ct = default,
                                        Func<string, string?>? tituloDe = null)
    {
        if (mapa.Count == 0) return new RelocateResult(0, 0, 0, "No hay ningún renombrado que reparar.");
        if (ComparadorRutas.Instancia.Equals(Path.GetFullPath(xmlOrigen), Path.GetFullPath(destino)))
            return new RelocateResult(0, 0, 0, "La colección reparada se guarda en un archivo nuevo: elige otro nombre para no sustituir la exportada.");

        // Las rutas del XML pueden venir con los acentos en otra forma Unicode que las del mapa (un Mac).
        var porRuta = new Dictionary<string, string>(ComparadorRutas.Instancia);
        foreach (var kv in mapa) porRuta[kv.Key] = kv.Value;

        var tmp = destino + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(destino);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            Copia copia;
            using (var entrada = new FileStream(xmlOrigen, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
            using (var lector = XmlReader.Create(entrada, AjustesCopia()))
            using (var salida = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var escritor = XmlWriter.Create(salida, new XmlWriterSettings
                   {
                       Encoding = new System.Text.UTF8Encoding(false), CheckCharacters = false,
                       NewLineHandling = NewLineHandling.None,
                   }))
            {
                copia = new Copia(porRuta, conLista, tituloDe);
                copia.Copiar(lector, escritor, ct);
            }
            File.Move(tmp, destino, overwrite: true);
            return new RelocateResult(copia.Reparadas, copia.SinCambio, copia.NoEncontradas);
        }
        catch (OperationCanceledException) { throw; }
        catch (RekordboxXmlException e) { return new RelocateResult(0, 0, 0, e.Message); }
        catch (XmlException e)
        {
            return new RelocateResult(0, 0, 0, $"La colección no es un XML válido (línea {e.LineNumber:N0}): {e.Message} "
                                             + "Vuelve a exportarla desde rekordbox (Archivo → Exportar colección en formato xml).");
        }
        catch (Exception e) { return new RelocateResult(0, 0, 0, e.Message); }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>Como la lectura, pero conservando los espacios entre etiquetas para que la copia se lea igual.</summary>
    private static XmlReaderSettings AjustesCopia()
    {
        var a = LectorRekordbox.Ajustes();
        a.IgnoreWhitespace = false;
        a.IgnoreComments = false;
        a.IgnoreProcessingInstructions = false;
        return a;
    }

    /// <summary>La copia del XML nodo a nodo, cambiando solo lo que hay que reparar.</summary>
    private sealed class Copia
    {
        private readonly Dictionary<string, string> _mapa;
        private readonly bool _conLista;
        private readonly Func<string, string?>? _tituloDe;
        private readonly List<(string Id, string Location)> _reparadas = new();
        public int Reparadas, SinCambio, NoEncontradas;

        public Copia(Dictionary<string, string> mapa, bool conLista, Func<string, string?>? tituloDe)
        {
            _mapa = mapa;
            _conLista = conLista;
            _tituloDe = tituloDe;
        }

        public void Copiar(XmlReader r, XmlWriter w, CancellationToken ct)
        {
            if (r.MoveToContent() != XmlNodeType.Element || r.LocalName != "DJ_PLAYLISTS")
                throw new RekordboxXmlException("El archivo no es una colección de rekordbox. " + LectorRekordbox.ComoExportar);

            w.WriteStartDocument();
            var enColeccion = false;
            var profundidadRaiz = -1;     // la del NODE «ROOT» de las listas
            var raizAnade = false;        // a ROOT se le sumó la lista nueva en su Count
            var listaPuesta = false;
            var pistas = 0;

            // MoveToContent ya ha dejado el lector en el elemento raíz: se procesa antes de leer más.
            var primero = true;
            while (primero || r.Read())
            {
                primero = false;
                switch (r.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        var nombre = r.LocalName;
                        var vacio = r.IsEmptyElement;
                        var profundidad = r.Depth;
                        w.WriteStartElement(r.Prefix, nombre, r.NamespaceURI);

                        if (nombre == "COLLECTION") enColeccion = !vacio;

                        if (nombre == "TRACK" && enColeccion)
                        {
                            CopiarPista(r, w);
                            if (++pistas % 2048 == 0) ct.ThrowIfCancellationRequested();
                        }
                        else if (nombre == "NODE" && profundidadRaiz < 0 && r.GetAttribute("Name") == "ROOT")
                        {
                            // La lista nueva cuelga de ROOT, que lleva la cuenta de lo que tiene dentro.
                            // COLLECTION va antes que PLAYLISTS: aquí ya se sabe qué se ha reparado.
                            profundidadRaiz = profundidad;
                            raizAnade = TocaLista;
                            CopiarAtributos(r, w, (n, v) => n == "Count" && raizAnade ? Mas1(v) : v);
                            if (vacio && raizAnade) { EscribirLista(w); listaPuesta = true; }
                        }
                        else w.WriteAttributes(r, true);

                        if (vacio) w.WriteEndElement();
                        break;
                    }
                    case XmlNodeType.EndElement:
                        if (r.LocalName == "COLLECTION") enColeccion = false;
                        if (r.LocalName == "NODE" && r.Depth == profundidadRaiz && !listaPuesta && raizAnade)
                        {
                            EscribirLista(w);
                            listaPuesta = true;
                        }
                        if (r.LocalName == "DJ_PLAYLISTS" && profundidadRaiz < 0 && TocaLista)
                        {
                            // Un XML sin listas: se crean PLAYLISTS y ROOT para la nueva.
                            w.WriteStartElement("PLAYLISTS");
                            w.WriteStartElement("NODE");
                            w.WriteAttributeString("Type", "0");
                            w.WriteAttributeString("Name", "ROOT");
                            w.WriteAttributeString("Count", "1");
                            EscribirLista(w);
                            w.WriteEndElement();
                            w.WriteEndElement();
                            listaPuesta = true;
                        }
                        w.WriteFullEndElement();
                        break;
                    case XmlNodeType.Text: w.WriteString(r.Value); break;
                    case XmlNodeType.CDATA: w.WriteCData(r.Value); break;
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace: w.WriteWhitespace(r.Value); break;
                    case XmlNodeType.Comment: w.WriteComment(r.Value); break;
                    case XmlNodeType.ProcessingInstruction: w.WriteProcessingInstruction(r.Name, r.Value); break;
                    // La declaración la pone WriteStartDocument; un DOCTYPE no se copia (no se procesa).
                }
            }
            w.WriteEndDocument();
        }

        private bool TocaLista => _conLista && _reparadas.Count > 0;

        private static string Mas1(string v)
            => int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? (n + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : v;

        private static void CopiarAtributos(XmlReader r, XmlWriter w, Func<string, string, string> valor)
        {
            if (!r.MoveToFirstAttribute()) return;
            do w.WriteAttributeString(r.Prefix, r.LocalName, r.NamespaceURI, valor(r.LocalName, r.Value));
            while (r.MoveToNextAttribute());
            r.MoveToElement();
        }

        private void CopiarPista(XmlReader r, XmlWriter w)
        {
            var loc = r.GetAttribute("Location");
            string? nueva = null;
            if (!string.IsNullOrEmpty(loc) && loc.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                var actual = RekordboxImporter.LocationToPath(loc);
                if (_mapa.TryGetValue(actual, out var destino))
                {
                    // Solo se toca lo que rekordbox tiene PERDIDO, y solo para mandarlo a un archivo
                    // que existe. Una pista cuyo archivo sigue en su sitio está bien como está (y con
                    // ella, sus cue points); cambiarla a una ruta que no existe es justo perderla.
                    if (ComparadorRutas.Instancia.Equals(actual, destino) || File.Exists(actual)) SinCambio++;
                    else if (!File.Exists(destino)) NoEncontradas++;
                    else
                    {
                        nueva = destino;
                        Reparadas++;
                    }
                }
                else if (!File.Exists(actual)) NoEncontradas++;
                else SinCambio++;

                if (nueva != null)
                {
                    var nuevaLocation = PathToLocation(nueva);
                    // El nombre visible también cambia al renombrar; si no se actualiza, la colección
                    // apunta bien pero se sigue leyendo el nombre viejo. Si el archivo solo se movió
                    // de carpeta (mismo nombre), el Name es el título que puso rekordbox y se respeta.
                    var renombrado = !ComparadorRutas.Instancia.Equals(Path.GetFileNameWithoutExtension(actual),
                                                                      Path.GetFileNameWithoutExtension(nueva));
                    var titulo = (_tituloDe?.Invoke(nueva) ?? "").Trim();
                    var nombreNuevo = titulo.Length > 0 ? titulo : Path.GetFileNameWithoutExtension(nueva);
                    CopiarAtributos(r, w, (n, v) => n == "Location" ? nuevaLocation
                                                  : n == "Name" && renombrado && nombreNuevo.Length > 0 ? nombreNuevo
                                                  : v);
                    _reparadas.Add((r.GetAttribute("TrackID") ?? "", nuevaLocation));
                    return;
                }
            }
            w.WriteAttributes(r, true);
        }

        /// <summary>La lista con las pistas reparadas, por su TrackID (o por su Location si falta alguno).</summary>
        private void EscribirLista(XmlWriter w)
        {
            var porId = _reparadas.All(x => x.Id.Length > 0);
            w.WriteStartElement("NODE");
            w.WriteAttributeString("Name", ListaReparadas);
            w.WriteAttributeString("Type", "1");
            w.WriteAttributeString("KeyType", porId ? "0" : "1");
            w.WriteAttributeString("Entries", _reparadas.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var (id, location) in _reparadas)
            {
                w.WriteStartElement("TRACK");
                w.WriteAttributeString("Key", porId ? id : location);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
    }
}
