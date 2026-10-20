using System.Text;
using System.Xml.Linq;
using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// P3: leer la colección exportada de rekordbox en streaming y cruzarla con la biblioteca: qué falta
/// por importar, qué entradas están rotas (y cómo repararlas), qué no está preparado y qué no se ha
/// pinchado nunca.
/// </summary>
public class RekordboxCruceTests
{
    // ---------------------------------------------------------------------------------------------
    // Lectura
    // ---------------------------------------------------------------------------------------------

    private const string Coleccion = """
    <?xml version="1.0" encoding="UTF-8"?>
    <DJ_PLAYLISTS Version="1.0.0">
      <PRODUCT Name="rekordbox" Version="7.2.17" Company="AlphaTheta"/>
      <COLLECTION Entries="4">
        <TRACK TrackID="11" Name="Gasolina" Artist="Daddy Yankee" Size="9876543" TotalTime="192"
               AverageBpm="96.00" DateAdded="2025-03-02" PlayCount="7" Rating="204" Tonality="Am"
               Location="file://localhost/D:/Musica/Daddy%20Yankee%20-%20Gasolina.mp3">
          <TEMPO Inizio="0.250" Bpm="96.00" Metro="4/4" Battito="1"/>
          <TEMPO Inizio="60.250" Bpm="96.00" Metro="4/4" Battito="1"/>
          <POSITION_MARK Name="Intro" Type="0" Start="0.250" Num="0"/>
          <POSITION_MARK Name="Drop" Type="0" Start="45.250" Num="1"/>
          <POSITION_MARK Name="" Type="0" Start="80.000" Num="-1"/>
        </TRACK>
        <TRACK TrackID="12" Name="Auto" Artist="X" Size="100" TotalTime="200" PlayCount="0" DateAdded="2026-08-01"
               Location="file://localhost/D:/Musica/Auto.mp3">
          <TEMPO Inizio="0.500" Bpm="120.00" Metro="4/4" Battito="3"/>
          <POSITION_MARK Name="1.1Bars" Type="0" Start="1.500" Num="0" Red="255" Green="55" Blue="111"/>
        </TRACK>
        <TRACK TrackID="13" Name="Sin nada" Artist="Y" Location="file://localhost/D:/Musica/Sin%20nada.mp3"/>
        <TRACK TrackID="14" Name="De streaming" Artist="Z" Location="https://www.beatport.com/track/x/1"/>
      </COLLECTION>
      <PLAYLISTS>
        <NODE Type="0" Name="ROOT" Count="1">
          <NODE Name="Sesión" Type="1" KeyType="0" Entries="1">
            <TRACK Key="11"/>
          </NODE>
        </NODE>
      </PLAYLISTS>
    </DJ_PLAYLISTS>
    """;

    [Fact]
    public void Lee_los_datos_de_cada_pista()
    {
        var c = LectorRekordbox.LeerTexto(Coleccion);

        Assert.Equal("rekordbox 7.2.17", c.Producto);
        Assert.Equal(1, c.Listas);
        Assert.Equal(1, c.SinArchivo);            // la de streaming no tiene archivo que cruzar
        Assert.Equal(3, c.Pistas.Count);          // los TRACK de las listas no son pistas

        var g = c.Pistas[0];
        Assert.Equal("11", g.TrackId);
        Assert.Equal(@"D:\Musica\Daddy Yankee - Gasolina.mp3", g.Ruta);
        Assert.Equal("file://localhost/D:/Musica/Daddy%20Yankee%20-%20Gasolina.mp3", g.Location);   // tal cual
        Assert.Equal(9876543, g.Tamano);
        Assert.Equal(192, g.Duracion);
        Assert.Equal(7, g.Reproducciones);
        Assert.Equal(new DateTime(2025, 3, 2), g.Alta);
        Assert.Equal(4, g.Valoracion);            // 204 = cuatro estrellas
        Assert.Equal(2, g.HotCues);               // Num 0 y 1
        Assert.Equal(1, g.CuesMemoria);           // Num -1
        Assert.True(g.Beatgrid);
        Assert.False(g.SoloCueInicial);
        Assert.Equal("96.00", g.Bpm);
        Assert.Equal("Am", g.Tonalidad);

        var sinNada = c.Pistas[2];
        Assert.Equal(0, sinNada.HotCues);
        Assert.False(sinNada.Beatgrid);
        Assert.Null(sinNada.Alta);
        Assert.Equal(0, sinNada.Tamano);
    }

    // El caso real: los 14.937 hot cues de una colección de 19.690 pistas eran uno por pista, en la
    // ranura A y en el primer tiempo del beatgrid. Eso lo deja el análisis, no quien prepara la pista.
    [Fact]
    public void Un_unico_hot_cue_en_el_primer_tiempo_de_compas_es_el_automatico()
    {
        var auto = LectorRekordbox.LeerTexto(Coleccion).Pistas[1];

        // Beatgrid en el tercer tiempo a 120 BPM: el primero de compás llega dos tiempos (1 s) después.
        Assert.Equal(1.5, auto.PrimerCompas!.Value, 3);
        Assert.True(auto.SoloCueInicial);
        Assert.True(auto.SinHotCues(contarCueInicial: true));
        Assert.False(auto.SinHotCues(contarCueInicial: false));
    }

    [Fact]
    public void Un_hot_cue_puesto_en_otro_sitio_si_es_preparacion()
    {
        var xml = Coleccion.Replace("Name=\"1.1Bars\" Type=\"0\" Start=\"1.500\"", "Name=\"Drop\" Type=\"0\" Start=\"32.000\"");
        Assert.False(LectorRekordbox.LeerTexto(xml).Pistas[1].SoloCueInicial);
        // En el primer tiempo y sin nombre: por su sitio, se toma por el del análisis (la página lo dice
        // y deja contarlo como preparación con una casilla).
        var enSuSitio = Coleccion.Replace("Name=\"1.1Bars\"", "Name=\"\"");
        Assert.True(LectorRekordbox.LeerTexto(enSuSitio).Pistas[1].SoloCueInicial);
        // En el primer tiempo pero con un nombre puesto por alguien: es un cue de entrada a propósito.
        var conNombre = Coleccion.Replace("Name=\"1.1Bars\"", "Name=\"Entrada\"");
        var p = LectorRekordbox.LeerTexto(conNombre).Pistas[1];
        Assert.False(p.SoloCueInicial);
        Assert.False(CruceRekordbox.SinPreparar(p, sinHotCues: true, sinBeatgrid: false, cueInicialCuenta: false));
    }

    // MEDIDO: 37 de los 14.937 hot cues «1.1Bars» de la colección real quedaban a más de una décima del
    // primer tiempo calculado (su beatgrid empieza de otra forma). Por el nombre, también son el del análisis.
    [Fact]
    public void El_hot_cue_llamado_1_1Bars_es_el_automatico_aunque_el_beatgrid_no_case()
    {
        var xml = Coleccion.Replace("Start=\"1.500\" Num=\"0\" Red", "Start=\"1.180\" Num=\"0\" Red");
        var p = LectorRekordbox.LeerTexto(xml).Pistas[1];
        Assert.Equal("1.1Bars", p.NombreUnicoHotCue);
        Assert.True(p.SoloCueInicial);
        Assert.True(PistaRekordbox.EsNombreAutomatico(" 1.1bars "));
        Assert.False(PistaRekordbox.EsNombreAutomatico("Drop"));
        // Con más marcadores ya no es «solo» el automático.
        Assert.False(LectorRekordbox.LeerTexto(xml.Replace("Num=\"0\" Red=\"255\" Green=\"55\" Blue=\"111\"/>",
                     "Num=\"0\" Red=\"255\" Green=\"55\" Blue=\"111\"/><POSITION_MARK Name=\"\" Type=\"0\" Start=\"9.0\" Num=\"-1\"/>")).Pistas[1].SoloCueInicial);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(51, 1)]
    [InlineData(153, 3)]
    [InlineData(255, 5)]
    [InlineData(250, 5)]   // lo que no es exacto, redondeado
    public void Las_estrellas_salen_de_la_escala_de_rekordbox(int valor, int estrellas)
        => Assert.Equal(estrellas, LectorRekordbox.Estrellas(valor));

    // Antes un XML roto se tragaba y la importación decía «0 coincidencias».
    [Fact]
    public void Un_xml_roto_se_dice_con_la_linea()
    {
        var e = Assert.Throws<RekordboxXmlException>(() => LectorRekordbox.LeerTexto("<DJ_PLAYLISTS><COLLECTION><TRACK Location=\"x\" </COLLECTION></DJ_PLAYLISTS>"));
        Assert.Contains("línea", e.Message);
        Assert.Contains("Exportar colección en formato xml", e.Message);
    }

    [Fact]
    public void Un_archivo_cortado_se_dice_como_incompleto()
    {
        var cortado = Coleccion[..(Coleccion.Length / 2)];
        var e = Assert.Throws<RekordboxXmlException>(() => LectorRekordbox.LeerTexto(cortado));
        Assert.Contains("incompleto", e.Message);
    }

    [Fact]
    public void Otro_xml_no_pasa_por_una_coleccion()
    {
        var e = Assert.Throws<RekordboxXmlException>(() => LectorRekordbox.LeerTexto("<?xml version=\"1.0\"?><VBAudioVoicemeeterSettings/>"));
        Assert.Contains("no una colección de rekordbox", e.Message);
        Assert.Contains("VBAudioVoicemeeterSettings", e.Message);

        var sinColeccion = Assert.Throws<RekordboxXmlException>(() => LectorRekordbox.LeerTexto("<DJ_PLAYLISTS Version=\"1.0.0\"><PLAYLISTS/></DJ_PLAYLISTS>"));
        Assert.Contains("COLLECTION", sinColeccion.Message);
    }

    [Fact]
    public void Un_archivo_que_no_existe_se_dice()
    {
        var e = Assert.Throws<RekordboxXmlException>(() => LectorRekordbox.Leer(Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid() + ".xml")));
        Assert.Contains("No se encuentra", e.Message);
    }

    // La importación de BPM y tonalidad usa la misma lectura: el error también le llega.
    [Fact]
    public void La_importacion_ya_no_se_traga_los_errores()
        => Assert.Throws<RekordboxXmlException>(() => RekordboxImporter.ParseContent("<DJ_PLAYLISTS><COLLECTION>"));

    [Fact]
    public void Lee_de_un_archivo_en_disco()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var xml = Path.Combine(dir, "coleccion.xml");
            File.WriteAllText(xml, Coleccion, new UTF8Encoding(false));
            Assert.Equal(3, LectorRekordbox.Leer(xml).Pistas.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------------------------------------------------------------------------------------------
    // Rutas: Unicode de Mac y escapado de rekordbox
    // ---------------------------------------------------------------------------------------------

    // En un Mac, la «ó» puede llegar como un carácter (NFC) o como «o» + acento (NFD). Son el mismo archivo.
    [Fact]
    public void Las_rutas_nfd_de_un_mac_casan_con_las_nfc()
    {
        var nfc = "/Users/dj/Música/Canción.mp3".Normalize(NormalizationForm.FormC);
        var nfd = nfc.Normalize(NormalizationForm.FormD);
        Assert.NotEqual(nfc, nfd);
        Assert.True(ComparadorRutas.Instancia.Equals(nfc, nfd));
        Assert.Equal(ComparadorRutas.Instancia.GetHashCode(nfc), ComparadorRutas.Instancia.GetHashCode(nfd));
        Assert.True(ComparadorRutas.Instancia.Equals(@"E:\Musica\a.mp3", @"e:\MUSICA\A.mp3"));
    }

    [Fact]
    public void Una_location_nfd_se_lee_y_casa_con_la_biblioteca()
    {
        var nfd = "Canción".Normalize(NormalizationForm.FormD);
        var location = "file://localhost/Users/dj/M%c3%basica/" + Uri.EscapeDataString(nfd) + ".mp3";
        var ruta = RekordboxImporter.LocationToPath(location);
        Assert.True(ComparadorRutas.Instancia.Equals("/Users/dj/Música/Canción.mp3", ruta));
    }

    // MEDIDO en una colección real de rekordbox 7.2: con el escapado de manual, 15.018 de 19.690 Location
    // salían distintas de las suyas; con este, idénticas las 19.690. Estos son casos de esa colección.
    [Theory]
    [InlineData(@"E:\Musica\ELECTRONICA\Afro House\DJ Kent, Mo-T, Brenden Praise, MÖRDA - Horns In The Sun (feat. Mo-T, Brenden Praise & Mörda) [Thakzin Remix] The Extended Mix.mp3",
                "file://localhost/E:/Musica/ELECTRONICA/Afro%20House/DJ%20Kent,%20Mo-T,%20Brenden%20Praise,%20M%c3%96RDA%20-%20Horns%20In%20The%20Sun%20(feat.%20Mo-T,%20Brenden%20Praise%20%26%20M%c3%b6rda)%20%5bThakzin%20Remix%5d%20The%20Extended%20Mix.mp3")]
    [InlineData(@"E:\Musica\COMERCIAL\Bachata - Salsa - Merengue\Dimelo Flow - Trago #1 Lololo (Extended).mp3",
                "file://localhost/E:/Musica/COMERCIAL/Bachata%20-%20Salsa%20-%20Merengue/Dimelo%20Flow%20-%20Trago%20#1%20Lololo%20(Extended).mp3")]
    [InlineData(@"E:\Musica\COMERCIAL\Reggaeton1 A-D\Aya Nakamura - 40% (Yaniss X DJ Nurz Remix).mp3",
                "file://localhost/E:/Musica/COMERCIAL/Reggaeton1%20A-D/Aya%20Nakamura%20-%2040%25%20(Yaniss%20X%20DJ%20Nurz%20Remix).mp3")]
    [InlineData(@"E:\Musica\Rock\Guns N' Roses - Sweet Child O' Mine.mp3",
                "file://localhost/E:/Musica/Rock/Guns%20N%27%20Roses%20-%20Sweet%20Child%20O%27%20Mine.mp3")]
    public void Escapa_como_rekordbox(string ruta, string location)
    {
        Assert.Equal(location, RekordboxRelocator.PathToLocation(ruta));
        Assert.Equal(ruta.Replace('\\', '/'), RekordboxImporter.LocationToPath(location).Replace('\\', '/'));
    }

    // ---------------------------------------------------------------------------------------------
    // Cruce
    // ---------------------------------------------------------------------------------------------

    /// <summary>Un disco de mentira: lo que existe, su tamaño y qué unidades están conectadas.</summary>
    private sealed class DiscoFalso : IArchivosCruce
    {
        public Dictionary<string, long> Archivos { get; } = new(ComparadorRutas.Instancia);
        public HashSet<string> Desconectadas { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int ConsultasTamano;
        public bool Existe(string ruta) => Archivos.ContainsKey(ruta);
        public bool DiscoDisponible(string ruta) => !Desconectadas.Any(d => ruta.StartsWith(d, StringComparison.OrdinalIgnoreCase));
        public long Tamano(string ruta) { ConsultasTamano++; return Archivos.TryGetValue(ruta, out var t) ? t : -1; }
    }

    private static string R(params string[] partes) => Path.Combine(new[] { Raiz }.Concat(partes).ToArray());
    private static readonly string Raiz = OperatingSystem.IsWindows() ? @"E:\Musica" : "/Volumes/Musica";

    private static Track Cancion(string ruta, int duracion = 200, string titulo = "", string artista = "")
        => new() { FilePath = ruta, DurationSeconds = duracion, Title = titulo, Artist = artista };

    private static PistaRekordbox Pista(string ruta, long tamano = 0, int duracion = 0, string id = "")
        => new() { Ruta = ruta, Location = RekordboxRelocator.PathToLocation(ruta), Tamano = tamano, Duracion = duracion, TrackId = id, Nombre = Path.GetFileNameWithoutExtension(ruta) };

    private static ColeccionRekordbox Col(params PistaRekordbox[] p) => new() { Pistas = p };

    [Fact]
    public void Lo_que_falta_en_rekordbox_y_lo_que_esta()
    {
        var a = Cancion(R("A.mp3"));
        var b = Cancion(R("B.mp3"));
        var disco = new DiscoFalso();
        disco.Archivos[R("Otra carpeta", "C.mp3")] = 1;

        var r = CruceRekordbox.Cruzar(Col(Pista(R("A.mp3")), Pista(R("Otra carpeta", "C.mp3"))), new[] { a, b },
                                      new Dictionary<string, string>(), disco);

        Assert.Equal(new[] { b }, r.FaltanEnRekordbox);           // (a)
        Assert.Equal(1, r.EnBiblioteca);
        Assert.Equal(1, r.FueraDeBiblioteca);                    // existe, pero no es de la biblioteca
        Assert.Same(a, r.Presentes.Single(p => p.Cancion != null).Cancion);
        Assert.Empty(r.Rotas);
    }

    // MEDIDO en una colección real: 3.378 de las 4.385 que salían como «no están en rekordbox» eran el
    // archivo de una entrada rota. Guardar esa tabla e importarla antes de reparar creaba otros tantos
    // duplicados sin cue points, y en el cruce siguiente esas entradas ya no se reparaban.
    [Fact]
    public void Lo_que_rekordbox_tiene_con_una_ruta_antigua_no_cuenta_como_que_falta()
    {
        var disco = new DiscoFalso();
        var esta = Cancion(R("Esta.mp3"));
        var movida = Cancion(R("Nueva", "Movida.mp3"), duracion: 180);                 // reparable
        var compartida = Cancion(R("Nueva", "Compartida.mp3"), duracion: 200);         // dos entradas rotas la apuntan
        var copia1 = Cancion(R("X", "Repetida.mp3"), duracion: 150);                   // varias candidatas
        var copia2 = Cancion(R("Y", "Repetida.mp3"), duracion: 150);
        var yaEsta = Cancion(R("Nueva", "YaEsta.mp3"), duracion: 120);                // rekordbox ya la tiene con su ruta nueva
        var nueva = Cancion(R("Descargas", "Nueva.mp3"));                               // de verdad no está
        var mismoNombreOtraCancion = Cancion(R("Z", "Intro.mp3"), duracion: 30);        // otro tema con el mismo nombre

        var compartidaA = Pista(R("A", "Compartida.mp3"), duracion: 200) with { HotCues = 1 };
        var compartidaB = Pista(R("B", "Compartida.mp3"), duracion: 200) with { HotCues = 4, Beatgrid = true };
        var col = Col(
            Pista(esta.FilePath),
            Pista(R("Vieja", "Movida.mp3"), duracion: 180),
            compartidaA, compartidaB,
            Pista(R("Vieja", "Repetida.mp3"), duracion: 150),
            Pista(R("Vieja", "YaEsta.mp3"), duracion: 120), Pista(yaEsta.FilePath, duracion: 120),
            Pista(R("Vieja", "Intro.mp3"), duracion: 95));

        var r = CruceRekordbox.Cruzar(col, new[] { esta, movida, compartida, copia1, copia2, yaEsta, nueva, mismoNombreOtraCancion },
                                      new Dictionary<string, string>(), disco);

        Assert.Equal(new[] { nueva, mismoNombreOtraCancion }, r.FaltanEnRekordbox);
        Assert.Equal(new[] { movida, compartida, copia1, copia2 }, r.ConRutaAntigua);
        Assert.Equal(R("Vieja", "Movida.mp3"), r.EntradaAntigua[movida.FilePath].Ruta);
        Assert.Same(compartidaB, r.EntradaAntigua[compartida.FilePath]);              // de las dos, la más preparada
        Assert.Equal(R("Vieja", "Repetida.mp3"), r.EntradaAntigua[copia2.FilePath].Ruta);
        Assert.False(r.EntradaAntigua.ContainsKey(yaEsta.FilePath));                   // está con su ruta de hoy
        Assert.Equal(new[] { copia1.FilePath, copia2.FilePath },
                     r.Rotas.Single(x => x.Como == ComoReparar.VariasCandidatas).Candidatas);
    }

    // Las que solo cambiaron de carpeta las puede reubicar el propio rekordbox, sin crear pistas nuevas.
    [Fact]
    public void Se_distinguen_las_que_solo_cambiaron_de_carpeta()
    {
        var movida = Cancion(R("Nueva", "Tema.mp3"));
        var renombrada = Cancion(R("Artista - Tema.mp3"));
        var col = Col(Pista(R("Vieja", "Tema.mp3"), duracion: 200), Pista(R("pista01.mp3"), duracion: 200));
        var r = CruceRekordbox.Cruzar(col, new[] { movida, renombrada },
                                      new Dictionary<string, string> { [R("pista01.mp3")] = renombrada.FilePath }, new DiscoFalso());

        Assert.True(r.Rotas.Single(x => x.NuevaRuta == movida.FilePath).MismoNombre);
        Assert.False(r.Rotas.Single(x => x.NuevaRuta == renombrada.FilePath).MismoNombre);
        Assert.Equal(1, r.SoloMovidas);
        // Sin reparación posible no cuenta, aunque se sepa adónde apunta.
        Assert.False(new PistaRota(Pista(R("Vieja", "Tema.mp3")), ComoReparar.YaEnRekordbox, movida.FilePath).MismoNombre);
    }

    [Fact]
    public void Una_ruta_en_otra_forma_unicode_no_cuenta_como_que_falta()
    {
        var nfc = R("Canción.mp3").Normalize(NormalizationForm.FormC);
        var r = CruceRekordbox.Cruzar(Col(Pista(nfc.Normalize(NormalizationForm.FormD))), new[] { Cancion(nfc) },
                                      new Dictionary<string, string>(), new DiscoFalso());
        Assert.Empty(r.FaltanEnRekordbox);
        Assert.Empty(r.Rotas);
        Assert.Equal(1, r.EnBiblioteca);
    }

    [Fact]
    public void Las_rotas_se_emparejan_por_historial_nombre_y_tamano_o_nombre_y_duracion()
    {
        var disco = new DiscoFalso();
        // 1. Renombrada por BeatTag.
        var renombrada = Cancion(R("Bad Bunny - Titi Me Pregunto.mp3"));
        // 2. Movida de carpeta, mismo tamaño.
        var movida = Cancion(R("Nueva", "Movida.mp3"), duracion: 180);
        disco.Archivos[movida.FilePath] = 5_000_000;
        // 3. Movida y con las etiquetas reescritas: otro tamaño, misma duración (con 1 s de margen).
        var retocada = Cancion(R("Nueva", "Retocada.mp3"), duracion: 201);
        disco.Archivos[retocada.FilePath] = 7_000_123;
        // 4. Dos copias con el mismo nombre y la misma duración: no se elige.
        var copia1 = Cancion(R("X", "Repetida.mp3"), duracion: 150);
        var copia2 = Cancion(R("Y", "Repetida.mp3"), duracion: 150);
        // 5. Mismo nombre, pero otra canción (otra duración): no es ella.
        var otra = Cancion(R("Z", "Intro.mp3"), duracion: 30);

        var col = Col(
            Pista(R("pista01.mp3"), duracion: 200),
            Pista(R("Vieja", "Movida.mp3"), tamano: 5_000_000, duracion: 999),
            Pista(R("Vieja", "Retocada.mp3"), tamano: 7_000_000, duracion: 200),
            Pista(R("Vieja", "Repetida.mp3"), duracion: 150),
            Pista(R("Vieja", "Intro.mp3"), duracion: 95),
            Pista(R("Vieja", "Perdida.mp3"), duracion: 95));
        var mapa = new Dictionary<string, string> { [R("pista01.mp3")] = renombrada.FilePath };

        var r = CruceRekordbox.Cruzar(col, new[] { renombrada, movida, retocada, copia1, copia2, otra }, mapa, disco);

        ComoReparar Como(string nombre) => r.Rotas.Single(x => x.Pista.Ruta.EndsWith(nombre, StringComparison.Ordinal)).Como;
        Assert.Equal(ComoReparar.RenombradaPorBeatTag, Como("pista01.mp3"));
        Assert.Equal(ComoReparar.MismoNombreYTamano, Como("Movida.mp3"));
        Assert.Equal(ComoReparar.MismoNombreYDuracion, Como("Retocada.mp3"));
        Assert.Equal(ComoReparar.VariasCandidatas, Como("Repetida.mp3"));
        Assert.Equal(ComoReparar.SinEncontrar, Como("Intro.mp3"));
        Assert.Equal(ComoReparar.SinEncontrar, Como("Perdida.mp3"));
        Assert.Equal(3, r.Reparables);

        var reparacion = CruceRekordbox.MapaReparacion(r.Rotas);
        Assert.Equal(3, reparacion.Count);
        Assert.Equal(retocada.FilePath, reparacion[R("Vieja", "Retocada.mp3")]);
    }

    // Lo que dice el historial manda, pero solo si el archivo está: si se movió después, se busca por nombre.
    [Fact]
    public void Si_el_destino_del_historial_ya_no_existe_se_busca_por_nombre()
    {
        var ahora = Cancion(R("Otra", "Tema.mp3"));
        var mapa = new Dictionary<string, string> { [R("Vieja", "Tema.mp3")] = R("Intermedia", "Tema.mp3") };
        var r = CruceRekordbox.Cruzar(Col(Pista(R("Vieja", "Tema.mp3"), duracion: 200)), new[] { ahora }, mapa, new DiscoFalso());
        var rota = Assert.Single(r.Rotas);
        Assert.Equal(ComoReparar.MismoNombreYDuracion, rota.Como);
        Assert.Equal(ahora.FilePath, rota.NuevaRuta);
    }

    // Repararla hacia un archivo que rekordbox ya tiene dejaría dos entradas del mismo archivo.
    [Fact]
    public void No_repara_hacia_un_archivo_que_rekordbox_ya_tiene()
    {
        var nueva = Cancion(R("Nueva", "Tema.mp3"));
        var col = Col(Pista(R("Vieja", "Tema.mp3"), duracion: 200), Pista(nueva.FilePath, duracion: 200));
        var r = CruceRekordbox.Cruzar(col, new[] { nueva }, new Dictionary<string, string>(), new DiscoFalso());
        var rota = Assert.Single(r.Rotas);
        Assert.Equal(ComoReparar.YaEnRekordbox, rota.Como);
        Assert.False(rota.Reparable);
        Assert.Equal(nueva.FilePath, rota.NuevaRuta);   // se dice dónde está, aunque no se repare
    }

    [Fact]
    public void Dos_entradas_rotas_hacia_el_mismo_archivo_no_se_reparan()
    {
        var nueva = Cancion(R("Nueva", "Tema.mp3"));
        var col = Col(Pista(R("A", "Tema.mp3"), duracion: 200), Pista(R("B", "Tema.mp3"), duracion: 200));
        var r = CruceRekordbox.Cruzar(col, new[] { nueva }, new Dictionary<string, string>(), new DiscoFalso());
        Assert.All(r.Rotas, x => Assert.Equal(ComoReparar.MismoDestino, x.Como));
        Assert.Empty(CruceRekordbox.MapaReparacion(r.Rotas));
    }

    // Un disco externo sin conectar no es una colección rota: basta con conectarlo.
    [Fact]
    public void Con_el_disco_desconectado_no_se_busca_otra_copia()
    {
        var disco = new DiscoFalso();
        var fuera = OperatingSystem.IsWindows() ? @"F:\USB\Tema.mp3" : "/Volumes/USB/Tema.mp3";
        disco.Desconectadas.Add(OperatingSystem.IsWindows() ? @"F:\" : "/Volumes/USB");
        var r = CruceRekordbox.Cruzar(Col(Pista(fuera, duracion: 200)), new[] { Cancion(R("Tema.mp3")) },
                                      new Dictionary<string, string>(), disco);
        Assert.Equal(ComoReparar.DiscoNoConectado, Assert.Single(r.Rotas).Como);
    }

    // Lo que está en la biblioteca existe: no se pregunta al disco por ello (en una colección real, 9.213 consultas menos).
    [Fact]
    public void No_pregunta_al_disco_por_lo_que_ya_esta_en_la_biblioteca()
    {
        var disco = new DiscoFalso();
        var r = CruceRekordbox.Cruzar(Col(Pista(R("A.mp3"))), new[] { Cancion(R("A.mp3")) }, new Dictionary<string, string>(), disco);
        Assert.Equal(0, disco.ConsultasTamano);
        Assert.Equal(1, r.EnBiblioteca);
    }

    [Fact]
    public void Sin_preparar_y_nunca_pinchadas()
    {
        var c = LectorRekordbox.LeerTexto(Coleccion).Pistas;
        var (preparada, auto, sinNada) = (c[0], c[1], c[2]);

        // Por defecto, el hot cue automático no cuenta como preparación.
        Assert.False(CruceRekordbox.SinPreparar(preparada, sinHotCues: true, sinBeatgrid: true, cueInicialCuenta: false));
        Assert.True(CruceRekordbox.SinPreparar(auto, sinHotCues: true, sinBeatgrid: false, cueInicialCuenta: false));
        Assert.False(CruceRekordbox.SinPreparar(auto, sinHotCues: true, sinBeatgrid: false, cueInicialCuenta: true));
        Assert.True(CruceRekordbox.SinPreparar(sinNada, sinHotCues: false, sinBeatgrid: true, cueInicialCuenta: false));
        Assert.False(CruceRekordbox.SinPreparar(auto, sinHotCues: false, sinBeatgrid: true, cueInicialCuenta: false));

        var hoy = new DateTime(2026, 10, 20);
        Assert.False(CruceRekordbox.NuncaPinchada(preparada, 3, hoy));   // se ha pinchado 7 veces
        Assert.True(CruceRekordbox.NuncaPinchada(auto, 2, hoy));         // añadida el 1 de agosto
        Assert.False(CruceRekordbox.NuncaPinchada(auto, 3, hoy));
        Assert.False(CruceRekordbox.NuncaPinchada(sinNada, 0, hoy));     // sin fecha de alta no se afirma
    }

    // ---------------------------------------------------------------------------------------------
    // El disco de verdad
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"E:\Musica\x.mp3", @"E:")]
    [InlineData(@"\\NAS\Musica\Pop\x.mp3", @"\\NAS\Musica")]
    [InlineData("/Volumes/USB DJ/Pop/x.mp3", "/Volumes/USB DJ")]
    [InlineData("/Users/dj/Music/x.mp3", "")]
    public void La_raiz_que_tiene_que_estar_conectada(string ruta, string raiz)
    {
        var r = ArchivosEnDisco.Raiz(ruta);
        if (raiz == "E:") Assert.Equal("E:" + Path.DirectorySeparatorChar, r);
        else Assert.Equal(raiz, r);
    }

    [Fact]
    public void Existe_lista_cada_carpeta_una_vez()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var a = Path.Combine(dir, "Canción (Extended).mp3");
            File.WriteAllText(a, "audio");
            var disco = new ArchivosEnDisco();
            Assert.True(disco.Existe(a));
            Assert.True(disco.Existe(a.Normalize(NormalizationForm.FormD)));   // la otra forma Unicode también
            Assert.False(disco.Existe(Path.Combine(dir, "otra.mp3")));
            Assert.False(disco.Existe(Path.Combine(dir, "no-existe", "x.mp3")));
            Assert.Equal(5, disco.Tamano(a));
            Assert.Equal(-1, disco.Tamano(Path.Combine(dir, "otra.mp3")));
            Assert.True(disco.DiscoDisponible(a));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
