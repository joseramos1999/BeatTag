using System.Text.Json;
using System.Text.Json.Nodes;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// C14: el ISRC y la marca de letra explícita que Deezer ya devolvía (y ya estaban en la caché) y se
/// tiraban. Se prueba de dónde salen, que eligen entre la versión explícita y la editada de un mismo
/// tema, cuándo el ISRC es de verdad el del archivo, que se escribe solo si el usuario lo activa y se
/// deshace, y que lo guardado por versiones anteriores sigue valiendo sin invalidar nada.
/// </summary>
public class CatalogoIsrcLetraTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---------------------------------------------------------------- respuestas de Deezer

    /// <summary>Una pista de Deezer tal como llega en /search, con sus campos de letra e ISRC.</summary>
    private static JsonObject PistaDz(long id, string titulo, int duracion, bool? explicita = null, int? contenido = null,
                                      string? isrc = null, string artista = "Fisher")
    {
        var o = new JsonObject
        {
            ["id"] = id, ["title"] = titulo, ["duration"] = duracion,
            ["artist"] = new JsonObject { ["name"] = artista },
            ["album"] = new JsonObject { ["title"] = "Losing It", ["id"] = 77, ["cover_big"] = "http://portadas/big.jpg" },
        };
        if (explicita != null) o["explicit_lyrics"] = explicita.Value;
        if (contenido != null) o["explicit_content_lyrics"] = contenido.Value;
        if (isrc != null) o["isrc"] = isrc;
        return o;
    }

    private static JsonArray Datos(params JsonObject[] pistas)
    {
        var a = new JsonArray();
        foreach (var p in pistas) a.Add(p);
        return a;
    }

    // MEDIDO en la caché: true/1 (16.106), false/0 (53.178), false/2 (5.520), false/6 (3.724),
    // false/3 (358), true/4 (81), false/7 (8).
    [Theory]
    [InlineData(true, 1, Explicitness.Explicit)]
    [InlineData(true, 4, Explicitness.Explicit)]      // en parte explícita: mejor apartarla
    [InlineData(false, 3, Explicitness.Clean)]        // «editada»: la versión clean
    [InlineData(false, 0, Explicitness.Unknown)]      // «no explícita» es la marca por defecto, no una garantía
    [InlineData(false, 2, Explicitness.Unknown)]
    [InlineData(false, 6, Explicitness.Unknown)]
    [InlineData(false, 7, Explicitness.Unknown)]
    public void La_letra_de_deezer_se_lee_con_criterio(bool explicita, int contenido, Explicitness esperado)
        => Assert.Equal(esperado, DeezerProvider.LetraDe(PistaDz(1, "x", 200, explicita, contenido)));

    [Fact]
    public void Sin_campos_de_letra_no_hay_dato()
    {
        Assert.Equal(Explicitness.Unknown, DeezerProvider.LetraDe(PistaDz(1, "x", 200)));
        Assert.Equal(Explicitness.Unknown, DeezerProvider.LetraDe(null));
    }

    [Theory]
    [InlineData("USUYG1017706", "USUYG1017706")]
    [InlineData("us-uyg-10-17706", "USUYG1017706")]
    [InlineData(" ES5109200015 ", "ES5109200015")]
    [InlineData("USUYG101770", "")]       // le falta una cifra
    [InlineData("1234567890AB", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void El_isrc_se_comprueba_antes_de_aceptarlo(string? entrada, string esperado)
        => Assert.Equal(esperado, DeezerProvider.NormalizarIsrc(entrada));

    // ---------------------------------------------------------------- SelectBest: clean o explícita

    // Los pools publican «Tema (Clean)» y «Tema (Dirty)»; Deezer, la explícita y la editada con el
    // mismo título y la misma duración. Antes ganaba siempre la primera de la lista.
    [Theory]
    [InlineData(Explicitness.Clean, 2)]
    [InlineData(Explicitness.Explicit, 1)]
    [InlineData(Explicitness.Unknown, 1)]   // sin marca en el nombre, como siempre: la primera
    public void Con_dos_versiones_gana_la_de_la_letra_del_archivo(Explicitness archivo, int esperada)
    {
        var data = Datos(PistaDz(1, "Losing It", 200, true, 1), PistaDz(2, "Losing It", 200, false, 3));

        var pick = DeezerProvider.SelectBest(data, "Fisher", "Losing It", false, false, 200, false, out _, out var why, letra: archivo);

        Assert.Equal(esperada, Id(pick));
        if (archivo != Explicitness.Unknown) Assert.Contains("letra como la del archivo", why);
    }

    // «No explícita» no es «clean»: para un archivo clean, solo se aparta la explícita.
    [Fact]
    public void Para_un_archivo_clean_la_no_explicita_gana_a_la_explicita()
    {
        var data = Datos(PistaDz(1, "Losing It", 200, true, 1), PistaDz(2, "Losing It", 200, false, 0));
        var pick = DeezerProvider.SelectBest(data, "Fisher", "Losing It", false, false, 200, false, out _, letra: Explicitness.Clean);
        Assert.Equal(2, Id(pick));
    }

    // Es un desempate: no pasa por encima de una duración que no cuadra.
    [Fact]
    public void La_letra_no_pasa_por_encima_de_la_duracion()
    {
        var data = Datos(PistaDz(1, "Losing It", 200, true, 1), PistaDz(2, "Losing It", 208, false, 3));
        var pick = DeezerProvider.SelectBest(data, "Fisher", "Losing It", false, false, 200, false, out _, letra: Explicitness.Clean);
        Assert.Equal(1, Id(pick));
    }

    private static int Id(JsonNode? n) => (int)(n?["id"]?.GetValue<long>() ?? -1);

    // ---------------------------------------------------------------- ExplicitDetector con el catálogo

    [Theory]
    [InlineData(Explicitness.Unknown, Explicitness.Explicit, Explicitness.Explicit)]   // el nombre no dice nada
    [InlineData(Explicitness.Clean, Explicitness.Explicit, Explicitness.Clean)]        // «Tusa (Clean)»: manda el archivo
    [InlineData(Explicitness.Explicit, Explicitness.Clean, Explicitness.Explicit)]
    [InlineData(Explicitness.Unknown, Explicitness.Clean, Explicitness.Unknown)]       // la editada pudo no ser este archivo
    [InlineData(Explicitness.Unknown, Explicitness.Unknown, Explicitness.Unknown)]
    public void Manda_el_nombre_y_del_catalogo_solo_cuenta_explicita(Explicitness nombre, Explicitness catalogo, Explicitness esperado)
        => Assert.Equal(esperado, ExplicitDetector.Combinar(nombre, catalogo));

    [Fact]
    public void Con_el_dato_del_catalogo_una_cancion_sin_marca_sale_explicita()
    {
        var t = new Track { FilePath = Path.Combine(_dir, "Bad Bunny - Safaera.mp3") };
        Assert.Equal(Explicitness.Unknown, ExplicitDetector.Detect(t));
        Assert.Equal(Explicitness.Explicit, ExplicitDetector.Detect(t, Explicitness.Explicit));
        Assert.Equal(Explicitness.Clean, ExplicitDetector.Detect(new Track { FilePath = Path.Combine(_dir, "Safaera (Clean).mp3") }, Explicitness.Explicit));
    }

    // El contrato para la biblioteca: quien tenga los análisis pone el dato por ruta y la columna
    // «Explícito», las estadísticas y la letra de la ficha lo usan sin tocar el disco.
    [Fact]
    public void El_dato_registrado_por_ruta_llega_a_la_columna_y_a_la_ficha()
    {
        var ruta = Path.Combine(_dir, "Bad Bunny - Safaera.mp3");
        var t = new Track { FilePath = ruta };
        try
        {
            ExplicitDetector.PonerCatalogo(new[] { new KeyValuePair<string, Explicitness>(ruta.ToUpperInvariant(), Explicitness.Explicit) });

            Assert.Equal(Explicitness.Explicit, ExplicitDetector.Detect(t));
            Assert.Equal("Explícito", t.Explicit);
            Assert.Equal(TipoLetra.Explicita, Fichas.LetraEfectiva(null, t));
            Assert.Equal(Explicitness.Unknown, ExplicitDetector.DelNombre(t));   // comparar versiones no lo usa
            Assert.Equal(Explicitness.Unknown, ExplicitDetector.Detect(new Track { FilePath = Path.Combine(_dir, "otra.mp3") }));
        }
        finally { ExplicitDetector.PonerCatalogo(null); }
        Assert.Equal(Explicitness.Unknown, ExplicitDetector.Detect(t));
    }

    // ---------------------------------------------------------------- de punta a punta (FileProcessor)

    private async Task<ProcessResult> Procesar(CatalogoFalso cat, string nombre)
    {
        var p = Path.Combine(_dir, nombre);
        Mp3Fixture.WriteMinMp3(p);
        return await cat.Procesador(_dir).ProcessAsync(p, false, new ProcessOptions { Deezer = true });
    }

    private static CatalogoFalso Catalogo(params JsonObject[] pistas)
    {
        var cat = new CatalogoFalso().Responde(CatalogoFalso.BusquedaDeezer("Fisher", "Losing It"), new JsonObject { ["data"] = Datos(pistas) });
        // track/{id} con lo mismo que la búsqueda (y el resto de su ficha): así llega de verdad.
        foreach (var p in pistas)
            cat.Responde(CatalogoFalso.PistaDeezer(p["id"]!.GetValue<long>()), JsonNode.Parse(p.ToJsonString())!);
        return cat;
    }

    [Fact]
    public async Task El_isrc_y_la_letra_llegan_a_la_propuesta_sin_peticiones_nuevas()
    {
        var cat = Catalogo(PistaDz(38217121, "Losing It", 1, true, 1, "USUYG1017706"));

        var r = await Procesar(cat, "Fisher - Losing It.mp3");

        Assert.True(r.Found);
        Assert.Equal("USUYG1017706", r.Isrc);
        Assert.Equal(ProcessResult.ClaveIsrc(r.Artist, r.Title), r.IsrcDe);
        Assert.Equal("USUYG1017706", r.IsrcVigente);
        Assert.Equal(Explicitness.Explicit, r.Explicit);
        // Las mismas peticiones de siempre: búsqueda, pista y álbum.
        Assert.DoesNotContain(cat.Pedidas, u => u.Contains("isrc", StringComparison.OrdinalIgnoreCase));
    }

    // Si la ficha de la pista no llegó (sin conexión a mitad), la búsqueda también lo trae.
    [Fact]
    public async Task Sin_la_ficha_de_la_pista_vale_lo_de_la_busqueda()
    {
        var cat = new CatalogoFalso().Responde(CatalogoFalso.BusquedaDeezer("Fisher", "Losing It"),
            new JsonObject { ["data"] = Datos(PistaDz(5, "Losing It", 1, true, 1, "USUYG1017706")) });

        var r = await Procesar(cat, "Fisher - Losing It.mp3");

        Assert.Equal("USUYG1017706", r.Isrc);
        Assert.Equal(Explicitness.Explicit, r.Explicit);
    }

    // Una extended o una intro de DJ no son la grabación del catálogo: su ISRC sería el de otra.
    [Theory]
    [InlineData("Fisher - Losing It (Extended Mix).mp3")]
    [InlineData("Fisher - Losing It (Dirty Intro).mp3")]
    public async Task Una_version_que_el_catalogo_no_nombra_no_se_lleva_el_isrc(string archivo)
    {
        var r = await Procesar(Catalogo(PistaDz(9, "Losing It", 1, true, 1, "USUYG1017706")), archivo);
        Assert.True(r.Found);
        Assert.Equal("", r.Isrc);
    }

    // El pool publica la clean: el catálogo tiene las dos, y se elige la editada, con SU ISRC.
    [Fact]
    public async Task Un_archivo_clean_coge_la_version_editada_y_su_isrc()
    {
        var cat = Catalogo(PistaDz(1, "Losing It", 1, true, 1, "USUYG1017706"), PistaDz(2, "Losing It", 1, false, 3, "USUYG1017799"));

        var r = await Procesar(cat, "Fisher - Losing It (Clean).mp3");

        Assert.Equal("USUYG1017799", r.Isrc);
        Assert.Equal(Explicitness.Clean, r.Explicit);
    }

    // Si el catálogo solo tiene la explícita, su ISRC no es el de la versión clean del archivo.
    [Fact]
    public async Task Un_archivo_clean_no_se_lleva_el_isrc_de_la_explicita()
    {
        var r = await Procesar(Catalogo(PistaDz(1, "Losing It", 1, true, 1, "USUYG1017706")), "Fisher - Losing It (Clean).mp3");
        Assert.True(r.Found);
        Assert.Equal("", r.Isrc);
    }

    [Fact]
    public void Las_reglas_del_isrc_dicen_por_que_no_se_propone()
    {
        Assert.Equal("USUYG1017706", FileProcessor.IsrcDelArchivo("USUYG1017706", "Losing It", Explicitness.Unknown,
                                                                   Array.Empty<string>(), Explicitness.Unknown, out var motivo));
        Assert.Equal("", motivo);
        // La versión que el catálogo ya nombra es la suya.
        Assert.Equal("USUYG1017706", FileProcessor.IsrcDelArchivo("USUYG1017706", "Losing It (Tiesto Remix)", Explicitness.Unknown,
                                                                   new[] { "Tiesto Remix" }, Explicitness.Unknown, out _));
        Assert.Equal("", FileProcessor.IsrcDelArchivo("USUYG1017706", "Losing It", Explicitness.Unknown,
                                                       new[] { "Extended Mix" }, Explicitness.Unknown, out motivo));
        Assert.Contains("Extended Mix", motivo);
        Assert.Equal("USUYG1017799", FileProcessor.IsrcDelArchivo("USUYG1017799", "Losing It", Explicitness.Clean,
                                                                   new[] { "Clean" }, Explicitness.Clean, out _));
    }

    // ---------------------------------------------------------------- compatibilidad con la caché

    // Lo que guardó una versión anterior no trae los campos: se lee como «sin dato» y nada se invalida.
    [Fact]
    public void Un_resultado_guardado_antes_se_lee_sin_isrc_ni_letra()
    {
        var r = JsonSerializer.Deserialize<ProcessResult>("""{"FilePath":"a.mp3","Title":"Losing It","Artist":"Fisher","Found":true,"Reglas":2}""")!;
        Assert.Equal("", r.Isrc);
        Assert.Equal("", r.IsrcVigente);
        Assert.Equal(Explicitness.Unknown, r.Explicit);
    }

    [Fact]
    public void La_letra_se_guarda_como_texto_y_vuelve_igual()
    {
        var json = JsonSerializer.Serialize(new ProcessResult { Explicit = Explicitness.Explicit, Isrc = "USUYG1017706", IsrcDe = "x|y" });
        Assert.Contains("\"Explicit\":\"Explicit\"", json);
        var r = JsonSerializer.Deserialize<ProcessResult>(json)!;
        Assert.Equal((Explicitness.Explicit, "USUYG1017706", "x|y"), (r.Explicit, r.Isrc, r.IsrcDe));
    }

    // La caché de análisis da el dato por ruta para ExplicitDetector, sin firmas ni disco.
    [Fact]
    public void La_cache_de_analisis_da_la_letra_de_cada_cancion()
    {
        var a = Path.Combine(_dir, "a.mp3");
        var b = Path.Combine(_dir, "b.mp3");
        Mp3Fixture.WriteMinMp3(a);
        Mp3Fixture.WriteMinMp3(b);
        var cache = new AnalysisCache(Path.Combine(_dir, "analysis-cache.json"));
        cache.Set(a, "sig", new ProcessResult { FilePath = a, Found = true, Explicit = Explicitness.Explicit });
        cache.Set(b, "sig", new ProcessResult { FilePath = b, Found = true });
        Assert.Equal("", cache.Save());

        var letras = new AnalysisCache(Path.Combine(_dir, "analysis-cache.json")).LetraDelCatalogo();

        var (ruta, letra) = Assert.Single(letras);
        Assert.Equal((a, Explicitness.Explicit), (ruta, letra));
    }

    // ---------------------------------------------------------------- revisión: el dato llega a la columna

    // Aplicar casi siempre renombra («Artista - Título»), y la caché va por ruta: tras reiniciar, la
    // marca se quedaba en la ruta antigua y la columna volvía a «sin indicar». Vale también para el
    // nombre propuesto, sin pisar el dato propio de esa ruta.
    [Fact]
    public void La_letra_vale_tambien_para_el_nombre_que_tendra_al_aplicar()
    {
        var a = Path.Combine(_dir, "bad bunny safaera.mp3");
        var b = Path.Combine(_dir, "Otro - Tema.mp3");
        Mp3Fixture.WriteMinMp3(a);
        Mp3Fixture.WriteMinMp3(b);
        var cache = new AnalysisCache(Path.Combine(_dir, "analysis-cache.json"));
        cache.Set(a, "sig", new ProcessResult { FilePath = a, Found = true, Explicit = Explicitness.Explicit,
                                                Old = "bad bunny safaera.mp3", New = "Bad Bunny - Safaera.mp3" });
        // Una propuesta que apunta al nombre de un archivo que ya tiene su propio dato no lo pisa.
        cache.Set(b, "sig", new ProcessResult { FilePath = b, Found = true, Explicit = Explicitness.Clean });
        var c = Path.Combine(_dir, "otro tema.mp3");
        Mp3Fixture.WriteMinMp3(c);
        cache.Set(c, "sig", new ProcessResult { FilePath = c, Found = true, Explicit = Explicitness.Explicit,
                                                Old = "otro tema.mp3", New = "Otro - Tema.mp3" });

        var letras = cache.LetraDelCatalogo().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(Explicitness.Explicit, letras[a]);
        Assert.Equal(Explicitness.Explicit, letras[Path.Combine(_dir, "Bad Bunny - Safaera.mp3")]);
        Assert.Equal(Explicitness.Clean, letras[b]);
        Assert.Equal(4, letras.Count);
    }

    [Fact]
    public void Las_rutas_de_la_letra_siguen_las_reglas_del_renombrado()
    {
        var ruta = Path.Combine(_dir, "x.MP3");
        Assert.Equal(new[] { ruta }, AnalysisCache.RutasDeLaLetra(ruta, new ProcessResult { Old = "x.MP3", New = "x.MP3" }));
        Assert.Equal(new[] { ruta }, AnalysisCache.RutasDeLaLetra(ruta, new ProcessResult { Old = "x.MP3", New = "" }));
        Assert.Equal(new[] { ruta }, AnalysisCache.RutasDeLaLetra(ruta, new ProcessResult { Old = "x.MP3", New = "..\\y.mp3" }));
        // La extensión es la del archivo, como al renombrar de verdad.
        Assert.Equal(new[] { ruta, Path.Combine(_dir, "Fisher - Losing It.MP3") },
                     AnalysisCache.RutasDeLaLetra(ruta, new ProcessResult { Old = "x.MP3", New = "Fisher - Losing It.mp3" }));
    }

    // Una entrada sin resultado («"R": null» en el JSON) no impide leer las demás.
    [Fact]
    public void Una_entrada_sin_resultado_no_rompe_la_lectura()
    {
        var a = Path.Combine(_dir, "a.mp3");
        File.WriteAllText(Path.Combine(_dir, "analysis-cache.json"),
            "{" + JsonSerializer.Serialize(a) + """:{"M":1,"S":2,"Sig":"x","R":null},"b.mp3":{"M":1,"S":2,"Sig":"x","R":{"Explicit":"Explicit"}}}""");

        var letras = new AnalysisCache(Path.Combine(_dir, "analysis-cache.json")).LetraDelCatalogo();

        var (ruta, letra) = Assert.Single(letras);
        Assert.Equal(("b.mp3", Explicitness.Explicit), (ruta, letra));
    }

    // Lo anota el motor con cada canción analizada: un análisis nuevo manda sobre el anterior, y el
    // nombre propuesto no pisa lo que se sepa de otro archivo.
    [Fact]
    public void Cada_analisis_pone_al_dia_el_dato_de_la_columna()
    {
        var ruta = Path.Combine(_dir, "Bad Bunny - Safaera.mp3");
        var otra = Path.Combine(_dir, "otra.mp3");
        try
        {
            ExplicitDetector.PonerCatalogo(new[] { new KeyValuePair<string, Explicitness>(otra, Explicitness.Clean) });

            ExplicitDetector.AnotarCatalogo(ruta, Explicitness.Explicit);
            Assert.Equal(Explicitness.Explicit, ExplicitDetector.DelCatalogo(ruta.ToUpperInvariant()));
            ExplicitDetector.AnotarCatalogo(ruta, Explicitness.Unknown);   // el análisis nuevo no tiene dato
            Assert.Equal(Explicitness.Unknown, ExplicitDetector.DelCatalogo(ruta));

            ExplicitDetector.AnotarCatalogo(otra, Explicitness.Explicit, sustituir: false);
            Assert.Equal(Explicitness.Clean, ExplicitDetector.DelCatalogo(otra));
            ExplicitDetector.AnotarCatalogo(ruta, Explicitness.Explicit, sustituir: false);
            Assert.Equal(Explicitness.Explicit, ExplicitDetector.DelCatalogo(ruta));
            ExplicitDetector.AnotarCatalogo(ruta, Explicitness.Unknown, sustituir: false);   // sin dato, no quita nada
            Assert.Equal(Explicitness.Explicit, ExplicitDetector.DelCatalogo(ruta));
        }
        finally { ExplicitDetector.PonerCatalogo(null); }
        Assert.Equal(Explicitness.Unknown, ExplicitDetector.DelCatalogo(ruta));
    }

    // La letra decide entre dos candidatas, pero no cambia la confianza que se devuelve: un archivo
    // «(Clean)» del que Deezer solo tiene la explícita (lo habitual) no pierde un punto por eso.
    [Fact]
    public void La_letra_no_cambia_la_confianza_devuelta()
    {
        var solo = Datos(PistaDz(1, "Losing It", 200, true, 1));
        DeezerProvider.SelectBest(solo, "Fisher", "Losing It", false, false, 200, false, out var sin, letra: Explicitness.Unknown);
        DeezerProvider.SelectBest(solo, "Fisher", "Losing It", false, false, 200, false, out var clean, out var why, letra: Explicitness.Clean);
        Assert.Equal(sin, clean);
        Assert.Contains("letra contraria a la del archivo (desempate)", why);

        var dos = Datos(PistaDz(1, "Losing It", 200, true, 1), PistaDz(2, "Losing It", 200, false, 3));
        var pick = DeezerProvider.SelectBest(dos, "Fisher", "Losing It", false, false, 200, false, out var conLetra, letra: Explicitness.Clean);
        Assert.Equal(2, Id(pick));
        Assert.Equal(sin, conLetra);
    }

    // ---------------------------------------------------------------- escribir y deshacer

    private ProcessResult ConIsrc(string ruta, string isrc = "USUYG1017706") => new()
    {
        FilePath = ruta, Old = Path.GetFileName(ruta), New = Path.GetFileName(ruta),
        Title = "Losing It", Artist = "Fisher", Found = true,
        Isrc = isrc, IsrcDe = ProcessResult.ClaveIsrc("Fisher", "Losing It"),
    };

    private static string Isrc(string ruta)
    {
        using var f = TagLib.File.Create(ruta);
        return f.Tag.ISRC ?? "";
    }

    private static FieldFlags Campos(bool isrc) => new() { Title = false, Artist = false, Album = false, Genre = false, Year = false, Bpm = false, Isrc = isrc };

    [Fact]
    public void Por_defecto_no_se_escribe()
    {
        Assert.False(new AppConfig().WriteIsrc);
        Assert.False(FieldFlags.All.Isrc);

        var mp3 = Path.Combine(_dir, "a.mp3");
        Mp3Fixture.WriteMinMp3(mp3);
        var log = Tagging.ApplyTags(ConIsrc(mp3), true, FieldFlags.All, null);
        Assert.False(log.ContainsKey(Tagging.IsrcField));
        Assert.Equal("", Isrc(mp3));
    }

    /// <summary>Un WAV mínimo válido: RIFF/WAVE, fmt (estéreo, 16 bits, 44,1 kHz) y un segundo de silencio.</summary>
    private static void WriteMinWav(string path)
    {
        const int frecuencia = 44100, canales = 2, bits = 16;
        var datos = frecuencia * canales * bits / 8;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(4 + (8 + 16) + (8 + datos)); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)canales); w.Write(frecuencia);
        w.Write(frecuencia * canales * bits / 8); w.Write((short)(canales * bits / 8)); w.Write((short)bits);
        w.Write("data"u8); w.Write(datos); w.Write(new byte[datos]);
    }

    // TSRC en MP3, y también en AIFF y WAV, que llevan ID3. Se deshace como cualquier otro campo.
    [Theory]
    [InlineData(".mp3")]
    [InlineData(".aiff")]
    [InlineData(".wav")]
    public async Task Activado_se_escribe_y_se_deshace(string ext)
    {
        var ruta = Path.Combine(_dir, "a" + ext);
        if (ext == ".mp3") Mp3Fixture.WriteMinMp3(ruta);
        else if (ext == ".wav") WriteMinWav(ruta);
        else LibraryScannerTests.WriteMinAiff(ruta);
        var man = Path.Combine(_dir, "run_20261019_120000.jsonl");

        var res = await new ApplyEngine().ApplyOneAsync(ConIsrc(ruta), false, Campos(isrc: true), "keep", null, man, Path.Combine(_dir, "done.log"));

        Assert.True(res.TagOk, res.TagErr);
        Assert.Equal("USUYG1017706", Isrc(ruta));
        Assert.True(res.Fields!.ContainsKey(Tagging.IsrcField));

        var undo = new UndoEngine(new AppPaths(_dir)).UndoLastRun(man);
        Assert.Equal(1, undo.Reverted);
        Assert.Equal("", Isrc(ruta));
    }

    // El ISRC de la tienda es el de ESE archivo: no se sustituye ni con «Sobrescribir».
    [Fact]
    public void No_se_sustituye_el_que_ya_trae_el_archivo()
    {
        var mp3 = Path.Combine(_dir, "a.mp3");
        Mp3Fixture.WriteMinMp3(mp3);
        using (var f = TagLib.File.Create(mp3)) { f.Tag.ISRC = "GBAYE0601498"; f.Save(); }

        var log = Tagging.ApplyTags(ConIsrc(mp3), overwrite: true, Campos(isrc: true), null);

        Assert.False(log.ContainsKey(Tagging.IsrcField));
        Assert.Equal("GBAYE0601498", Isrc(mp3));
    }

    // Si el usuario corrige a mano la propuesta (era otra canción), el ISRC deja de valer.
    [Fact]
    public void Si_se_edita_la_propuesta_el_isrc_no_se_escribe()
    {
        var mp3 = Path.Combine(_dir, "a.mp3");
        Mp3Fixture.WriteMinMp3(mp3);
        var info = ConIsrc(mp3);
        info.Title = "Stop It";

        var log = Tagging.ApplyTags(info, true, Campos(isrc: true), null);

        Assert.Equal("", info.IsrcVigente);
        Assert.False(log.ContainsKey(Tagging.IsrcField));
        Assert.Equal("", Isrc(mp3));
        // Cambiar solo mayúsculas o acentos no la convierte en otra canción.
        info.Title = "LOSING IT";
        Assert.Equal("USUYG1017706", info.IsrcVigente);
    }

    // Deshacer no pisa un ISRC que se cambió a mano después.
    [Fact]
    public async Task Deshacer_respeta_un_isrc_cambiado_a_mano()
    {
        var mp3 = Path.Combine(_dir, "a.mp3");
        Mp3Fixture.WriteMinMp3(mp3);
        var man = Path.Combine(_dir, "run_20261019_130000.jsonl");
        await new ApplyEngine().ApplyOneAsync(ConIsrc(mp3), false, Campos(isrc: true), "keep", null, man, Path.Combine(_dir, "done.log"));
        using (var f = TagLib.File.Create(mp3)) { f.Tag.ISRC = "GBAYE0601498"; f.Save(); }

        var undo = new UndoEngine(new AppPaths(_dir)).UndoLastRun(man);

        Assert.Equal(1, undo.Manual);
        Assert.Equal("GBAYE0601498", Isrc(mp3));
    }

    // La opción se guarda en la configuración; una configuración anterior se lee como desactivada.
    [Fact]
    public void La_opcion_se_guarda_y_una_configuracion_anterior_la_deja_apagada()
    {
        var paths = new AppPaths(_dir);
        File.WriteAllText(paths.ConfigPath, """{"UseDeezer":true}""");
        var cfg = AppConfig.Load(paths, out var error);
        Assert.Equal("", error);
        Assert.False(cfg.WriteIsrc);

        cfg.WriteIsrc = true;
        Assert.True(cfg.Save(paths, out var errGuardar), errGuardar);
        Assert.True(AppConfig.Load(paths).WriteIsrc);
    }
}
