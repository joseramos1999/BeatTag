using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// Reparar la coleccion de rekordbox despues de renombrar. rekordbox guarda los cue points y el
/// beatgrid en su base de datos, ligados a la RUTA: al renombrar un archivo los da por perdidos.
/// Como BeatTag renombra, tiene que saber devolverle las rutas buenas.
/// </summary>
public class RekordboxRelocatorTests
{
    // Ida y vuelta: lo que escribimos, rekordbox tiene que poder leerlo, y nosotros tambien.
    [Theory]
    [InlineData(@"D:\Musica\tema.mp3")]
    [InlineData(@"E:\Musica\COMERCIAL\Reggaeton1 A-D\Bad Bunny - Titi Me Pregunto.mp3")]
    [InlineData(@"C:\Mi Musica\Rosalia - Malamente (Extended).mp3")]
    public void La_ruta_sobrevive_a_la_ida_y_vuelta(string ruta)
    {
        var loc = RekordboxRelocator.PathToLocation(ruta);
        Assert.StartsWith("file://localhost/", loc);
        Assert.Equal(ruta.Replace('\\', '/'), RekordboxImporter.LocationToPath(loc).Replace('\\', '/'));
    }

    // Los acentos y los espacios se escapan, pero la unidad NO: rekordbox espera "D:/", no "D%3A/".
    [Fact]
    public void La_unidad_no_se_escapa_y_lo_demas_si()
    {
        var loc = RekordboxRelocator.PathToLocation(@"D:\Música\Canción con espacios.mp3");
        Assert.StartsWith("file://localhost/D:/", loc);
        Assert.DoesNotContain(" ", loc);
        Assert.Contains("%", loc);        // el acento y los espacios van escapados
    }

    private static string XmlCon(params string[] rutas)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<DJ_PLAYLISTS Version=\"1.0.0\"><COLLECTION>");
        foreach (var r in rutas)
            sb.Append($"<TRACK Name=\"{Path.GetFileNameWithoutExtension(r)}\" Location=\"{RekordboxRelocator.PathToLocation(r)}\" />");
        sb.Append("</COLLECTION></DJ_PLAYLISTS>");
        return sb.ToString();
    }

    [Fact]
    public void Reescribe_la_ruta_de_los_archivos_renombrados()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-rb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var origen = Path.Combine(dir, "collection.xml");
        var destino = Path.Combine(dir, "reparada.xml");
        try
        {
            // Rutas de verdad, y el archivo NUEVO existe: la reparación ya no manda una pista a una
            // ruta que no existe (con los renombrados deshechos eso era perderla, ver
            // No_reescribe_hacia_un_archivo_que_no_existe). Antes la prueba usaba rutas inventadas.
            var vieja = Path.Combine(dir, "pista01.mp3");
            var nueva = Path.Combine(dir, "Bad Bunny - Titi Me Pregunto.mp3");
            File.WriteAllText(nueva, "audio");
            File.WriteAllText(origen, XmlCon(vieja, Path.Combine(dir, "otra.mp3")));

            var res = RekordboxRelocator.Repair(origen, destino,
                new Dictionary<string, string> { [vieja] = nueva });

            Assert.True(res.Ok);
            Assert.Equal(1, res.Reparadas);

            var xml = File.ReadAllText(destino);
            Assert.Contains(RekordboxRelocator.PathToLocation(nueva), xml);
            Assert.DoesNotContain(RekordboxRelocator.PathToLocation(vieja), xml);
            // El nombre visible tambien se actualiza, o la coleccion apunta bien pero se lee mal.
            Assert.Contains("Bad Bunny - Titi Me Pregunto", xml);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Guarda: el original NO se toca. El usuario reimporta cuando quiere y conserva el de partida.
    [Fact]
    public void No_modifica_el_xml_original()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-rb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var origen = Path.Combine(dir, "collection.xml");
        try
        {
            File.WriteAllText(origen, XmlCon(@"D:\Musica\a.mp3"));
            var antes = File.ReadAllText(origen);

            RekordboxRelocator.Repair(origen, Path.Combine(dir, "out.xml"),
                new Dictionary<string, string> { [@"D:\Musica\a.mp3"] = @"D:\Musica\b.mp3" });

            Assert.Equal(antes, File.ReadAllText(origen));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Sin_renombrados_avisa_en_vez_de_escribir()
    {
        var res = RekordboxRelocator.Repair("cualquiera.xml", "salida.xml", new Dictionary<string, string>());
        Assert.False(res.Ok);
        Assert.False(File.Exists("salida.xml"));
    }

    // ---- Lectura del mapa desde los manifiestos ----

    private static string Manifiesto(string dir, string nombre, params (string Orig, string Final)[] filas)
    {
        var ruta = Path.Combine(dir, nombre);
        var lineas = filas.Select(f =>
            $"{{\"OrigPath\":{System.Text.Json.JsonSerializer.Serialize(f.Orig)}," +
            $"\"FinalPath\":{System.Text.Json.JsonSerializer.Serialize(f.Final)},\"Renamed\":true,\"Fields\":{{}}}}");
        File.WriteAllLines(ruta, lineas);
        return ruta;
    }

    // Lo esencial: si un archivo se renombro DOS veces, rekordbox necesita el salto completo
    // (A -> C), no dos tramos sueltos, porque su coleccion sigue apuntando al nombre original.
    [Fact]
    public void Encadena_los_renombrados_sucesivos()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-mf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Manifiesto(dir, "run_20260101_100000.jsonl", (@"D:\m\A.mp3", @"D:\m\B.mp3"));
            Manifiesto(dir, "run_20260202_100000.jsonl", (@"D:\m\B.mp3", @"D:\m\C.mp3"));

            var mapa = RekordboxRelocator.ReadRenames(dir);

            Assert.Equal(@"D:\m\C.mp3", mapa[@"D:\m\A.mp3"]);
            Assert.Equal(@"D:\m\C.mp3", mapa[@"D:\m\B.mp3"]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Un archivo que acaba donde empezo no necesita que se toque SU entrada; pero si la coleccion
    // llego a apuntar al nombre intermedio, hay que saber devolverla al bueno.
    [Fact]
    public void Lo_que_vuelve_a_su_nombre_original_no_se_reencamina_a_si_mismo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-mf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Manifiesto(dir, "run_20260101_100000.jsonl", (@"D:\m\A.mp3", @"D:\m\B.mp3"));
            Manifiesto(dir, "run_20260202_100000.jsonl", (@"D:\m\B.mp3", @"D:\m\A.mp3"));

            var mapa = RekordboxRelocator.ReadRenames(dir);

            Assert.False(mapa.ContainsKey(@"D:\m\A.mp3"));          // esa no hay que tocarla
            Assert.Equal(@"D:\m\A.mp3", mapa[@"D:\m\B.mp3"]);       // pero el nombre intermedio si
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Sin_carpeta_de_manifiestos_no_rompe()
        => Assert.Empty(RekordboxRelocator.ReadRenames(Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid())));

    // ---- Renombrados que se deshicieron ----

    // Visto en una biblioteca real: run_20260920_230254.deshecho.jsonl tiene 12 renombrados que se
    // deshicieron esa misma noche (23:56). La reparación los seguía leyendo como vigentes y cambiaba
    // la Location de esos temas -que estaban bien en su nombre original- por el nombre nuevo, que ya
    // no existía: rekordbox los perdía con sus cue points.
    [Fact]
    public void Los_renombrados_deshechos_no_se_aplican()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-mf-" + Guid.NewGuid().ToString("N"));
        var undoDir = Path.Combine(dir, "deshacer");
        Directory.CreateDirectory(undoDir);
        try
        {
            var filas = Enumerable.Range(1, 12)
                .Select(i => (Orig: Path.Combine(dir, $"pista{i:00}.mp3"), Final: Path.Combine(dir, $"Artista {i:00} - Tema {i:00}.mp3")))
                .ToArray();
            foreach (var f in filas) File.WriteAllText(f.Orig, "audio");   // de vuelta en su nombre original
            var archivado = Manifiesto(undoDir, "run_20260920_230254.deshecho.jsonl", filas);
            // Como los archivados antiguos: renombrar conservó la fecha de la ejecución.
            File.SetLastWriteTime(archivado, new DateTime(2026, 9, 20, 23, 2, 54));

            var mapa = RekordboxRelocator.ReadRenames(undoDir);

            foreach (var f in filas)
            {
                Assert.False(mapa.ContainsKey(f.Orig));       // la ruta original NO se toca
                Assert.Equal(f.Orig, mapa[f.Final]);          // y si rekordbox llegó a ver la nueva, vuelve a la original
            }

            // La colección exportada apunta a las originales: queda exactamente igual.
            var origen = Path.Combine(dir, "collection.xml");
            File.WriteAllText(origen, XmlCon(filas.Select(f => f.Orig).ToArray()));
            var res = RekordboxRelocator.Repair(origen, Path.Combine(dir, "reparada.xml"), mapa);
            Assert.True(res.Ok);
            Assert.Equal(0, res.Reparadas);
            Assert.Equal(12, res.SinCambio);
            foreach (var f in filas)
                Assert.Contains(RekordboxRelocator.PathToLocation(f.Orig), File.ReadAllText(Path.Combine(dir, "reparada.xml")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Si rekordbox importó el archivo con el nombre nuevo antes del deshacer, esa Location apunta a
    // un archivo que ya no existe: se devuelve al original.
    [Fact]
    public void Una_location_con_el_nombre_deshecho_vuelve_al_original()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-mf-" + Guid.NewGuid().ToString("N"));
        var undoDir = Path.Combine(dir, "deshacer");
        Directory.CreateDirectory(undoDir);
        try
        {
            var orig = Path.Combine(dir, "pista01.mp3");
            var final = Path.Combine(dir, "Bad Bunny - Titi Me Pregunto.mp3");
            File.WriteAllText(orig, "audio");
            Manifiesto(undoDir, "run_20260920_230254.deshecho.jsonl", (orig, final));

            var origen = Path.Combine(dir, "collection.xml");
            var destino = Path.Combine(dir, "reparada.xml");
            File.WriteAllText(origen, XmlCon(final));
            var res = RekordboxRelocator.Repair(origen, destino, RekordboxRelocator.ReadRenames(undoDir));

            Assert.Equal(1, res.Reparadas);
            Assert.Contains(RekordboxRelocator.PathToLocation(orig), File.ReadAllText(destino));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // El orden del deshacer cuenta: A->B (10:00), B->C (11:00); se deshace la de las 11 a las 12:00
    // y la de las 10 a las 12:05. El archivo acaba en A, y tanto B como C tienen que volver a A.
    [Fact]
    public void Las_vueltas_se_colocan_cuando_se_deshicieron()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-mf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "A.mp3"), b = Path.Combine(dir, "B.mp3"), c = Path.Combine(dir, "C.mp3");
            var primera = Manifiesto(dir, "run_20260101_100000.deshecho.jsonl", (a, b));
            var segunda = Manifiesto(dir, "run_20260101_110000.deshecho.jsonl", (b, c));
            File.SetLastWriteTime(segunda, new DateTime(2026, 1, 1, 12, 0, 0));
            File.SetLastWriteTime(primera, new DateTime(2026, 1, 1, 12, 5, 0));

            var mapa = RekordboxRelocator.ReadRenames(dir);

            Assert.False(mapa.ContainsKey(a));
            Assert.Equal(a, mapa[b]);
            Assert.Equal(a, mapa[c]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Deshacer a medias: lo que se pudo revertir va al archivado, lo pendiente sigue en el manifiesto.
    // Lo pendiente sigue vigente; lo revertido, no.
    [Fact]
    public void Un_deshacer_a_medias_solo_anula_lo_revertido()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-mf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a1 = Path.Combine(dir, "pista01.mp3"), b1 = Path.Combine(dir, "Tema 01.mp3");
            string a2 = Path.Combine(dir, "pista02.mp3"), b2 = Path.Combine(dir, "Tema 02.mp3");
            Manifiesto(dir, "run_20261009_210831.jsonl", (a2, b2));            // pendiente
            Manifiesto(dir, "run_20261009_210831.deshecho.jsonl", (a1, b1));   // revertido

            var mapa = RekordboxRelocator.ReadRenames(dir);

            Assert.Equal(b2, mapa[a2]);
            Assert.False(mapa.ContainsKey(a1));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Las fichas de DJ usan el mismo mapa: una ficha que se fue al nombre nuevo vuelve al original
    // cuando el renombrado se deshace.
    [Fact]
    public void Las_fichas_siguen_volviendo_al_nombre_original()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-mf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var orig = Path.Combine(dir, "pista01.mp3");
            var final = Path.Combine(dir, "Bad Bunny - Titi Me Pregunto.mp3");
            File.WriteAllText(orig, "audio");
            Manifiesto(dir, "run_20260920_230254.deshecho.jsonl", (orig, final));

            var fichas = new Etiquetador.Core.Dj.AlmacenFichas(Path.Combine(dir, "fichas.json"));
            fichas.Poner(final, new Etiquetador.Core.Dj.FichaDj { Energia = 8 });

            var movidas = fichas.Reubicar(RekordboxRelocator.ReadRenames(dir), File.Exists);

            Assert.Equal(1, movidas);
            Assert.Equal(8, fichas.Obtener(orig)!.Energia);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Nunca se manda una pista a un archivo que no existe: sería perderla.
    [Fact]
    public void No_reescribe_hacia_un_archivo_que_no_existe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-rb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var vieja = Path.Combine(dir, "pista01.mp3");
            var nueva = Path.Combine(dir, "Bad Bunny - Titi Me Pregunto.mp3");   // tampoco existe
            var origen = Path.Combine(dir, "collection.xml");
            var destino = Path.Combine(dir, "reparada.xml");
            File.WriteAllText(origen, XmlCon(vieja));

            var res = RekordboxRelocator.Repair(origen, destino, new Dictionary<string, string> { [vieja] = nueva });

            Assert.Equal(0, res.Reparadas);
            Assert.Equal(1, res.NoEncontradas);
            Assert.Contains(RekordboxRelocator.PathToLocation(vieja), File.ReadAllText(destino));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Y una pista cuyo archivo sigue en su sitio no se toca, aunque el mapa diga otra cosa.
    [Fact]
    public void No_toca_una_pista_que_sigue_en_su_sitio()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-rb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var vieja = Path.Combine(dir, "pista01.mp3");
            var nueva = Path.Combine(dir, "Bad Bunny - Titi Me Pregunto.mp3");
            File.WriteAllText(vieja, "audio");
            File.WriteAllText(nueva, "otro audio");
            var origen = Path.Combine(dir, "collection.xml");
            var destino = Path.Combine(dir, "reparada.xml");
            File.WriteAllText(origen, XmlCon(vieja));

            var res = RekordboxRelocator.Repair(origen, destino, new Dictionary<string, string> { [vieja] = nueva });

            Assert.Equal(0, res.Reparadas);
            Assert.Equal(1, res.SinCambio);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- macOS ----

    // rekordbox en un Mac escribe «file://localhost/Users/…». Antes salía «file://localhost//Users/…»
    // al escribir y «Users\jose\…» al leer: Importar daba 0 coincidencias y Reparar no encontraba nada.
    [Theory]
    [InlineData("/Users/jose/Music/x.mp3", "file://localhost/Users/jose/Music/x.mp3")]
    [InlineData("/Users/x/Música/tema con espacios.mp3", "file://localhost/Users/x/M%c3%basica/tema%20con%20espacios.mp3")]
    // Los paréntesis y las comas, sin escapar, como los deja rekordbox (ver Escapa_como_rekordbox).
    [InlineData("/Volumes/USB DJ/Bad Bunny - Titi Me Pregunto (Intro).mp3", "file://localhost/Volumes/USB%20DJ/Bad%20Bunny%20-%20Titi%20Me%20Pregunto%20(Intro).mp3")]
    public void Las_rutas_de_macOS_se_escriben_y_se_leen_bien(string ruta, string location)
    {
        Assert.Equal(location, RekordboxRelocator.PathToLocation(ruta));
        Assert.Equal(ruta, RekordboxImporter.LocationToPath(location));
    }

    // Lo que exporta rekordbox en un Mac no siempre escapa igual que nosotros: se lee igual.
    [Fact]
    public void Lee_la_location_de_un_mac_tal_como_la_exporta_rekordbox()
        => Assert.Equal("/Users/jose/Music/Daddy Yankee - Gasolina.mp3",
                        RekordboxImporter.LocationToPath("file://localhost/Users/jose/Music/Daddy%20Yankee%20-%20Gasolina.mp3"));

    // La sintaxis sin host («file:///») también existe.
    [Fact]
    public void Sin_host_tambien_se_entiende()
        => Assert.Equal("/Users/jose/Music/x.mp3", RekordboxImporter.LocationToPath("file:///Users/jose/Music/x.mp3"));

    // En macOS la barra invertida es un carácter más del nombre: no se puede convertir en carpeta.
    [Fact]
    public void En_macOS_la_barra_invertida_es_parte_del_nombre()
    {
        if (OperatingSystem.IsWindows()) return;   // en Windows sí es un separador
        var ruta = "/Users/jose/Music/AC\\DC - Thunderstruck.mp3";
        var loc = RekordboxRelocator.PathToLocation(ruta);
        Assert.Contains("%5c", loc);
        Assert.Equal(ruta, RekordboxImporter.LocationToPath(loc));
    }

    // Unidades de red de Windows: el servidor va como host.
    [WindowsFact]
    public void Las_rutas_de_red_sobreviven_a_la_ida_y_vuelta()
    {
        var ruta = @"\\NAS\Musica\Bad Bunny - Titi Me Pregunto.mp3";
        var loc = RekordboxRelocator.PathToLocation(ruta);
        Assert.Equal("file://NAS/Musica/Bad%20Bunny%20-%20Titi%20Me%20Pregunto.mp3", loc);
        Assert.Equal(ruta, RekordboxImporter.LocationToPath(loc));
    }
}
