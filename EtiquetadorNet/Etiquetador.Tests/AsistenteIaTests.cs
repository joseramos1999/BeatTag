using System.Text.Json.Nodes;
using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// El Asistente IA, sin IA: lo que se prueba aquí es todo lo que rodea al modelo. Las reglas fijas
/// que hacen el trabajo fiable y, sobre todo, las comprobaciones que impiden que un error del modelo
/// llegue a la biblioteca.
///
/// Las respuestas «de la IA» de estas pruebas no son inventadas: son las que dieron llama3.2 y
/// llama3.1:8b al medirlos, con los mismos errores.
/// </summary>
public class AsistenteIaTests
{
    private static Track T(string artista, string titulo, string genero, uint bpm = 0, uint anio = 0, string key = "") => new()
    {
        Artist = artista, Title = titulo, Genre = genero, Bpm = bpm, Year = anio, Key = key,
        FilePath = $@"C:\Musica\{artista} - {titulo}.mp3",
    };

    /// <summary>Una biblioteca pequeña con géneros suficientes para que cuenten (3 canciones o más).</summary>
    private static VocabularioBiblioteca Voc()
    {
        var canciones = new List<Track>();
        void Varias(string genero, string artista, int n)
        {
            for (var i = 0; i < n; i++) canciones.Add(T(artista, $"Tema {genero} {i}", genero));
        }
        Varias("Reggaeton", "Bad Bunny", 3);
        Varias("House", "Fisher", 3);
        Varias("Afro House", "Black Coffee", 3);
        Varias("Techno", "Amelie Lens", 3);
        Varias("Trap", "Quevedo", 3);
        Varias("Rap", "Nach", 3);
        Varias("Bachata", "Romeo Santos", 3);
        Varias("Cumbia", "Los Palmeras", 3);
        Varias("Merengue", "Juan Luis Guerra", 3);
        canciones.Add(T("Miles Davis", "So What", "Jazz"));   // un solo tema: no llega a contar como género
        return new VocabularioBiblioteca(canciones);
    }

    private static JsonNode Ia(params (string Campo, object Valor, string Cita)[] filtros)
    {
        var arr = new JsonArray();
        foreach (var (c, v, cita) in filtros)
            arr.Add(new JsonObject { ["campo"] = c, ["valor"] = JsonValue.Create(v), ["cita"] = cita });
        return new JsonObject { ["filtros"] = arr };
    }

    private static string? Valor(IEnumerable<FiltroPropuesto> fs, string campo)
        => fs.FirstOrDefault(f => f.Campo == campo && f.Valido)?.Valor;

    // --- Buscar con una frase: reglas fijas ---

    [Fact]
    public void La_frase_se_lee_sin_ia_cuando_se_puede()
    {
        var fs = BusquedaIa.DesdeFrase("house vocal entre 120 y 124 bpm para el pico de la noche", Voc());

        Assert.Equal("House", Valor(fs, "genero"));
        Assert.Equal("vocal", Valor(fs, "voz"));
        Assert.Equal("120-124", Valor(fs, "bpm"));
        Assert.Equal("pico", Valor(fs, "momento"));
    }

    [Fact]
    public void Las_decadas_se_convierten_en_anos()
    {
        var fs = BusquedaIa.DesdeFrase("temas en inglés de los 80 para cerrar", Voc());

        Assert.Equal("EN", Valor(fs, "idioma"));
        Assert.Equal("1980-1989", Valor(fs, "anio"));
        Assert.Equal("cierre", Valor(fs, "momento"));
    }

    // Lo que llama3.2 convirtió en «instrumental» es, leído con reglas, letra limpia.
    [Fact]
    public void Sin_palabrotas_es_letra_limpia_y_no_instrumental()
    {
        var fs = BusquedaIa.DesdeFrase("reggaeton suave para abrir una boda, sin palabrotas", Voc());

        Assert.Equal("Reggaeton", Valor(fs, "genero"));
        Assert.Equal("baja", Valor(fs, "energia"));
        Assert.Equal("apertura", Valor(fs, "momento"));
        Assert.Equal("limpia", Valor(fs, "letra"));
        Assert.Null(Valor(fs, "voz"));
    }

    // «trap» no es «rap» por contener sus letras, y «afro house» no se cuenta además como «house».
    [Fact]
    public void Los_generos_se_buscan_como_palabras_enteras()
    {
        Assert.Equal("Trap", Valor(BusquedaIa.DesdeFrase("trap duro", Voc()), "genero"));
        Assert.Equal("Afro House", Valor(BusquedaIa.DesdeFrase("afro house para el after", Voc()), "genero"));
    }

    [Fact]
    public void Varios_generos_en_la_frase_suman()
    {
        var fs = BusquedaIa.DesdeFrase("cumbia y merengue de los 90", Voc());

        Assert.Equal(new[] { "Cumbia", "Merengue" }, Fichas.PartirLista(Valor(fs, "genero")).OrderBy(x => x));
        Assert.Equal("1990-1999", Valor(fs, "anio"));
    }

    // Un género que no está en la biblioteca no se busca: la colección saldría vacía sin explicación.
    [Fact]
    public void Un_genero_que_no_tienes_no_se_convierte_en_filtro()
    {
        Assert.Null(Valor(BusquedaIa.DesdeFrase("jazz para la cena", Voc()), "genero"));
    }

    [Fact]
    public void Reconoce_artistas_de_la_biblioteca()
    {
        var fs = BusquedaIa.DesdeFrase("algo de Bad Bunny con mucha energía", Voc());

        Assert.Equal("Bad Bunny", Valor(fs, "artista"));
        Assert.Equal("alta", Valor(fs, "energia"));
    }

    [Theory]
    [InlineData("80", "1980-1989")]
    [InlineData("90", "1990-1999")]
    [InlineData("00", "2000-2009")]
    [InlineData("10", "2010-2019")]
    [InlineData("1970", "1970-1979")]
    [InlineData("85", null)]
    public void Rango_de_decada(string d, string? esperado) => Assert.Equal(esperado, BusquedaIa.RangoDecada(d));

    // --- Buscar con una frase: lo que propone la IA ---

    // Respuesta real de llama3.2: cita palabras de la frase, pero no hablan de la voz.
    [Fact]
    public void Se_descarta_una_voz_cuya_cita_no_habla_de_la_voz()
    {
        var fs = BusquedaIa.DesdeIa(Ia(("voz", "instrumental", "suave")), "reggaeton suave para abrir una boda", Voc());

        var f = Assert.Single(fs);
        Assert.False(f.Valido);
    }

    // Respuesta real de llama3.2: idioma español «porque» la frase dice bachata.
    [Fact]
    public void Se_descarta_un_idioma_deducido_del_genero()
    {
        var fs = BusquedaIa.DesdeIa(Ia(("idioma", "ES", "bachata")), "bachata romántica", Voc());

        Assert.False(Assert.Single(fs).Valido);
    }

    [Fact]
    public void Se_descarta_lo_que_cita_palabras_que_no_escribiste()
    {
        var fs = BusquedaIa.DesdeIa(Ia(("momento", "pico", "pico")), "algo de Bad Bunny con mucha energía", Voc());

        Assert.False(Assert.Single(fs).Valido);
    }

    // Respuesta real de llama3.2: Bad Bunny como género. Existe como artista, así que se recoloca.
    [Fact]
    public void Un_artista_propuesto_como_genero_se_recoloca_como_artista()
    {
        var fs = BusquedaIa.DesdeIa(Ia(("genero", "Bad Bunny", "Bad Bunny")), "algo de Bad Bunny", Voc());

        Assert.Equal("Bad Bunny", Valor(fs, "artista"));
        Assert.Null(Valor(fs, "genero"));
    }

    [Fact]
    public void Un_genero_propuesto_que_no_tienes_se_descarta()
    {
        var fs = BusquedaIa.DesdeIa(Ia(("genero", "jazz", "jazz")), "jazz tranquilo", Voc());

        Assert.False(Assert.Single(fs).Valido);
    }

    // Respuesta real de llama3.2: «de los 80» como año mínimo 80.
    [Fact]
    public void El_ano_80_de_la_ia_es_la_decada_de_los_80()
    {
        var fs = BusquedaIa.DesdeIa(Ia(("anio_min", 80, "de los 80")), "temas de los 80", Voc());

        Assert.Equal("1980-1989", Valor(fs, "anio"));
    }

    // Respuesta real de llama3.1:8b: el nivel de energía escrito con la palabra de la frase.
    [Fact]
    public void La_energia_escrita_con_la_palabra_de_la_frase_se_traduce()
    {
        var fs = BusquedaIa.DesdeIa(Ia(("energia", "suave", "suave")), "reggaeton suave", Voc());

        Assert.Equal("baja", Valor(fs, "energia"));
    }

    // Lo que ya encontró la regla manda: la IA no puede sustituirlo.
    [Fact]
    public void La_regla_fija_manda_sobre_la_ia()
    {
        var frase = "reggaeton suave para abrir una boda";
        var reglas = BusquedaIa.DesdeFrase(frase, Voc());
        // llama3.2 propuso energía ALTA citando «para abrir una boda».
        var ia = BusquedaIa.DesdeIa(Ia(("energia", "alta", "para abrir una boda")), frase, Voc());

        var todos = BusquedaIa.Combinar(reglas, ia);

        Assert.Equal("baja", Valor(todos, "energia"));
        Assert.Single(todos, f => f.Campo == "energia");
    }

    [Fact]
    public void Los_filtros_aceptados_forman_una_coleccion_que_funciona()
    {
        var fs = BusquedaIa.DesdeFrase("cumbia y merengue de los 90", Voc());
        var c = BusquedaIa.AColeccion(fs, "Para los mayores");

        Assert.True(FiltroColeccion.Cumple(c, T("A", "B", "Cumbia", anio: 1994), null));
        Assert.True(FiltroColeccion.Cumple(c, T("A", "B", "Merengue", anio: 1999), null));
        Assert.False(FiltroColeccion.Cumple(c, T("A", "B", "Cumbia", anio: 2005), null));
        Assert.False(FiltroColeccion.Cumple(c, T("A", "B", "Salsa", anio: 1994), null));
        Assert.False(FiltroColeccion.Cumple(c, T("A", "B", "Cumbia", anio: 0), null));   // sin año no se puede afirmar
    }

    [Fact]
    public void La_energia_baja_es_de_1_a_4()
    {
        var c = BusquedaIa.AColeccion(new[] { new FiltroPropuesto("energia", "baja", "suave", OrigenFiltro.Frase) }, "x");

        Assert.Equal((1, 4), (c.EnergiaMin, c.EnergiaMax));
    }

    // --- Proponer fichas ---

    [Fact]
    public void Sin_letra_se_propone_instrumental()
    {
        var p = FichasIa.Interpretar(T("Darude", "Sandstorm", "Trance"), null, new JsonObject { ["idioma"] = "--" });

        Assert.Equal(TipoVoz.Instrumental, p.Voz);
        Assert.Equal("", p.Idioma);
    }

    [Fact]
    public void Con_idioma_se_propone_vocal()
    {
        var p = FichasIa.Interpretar(T("Rosalía", "Despechá", "Latin"), null, new JsonObject { ["idioma"] = "es" });

        Assert.Equal("ES", p.Idioma);
        Assert.Equal(TipoVoz.Vocal, p.Voz);
    }

    // Respuesta real con un prompt mal hecho: el modelo copió la plantilla «ES|EN|PT…».
    [Fact]
    public void Una_respuesta_que_no_es_un_idioma_no_propone_nada()
    {
        var p = FichasIa.Interpretar(T("A", "B", "Pop"), null, new JsonObject { ["idioma"] = "ES|EN|PT|FR|IT" });

        Assert.False(p.HayAlgo);
    }

    // Lo que el usuario ya rellenó no se discute.
    [Fact]
    public void Nunca_propone_encima_de_lo_que_ya_tiene_la_ficha()
    {
        var ficha = new FichaDj { Idioma = "PT" };
        var p = FichasIa.Interpretar(T("Anitta", "Envolver", "Reggaeton"), ficha, new JsonObject { ["idioma"] = "ES" });

        Assert.Equal("", p.Idioma);
        Assert.Equal(TipoVoz.Vocal, p.Voz);   // la voz sí estaba vacía
        Assert.Null(p.Cambios().Idioma);
    }

    [Fact]
    public void Si_el_nombre_dice_instrumental_no_hace_falta_preguntar()
    {
        var p = FichasIa.PorNombre(T("Bad Bunny", "Titi Me Pregunto (Instrumental)", "Reggaeton"), null);

        Assert.NotNull(p);
        Assert.Equal(TipoVoz.Instrumental, p!.Voz);
    }

    // --- Unificar géneros ---

    [Theory]
    [InlineData("@luigi.beltran", true)]
    [InlineData("Género desconocido", true)]
    [InlineData("Other", true)]
    [InlineData("Intensa Music", true)]   // un record pool
    [InlineData("12345", true)]
    [InlineData("z1.fm", true)]                        // una web: la IA lo convertía en «EDM»
    [InlineData("@djxizmusic.blogspot.com", true)]
    [InlineData("R&B", false)]
    [InlineData("Drum & Bass", false)]
    // Los que llama3.2 daba por «Desconocido» con el prompt libre: SON géneros y no se tocan.
    [InlineData("Folk", false)]
    [InlineData("Soundtrack", false)]
    [InlineData("Dance-Pop", false)]
    [InlineData("Electronica", false)]
    public void Distingue_lo_que_no_es_un_genero(string valor, bool noEs) => Assert.Equal(noEs, GenerosIa.NoEsGenero(valor));

    [Fact]
    public void Las_grafias_conocidas_se_unifican_sin_ia()
    {
        var p = GenerosIa.PorReglas("Hip-Hop", 38);

        Assert.Equal(OrigenGenero.Regla, p.Origen);
        Assert.Equal("Hip Hop", p.Propuesto);
    }

    [Fact]
    public void Un_espacio_de_mas_tambien_se_corrige()
    {
        Assert.Equal("Reggaeton", GenerosIa.PorReglas("Reggaeton ", 22).Propuesto);
    }

    // La IA solo puede elegir de la lista: ni inventar un nombre ni vaciar un género.
    [Theory]
    [InlineData("Latino", "Latino")]
    [InlineData("latino", "Latino")]          // se escribe como está en la lista
    [InlineData("Ballada", null)]             // respuesta real de llama3.2 para «Balada»: fuera de lista
    [InlineData("", null)]                    // vaciar no es una opción
    [InlineData("Latijnse muziek", null)]     // devolverlo igual es «no cambiar»
    public void La_ia_solo_puede_elegir_de_la_lista(string respuesta, string? esperado)
    {
        var destinos = new[] { "Latino", "Pop", "House" };

        Assert.Equal(esperado, GenerosIa.Interpretar(new JsonObject { ["propuesto"] = respuesta }, "Latijnse muziek", destinos));
    }

    [Fact]
    public void Se_pregunta_por_lo_que_las_reglas_no_conocen()
    {
        var propuestas = new[]
        {
            GenerosIa.PorReglas("Reggaeton", 3000),
            GenerosIa.PorReglas("Latino", 350),
            GenerosIa.PorReglas("Latijnse muziek", 12),
            GenerosIa.PorReglas("@luigi.beltran", 32),
            GenerosIa.PorReglas("Hip-Hop", 38),
        };

        var preguntar = GenerosIa.ParaIa(propuestas);

        Assert.Contains("Latijnse muziek", preguntar);   // el caso que fallaba: doce canciones no lo hacen referencia
        Assert.DoesNotContain("Reggaeton", preguntar);   // nombre conocido: nada que preguntar
        Assert.DoesNotContain("Hip-Hop", preguntar);     // ya lo resuelve la regla
        Assert.DoesNotContain("@luigi.beltran", preguntar);
    }

    [Fact]
    public void Los_destinos_son_los_conocidos_y_los_muy_usados()
    {
        var propuestas = new[]
        {
            GenerosIa.PorReglas("Reggaeton", 3000),
            GenerosIa.PorReglas("Latino", 350),
            GenerosIa.PorReglas("Latijnse muziek", 12),
            GenerosIa.PorReglas("Salsa", 5),              // poco usado, pero es un nombre conocido
            GenerosIa.PorReglas("@luigi.beltran", 32),
        };

        var destinos = GenerosIa.Destinos(propuestas);

        Assert.Contains("Latino", destinos);
        Assert.Contains("Salsa", destinos);
        Assert.DoesNotContain("Latijnse muziek", destinos);
        Assert.DoesNotContain("", destinos);               // «quitar el género» nunca es un destino
    }

    // --- Ordenar para mezclar ---

    [Theory]
    [InlineData("8A", "8A", EncajeTono.Igual)]
    [InlineData("8A", "9A", EncajeTono.Compatible)]
    [InlineData("12A", "1A", EncajeTono.Compatible)]   // la rueda da la vuelta
    [InlineData("8A", "8B", EncajeTono.Compatible)]    // relativa mayor/menor
    [InlineData("8A", "10A", EncajeTono.Salto)]
    [InlineData("8A", "3B", EncajeTono.Choque)]
    [InlineData("8A", "", EncajeTono.Desconocido)]
    public void Encaje_de_tonos_en_la_rueda_camelot(string a, string b, EncajeTono esperado)
        => Assert.Equal(esperado, OrdenMezcla.Encaje(a, b));

    [Theory]
    [InlineData(124u, 126u, 2)]
    [InlineData(85u, 170u, 0)]    // doble tempo: entra sin tocar el pitch
    [InlineData(128u, 0u, 0)]
    public void Diferencia_de_bpm_con_doble_y_mitad(uint a, uint b, int esperado)
        => Assert.Equal(esperado, OrdenMezcla.DiferenciaBpm(a, b));

    [Fact]
    public void Empieza_por_la_mas_tranquila_y_encadena_por_tono()
    {
        var canciones = new List<(Track, FichaDj?)>
        {
            (T("C", "Pico", "House", 128, key: "3B"), new FichaDj { Energia = 9 }),
            (T("A", "Abre", "House", 122, key: "8A"), new FichaDj { Energia = 3 }),
            (T("D", "Choca", "House", 123, key: "2B"), new FichaDj { Energia = 5 }),
            (T("B", "Sigue", "House", 123, key: "9A"), new FichaDj { Energia = 5 }),
        };

        var orden = OrdenMezcla.Ordenar(canciones).Select(p => p.Track.Title).ToList();

        Assert.Equal("Abre", orden[0]);
        Assert.Equal("Sigue", orden[1]);   // misma energía y BPM que «Choca», pero entra armónica
    }

    [Fact]
    public void El_orden_sale_igual_cada_vez()
    {
        var canciones = Enumerable.Range(0, 30)
            .Select(i => (T($"A{i}", $"T{i}", "House", (uint)(118 + i % 9), key: $"{1 + i % 12}{(i % 2 == 0 ? "A" : "B")}"), (FichaDj?)null))
            .ToList();

        var uno = OrdenMezcla.Ordenar(canciones).Select(p => p.Track.FilePath);
        var otro = OrdenMezcla.Ordenar(canciones.AsEnumerable().Reverse().ToList()).Select(p => p.Track.FilePath);

        Assert.Equal(uno, otro);
    }

    // --- Elegir modelo ---

    // Descargar un segundo modelo para probarlo lo ponía el primero de la lista, y «automático»
    // cambiaba de modelo sin que nadie lo decidiera.
    [Fact]
    public void Automatico_prefiere_el_recomendado_aunque_no_sea_el_primero()
    {
        Assert.Equal("llama3.2:latest", Core.Ai.OllamaClient.Automatico(new[] { "llama3.1:8b", "llama3.2:latest" }));
        Assert.Equal("qwen2.5:3b", Core.Ai.OllamaClient.Automatico(new[] { "qwen2.5:3b" }));
    }
}
