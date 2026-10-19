using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// N4 · Se proponía el BPM del tema original de Deezer para remixes y ediciones de DJ que van a otro
/// tempo. MEDIDO en los registros: de 39 archivos que declaraban su BPM en el nombre y recibían otro,
/// solo 13 coincidían, 9 eran el doble o la mitad y 17 iban a otro tempo. Ahora el BPM del nombre
/// manda, y el del catálogo solo vale si el archivo es esa misma versión.
/// </summary>
public class BpmPropuestoTests
{
    // Casos reales: el nombre declaraba un BPM y el catálogo daba otro.
    [Theory]
    [InlineData("Ahora Me Llama (ADRUKID Remix) - KAROL G, Bad Bunny - 155bpm - DJTOOLSVIP.mp3", 135, "155")]
    [InlineData("Chris Brown - Look At Me Now  Beauz Hard Techno Remix 154bpm .mp3", 146, "154")]
    [InlineData("Calvin Harris - How Deep Is Your Love (VD Soundsystem Remix 126Bpm).mp3", 122, "126")]
    [InlineData("Daddy Yankee - Rompe (Bayne Open Show 88bpm).mp3", 176, "88")]
    [InlineData("Alicante X Cafe Con Ron (Short Edit Clean)(Afro House) - Hugel Ft Bad Bunny - 126.01bpm - DJTOOLSVIP.mp3", 122, "126")]
    [InlineData("Crazy - Ozuna, Wisin, Arcangel - 88.00bpm - .mp3", 0, "88")]
    public void Manda_el_bpm_que_declara_el_nombre(string archivo, int catalogo, string esperado)
        => Assert.Equal(esperado, FileProcessor.ElegirBpm(archivo, new[] { "Remix" }, catalogo, out _));

    // Dos tempos en el nombre: es una transición y no tiene un BPM único.
    [Theory]
    [InlineData("Anuel Aa - Las Mas Bonitas Son P#Tas (Dj Mars Open Show Rkt 128-102bpm).mp3")]
    [InlineData("Bad Bunny - NUEVAYoL (Miguel Romá Open Show) (118-128 Bpm).mp3")]
    [InlineData("Bad Bunny, Vaskan - Burrito Sabanero (Diego Step Chritsmas Open Show 100 150 BPM) (Extended Latino).mp3")]
    public void Un_rango_de_tempo_no_propone_ningun_bpm(string archivo)
        => Assert.Equal("", FileProcessor.ElegirBpm(archivo, Array.Empty<string>(), 120, out _));

    // Sin BPM en el nombre: el del catálogo solo si la versión no puede cambiar el tempo.
    [Theory]
    [InlineData("Remix")]
    [InlineData("Bayne Open Show")]
    [InlineData("Juan Edit")]
    [InlineData("Transition")]
    [InlineData("Bootleg")]
    public void Una_version_de_otro_tempo_no_hereda_el_bpm_del_catalogo(string version)
        => Assert.Equal("", FileProcessor.ElegirBpm("Daddy Yankee - Rompe.mp3", new[] { version }, 176, out _));

    [Theory]
    [InlineData("Extended")]
    [InlineData("Miguel Roma Hype Intro")]
    [InlineData("Acapella Out")]
    [InlineData("Clean")]
    [InlineData("Intro Acapella")]
    public void Las_versiones_que_conservan_el_tempo_si_lo_heredan(string version)
        => Assert.Equal("176", FileProcessor.ElegirBpm("Daddy Yankee - Rompe.mp3", new[] { version }, 176, out _));

    // Si el catálogo ya trae la versión del archivo, no queda en la lista de versiones pendientes y
    // el BPM es el de esa pista.
    [Fact]
    public void El_tema_original_hereda_el_bpm_del_catalogo()
        => Assert.Equal("180", FileProcessor.ElegirBpm("50 Cent - In Da Club.mp3", Array.Empty<string>(), 180, out _));

    [Fact]
    public void Lee_el_bpm_del_nombre_en_sus_formas_habituales()
    {
        Assert.Equal(124, FileProcessor.BpmDelNombre("BAILALO ROCKY - Lomiiel (Benavente Remix 124Bpm) - 124bpm - DJTOOLSVIP", out var r1));
        Assert.False(r1);
        Assert.Equal(90, FileProcessor.BpmDelNombre("Daddy Yankee, Jason Derulo, Snoop Dogg - Somos De Calle (Carlos Iniesta Hype Intro 90bpm) 90", out _));
        Assert.Equal(0, FileProcessor.BpmDelNombre("Quevedo - Columbia", out var r2));
        Assert.False(r2);
    }

    // El BPM que el archivo ya tiene (normalmente analizado por rekordbox o Serato) no se pisa con el
    // del catálogo ni con «Sobrescribir»; sí con el que declara el nombre o con el que escriba el
    // usuario en la tabla.
    [Theory]
    [InlineData("180", "180", 90u, false)]   // del catálogo: se respeta el 90 que ya tenía
    [InlineData("88", "", 176u, true)]       // del nombre: manda
    [InlineData("92", "180", 90u, true)]     // el usuario lo cambió en la tabla: manda
    [InlineData("180", "180", 0u, true)]     // el archivo no tenía BPM: se rellena
    public void El_bpm_del_catalogo_no_pisa_el_del_archivo(string bpm, string bpmCatalogo, uint actual, bool escribe)
    {
        var info = new ProcessResult { FilePath = "x.mp3", New = "x.mp3", Bpm = bpm, BpmCatalogo = bpmCatalogo };
        var pista = new Etiquetador.Core.Track { FilePath = "x.mp3", Bpm = actual };
        var soloBpm = new FieldFlags { Title = false, Artist = false, Album = false, Genre = false, Year = false, Bpm = true };
        Assert.Equal(escribe, Tagging.WouldChange(info, pista, overwrite: true, soloBpm));
    }

    [Fact]
    public void Al_aplicar_tampoco_se_pisa()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(p);
            using (var f = TagLib.File.Create(p)) { f.Tag.BeatsPerMinute = 90; f.Save(); }
            var soloBpm = new FieldFlags { Title = false, Artist = false, Album = false, Genre = false, Year = false, Bpm = true };

            var log = Tagging.ApplyTags(new ProcessResult { FilePath = p, Bpm = "180", BpmCatalogo = "180" }, overwrite: true, soloBpm, null);

            Assert.False(log.ContainsKey("Bpm"));
            using var g = TagLib.File.Create(p);
            Assert.Equal(90u, g.Tag.BeatsPerMinute);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // También en «solo limpieza»: el BPM que declara el nombre es un dato del propio nombre.
    [Fact]
    public async Task En_solo_limpieza_se_propone_el_bpm_del_nombre()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Daddy Yankee - Rompe (Bayne Open Show 88bpm).mp3");
            Mp3Fixture.WriteMinMp3(p);
            var r = await new CatalogoFalso().Procesador(dir).ProcessAsync(p, false, new ProcessOptions { CleanOnly = true });
            Assert.Equal("88", r.Bpm);
            Assert.Equal("Daddy Yankee - Rompe (Bayne Open Show).mp3", r.New);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // De punta a punta, con el caso real: Deezer da 135 y el nombre declara 155.
    [Fact]
    public async Task Ahora_me_llama_lleva_el_bpm_del_nombre()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Ahora Me Llama (ADRUKID Remix) - KAROL G, Bad Bunny - 155bpm - DJTOOLSVIP.mp3");
            Mp3Fixture.WriteMinMp3(p);
            var cat = new CatalogoFalso()
                .Responde(CatalogoFalso.BusquedaDeezer("Ahora Me Llama"),
                          CatalogoFalso.DeezerBusqueda(20, "KAROL G", "Ahora Me Llama", 1))
                .Responde(CatalogoFalso.PistaDeezer(20), CatalogoFalso.DeezerPista(135, "2017-05-26", "KAROL G", "Bad Bunny"))
                .Responde(CatalogoFalso.AlbumDeezer(77), CatalogoFalso.DeezerAlbum("Latino"));

            var r = await cat.Procesador(dir).ProcessAsync(p, false, new ProcessOptions { Deezer = true });

            Assert.True(r.Found);
            Assert.Equal("155", r.Bpm);
            Assert.Equal("KAROL G, Bad Bunny - Ahora Me Llama (Adrukid Remix).mp3", r.New);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Y un remix sin BPM en el nombre se queda sin el del tema original.
    [Fact]
    public async Task Un_remix_sin_bpm_en_el_nombre_no_hereda_el_del_original()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Calvin Harris - How Deep Is Your Love (VD Soundsystem Remix).mp3");
            Mp3Fixture.WriteMinMp3(p);
            var cat = new CatalogoFalso()
                .Responde(CatalogoFalso.BusquedaDeezer("Calvin Harris"),
                          CatalogoFalso.DeezerBusqueda(30, "Calvin Harris", "How Deep Is Your Love", 1))
                .Responde(CatalogoFalso.PistaDeezer(30), CatalogoFalso.DeezerPista(122, "2015-07-17", "Calvin Harris", "Disciples"))
                .Responde(CatalogoFalso.AlbumDeezer(77), CatalogoFalso.DeezerAlbum("Dance"));

            var r = await cat.Procesador(dir).ProcessAsync(p, false, new ProcessOptions { Deezer = true });

            Assert.True(r.Found);
            Assert.Equal("", r.Bpm);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
