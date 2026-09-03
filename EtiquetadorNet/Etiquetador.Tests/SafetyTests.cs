using Etiquetador.Core;
using Etiquetador.Core.Analysis;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Salvaguardas: cosas que NO deben pasarle nunca a los archivos del usuario, aunque el resto
/// funcione. Cada una nace de un fallo real localizado en revision.
/// </summary>
public class SafetyTests
{
    /// <summary>Escapa una ruta para meterla dentro de una cadena JSON (las barras invertidas van dobles).</summary>
    private static string Json(string ruta) => ruta.Replace("\\", "\\\\");

    // El recorrido de tramas busca la marca de sincronismo byte a byte, asi que en un FLAC o un WAV
    // puede dar con una secuencia que la imite y reescribir encima. La extension corta ese camino.
    [Theory]
    [InlineData("cancion.flac")]
    [InlineData("cancion.wav")]
    [InlineData("cancion.m4a")]
    [InlineData("cancion.ogg")]
    [InlineData("cancion.aiff")]
    [InlineData("cancion")]
    public void Solo_se_ajusta_el_volumen_de_los_mp3(string archivo)
        => Assert.False(Mp3Gain.EsAjustable(archivo));

    [Theory]
    [InlineData("cancion.mp3")]
    [InlineData("CANCION.MP3")]
    [InlineData(@"C:\musica\Bad Bunny - Tema.Mp3")]
    public void Un_mp3_si_se_ajusta(string archivo)
        => Assert.True(Mp3Gain.EsAjustable(archivo));

    // Y la guarda esta DENTRO del nucleo, no solo en la vista: aunque alguien llame directamente,
    // un archivo que no es MP3 no se toca.
    [Fact]
    public void El_nucleo_rechaza_lo_que_no_es_mp3_sin_leerlo()
    {
        var r = Mp3Gain.Apply(@"C:\no\existe\cancion.flac", 2);
        Assert.False(r.Ok);
        Assert.Equal(Mp3Gain.MotivoNoAjustable, r.Error);
    }

    // Analyze tampoco: contar tramas de un FLAC no significa nada.
    [Fact]
    public void Analizar_un_no_mp3_tampoco_tiene_sentido()
        => Assert.False(Mp3Gain.Analyze(@"C:\no\existe\cancion.wav").Ok);

    // La firma de cache distingue QUE credenciales, no solo si las hay: cambiar un token que no
    // funcionaba tiene que invalidar lo cacheado con el anterior.
    [Fact]
    public void Cambiar_una_credencial_invalida_la_cache()
    {
        var a = new ProcessOptions { Discogs = true, DiscogsToken = "token-viejo" };
        var b = new ProcessOptions { Discogs = true, DiscogsToken = "token-nuevo" };
        Assert.NotEqual(a.Signature(), b.Signature());
    }

    [Fact]
    public void Con_las_mismas_credenciales_la_firma_no_cambia()
    {
        var a = new ProcessOptions { Discogs = true, DiscogsToken = "mismo" };
        var b = new ProcessOptions { Discogs = true, DiscogsToken = "mismo" };
        Assert.Equal(a.Signature(), b.Signature());
    }

    // Y la clave NO puede acabar escrita en la cache de analisis, que es un JSON en claro.
    [Fact]
    public void La_firma_no_contiene_la_credencial()
    {
        var o = new ProcessOptions { Discogs = true, DiscogsToken = "ClaveSecretaDeDiscogs" };
        Assert.DoesNotContain("ClaveSecretaDeDiscogs", o.Signature());
    }

    [Fact]
    public void Sin_credenciales_la_firma_lo_dice_en_claro()
        => Assert.Contains("sin-claves", new ProcessOptions().Signature());

    // Cambiar el FORMATO de la firma no puede tirar la cache entera: lo analizado con las mismas
    // opciones sigue valiendo, y reanalizar 12.428 canciones para acabar igual no le sirve a nadie.
    [Fact]
    public void Al_cambiar_el_formato_de_la_firma_la_cache_se_conserva()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(mp3);

            var o = new ProcessOptions { Discogs = true, DiscogsToken = "un-token" };
            var cache = new AnalysisCache(Path.Combine(dir, "cache.json"));
            cache.Set(mp3, o.SignatureLegacy(), new ProcessResult { FilePath = mp3, Old = "a.mp3" });

            Assert.Null(cache.Get(mp3, o.Signature()));          // con la firma nueva aun no vale
            Assert.Equal(1, cache.MigrarFirma(o.SignatureLegacy(), o.Signature()));
            Assert.NotNull(cache.Get(mp3, o.Signature()));       // y despues de migrar, si
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Si los tags no se pueden escribir, el archivo NO se renombra: quedaria con el nombre del
    // catalogo y los datos viejos dentro, y la lista lo daria por fallido con la ruta ya cambiada.
    [Fact]
    public async Task Si_fallan_los_tags_no_se_renombra()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            // Un .mp3 que no es un MP3: TagLib no puede abrirlo y la escritura de tags falla.
            var roto = Path.Combine(dir, "roto.mp3");
            File.WriteAllText(roto, "esto no es audio");

            var info = new ProcessResult
            {
                FilePath = roto, Old = "roto.mp3", New = "Bad Bunny - Tema.mp3",
                Artist = "Bad Bunny", Title = "Tema", Found = true,
            };

            var res = await new ApplyEngine().ApplyOneAsync(info, over: true, FieldFlags.All, "keep", null,
                                                            Path.Combine(dir, "undo.jsonl"), Path.Combine(dir, "done.log"));

            Assert.False(res.TagOk);
            Assert.False(res.DidRename);
            Assert.True(File.Exists(roto), "el archivo tiene que seguir donde estaba");
            Assert.False(File.Exists(Path.Combine(dir, "Bad Bunny - Tema.mp3")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Cuando todo va bien, se escribe Y se renombra: la guarda de arriba no puede haber roto esto.
    [Fact]
    public async Task Con_un_mp3_bueno_se_escribe_y_se_renombra()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "pista01.mp3");
            Mp3Fixture.WriteMinMp3(mp3);

            var info = new ProcessResult
            {
                FilePath = mp3, Old = "pista01.mp3", New = "Bad Bunny - Tema.mp3",
                Artist = "Bad Bunny", Title = "Tema", Found = true,
            };

            var res = await new ApplyEngine().ApplyOneAsync(info, over: true, FieldFlags.All, "keep", null,
                                                            Path.Combine(dir, "undo.jsonl"), Path.Combine(dir, "done.log"));

            Assert.True(res.TagOk);
            Assert.True(res.DidRename);
            Assert.Equal("", res.UndoErr);
            Assert.Equal("Tema", Mp3Fixture.GetTitle(Path.Combine(dir, "Bad Bunny - Tema.mp3")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Los manifiestos antiguos tambien describen renombrados, y son los que llevan mas tiempo
    // rotos en rekordbox. Deshacer siempre los entendio; la reparacion tenia que entenderlos igual.
    [Fact]
    public void La_reparacion_de_rekordbox_lee_los_manifiestos_antiguos()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            // Las rutas se construyen para la plataforma donde corra: el producto las parte con
            // Path, y en macOS la barra invertida no separa carpetas.
            var actual = Rutas.De("Musica", "Bad Bunny - Tema.mp3");
            var antes = Rutas.De("Musica", "pista01.mp3");

            // Formato antiguo: "new" es la ruta ACTUAL y "orig" solo el NOMBRE de partida.
            File.WriteAllText(Path.Combine(dir, "run_20240101_101010.jsonl"),
                $$"""{"new":"{{Json(actual)}}","orig":"pista01.mp3","renamed":true}""" + "\n");

            var mapa = RekordboxRelocator.ReadRenames(dir);

            Assert.Single(mapa);
            Assert.Equal(actual, mapa[antes]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Y encadenando los dos formatos: A->B en un manifiesto viejo y B->C en uno nuevo son A->C.
    [Fact]
    public void Los_dos_formatos_se_encadenan_entre_si()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            string a = Rutas.De("M", "a.mp3"), b = Rutas.De("M", "b.mp3"), c = Rutas.De("M", "c.mp3");

            File.WriteAllText(Path.Combine(dir, "run_20240101_101010.jsonl"),
                $$"""{"new":"{{Json(b)}}","orig":"a.mp3","renamed":true}""" + "\n");
            File.WriteAllText(Path.Combine(dir, "run_20250101_101010.jsonl"),
                $$"""{"OrigPath":"{{Json(b)}}","FinalPath":"{{Json(c)}}","Renamed":true}""" + "\n");

            var mapa = RekordboxRelocator.ReadRenames(dir);

            Assert.Equal(c, mapa[a]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
