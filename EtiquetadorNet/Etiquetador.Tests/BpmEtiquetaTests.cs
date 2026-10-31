using System.Globalization;
using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// El BPM de las etiquetas se lee igual con cualquier configuración regional. Con la española, TagLib
/// leía el TBPM «102.000000» como 102.000.000 BPM (visto en 2 archivos de una biblioteca real). Todas
/// las pruebas corren con es-ES, que es donde fallaba.
/// </summary>
public class BpmEtiquetaTests
{
    private static T EnEspanol<T>(Func<T> f)
    {
        var antes = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
            return f();
        }
        finally { CultureInfo.CurrentCulture = antes; }
    }

    /// <summary>Un MP3 cuyo TBPM lleva ese texto tal cual, como lo dejan otros programas.</summary>
    private static void EscribirMp3ConTbpm(string ruta, string tbpm)
    {
        Mp3Fixture.WriteMinMp3(ruta);
        using var f = TagLib.File.Create(ruta);
        var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true);
        id3.Title = "Tema";
        id3.SetTextFrame("TBPM", tbpm);
        f.Save();
    }

    [Theory]
    [InlineData("102", 102u)]
    [InlineData("102.5", 103u)]
    [InlineData("102,5", 103u)]
    [InlineData("102.000000", 102u)]
    [InlineData("127,99", 128u)]
    [InlineData(" 128 ", 128u)]
    [InlineData("999", 999u)]
    [InlineData("1000", 0u)]
    [InlineData("102000000", 0u)]
    [InlineData("1.024,5", 0u)]
    [InlineData("-5", 0u)]
    [InlineData("128 bpm", 0u)]
    [InlineData("", 0u)]
    [InlineData(null, 0u)]
    public void El_texto_del_bpm_admite_punto_y_coma_y_descarta_lo_imposible(string? texto, uint esperado)
        => Assert.Equal(esperado, EnEspanol(() => BpmEtiqueta.DeTexto(texto)));

    [Fact]
    public void Un_mp3_con_tbpm_102_000000_da_102_en_la_biblioteca()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "Artista - Tema.mp3");
            EscribirMp3ConTbpm(mp3, "102.000000");

            var (taglib, escaneado, editor, dj) = EnEspanol(() =>
            {
                using var f = TagLib.File.Create(mp3);
                return (f.Tag.BeatsPerMinute, LibraryScanner.ReadTrack(mp3).Bpm, TagEditor.Read(mp3).Bpm, SustitucionBandeja.LeerEtiquetasDj(mp3)!.Bpm);
            });

            // Lo que hace TagLib por sí solo con la cultura española: comprueba que la prueba reproduce el fallo.
            Assert.Equal(102_000_000u, taglib);
            Assert.Equal((102u, 102u, 102u), (escaneado, editor, dj));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void La_cache_de_escaneo_guarda_el_bpm_bien_leido()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "a.mp3");
            EscribirMp3ConTbpm(mp3, "102.000000");
            var cacheFile = Path.Combine(dir, "cache.json");

            EnEspanol(() => { var c = new ScanCache(cacheFile); c.Read(mp3); return c.Save(); });
            var guardada = EnEspanol(() => new ScanCache(cacheFile).Read(mp3).Bpm);

            Assert.Equal(102u, guardada);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Una caché escrita antes de la corrección tiene 102000000 para un archivo que no ha cambiado:
    // sin releerlo, el número imposible se quedaría para siempre.
    [Fact]
    public void Una_entrada_de_cache_con_un_bpm_imposible_se_relee()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "a.mp3");
            EscribirMp3ConTbpm(mp3, "102.000000");
            var cacheFile = Path.Combine(dir, "cache.json");
            var fi = new FileInfo(mp3);
            var vieja = new Dictionary<string, ScanCache.Entry>(StringComparer.OrdinalIgnoreCase)
            {
                [mp3] = new() { M = fi.LastWriteTimeUtc.Ticks, S = fi.Length, Ti = "Tema", Bp = 102_000_000, Sr = 44100 },
            };
            Assert.Equal("", ArchivoDatos.EscribirJson(cacheFile, vieja));

            var (alArrancar, alComprobar) = EnEspanol(() =>
            {
                var c = new ScanCache(cacheFile);
                var publicada = c.Guardadas(_ => dir).Single().Bpm;   // lo que se enseña antes de ir al disco
                return (publicada, c.Read(mp3).Bpm);
            });

            Assert.Equal(0u, alArrancar);
            Assert.Equal(102u, alComprobar);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Lo anotado para deshacer es el BPM de verdad, no el número mal leído: deshacer escribiría 102000000.
    [Fact]
    public void Importar_de_rekordbox_anota_el_bpm_anterior_bien_leido()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "a.mp3");
            EscribirMp3ConTbpm(mp3, "102.000000");

            var (sinSobrescribir, sobrescribiendo) = EnEspanol(() =>
                (TagEditor.ImportBpmKey(mp3, 128, null, overwrite: false).Count,
                 TagEditor.ImportBpmKey(mp3, 128, null, overwrite: true)["Bpm"]));

            Assert.Equal(0, sinSobrescribir);   // ya tenía BPM: sin «Sobrescribir» no se toca
            Assert.Equal((102u, 128u), (sobrescribiendo.OldNum, sobrescribiendo.NewNum));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Theory]
    [InlineData("94.00", 94u)]
    [InlineData("128,50", 129u)]
    [InlineData("0.00", 0u)]
    [InlineData("", 0u)]
    public void El_bpm_de_rekordbox_se_lee_igual_en_espanol(string texto, uint esperado)
        => Assert.Equal(esperado, EnEspanol(() => RekordboxImporter.ParseBpm(texto)));

    // Los otros formatos que guardan el BPM como texto: APE (en MP3), Xiph (FLAC, OGG, Opus) y WMA.
    [Fact]
    public void Las_demas_etiquetas_de_texto_se_leen_igual()
    {
        var ape = new TagLib.Ape.Tag();
        ape.SetValue("BPM", "102.000000");
        var tempo = new TagLib.Ogg.XiphComment();
        tempo.SetField("TEMPO", "102,5");
        var bpm = new TagLib.Ogg.XiphComment();
        bpm.SetField("BPM", "102.5");
        var wmaTexto = new TagLib.Asf.Tag();
        wmaTexto.SetDescriptorString("102.000000", "WM/BeatsPerMinute");
        var wmaNumero = new TagLib.Asf.Tag();
        wmaNumero.SetDescriptors("WM/BeatsPerMinute", new TagLib.Asf.ContentDescriptor("WM/BeatsPerMinute", 128u));

        var leidos = EnEspanol(() => new TagLib.Tag[] { ape, tempo, bpm, wmaTexto, wmaNumero }.Select(BpmEtiqueta.Leer).ToArray());

        Assert.Equal(new[] { 102u, 103u, 103u, 102u, 128u }, leidos);
    }

    // Como TagLib, manda la primera etiqueta con BPM; pero una con un BPM imposible no tapa a la siguiente.
    [Fact]
    public void Un_bpm_imposible_en_id3v2_deja_paso_al_de_ape()
    {
        var id3 = new TagLib.Id3v2.Tag();
        id3.SetTextFrame("TBPM", "102000000");
        var ape = new TagLib.Ape.Tag();
        ape.SetValue("BPM", "124");

        Assert.Equal(124u, EnEspanol(() => BpmEtiqueta.Leer(new TagLib.CombinedTag(id3, ape))));
    }
}
