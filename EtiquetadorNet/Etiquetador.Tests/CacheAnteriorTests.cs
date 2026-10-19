using System.Text.Json;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// N14 (bloqueante) · La firma de la caché de análisis no cambia al actualizar, así que lo que guardó
/// la versión anterior se reutiliza tal cual. En esa versión ProcessResult.Remixer era el texto en
/// bruto, también cuando se había descartado por ser «el propio título». Escribiendo el tag Remixer
/// desde ese campo, al aplicar sin reanalizar acababan en TPE4 cosas como «Intro, Alex Torres Intro»
/// (9 archivos en los registros), «Look At Me Now» o «Tik Tok». Además, esas filas no traían las
/// comprobaciones nuevas (fuente de rescate, nombre, BPM del nombre).
/// </summary>
public class CacheAnteriorTests
{
    private static FieldFlags SoloTitulo => new() { Title = true, Artist = false, Album = false, Genre = false, Year = false, Bpm = false };

    // Lo que escribía la versión anterior en analysis-cache.json: sin RemixerTag ni Reglas.
    private const string ResultadoViejoJson =
        "{\"FilePath\":\"x.mp3\",\"Old\":\"Mc Yoshi - Y a une meuf (Intro, Alex Torres Intro Acapella).mp3\"," +
        "\"New\":\"Mc Yoshi - Y a une meuf (Intro, Alex Torres Intro Acapella).mp3\"," +
        "\"Title\":\"Y a une meuf (Intro, Alex Torres Intro Acapella)\",\"Artist\":\"MC YOSHI\"," +
        "\"Found\":true,\"Source\":\"Deezer\",\"Score\":\"10\",\"Remixer\":\"Intro, Alex Torres Intro\",\"RemixKind\":\"Acapella\"}";

    [Fact]
    public void Lo_guardado_por_la_version_anterior_no_trae_el_remixer_del_tag()
    {
        var r = JsonSerializer.Deserialize<ProcessResult>(ResultadoViejoJson)!;
        Assert.Equal("Intro, Alex Torres Intro", r.Remixer);   // se sigue enseñando como antes
        Assert.Equal("", r.RemixerTag);
        Assert.Equal(0, r.Reglas);
    }

    [Fact]
    public void Un_resultado_viejo_no_escribe_el_tag_remixer()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(p);
            var viejo = JsonSerializer.Deserialize<ProcessResult>(ResultadoViejoJson.Replace("\"x.mp3\"", JsonSerializer.Serialize(p)))!;

            var log = Tagging.ApplyTags(viejo, overwrite: true, SoloTitulo, null);

            Assert.True(log.ContainsKey("Title"));      // el título sí se escribe
            Assert.False(log.ContainsKey("Remixer"));
            using var f = TagLib.File.Create(p);
            Assert.True(string.IsNullOrEmpty(f.Tag.RemixedBy));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // El camino entero: caché guardada por la versión anterior → se lee → se aplica.
    [Fact]
    public async Task Aplicar_desde_la_cache_vieja_no_toca_el_tag_remixer()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(p);
            var archivoCache = Path.Combine(dir, "analysis-cache.json");
            var vieja = new AnalysisCache(archivoCache);
            vieja.Set(p, "firma", new ProcessResult
            {
                FilePath = p, Old = Path.GetFileName(p), New = Path.GetFileName(p),
                Title = "Look At Me Now (Beauz Hard Techno Remix)", Found = true, Source = "Deezer", Score = "10",
                Remixer = "Look At Me Now",   // lo que guardaba la versión anterior: el propio título
            });
            vieja.Save();

            var r = new AnalysisCache(archivoCache).Get(p, "firma")!;
            await new ApplyEngine().ApplyOneAsync(r, true, SoloTitulo, "keep", null,
                Path.Combine(dir, "run_1.jsonl"), Path.Combine(dir, "done.log"));

            using var f = TagLib.File.Create(Path.Combine(dir, r.New));
            Assert.Equal("Look At Me Now (Beauz Hard Techno Remix)", f.Tag.Title);
            Assert.True(string.IsNullOrEmpty(f.Tag.RemixedBy));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Lo que guarda la versión actual sí lleva el tag, y la caché no lo toca al leerlo.
    [Fact]
    public void Un_resultado_nuevo_sale_de_la_cache_tal_cual()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(p);
            var cache = new AnalysisCache(Path.Combine(dir, "c.json"));
            cache.Set(p, "s", new ProcessResult
            {
                FilePath = p, Old = "a.mp3", New = "Kartashow, - (Remix).mp3", Source = "MusicBrainz", Bpm = "120",
                Remixer = "Bayne", RemixerTag = "Bayne", Reglas = ProcessResult.ReglasActuales,
            });

            var r = cache.Get(p, "s")!;

            Assert.Equal("Bayne", r.RemixerTag);
            Assert.Equal("", r.Revisar);       // lo nuevo ya trae sus comprobaciones: no se repiten
            Assert.Equal("120", r.Bpm);
            Assert.Equal("", r.BpmCatalogo);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static ProcessResult Viejo(string nombre, string nuevo, string bpm = "", string fuente = "Deezer")
        => new() { FilePath = "x.mp3", Old = nombre, New = nuevo, Found = true, Source = fuente, Bpm = bpm, Score = "10" };

    // Lo que solo identificó una fuente de rescate sale para revisar, como con las reglas de ahora.
    [Theory]
    [InlineData("MusicBrainz")]
    [InlineData("AcoustID")]
    public void Lo_viejo_de_una_fuente_de_rescate_sale_para_revisar(string fuente)
    {
        var r = Viejo("Bellaqueo 4 Life - Nengo Flow.mp3", "Nengo Flow - Otra Cancion.mp3", fuente: fuente);

        Assert.True(ResultadoAnterior.Actualizar(r));

        Assert.Equal(FileProcessor.MotivoSoloRescate(fuente), r.Revisar);
        Assert.StartsWith("revisar: ", r.Why);
        Assert.Equal(ProcessResult.ReglasActuales, r.Reglas);
        Assert.False(ResultadoAnterior.Actualizar(r));   // una sola vez
    }

    // Las huellas de una regla fallida en el nombre guardado también.
    [Theory]
    [InlineData("Kartashow - Золото (Rakurs & Ramirez Remix).mp3", "KARTASHOW, - (Rakurs & Ramirez Remix).mp3", "guion o coma colgando")]
    [InlineData("Gayle x Eric Prydz - abcdepjanoo.mp3", "Eric Prydz - Eric Prydz.mp3", "el título es igual que el artista")]
    [InlineData("Кино - Группа крови.mp3", "-.mp3", "el título se queda sin letras")]
    public void Lo_viejo_con_un_nombre_raro_sale_para_revisar(string nombre, string nuevo, string motivo)
    {
        var r = Viejo(nombre, nuevo);
        ResultadoAnterior.Actualizar(r);
        Assert.Contains(motivo, r.Revisar);
    }

    [Fact]
    public void Lo_viejo_sin_nada_raro_no_sale_para_revisar()
    {
        var r = Viejo("Quevedo - Columbia.mp3", "Quevedo - Columbia.mp3", bpm: "100");
        ResultadoAnterior.Actualizar(r);
        Assert.Equal("", r.Revisar);
        Assert.Equal("100", r.Bpm);
    }

    // BPM: todo lo que guardaba la versión anterior era del catálogo. Manda el del nombre, un rango no
    // propone ninguno y el del catálogo no pisa el que el archivo ya tiene.
    [Theory]
    [InlineData("Ahora Me Llama (ADRUKID Remix) - KAROL G, Bad Bunny - 155bpm - DJTOOLSVIP.mp3", "135", "155", "")]
    [InlineData("Bad Bunny - NUEVAYoL (Miguel Romá Open Show) (118-128 Bpm).mp3", "120", "", "")]
    [InlineData("Daddy Yankee - Rompe.mp3", "176", "176", "176")]
    [InlineData("Crazy - Ozuna, Wisin, Arcangel - 88.00bpm - .mp3", "", "88", "")]
    public void El_bpm_viejo_sigue_las_reglas_del_nombre(string nombre, string bpmViejo, string bpm, string bpmCatalogo)
    {
        var r = Viejo(nombre, "Artista - Tema.mp3", bpm: bpmViejo);
        ResultadoAnterior.Actualizar(r);
        Assert.Equal(bpm, r.Bpm);
        Assert.Equal(bpmCatalogo, r.BpmCatalogo);
        Assert.Equal("", r.Revisar);   // ya no hay BPM que choque con el del nombre
    }

    [Fact]
    public void El_bpm_viejo_del_catalogo_no_pisa_el_del_archivo()
    {
        var r = Viejo("Daddy Yankee - Rompe.mp3", "Daddy Yankee - Rompe.mp3", bpm: "176");
        ResultadoAnterior.Actualizar(r);
        var pista = new Track { FilePath = "Daddy Yankee - Rompe.mp3", Bpm = 88 };
        var soloBpm = new FieldFlags { Title = false, Artist = false, Album = false, Genre = false, Year = false, Bpm = true };
        Assert.False(Tagging.WouldChange(r, pista, overwrite: true, soloBpm));
    }

    // Lo saltado (mezclas) no se toca, solo se marca como visto.
    [Fact]
    public void Lo_saltado_no_se_toca()
    {
        var r = new ProcessResult { FilePath = "x.mp3", Old = "A x B.mp3", New = "A x B.mp3", Source = "Mezcla", Skip = true };
        Assert.True(ResultadoAnterior.Actualizar(r));
        Assert.Equal("", r.Revisar);
        Assert.Equal(ProcessResult.ReglasActuales, r.Reglas);
    }

    // Puesto al día una vez, se guarda así: no se repite en cada lectura.
    [Fact]
    public void La_cache_guarda_lo_puesto_al_dia()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(p);
            var archivo = Path.Combine(dir, "c.json");
            var c1 = new AnalysisCache(archivo);
            c1.Set(p, "s", new ProcessResult { FilePath = p, Old = "a.mp3", New = "Eric Prydz - Eric Prydz.mp3", Found = true, Source = "MusicBrainz" });
            c1.Save();

            var c2 = new AnalysisCache(archivo);
            Assert.NotEqual("", c2.Get(p, "s")!.Revisar);
            c2.Save();

            var guardado = File.ReadAllText(archivo);
            Assert.Matches(@"""Reglas""\s*:\s*" + ProcessResult.ReglasActuales, guardado);
            Assert.Contains("MusicBrainz, una fuente de rescate", guardado);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // FileProcessor marca lo que analiza con las reglas actuales y el remixer del tag.
    [Fact]
    public async Task Lo_que_analiza_ahora_lleva_las_reglas_y_el_remixer_del_tag()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "David Guetta - Titanium (Remix) (Bayne Remix 128bpm).mp3");
            Mp3Fixture.WriteMinMp3(p);
            var r = await new CatalogoFalso().Procesador(dir).ProcessAsync(p, false, new ProcessOptions { CleanOnly = true });
            Assert.Equal(ProcessResult.ReglasActuales, r.Reglas);
            Assert.Equal("Bayne", r.RemixerTag);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
