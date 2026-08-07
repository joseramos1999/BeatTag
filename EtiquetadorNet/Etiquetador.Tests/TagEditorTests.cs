using Etiquetador.Core;

namespace Etiquetador.Tests;

public class TagEditorTests
{
    [Fact]
    public void Escribe_y_relee_los_tags()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "e.mp3");
            Mp3Fixture.WriteMinMp3(p);
            TagEditor.Write(p, "Mi Título", "Artista A, Artista B", "Mi Álbum", "Reggaeton", 2023, 128, "nota");

            var v = TagEditor.Read(p);
            Assert.Equal("Mi Título", v.Title);
            Assert.Equal("Artista A, Artista B", v.Artist);
            Assert.Equal("Mi Álbum", v.Album);
            Assert.Equal("Reggaeton", v.Genre);
            Assert.Equal((uint)2023, v.Year);
            Assert.Equal((uint)128, v.Bpm);
            Assert.Equal("nota", v.Comment);

            // También lo ve el scanner de la biblioteca
            var t = LibraryScanner.ReadTrack(p);
            Assert.Equal("Mi Título", t.Title);
            Assert.Equal((uint)2023, t.Year);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Vacios_limpian_el_tag()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "e.mp3");
            Mp3Fixture.WriteMinMp3(p);
            Mp3Fixture.SetTags(p, "Viejo", "Alguien", "Disco");
            TagEditor.Write(p, "", "", "", "", 0, 0, "");
            var v = TagEditor.Read(p);
            Assert.Equal("", v.Title);
            Assert.Equal("", v.Artist);
            Assert.Equal((uint)0, v.Year);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
