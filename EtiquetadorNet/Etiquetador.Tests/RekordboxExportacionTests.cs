using System.Text;
using System.Xml.Linq;
using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// La colección reparada se escribe copiando el XML en streaming (P3, b), y las listas de BeatTag se
/// llevan a rekordbox en un solo XML con su carpeta (P11).
/// </summary>
public class RekordboxExportacionTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Archivo(string nombre, string contenido = "audio")
    {
        var r = Path.Combine(_dir, nombre);
        Directory.CreateDirectory(Path.GetDirectoryName(r)!);
        File.WriteAllText(r, contenido);
        return r;
    }

    // ---------------------------------------------------------------------------------------------
    // Reparación en streaming
    // ---------------------------------------------------------------------------------------------

    private static string ColeccionCon(params (string Id, string Ruta, string Nombre)[] pistas)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<DJ_PLAYLISTS Version=\"1.0.0\">");
        sb.AppendLine("  <PRODUCT Name=\"rekordbox\" Version=\"7.2.17\" Company=\"AlphaTheta\"/>");
        sb.AppendLine($"  <COLLECTION Entries=\"{pistas.Length}\">");
        foreach (var (id, ruta, nombre) in pistas)
        {
            sb.AppendLine($"    <TRACK TrackID=\"{id}\" Name=\"{nombre}\" Artist=\"A &amp; B\" Rating=\"204\" Comments=\"Pico de la noche\"");
            sb.AppendLine($"           Location=\"{RekordboxRelocator.PathToLocation(ruta)}\">");
            sb.AppendLine("      <TEMPO Inizio=\"0.250\" Bpm=\"96.00\" Metro=\"4/4\" Battito=\"1\"/>");
            sb.AppendLine("      <POSITION_MARK Name=\"Drop\" Type=\"0\" Start=\"45.250\" Num=\"1\"/>");
            sb.AppendLine("    </TRACK>");
        }
        sb.AppendLine("  </COLLECTION>");
        sb.AppendLine("  <PLAYLISTS>");
        sb.AppendLine("    <NODE Type=\"0\" Name=\"ROOT\" Count=\"1\">");
        sb.AppendLine("      <NODE Name=\"Sesión\" Type=\"1\" KeyType=\"0\" Entries=\"1\">");
        sb.AppendLine($"        <TRACK Key=\"{pistas[0].Id}\"/>");
        sb.AppendLine("      </NODE>");
        sb.AppendLine("    </NODE>");
        sb.AppendLine("  </PLAYLISTS>");
        sb.AppendLine("</DJ_PLAYLISTS>");
        return sb.ToString();
    }

    [Fact]
    public void La_copia_reparada_conserva_todo_lo_demas_y_trae_la_lista_de_reparadas()
    {
        var vieja = Path.Combine(_dir, "Vieja", "Tema.mp3");                 // movida: ya no existe
        var nueva = Archivo(Path.Combine("Nueva", "Tema.mp3"));
        var quieta = Archivo("Quieta.mp3");
        var origen = Path.Combine(_dir, "collection.xml");
        var destino = Path.Combine(_dir, "reparada.xml");
        File.WriteAllText(origen, ColeccionCon(("101", vieja, "Tema bonito"), ("102", quieta, "Quieta")));
        var antes = File.ReadAllText(origen);

        var r = RekordboxRelocator.Repair(origen, destino, new Dictionary<string, string> { [vieja] = nueva }, conLista: true);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(1, r.Reparadas);
        Assert.Equal(1, r.SinCambio);
        Assert.Equal(antes, File.ReadAllText(origen));                       // el original, intacto
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp", SearchOption.AllDirectories));   // ningún temporal suelto

        var doc = XDocument.Load(destino);
        var pistas = doc.Root!.Element("COLLECTION")!.Elements("TRACK").ToList();
        var reparada = pistas.Single(t => (string?)t.Attribute("TrackID") == "101");
        Assert.Equal(RekordboxRelocator.PathToLocation(nueva), (string?)reparada.Attribute("Location"));
        // Solo se movió de carpeta: el Name es el título que tenía en rekordbox, y se respeta.
        Assert.Equal("Tema bonito", (string?)reparada.Attribute("Name"));
        // Lo que rekordbox tenía de la pista viaja con ella: valoración, comentario, beatgrid y cues.
        Assert.Equal("204", (string?)reparada.Attribute("Rating"));
        Assert.Equal("Pico de la noche", (string?)reparada.Attribute("Comments"));
        Assert.Equal("A & B", (string?)reparada.Attribute("Artist"));
        Assert.Single(reparada.Elements("TEMPO"));
        Assert.Single(reparada.Elements("POSITION_MARK"));

        // ROOT cuenta una lista más, y la nueva nombra la reparada por su TrackID.
        var root = doc.Root!.Element("PLAYLISTS")!.Element("NODE")!;
        Assert.Equal("2", (string?)root.Attribute("Count"));
        var lista = root.Elements("NODE").Single(n => (string?)n.Attribute("Name") == RekordboxRelocator.ListaReparadas);
        Assert.Equal("0", (string?)lista.Attribute("KeyType"));
        Assert.Equal("1", (string?)lista.Attribute("Entries"));
        Assert.Equal("101", (string?)lista.Element("TRACK")!.Attribute("Key"));
        Assert.Contains(root.Elements("NODE"), n => (string?)n.Attribute("Name") == "Sesión");   // la que había sigue

        // Y la copia se vuelve a leer como colección, con sus marcadores.
        var relectura = LectorRekordbox.Leer(destino);
        Assert.Equal(2, relectura.Pistas.Count);
        Assert.Equal(2, relectura.Listas);
        Assert.All(relectura.Pistas, p => Assert.Equal(1, p.HotCues));
    }

    // Un comentario de varias líneas (Comments="x&#10;y") se escribía con el salto tal cual, y al releer
    // el XML un salto dentro de un atributo se convierte en espacio: la copia lo dejaba en «x y».
    [Fact]
    public void La_copia_reparada_conserva_los_saltos_de_linea_de_los_atributos()
    {
        var vieja = Path.Combine(_dir, "Vieja", "Tema.mp3");
        var nueva = Archivo(Path.Combine("Nueva", "Tema.mp3"));
        var origen = Path.Combine(_dir, "collection.xml");
        var destino = Path.Combine(_dir, "reparada.xml");
        File.WriteAllText(origen, ColeccionCon(("101", vieja, "Tema"), ("102", Archivo("Quieta.mp3"), "Quieta"))
            .Replace("Comments=\"Pico de la noche\"", "Comments=\"Pico&#10;de la&#13;&#10;noche&#9;fin\""));

        var r = RekordboxRelocator.Repair(origen, destino, new Dictionary<string, string> { [vieja] = nueva });

        Assert.True(r.Ok, r.Error);
        var comentarios = XDocument.Load(destino).Root!.Element("COLLECTION")!.Elements("TRACK").Select(t => (string?)t.Attribute("Comments")).ToList();
        Assert.All(comentarios, c => Assert.Equal("Pico\nde la\r\nnoche\tfin", c));   // la reparada y la que no se toca
    }

    // Dos reparaciones al mismo destino ya no chocan en el mismo temporal abierto en exclusiva.
    [Fact]
    public void Otra_reparacion_al_mismo_destino_no_la_hace_fallar()
    {
        var vieja = Path.Combine(_dir, "Vieja", "Tema.mp3");
        var nueva = Archivo(Path.Combine("Nueva", "Tema.mp3"));
        var origen = Path.Combine(_dir, "collection.xml");
        var destino = Path.Combine(_dir, "reparada.xml");
        File.WriteAllText(origen, ColeccionCon(("101", vieja, "Tema")));

        // Lo que dejaba abierto en exclusiva la otra reparación.
        using (new FileStream(destino + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var r = RekordboxRelocator.Repair(origen, destino, new Dictionary<string, string> { [vieja] = nueva });
            Assert.True(r.Ok, r.Error);
            Assert.Equal(1, r.Reparadas);
            Assert.Equal(1, r.SoloMovidas);   // mismo nombre, otra carpeta
        }
        Assert.Equal(RekordboxRelocator.PathToLocation(nueva), LectorRekordbox.Leer(destino).Pistas.Single().Location);
    }

    [Fact]
    public void Sin_reparadas_no_se_anade_lista()
    {
        var quieta = Archivo("Quieta.mp3");
        var origen = Path.Combine(_dir, "collection.xml");
        var destino = Path.Combine(_dir, "reparada.xml");
        File.WriteAllText(origen, ColeccionCon(("1", quieta, "Quieta")));

        var r = RekordboxRelocator.Repair(origen, destino, new Dictionary<string, string> { [Path.Combine(_dir, "x.mp3")] = quieta }, conLista: true);

        Assert.True(r.Ok);
        Assert.Equal(0, r.Reparadas);
        var root = XDocument.Load(destino).Root!.Element("PLAYLISTS")!.Element("NODE")!;
        Assert.Equal("1", (string?)root.Attribute("Count"));
        Assert.DoesNotContain(root.Elements("NODE"), n => (string?)n.Attribute("Name") == RekordboxRelocator.ListaReparadas);
    }

    [Fact]
    public void Una_coleccion_sin_listas_recibe_la_suya()
    {
        var vieja = Path.Combine(_dir, "pista01.mp3");
        var nueva = Archivo("Bad Bunny - Titi Me Pregunto.mp3");
        var origen = Path.Combine(_dir, "collection.xml");
        var destino = Path.Combine(_dir, "reparada.xml");
        File.WriteAllText(origen, $"<DJ_PLAYLISTS Version=\"1.0.0\"><COLLECTION Entries=\"1\"><TRACK TrackID=\"7\" Name=\"pista01\" Location=\"{RekordboxRelocator.PathToLocation(vieja)}\"/></COLLECTION></DJ_PLAYLISTS>");

        var r = RekordboxRelocator.Repair(origen, destino, new Dictionary<string, string> { [vieja] = nueva }, conLista: true);

        Assert.Equal(1, r.Reparadas);
        var doc = XDocument.Load(destino);
        // Renombrada: el Name pasa al nombre nuevo, como antes.
        Assert.Equal("Bad Bunny - Titi Me Pregunto", (string?)doc.Root!.Element("COLLECTION")!.Element("TRACK")!.Attribute("Name"));
        var lista = doc.Root!.Element("PLAYLISTS")!.Element("NODE")!.Element("NODE")!;
        Assert.Equal(RekordboxRelocator.ListaReparadas, (string?)lista.Attribute("Name"));
        Assert.Equal("7", (string?)lista.Element("TRACK")!.Attribute("Key"));
    }

    // Name es el TÍTULO en rekordbox: con el título de la etiqueta (de la biblioteca), no entra el nombre
    // del archivo con el artista delante. Si solo se movió de carpeta, se respeta el que tenía.
    [Fact]
    public void Al_reparar_un_renombrado_se_usa_el_titulo_de_la_etiqueta()
    {
        var vieja = Path.Combine(_dir, "pista01.mp3");
        var nueva = Archivo("Bad Bunny - Titi Me Pregunto.mp3");
        var movidaAntes = Path.Combine(_dir, "Antes", "Quieta.mp3");
        var movida = Archivo(Path.Combine("Despues", "Quieta.mp3"));
        var origen = Path.Combine(_dir, "collection.xml");
        var destino = Path.Combine(_dir, "reparada.xml");
        File.WriteAllText(origen, ColeccionCon(("7", vieja, "pista01"), ("8", movidaAntes, "Quieta (Edit)")));

        var r = RekordboxRelocator.Repair(origen, destino, new Dictionary<string, string> { [vieja] = nueva, [movidaAntes] = movida },
                                          tituloDe: ruta => ComparadorRutas.Instancia.Equals(ruta, nueva) ? "Tití Me Preguntó" : "Otro título");

        Assert.Equal(2, r.Reparadas);
        Assert.Equal(1, r.SoloMovidas);   // «Quieta.mp3» solo cambió de carpeta; la otra se renombró
        var pistas = XDocument.Load(destino).Root!.Element("COLLECTION")!.Elements("TRACK").ToList();
        Assert.Equal("Tití Me Preguntó", (string?)pistas.Single(t => (string?)t.Attribute("TrackID") == "7").Attribute("Name"));
        Assert.Equal("Quieta (Edit)", (string?)pistas.Single(t => (string?)t.Attribute("TrackID") == "8").Attribute("Name"));
    }

    // Antes de exportar encima de un archivo se mira qué es: una exportación anterior de BeatTag se
    // sustituye sin más; la colección de rekordbox (o cualquier otra cosa) pide confirmación.
    [Fact]
    public void Se_distingue_una_exportacion_de_BeatTag_de_la_coleccion()
    {
        var nuestra = Path.Combine(_dir, "BeatTag.xml");
        Assert.True(ExportadorRekordbox.Escribir(nuestra, new[] { new ListaParaRekordbox("", "L", new[] { Path.Combine(_dir, "uno.mp3") }) }, Datos).Ok);
        var coleccion = Path.Combine(_dir, "collection.xml");
        File.WriteAllText(coleccion, ColeccionCon(("1", Path.Combine(_dir, "a.mp3"), "a")));
        var otro = Archivo("notas.xml", "<notas/>");
        var roto = Archivo("roto.xml", "esto no es XML");

        Assert.Equal(DestinoExportacion.DeBeatTag, ExportadorRekordbox.QueHayEn(nuestra));
        Assert.Equal(DestinoExportacion.Otro, ExportadorRekordbox.QueHayEn(coleccion));
        Assert.Equal(DestinoExportacion.Otro, ExportadorRekordbox.QueHayEn(otro));
        Assert.Equal(DestinoExportacion.Otro, ExportadorRekordbox.QueHayEn(roto));
        Assert.Equal(DestinoExportacion.NoExiste, ExportadorRekordbox.QueHayEn(Path.Combine(_dir, "nuevo.xml")));
        // Y lo que escribe BeatTag lo reconoce la página como exportación, no como colección.
        Assert.StartsWith(ExportadorRekordbox.CarpetaRaiz + " ", LectorRekordbox.Leer(nuestra).Producto);
    }

    [Fact]
    public void No_se_puede_guardar_encima_de_la_exportada()
    {
        var origen = Path.Combine(_dir, "collection.xml");
        File.WriteAllText(origen, ColeccionCon(("1", Path.Combine(_dir, "a.mp3"), "a")));
        var antes = File.ReadAllText(origen);

        var r = RekordboxRelocator.Repair(origen, origen, new Dictionary<string, string> { [Path.Combine(_dir, "a.mp3")] = Archivo("b.mp3") });

        Assert.False(r.Ok);
        Assert.Contains("archivo nuevo", r.Error);
        Assert.Equal(antes, File.ReadAllText(origen));
    }

    [Fact]
    public void Un_xml_roto_no_deja_un_destino_a_medias()
    {
        var origen = Path.Combine(_dir, "collection.xml");
        var destino = Path.Combine(_dir, "reparada.xml");
        File.WriteAllText(origen, ColeccionCon(("1", Path.Combine(_dir, "a.mp3"), "a"))[..300]);

        var r = RekordboxRelocator.Repair(origen, destino, new Dictionary<string, string> { [Path.Combine(_dir, "a.mp3")] = Archivo("b.mp3") });

        Assert.False(r.Ok);
        Assert.Contains("rekordbox", r.Error);
        Assert.False(File.Exists(destino));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp", SearchOption.AllDirectories));
    }

    // ---------------------------------------------------------------------------------------------
    // P11: exportar las listas de BeatTag
    // ---------------------------------------------------------------------------------------------

    private static DatosPistaRekordbox Datos(string ruta) => Path.GetFileNameWithoutExtension(ruta) switch
    {
        "uno" => new DatosPistaRekordbox("Uno", "Artista 1"),
        "dos" => new DatosPistaRekordbox("Dos", "Artista 2"),
        _ => new DatosPistaRekordbox("", ""),
    };

    private static XDocument Exportar(IReadOnlyList<ListaParaRekordbox> listas, IReadOnlyDictionary<string, string>? locations,
                                      out ResultadoExportacionRekordbox r)
    {
        using var ms = new MemoryStream();
        r = ExportadorRekordbox.Escribir(ms, listas, Datos, locations);
        ms.Position = 0;
        return XDocument.Load(ms);
    }

    [Fact]
    public void Exporta_la_carpeta_BeatTag_con_sus_listas()
    {
        var uno = Path.Combine(_dir, "uno.mp3");
        var dos = Path.Combine(_dir, "dos.mp3");
        var tres = Path.Combine(_dir, "tres.mp3");
        var listas = new[]
        {
            new ListaParaRekordbox("Colecciones", "House vocal", new[] { uno, dos }),
            new ListaParaRekordbox("Colecciones", "House vocal", new[] { dos }),                 // mismo nombre
            new ListaParaRekordbox("Colecciones", "Vacía", Array.Empty<string>()),               // no se escribe
            new ListaParaRekordbox("Tendencias y playlists", "Top 50 de España", new[] { tres, uno, tres }),
            new ListaParaRekordbox("", "Pendientes conseguidas", new[] { dos }),
        };

        var doc = Exportar(listas, null, out var r);

        Assert.True(r.Ok);
        Assert.Equal(4, r.Listas);
        Assert.Equal(1, r.Vacias);
        Assert.Equal(3, r.Pistas);

        var coleccion = doc.Root!.Element("COLLECTION")!;
        Assert.Equal("3", (string?)coleccion.Attribute("Entries"));
        var pistas = coleccion.Elements("TRACK").ToList();
        Assert.Equal(3, pistas.Count);                                  // cada archivo, una vez
        var t1 = pistas.Single(t => (string?)t.Attribute("Name") == "Uno");
        Assert.Equal("Artista 1", (string?)t1.Attribute("Artist"));
        Assert.Equal(RekordboxRelocator.PathToLocation(uno), (string?)t1.Attribute("Location"));
        // Solo lo imprescindible: nada que pueda pisar lo que rekordbox ya tiene de la pista.
        Assert.Equal(new[] { "TrackID", "Name", "Artist", "Location" }, t1.Attributes().Select(a => a.Name.LocalName));
        Assert.Empty(t1.Elements());
        // Sin título, el nombre del archivo, para que no entre en blanco.
        Assert.Contains(pistas, t => (string?)t.Attribute("Name") == "tres");

        var root = doc.Root!.Element("PLAYLISTS")!.Element("NODE")!;
        Assert.Equal("ROOT", (string?)root.Attribute("Name"));
        Assert.Equal("1", (string?)root.Attribute("Count"));
        var beattag = root.Element("NODE")!;
        Assert.Equal(ExportadorRekordbox.CarpetaRaiz, (string?)beattag.Attribute("Name"));
        Assert.Equal("0", (string?)beattag.Attribute("Type"));
        Assert.Equal("3", (string?)beattag.Attribute("Count"));         // dos carpetas y una lista suelta

        var colecciones = beattag.Elements("NODE").Single(n => (string?)n.Attribute("Name") == "Colecciones");
        Assert.Equal(new[] { "House vocal", "House vocal (2)" }, colecciones.Elements("NODE").Select(n => (string?)n.Attribute("Name")));
        Assert.Equal("2", (string?)colecciones.Attribute("Count"));

        // Las listas nombran las pistas por su TrackID y en su orden, sin repetir.
        var ids = pistas.ToDictionary(t => (string)t.Attribute("TrackID")!, t => (string)t.Attribute("Location")!);
        var top = beattag.Descendants("NODE").Single(n => (string?)n.Attribute("Name") == "Top 50 de España");
        Assert.Equal("1", (string?)top.Attribute("Type"));
        Assert.Equal("0", (string?)top.Attribute("KeyType"));
        Assert.Equal("2", (string?)top.Attribute("Entries"));
        Assert.Equal(new[] { RekordboxRelocator.PathToLocation(tres), RekordboxRelocator.PathToLocation(uno) },
                     top.Elements("TRACK").Select(t => ids[(string)t.Attribute("Key")!]));

        Assert.Contains(beattag.Elements("NODE"), n => (string?)n.Attribute("Name") == "Pendientes conseguidas" && (string?)n.Attribute("Type") == "1");
    }

    // Si se conoce la Location que la pista ya tiene en rekordbox (porque se abrió su colección), se usa esa.
    [Fact]
    public void Usa_la_location_que_ya_tiene_rekordbox()
    {
        var uno = Path.Combine(_dir, "uno.mp3");
        var suya = "file://localhost/" + uno.Replace('\\', '/').TrimStart('/').Replace(" ", "%20") + "?como-rekordbox";
        var doc = Exportar(new[] { new ListaParaRekordbox("", "L", new[] { uno }) },
                           new Dictionary<string, string>(ComparadorRutas.Instancia) { [uno.ToUpperInvariant()] = suya }, out _);
        Assert.Equal(suya, (string?)doc.Root!.Element("COLLECTION")!.Element("TRACK")!.Attribute("Location"));
    }

    // Lo exportado se puede volver a leer como una colección de rekordbox.
    [Fact]
    public void Lo_exportado_se_lee_como_coleccion()
    {
        var uno = Path.Combine(_dir, "Canción (Extended), Remix & Más.mp3");
        var destino = Path.Combine(_dir, "BeatTag para rekordbox.xml");

        var r = ExportadorRekordbox.Escribir(destino, new[] { new ListaParaRekordbox("Colecciones", "Línea\nrota", new[] { uno }) }, Datos);

        Assert.True(r.Ok, r.Error);
        var c = LectorRekordbox.Leer(destino);
        Assert.Equal(uno, Assert.Single(c.Pistas).Ruta);
        Assert.Equal(1, c.Listas);
        Assert.Contains("Name=\"Línea rota\"", File.ReadAllText(destino));   // sin saltos de línea en el nombre
    }

    [Fact]
    public void Sin_canciones_no_se_escribe_nada()
    {
        var destino = Path.Combine(_dir, "vacio.xml");
        var r = ExportadorRekordbox.Escribir(destino, new[] { new ListaParaRekordbox("", "Vacía", Array.Empty<string>()) }, Datos);
        Assert.False(r.Ok);
        Assert.False(File.Exists(destino));
        Assert.False(File.Exists(destino + ".tmp"));
    }
}
