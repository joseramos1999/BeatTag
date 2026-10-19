using System.Text.Json;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

public class RekordboxImporterTests
{
    [Fact]
    public void Importacion_es_reversible()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "t.mp3");
            Mp3Fixture.WriteMinMp3(p);

            var log = TagEditor.ImportBpmKey(p, 128, "8A", overwrite: true);
            Assert.True(log.ContainsKey("Bpm") && log.ContainsKey("Key"));
            using (var f0 = TagLib.File.Create(p)) { Assert.Equal("8A", f0.Tag.InitialKey); Assert.Equal((uint)128, f0.Tag.BeatsPerMinute); }

            var man = Path.Combine(dir, "run_rb.jsonl");
            var rec = new UndoRecord { OrigPath = p, FinalPath = p, Renamed = false, Fields = log };
            File.WriteAllText(man, JsonSerializer.Serialize(rec) + "\n");

            var res = new UndoEngine(new AppPaths(dir)).UndoLastRun(man);
            Assert.Equal(1, res.Reverted);
            using var f = TagLib.File.Create(p);
            Assert.Equal((uint)0, f.Tag.BeatsPerMinute);          // BPM revertido
            Assert.True(string.IsNullOrEmpty(f.Tag.InitialKey));  // clave revertida
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }


    private const string Xml = """
    <?xml version="1.0" encoding="UTF-8"?>
    <DJ_PLAYLISTS Version="1.0.0">
      <COLLECTION Entries="2">
        <TRACK TrackID="1" Name="Gasolina" Artist="Daddy Yankee" AverageBpm="94.00" Tonality="Am"
               Location="file://localhost/D:/Musica/Daddy%20Yankee%20-%20Gasolina.mp3"/>
        <TRACK TrackID="2" Name="Sin key" Artist="X" AverageBpm="128.00" Tonality=""
               Location="file://localhost/D:/Musica/X%20-%20Sin%20key.mp3"/>
      </COLLECTION>
    </DJ_PLAYLISTS>
    """;

    [Fact]
    public void Parsea_bpm_clave_y_ruta()
    {
        var list = RekordboxImporter.ParseContent(Xml);
        Assert.Equal(2, list.Count);
        Assert.Equal(@"D:\Musica\Daddy Yankee - Gasolina.mp3", list[0].FilePath);   // URL decodificada
        Assert.Equal("94.00", list[0].Bpm);
        Assert.Equal("Am", list[0].Key);
        Assert.Equal((uint)94, RekordboxImporter.ParseBpm(list[0].Bpm));
    }

    // En un Mac, rekordbox exporta rutas POSIX. Antes salían como «Users\dj\Music\…» -sin la barra
    // inicial y con barras invertidas- y la importación de BPM y clave daba siempre 0 coincidencias.
    private const string XmlMac = """
    <?xml version="1.0" encoding="UTF-8"?>
    <DJ_PLAYLISTS Version="1.0.0">
      <COLLECTION Entries="1">
        <TRACK TrackID="1" Name="Gasolina" Artist="Daddy Yankee" AverageBpm="96.00" Tonality="Fm"
               Location="file://localhost/Users/dj/Music/Daddy%20Yankee%20-%20Gasolina.mp3"/>
      </COLLECTION>
    </DJ_PLAYLISTS>
    """;

    [Fact]
    public void Parsea_las_rutas_de_un_mac()
    {
        var list = RekordboxImporter.ParseContent(XmlMac);
        Assert.Equal("/Users/dj/Music/Daddy Yankee - Gasolina.mp3", Assert.Single(list).FilePath);
    }

    // Y con un archivo de verdad: la ruta leída casa con la de la biblioteca en el sistema donde se
    // ejecute (en la CI de macOS, con una ruta /var/folders/…).
    [Fact]
    public void La_ruta_leida_casa_con_la_del_archivo()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Daddy Yankee - Gasolina.mp3");
            Mp3Fixture.WriteMinMp3(p);
            var xml = $"""<DJ_PLAYLISTS><COLLECTION><TRACK AverageBpm="96.00" Tonality="Fm" Location="{RekordboxRelocator.PathToLocation(p)}"/></COLLECTION></DJ_PLAYLISTS>""";

            var leida = Assert.Single(RekordboxImporter.ParseContent(xml)).FilePath;

            Assert.Equal(p, leida);
            Assert.True(File.Exists(leida));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Escribe_bpm_y_clave_en_el_archivo()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "t.mp3");
            Mp3Fixture.WriteMinMp3(p);
            Assert.True(TagEditor.WriteBpmKey(p, 128, "8A", overwrite: true));
            var v = TagEditor.Read(p);
            Assert.Equal((uint)128, v.Bpm);
            // la clave se guarda en InitialKey (no está en TagValues) -> se relee con TagLib
            using var f = TagLib.File.Create(p);
            Assert.Equal("8A", f.Tag.InitialKey);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
