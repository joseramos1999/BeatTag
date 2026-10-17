using System.Text.Json;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// N11 · La carátula se sustituía aunque «Sobrescribir» estuviera desactivado, se llevaba por delante
/// TODAS las imágenes del archivo y deshacer no la devolvía. Escenario: un DJ con portadas propias deja
/// «Sobrescribir» desmarcado, elige la carátula del catálogo, aplica 2.000 canciones y las pierde todas.
///
/// N14 · Quién firma la versión se escribe en su propio tag (TPE4 en ID3), que rekordbox y Serato
/// enseñan en su columna, y se deshace como los demás campos.
/// </summary>
public class CaratulaYRemixerTests
{
    private static readonly byte[] PortadaPropia = Jpeg(1);
    private static readonly byte[] Contraportada = Jpeg(2);
    private static readonly byte[] DelCatalogo = Jpeg(3);

    // Bytes con la firma de un JPEG; lo de dentro da igual para TagLib.
    private static byte[] Jpeg(byte semilla)
    {
        var b = new byte[64];
        b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[3] = 0xE0;
        for (int i = 4; i < b.Length; i++) b[i] = (byte)(semilla * 31 + i);
        return b;
    }

    private static TagLib.Picture Imagen(byte[] datos, TagLib.PictureType tipo)
        => new(new TagLib.ByteVector(datos)) { Type = tipo, MimeType = "image/jpeg" };

    private static void PonerImagenes(string p, params TagLib.IPicture[] imagenes)
    {
        using var f = TagLib.File.Create(p);
        f.Tag.Pictures = imagenes;
        f.Save();
    }

    private static TagLib.IPicture[] LeerImagenes(string p)
    {
        using var f = TagLib.File.Create(p);
        return f.Tag.Pictures;
    }

    private static FieldFlags SoloTitulo => new() { Title = true, Artist = false, Album = false, Genre = false, Year = false, Bpm = false };

    [Fact]
    public void Sin_sobrescribir_no_se_toca_la_caratula_que_ya_habia()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(p);
            PonerImagenes(p, Imagen(PortadaPropia, TagLib.PictureType.FrontCover));

            var log = Tagging.ApplyTags(new ProcessResult { FilePath = p }, overwrite: false, SoloTitulo,
                Imagen(DelCatalogo, TagLib.PictureType.FrontCover), Path.Combine(dir, "portadas"));

            Assert.False(log.ContainsKey("Cover"));
            var imgs = LeerImagenes(p);
            Assert.Single(imgs);
            Assert.Equal(PortadaPropia, imgs[0].Data.Data);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Con_sobrescribir_cambia_solo_la_frontal_y_guarda_la_anterior()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(p);
            PonerImagenes(p, Imagen(PortadaPropia, TagLib.PictureType.FrontCover), Imagen(Contraportada, TagLib.PictureType.BackCover));
            var portadas = Path.Combine(dir, "deshacer", "portadas");

            var log = Tagging.ApplyTags(new ProcessResult { FilePath = p }, overwrite: true, SoloTitulo,
                Imagen(DelCatalogo, TagLib.PictureType.FrontCover), portadas);

            // La anterior, guardada con su SHA-1 por nombre y anotada con su ruta absoluta.
            var cambio = log["Cover"];
            Assert.Equal(FieldKind.Str, cambio.Kind);
            Assert.Equal(Tagging.CaratulaSustituida, cambio.NewStr);
            Assert.True(Path.IsPathRooted(cambio.OldStr));
            Assert.True(File.Exists(cambio.OldStr));
            Assert.Equal(PortadaPropia, File.ReadAllBytes(cambio.OldStr!));
            var sha1 = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(PortadaPropia)).ToLowerInvariant();
            Assert.Equal(sha1 + ".jpg", Path.GetFileName(cambio.OldStr));

            // La contraportada sigue ahí.
            var imgs = LeerImagenes(p);
            Assert.Equal(2, imgs.Length);
            Assert.Contains(imgs, i => i.Type == TagLib.PictureType.FrontCover && i.Data.Data.SequenceEqual(DelCatalogo));
            Assert.Contains(imgs, i => i.Type == TagLib.PictureType.BackCover && i.Data.Data.SequenceEqual(Contraportada));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Sin_caratula_se_pone_aunque_no_se_sobrescriba()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(p);
            PonerImagenes(p, Imagen(Contraportada, TagLib.PictureType.BackCover));   // solo contraportada

            var log = Tagging.ApplyTags(new ProcessResult { FilePath = p }, overwrite: false, SoloTitulo,
                Imagen(DelCatalogo, TagLib.PictureType.FrontCover), Path.Combine(dir, "portadas"));

            Assert.Equal("", log["Cover"].OldStr);   // no había frontal: deshacer la quita sin más
            var imgs = LeerImagenes(p);
            Assert.Equal(2, imgs.Length);
            Assert.Contains(imgs, i => i.Type == TagLib.PictureType.BackCover);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Muchos programas guardan la portada con el tipo «Otro»: esa cuenta como la frontal.
    [Fact]
    public void Una_portada_de_tipo_otro_cuenta_como_la_frontal()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(p);
            PonerImagenes(p, Imagen(PortadaPropia, TagLib.PictureType.Other));

            var log = Tagging.ApplyTags(new ProcessResult { FilePath = p }, overwrite: true, SoloTitulo,
                Imagen(DelCatalogo, TagLib.PictureType.FrontCover), Path.Combine(dir, "portadas"));

            Assert.True(File.Exists(log["Cover"].OldStr));
            var imgs = LeerImagenes(p);
            Assert.Single(imgs);
            Assert.Equal(DelCatalogo, imgs[0].Data.Data);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Sin dónde guardar la anterior, no hay marcha atrás: la carátula no se toca.
    [Fact]
    public void Sin_carpeta_para_la_copia_no_se_sustituye()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(p);
            PonerImagenes(p, Imagen(PortadaPropia, TagLib.PictureType.FrontCover));

            var log = Tagging.ApplyTags(new ProcessResult { FilePath = p }, overwrite: true, SoloTitulo,
                Imagen(DelCatalogo, TagLib.PictureType.FrontCover), carpetaPortadas: null);

            Assert.False(log.ContainsKey("Cover"));
            Assert.Equal(PortadaPropia, LeerImagenes(p)[0].Data.Data);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ApplyEngine guarda la copia junto al manifiesto, en «portadas», y la deja anotada en él.
    [Fact]
    public async Task El_manifiesto_apunta_a_la_copia_de_la_portada()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(p);
            PonerImagenes(p, Imagen(PortadaPropia, TagLib.PictureType.FrontCover));
            var deshacer = Path.Combine(dir, "deshacer");
            Directory.CreateDirectory(deshacer);
            var man = Path.Combine(deshacer, "run_1.jsonl");
            var info = new ProcessResult { FilePath = p, Old = "a.mp3", New = "a.mp3" };

            await new ApplyEngine().ApplyOneAsync(info, true, SoloTitulo, "png",
                Imagen(DelCatalogo, TagLib.PictureType.FrontCover), man, Path.Combine(dir, "done.log"));

            var rec = JsonSerializer.Deserialize<UndoRecord>(File.ReadAllLines(man).Single(l => l.Trim().Length > 0))!;
            var copia = rec.Fields!["Cover"].OldStr!;
            Assert.StartsWith(Path.Combine(deshacer, ApplyEngine.CarpetaPortadas), copia);
            Assert.Equal(PortadaPropia, File.ReadAllBytes(copia));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // --- N14 · Remixer ---

    [Fact]
    public void Quien_firma_la_version_va_a_su_tag_con_el_titulo()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(p);
            var info = new ProcessResult { FilePath = p, Title = "GIVENCHY (Alex Cortes Open Show)", Remixer = "Alex Cortes" };

            var log = Tagging.ApplyTags(info, overwrite: false, SoloTitulo, null);

            Assert.Equal("", log["Remixer"].OldStr);
            Assert.Equal("Alex Cortes", log["Remixer"].NewStr);
            using var f = TagLib.File.Create(p);
            Assert.Equal("Alex Cortes", f.Tag.RemixedBy);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Si_el_titulo_no_se_escribe_el_remixer_tampoco()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(p);
            var info = new ProcessResult { FilePath = p, Title = "GIVENCHY (Alex Cortes Open Show)", Remixer = "Alex Cortes" };
            var sinTitulo = new FieldFlags { Title = false, Artist = false, Album = false, Genre = false, Year = false, Bpm = false };

            var log = Tagging.ApplyTags(info, overwrite: true, sinTitulo, null);

            Assert.False(log.ContainsKey("Remixer"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Sin_sobrescribir_se_respeta_el_remixer_que_ya_habia()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(p);
            using (var f = TagLib.File.Create(p)) { f.Tag.RemixedBy = "Puesto a mano"; f.Save(); }
            var info = new ProcessResult { FilePath = p, Title = "Tema (Bayne Remix)", Remixer = "Bayne" };

            var log = Tagging.ApplyTags(info, overwrite: false, SoloTitulo, null);

            Assert.True(log.ContainsKey("Title"));
            Assert.False(log.ContainsKey("Remixer"));
            using var g = TagLib.File.Create(p);
            Assert.Equal("Puesto a mano", g.Tag.RemixedBy);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Deshacer_devuelve_el_remixer_anterior()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(p);
            using (var f = TagLib.File.Create(p)) { f.Tag.Title = "Viejo"; f.Tag.RemixedBy = "Antes"; f.Save(); }
            var info = new ProcessResult { FilePath = p, Old = "a.mp3", New = "a.mp3", Title = "Tema (Bayne Remix)", Remixer = "Bayne" };
            var man = Path.Combine(dir, "run_r.jsonl");
            await new ApplyEngine().ApplyOneAsync(info, true, SoloTitulo, "keep", null, man, Path.Combine(dir, "done.log"));
            using (var f = TagLib.File.Create(p)) Assert.Equal("Bayne", f.Tag.RemixedBy);

            new UndoEngine(new AppPaths(dir)).UndoLastRun(man);

            using var g = TagLib.File.Create(p);
            Assert.Equal("Antes", g.Tag.RemixedBy);
            Assert.Equal("Viejo", g.Tag.Title);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
