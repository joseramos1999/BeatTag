using System.Globalization;
using System.Text;
using System.Xml;

namespace Etiquetador.Core;

/// <summary>
/// Una pista de la colección de rekordbox, con lo que hace falta para cruzarla con la biblioteca:
/// dónde está el archivo, si se ha preparado (hot cues, beatgrid) y si se ha pinchado.
/// </summary>
public sealed record PistaRekordbox
{
    /// <summary>TrackID del XML: identifica la pista dentro de ESTE archivo, no en rekordbox.</summary>
    public string TrackId { get; init; } = "";

    /// <summary>La Location tal como la escribió rekordbox, sin tocar (ver <see cref="Ruta"/>).</summary>
    public string Location { get; init; } = "";

    /// <summary>La ruta local del archivo (<see cref="RekordboxImporter.LocationToPath"/>).</summary>
    public string Ruta { get; init; } = "";

    public string Nombre { get; init; } = "";
    public string Artista { get; init; } = "";

    /// <summary>Tamaño del archivo en bytes según rekordbox (Size). 0 si no lo trae.</summary>
    public long Tamano { get; init; }

    /// <summary>Duración en segundos (TotalTime). 0 si no la trae.</summary>
    public int Duracion { get; init; }

    /// <summary>
    /// Veces que se ha pinchado (PlayCount). Solo cuenta lo pinchado con rekordbox o lo que llegó en
    /// historiales importados: lo pinchado con otro programa no aparece.
    /// </summary>
    public int Reproducciones { get; init; }

    /// <summary>Cuándo entró en la colección (DateAdded), o null si no se entiende.</summary>
    public DateTime? Alta { get; init; }

    /// <summary>Estrellas de 0 a 5 (rekordbox las guarda como 0, 51, 102, 153, 204 y 255).</summary>
    public int Valoracion { get; init; }

    /// <summary>Hot cues: POSITION_MARK con Num de 0 en adelante (A = 0, B = 1…).</summary>
    public int HotCues { get; init; }

    /// <summary>Cues de memoria: POSITION_MARK con Num = -1.</summary>
    public int CuesMemoria { get; init; }

    /// <summary>Tiene beatgrid (algún TEMPO).</summary>
    public bool Beatgrid { get; init; }

    /// <summary>Segundo del primer tiempo de compás del beatgrid, o null sin beatgrid.</summary>
    public double? PrimerCompas { get; init; }

    /// <summary>Segundo del hot cue cuando solo tiene uno, o null.</summary>
    public double? UnicoHotCue { get; init; }

    public string Bpm { get; init; } = "";
    public string Tonalidad { get; init; } = "";

    /// <summary>
    /// Su único marcador es un hot cue en el primer tiempo del beatgrid. Es lo que dejan rekordbox y
    /// otras herramientas al analizar, sin que nadie haya preparado la pista: MEDIDO en una colección
    /// real exportada de rekordbox 7.2 (19.690 pistas), los 14.937 hot cues que había eran todos así
    /// -uno por pista, en la ranura A, llamado «1.1Bars», a menos de una décima del primer tiempo-;
    /// contarlas como preparadas habría escondido tres cuartas partes de la colección.
    /// </summary>
    public bool SoloCueInicial
        => HotCues == 1 && CuesMemoria == 0 && UnicoHotCue is { } c && PrimerCompas is { } p
           && Math.Abs(c - p) <= ToleranciaCueInicial;

    /// <summary>
    /// Margen para decir que un hot cue está «en el primer tiempo». Un tiempo a 175 BPM dura 0,34 s:
    /// con 0,1 s se distingue sin dudas un cue puesto en el primer tiempo de uno puesto en el
    /// siguiente, a cualquier tempo de baile.
    /// </summary>
    public const double ToleranciaCueInicial = 0.1;

    /// <summary>Sin hot cues, o con solo el automático del primer tiempo.</summary>
    public bool SinHotCues(bool contarCueInicial) => HotCues == 0 || (contarCueInicial && SoloCueInicial);
}

/// <summary>Lo leído de un XML de rekordbox.</summary>
public sealed class ColeccionRekordbox
{
    public IReadOnlyList<PistaRekordbox> Pistas { get; init; } = Array.Empty<PistaRekordbox>();

    /// <summary>«rekordbox 7.2.17», o vacío si el XML no lo dice.</summary>
    public string Producto { get; init; } = "";

    /// <summary>Entradas de la colección que no son un archivo del disco (streaming: Beatport, TIDAL…).</summary>
    public int SinArchivo { get; init; }

    /// <summary>Listas de reproducción que trae el XML.</summary>
    public int Listas { get; init; }
}

/// <summary>El archivo no se puede leer como colección de rekordbox. El mensaje se enseña tal cual.</summary>
public sealed class RekordboxXmlException : Exception
{
    public RekordboxXmlException(string mensaje, Exception? causa = null) : base(mensaje, causa) { }
}

/// <summary>
/// Lee la colección exportada de rekordbox (Archivo → Exportar colección en formato xml) sin cargarla
/// entera en memoria.
///
/// POR QUÉ EN STREAMING. MEDIDO: la colección real de 19.690 pistas pesa 96,6 MB, con 1,17 millones
/// de TEMPO; leerla con XDocument la monta entera en memoria antes de mirar nada. Aquí se recorre una
/// vez, de principio a fin, y de cada pista solo se guardan sus datos y la cuenta de marcadores.
/// </summary>
public static class LectorRekordbox
{
    /// <summary>Cómo se dice a quien lo usa que hay que exportar la colección.</summary>
    public const string ComoExportar = "En rekordbox, usa Archivo → Exportar colección en formato xml.";

    public static ColeccionRekordbox Leer(string ruta, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ruta) || !File.Exists(ruta))
            throw new RekordboxXmlException($"No se encuentra el archivo «{ruta}».");
        FileStream fs;
        try { fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16); }
        catch (Exception e) { throw new RekordboxXmlException($"No se puede abrir «{Path.GetFileName(ruta)}»: {e.Message}", e); }
        using (fs) return Leer(fs, ct);
    }

    public static ColeccionRekordbox LeerTexto(string xml, CancellationToken ct = default)
    {
        using var sr = new StringReader(xml ?? "");
        using var r = XmlReader.Create(sr, Ajustes());
        return Leer(r, ct);
    }

    public static ColeccionRekordbox Leer(Stream stream, CancellationToken ct = default)
    {
        using var r = XmlReader.Create(stream, Ajustes());
        return Leer(r, ct);
    }

    /// <summary>
    /// Sin DTD ni resolución externa (es un archivo que llega de fuera), y sin rechazar caracteres de
    /// control: rekordbox los deja pasar en los comentarios de algunas pistas y no por eso hay que dar
    /// por mala la colección entera.
    /// </summary>
    internal static XmlReaderSettings Ajustes() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = false,
    };

    private sealed class EnLectura
    {
        public string TrackId = "", Location = "", Nombre = "", Artista = "", Bpm = "", Tonalidad = "";
        public long Tamano;
        public int Duracion, Reproducciones, Valoracion, HotCues, CuesMemoria;
        public DateTime? Alta;
        public bool Beatgrid;
        public double? PrimerCompas, PrimerHotCue;
    }

    private static ColeccionRekordbox Leer(XmlReader r, CancellationToken ct)
    {
        var pistas = new List<PistaRekordbox>();
        string producto = "";
        int sinArchivo = 0, listas = 0;
        var vistaColeccion = false;
        var enColeccion = false;
        EnLectura? actual = null;

        try
        {
            if (r.MoveToContent() != XmlNodeType.Element)
                throw new RekordboxXmlException("El archivo está vacío o no es un XML. " + ComoExportar);
            if (r.LocalName != "DJ_PLAYLISTS")
                throw new RekordboxXmlException($"El archivo es un XML, pero no una colección de rekordbox (empieza por <{r.LocalName}>). " + ComoExportar);

            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element)
                {
                    switch (r.LocalName)
                    {
                        case "PRODUCT":
                            producto = $"{r.GetAttribute("Name")} {r.GetAttribute("Version")}".Trim();
                            break;
                        case "COLLECTION":
                            vistaColeccion = true;
                            enColeccion = !r.IsEmptyElement;
                            break;
                        case "TRACK" when enColeccion && actual == null:
                            actual = LeerPista(r);
                            if (r.IsEmptyElement) { Cerrar(actual, pistas, ref sinArchivo); actual = null; }
                            if (pistas.Count % 2048 == 0) ct.ThrowIfCancellationRequested();
                            break;
                        case "TEMPO" when actual != null:
                            if (!actual.Beatgrid) actual.PrimerCompas = PrimerCompas(r);
                            actual.Beatgrid = true;
                            break;
                        case "POSITION_MARK" when actual != null:
                            var num = Entero(r.GetAttribute("Num"), -1);
                            if (num >= 0)
                            {
                                actual.HotCues++;
                                if (actual.HotCues == 1) actual.PrimerHotCue = Decimal(r.GetAttribute("Start"));
                            }
                            else actual.CuesMemoria++;
                            break;
                        case "NODE":
                            if (r.GetAttribute("Type") == "1") listas++;
                            break;
                    }
                }
                else if (r.NodeType == XmlNodeType.EndElement)
                {
                    if (r.LocalName == "TRACK" && actual != null) { Cerrar(actual, pistas, ref sinArchivo); actual = null; }
                    else if (r.LocalName == "COLLECTION") enColeccion = false;
                }
            }
        }
        catch (XmlException e)
        {
            // Lo habitual: el archivo se copió a medias o se cortó al exportar.
            var corto = e.Message.Contains("end of file", StringComparison.OrdinalIgnoreCase)
                        || e.Message.Contains("final del archivo", StringComparison.OrdinalIgnoreCase);
            throw new RekordboxXmlException(
                (corto
                    ? $"El archivo está incompleto: se corta en la línea {e.LineNumber:N0}. "
                    : $"El archivo no es un XML válido (línea {e.LineNumber:N0}, posición {e.LinePosition:N0}): {e.Message} ")
                + "Vuelve a exportar la colección desde rekordbox (Archivo → Exportar colección en formato xml).", e);
        }

        if (!vistaColeccion)
            throw new RekordboxXmlException("El XML es de rekordbox, pero no trae la colección de pistas (falta <COLLECTION>). " + ComoExportar);

        return new ColeccionRekordbox { Pistas = pistas, Producto = producto, SinArchivo = sinArchivo, Listas = listas };
    }

    private static EnLectura LeerPista(XmlReader r)
    {
        var p = new EnLectura
        {
            TrackId = r.GetAttribute("TrackID") ?? "",
            Location = r.GetAttribute("Location") ?? "",
            Nombre = r.GetAttribute("Name") ?? "",
            Artista = r.GetAttribute("Artist") ?? "",
            Bpm = (r.GetAttribute("AverageBpm") ?? "").Trim(),
            Tonalidad = (r.GetAttribute("Tonality") ?? "").Trim(),
            Tamano = Largo(r.GetAttribute("Size")),
            Duracion = (int)Math.Round(Decimal(r.GetAttribute("TotalTime")) ?? 0),
            Reproducciones = Entero(r.GetAttribute("PlayCount"), 0),
            Valoracion = Estrellas(Entero(r.GetAttribute("Rating"), 0)),
            Alta = Fecha(r.GetAttribute("DateAdded")),
        };
        return p;
    }

    private static void Cerrar(EnLectura p, List<PistaRekordbox> pistas, ref int sinArchivo)
    {
        // Las pistas de streaming (Beatport, TIDAL, SoundCloud) no tienen archivo en el disco: no hay
        // nada que cruzar con la biblioteca ni ruta que reparar.
        if (!p.Location.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) { sinArchivo++; return; }
        var ruta = RekordboxImporter.LocationToPath(p.Location);
        if (ruta.Length == 0) { sinArchivo++; return; }
        pistas.Add(new PistaRekordbox
        {
            TrackId = p.TrackId, Location = p.Location, Ruta = ruta, Nombre = p.Nombre, Artista = p.Artista,
            Tamano = p.Tamano, Duracion = p.Duracion, Reproducciones = p.Reproducciones, Alta = p.Alta,
            Valoracion = p.Valoracion, HotCues = p.HotCues, CuesMemoria = p.CuesMemoria, Beatgrid = p.Beatgrid,
            PrimerCompas = p.PrimerCompas, UnicoHotCue = p.HotCues == 1 ? p.PrimerHotCue : null,
            Bpm = p.Bpm, Tonalidad = p.Tonalidad,
        });
    }

    /// <summary>
    /// El primer tiempo de compás del beatgrid a partir de su primer TEMPO: Inizio es el segundo de
    /// ese tiempo y Battito su número dentro del compás (1 a 4). Si el beatgrid empieza en el tercer
    /// tiempo, el primero de compás llega dos tiempos después.
    /// </summary>
    internal static double? PrimerCompas(XmlReader r)
    {
        if (Decimal(r.GetAttribute("Inizio")) is not { } inicio) return null;
        var bpm = Decimal(r.GetAttribute("Bpm")) ?? 0;
        var tiempo = Entero(r.GetAttribute("Battito"), 1);
        var porCompas = Compas(r.GetAttribute("Metro"));
        if (bpm <= 0 || tiempo <= 1 || tiempo > porCompas) return inicio;
        return inicio + (porCompas - tiempo + 1) % porCompas * 60.0 / bpm;
    }

    /// <summary>Tiempos por compás de «4/4», «3/4», «7/8»… (4 si no se entiende).</summary>
    private static int Compas(string? metro)
    {
        var s = metro ?? "";
        var barra = s.IndexOf('/');
        return barra > 0 && int.TryParse(s[..barra], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is > 0 and <= 16 ? n : 4;
    }

    /// <summary>De 0-255 (como lo guarda rekordbox) a estrellas, redondeando lo que no sea exacto.</summary>
    internal static int Estrellas(int valor) => Math.Clamp((int)Math.Round(valor / 51.0), 0, 5);

    private static int Entero(string? s, int siNo)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : siNo;

    private static long Largo(string? s)
        => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0;

    private static double? Decimal(string? s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    /// <summary>«2010-08-21» (el formato del XML); null si no se entiende.</summary>
    internal static DateTime? Fecha(string? s)
        => DateTime.TryParseExact((s ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)
           || DateTime.TryParseExact((s ?? "").Trim(), "yyyy-M-d", CultureInfo.InvariantCulture, DateTimeStyles.None, out f)
            ? f : null;
}

/// <summary>
/// Cómo se comparan rutas que vienen de rekordbox con las de la biblioteca.
///
/// Sin distinguir mayúsculas, como el resto de BeatTag (NTFS y APFS no las distinguen por defecto), y
/// sin distinguir la forma Unicode de los acentos. En un Mac, el sistema de archivos y rekordbox
/// pueden escribir «Canción» de dos formas: con la «ó» como un solo carácter (NFC) o como «o» más el
/// acento suelto (NFD, lo habitual en los discos HFS+ y en lo que llega de ellos). Las dos abren el
/// mismo archivo en macOS, pero como texto son distintas y la pista no casaba con la biblioteca.
/// La ruta en sí no se toca: en Windows una ruta NFD es OTRO archivo y hay que abrirla tal cual.
/// </summary>
public sealed class ComparadorRutas : IEqualityComparer<string>
{
    public static readonly ComparadorRutas Instancia = new();

    /// <summary>La forma con la que se compara: NFC y en mayúsculas invariantes.</summary>
    public static string Clave(string? ruta)
    {
        var s = ruta ?? "";
        if (!s.IsNormalized(NormalizationForm.FormC)) s = s.Normalize(NormalizationForm.FormC);
        return s.ToUpperInvariant();
    }

    public bool Equals(string? x, string? y)
        => ReferenceEquals(x, y) || (x != null && y != null && string.Equals(Clave(x), Clave(y), StringComparison.Ordinal));

    public int GetHashCode(string obj) => StringComparer.Ordinal.GetHashCode(Clave(obj));
}
