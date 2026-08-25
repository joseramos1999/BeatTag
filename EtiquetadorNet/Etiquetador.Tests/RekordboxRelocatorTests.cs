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
            var vieja = @"D:\Musica\pista01.mp3";
            var nueva = @"D:\Musica\Bad Bunny - Titi Me Pregunto.mp3";
            File.WriteAllText(origen, XmlCon(vieja, @"D:\Musica\otra.mp3"));

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
}
