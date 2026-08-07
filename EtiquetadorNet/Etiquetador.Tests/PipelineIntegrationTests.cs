using System.Text.Json;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

// Integración del pipeline de escritura/undo (con MP3 reales) tras el rework de seguridad:
// manifiesto con rutas absolutas + cambios por campo (old + written) + validación de renombrado.
public class PipelineIntegrationTests
{
    private static FieldFlags TitAlbArt => new() { Title = true, Artist = true, Album = true, Year = false, Genre = false, Bpm = false };
    private static FieldFlags TitGen => new() { Title = true, Artist = false, Album = false, Year = false, Genre = true, Bpm = false };

    [Fact]
    public void ApplyTags_captura_valor_anterior_y_escrito()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(p);
            Mp3Fixture.SetTags(p, "VIEJO", "ARTV", "ALBV");
            var info = new ProcessResult { FilePath = p, Title = "NUEVO", Artist = "ARTN", Album = "ALBN", Year = "2020", Genre = "Rock", Bpm = "" };
            var log = Tagging.ApplyTags(info, true, TitAlbArt, null);
            Assert.Equal("VIEJO", log["Title"].OldStr);
            Assert.Equal("NUEVO", log["Title"].NewStr);
            Assert.Equal("ALBV", log["Album"].OldStr);
            Assert.Equal("NUEVO", Mp3Fixture.GetTitle(p));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task ApplyOne_renombra_y_anota_manifiesto_con_rutas_absolutas()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "viejo.mp3");
            Mp3Fixture.WriteMinMp3(p);
            Mp3Fixture.SetTags(p, "VIEJO", "AV", "ALV");
            var info = new ProcessResult { FilePath = p, Old = "viejo.mp3", New = "ArtN - TitN.mp3", Title = "TitN", Artist = "ArtN", Album = "AlbN" };
            var undo = Path.Combine(dir, "run.jsonl");
            var res = await new ApplyEngine().ApplyOneAsync(info, true, TitAlbArt, "keep", null, undo, Path.Combine(dir, "done.log"));
            Assert.True(res is { DidRename: true, TagOk: true, RenOk: true });
            Assert.Equal("ArtN - TitN.mp3", Path.GetFileName(res.FinalPath));

            var lines = File.ReadAllLines(undo).Where(l => l.Trim().Length > 0).ToList();
            Assert.Single(lines);
            var rec = JsonSerializer.Deserialize<UndoRecord>(lines[0])!;
            Assert.True(Path.IsPathRooted(rec.OrigPath));
            Assert.True(Path.IsPathRooted(rec.FinalPath));
            Assert.Equal(p, rec.OrigPath);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Undo_restaura_nombre_y_tags()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "orig name.mp3");
            Mp3Fixture.WriteMinMp3(p);
            Mp3Fixture.SetTags(p, "T VIEJO", "A VIEJO", "AL VIEJO");
            var info = new ProcessResult { FilePath = p, Old = "orig name.mp3", New = "A Nuevo - T Nuevo.mp3", Title = "T Nuevo", Artist = "A Nuevo", Album = "AL Nuevo" };
            var man = Path.Combine(dir, "run_a.jsonl");
            await new ApplyEngine().ApplyOneAsync(info, true, TitAlbArt, "keep", null, man, Path.Combine(dir, "done.log"));

            var res = new UndoEngine(new AppPaths(dir)).UndoLastRun(man);
            Assert.Equal(1, res.Reverted);
            Assert.True(File.Exists(p));
            Assert.Equal("T VIEJO", Mp3Fixture.GetTitle(p));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Undo_conserva_edicion_manual_en_campo_distinto_al_titulo()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "g.mp3");
            Mp3Fixture.WriteMinMp3(p);
            Mp3Fixture.SetTags(p, "T VIEJO", "A", "AL");
            // aplica Título + Género (sin renombrar)
            var info = new ProcessResult { FilePath = p, Old = "g.mp3", New = "g.mp3", Title = "T NUEVO", Genre = "Rock" };
            var man = Path.Combine(dir, "run_g.jsonl");
            await new ApplyEngine().ApplyOneAsync(info, true, TitGen, "keep", null, man, Path.Combine(dir, "done.log"));

            // el usuario cambia MANUALMENTE el género (deja el título igual)
            var v = TagEditor.Read(p);
            TagEditor.Write(p, v.Title, v.Artist, v.Album, "MANUAL", v.Year, v.Bpm, v.Comment);

            var res = new UndoEngine(new AppPaths(dir)).UndoLastRun(man);
            // título se revierte (seguía siendo el escrito); género se conserva (lo cambió el usuario)
            Assert.Equal("T VIEJO", TagEditor.Read(p).Title);
            Assert.Equal("MANUAL", TagEditor.Read(p).Genre);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Undo_ausente_es_missing_y_no_marca_deshecho()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var man = Path.Combine(dir, "run_m.jsonl");
            var rec = new UndoRecord
            {
                OrigPath = Path.Combine(dir, "no_existe.mp3"),
                FinalPath = Path.Combine(dir, "no_existe.mp3"),
                Renamed = true,
                Fields = new() { ["Title"] = FieldChange.Str("x", "y") },
            };
            File.WriteAllText(man, JsonSerializer.Serialize(rec) + "\n");
            var res = new UndoEngine(new AppPaths(dir)).UndoLastRun(man);
            Assert.Equal(1, res.Missing);
            Assert.Equal(0, res.Reverted);
            Assert.False(res.Clean);
            Assert.True(File.Exists(man));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Undo_revierte_cambio_solo_mayus_minus()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Cancion.mp3");
            Mp3Fixture.WriteMinMp3(p);
            var info = new ProcessResult { FilePath = p, Old = "Cancion.mp3", New = "CANCION.mp3", Title = "T" };
            var man = Path.Combine(dir, "run_c.jsonl");
            await new ApplyEngine().ApplyOneAsync(info, true, new FieldFlags { Title = true }, "keep", null, man, Path.Combine(dir, "done.log"));

            var res = new UndoEngine(new AppPaths(dir)).UndoLastRun(man);
            Assert.Equal(1, res.Reverted);
            Assert.Single(Directory.EnumerateFiles(dir, "Cancion.mp3"), f => Path.GetFileName(f) == "Cancion.mp3");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task ApplyOne_rechaza_ruta_con_traversal()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "viejo.mp3");
            Mp3Fixture.WriteMinMp3(p);
            Mp3Fixture.SetTags(p, "VIEJO", null, null);
            var info = new ProcessResult { FilePath = p, Old = "viejo.mp3", New = @"..\evil.mp3", Title = "NUEVO" };
            var res = await new ApplyEngine().ApplyOneAsync(info, true, new FieldFlags { Title = true }, "keep", null, null, Path.Combine(dir, "done.log"));
            Assert.False(res.RenOk);           // renombrado rechazado
            Assert.False(res.DidRename);
            Assert.False(res.TagOk);           // y NO se escribieron tags (abortó antes de tocar nada)
            Assert.Equal("VIEJO", Mp3Fixture.GetTitle(p));   // el título no cambió
            Assert.True(File.Exists(p));        // el archivo NO se movió
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dir)!, "evil.mp3")));   // ni escapó a la carpeta padre
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task ApplyOne_fuerza_la_extension_origen()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "viejo.mp3");
            Mp3Fixture.WriteMinMp3(p);
            var info = new ProcessResult { FilePath = p, Old = "viejo.mp3", New = "Nuevo Nombre.txt", Title = "T" };
            var res = await new ApplyEngine().ApplyOneAsync(info, true, new FieldFlags { Title = true }, "keep", null, null, Path.Combine(dir, "done.log"));
            Assert.True(res.DidRename);
            Assert.Equal(".mp3", Path.GetExtension(res.FinalPath));   // conserva la extensión origen
            Assert.Equal("Nuevo Nombre", Path.GetFileNameWithoutExtension(res.FinalPath));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Undo_migra_manifiesto_de_formato_antiguo()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            // Simula un archivo ya aplicado por una versión anterior: renombrado + título "NUEVO".
            var orig = Path.Combine(dir, "orig.mp3");
            Mp3Fixture.WriteMinMp3(orig);
            Mp3Fixture.SetTags(orig, "NUEVO", null, null);
            var renamed = Path.Combine(dir, "nuevo.mp3");
            File.Move(orig, renamed);

            // Manifiesto en el FORMATO ANTIGUO (new/orig/renamed/tags/wtitle).
            var man = Path.Combine(dir, "run_old.jsonl");
            var rec = new Dictionary<string, object?>
            {
                ["new"] = renamed,
                ["orig"] = "orig.mp3",
                ["renamed"] = true,
                ["tags"] = new Dictionary<string, object?> { ["Title"] = "VIEJO" },
                ["wtitle"] = "NUEVO",
            };
            File.WriteAllText(man, JsonSerializer.Serialize(rec) + "\n");

            var res = new UndoEngine(new AppPaths(dir)).UndoLastRun(man);
            Assert.Equal(1, res.Reverted);
            Assert.True(File.Exists(orig));                 // renombrado de vuelta
            Assert.Equal("VIEJO", Mp3Fixture.GetTitle(orig));   // tag restaurado
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
