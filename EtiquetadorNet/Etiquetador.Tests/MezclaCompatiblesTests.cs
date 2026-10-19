using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// Ordenar para mezclar con momento de la sesión y canción de arranque (I13), y «Buscar compatibles»
/// (P2): qué entra después de una canción y sobre qué base lanzar una acapella. Todo con reglas
/// deterministas sobre lo que dicen las etiquetas, sin audio ni IA.
/// </summary>
public class MezclaCompatiblesTests
{
    private static readonly string Raiz = Path.Combine(Path.GetTempPath(), "Musica");

    private static Track T(string archivo, string key = "8A", uint bpm = 124, string? artista = null, string? titulo = null) => new()
    {
        FilePath = Path.Combine(Raiz, archivo),
        Key = key,
        Bpm = bpm,
        Artist = artista,
        Title = titulo,
    };

    private static FichaDj Ficha(MomentoSet m, int energia = 0) => new() { Momentos = m, Energia = energia };

    // --- I13: el momento de la sesión ---

    [Theory]
    [InlineData(MomentoSet.Pico, MomentoSet.Subida, 1)]
    [InlineData(MomentoSet.Cierre, MomentoSet.Apertura, 3)]
    [InlineData(MomentoSet.After, MomentoSet.Cierre, 1)]
    [InlineData(MomentoSet.Subida, MomentoSet.Pico, 0)]                              // avanzar no cuesta
    [InlineData(MomentoSet.Pico, MomentoSet.Pico, 0)]
    [InlineData(MomentoSet.Pico, MomentoSet.Apertura | MomentoSet.Pico, 0)]          // puede ir en el mismo momento
    [InlineData(MomentoSet.Subida | MomentoSet.Pico, MomentoSet.Subida, 0)]
    [InlineData(MomentoSet.Pico | MomentoSet.Cierre, MomentoSet.Apertura, 2)]        // desde lo más temprano posible
    [InlineData(MomentoSet.Ninguno, MomentoSet.Apertura, 0)]                         // sin momento no se sabe nada
    [InlineData(MomentoSet.Cierre, MomentoSet.Ninguno, 0)]
    public void Pasos_atras_en_la_sesion(MomentoSet desde, MomentoSet hasta, int esperado)
        => Assert.Equal(esperado, OrdenMezcla.PasosAtras(desde, hasta));

    [Theory]
    [InlineData(MomentoSet.Subida, MomentoSet.Cierre, 1)]                            // se salta el Pico
    [InlineData(MomentoSet.Apertura, MomentoSet.After, 3)]
    [InlineData(MomentoSet.Apertura, MomentoSet.Subida, 0)]                          // el siguiente no salta nada
    [InlineData(MomentoSet.Pico, MomentoSet.Pico, 0)]
    [InlineData(MomentoSet.Apertura | MomentoSet.Subida, MomentoSet.Cierre, 1)]      // desde lo más avanzado posible
    [InlineData(MomentoSet.Apertura, MomentoSet.Subida | MomentoSet.Cierre, 0)]
    [InlineData(MomentoSet.Pico, MomentoSet.Apertura, 0)]                            // retroceder no es saltar
    [InlineData(MomentoSet.Ninguno, MomentoSet.After, 0)]
    public void Momentos_saltados_hacia_delante(MomentoSet desde, MomentoSet hasta, int esperado)
        => Assert.Equal(esperado, OrdenMezcla.MomentosSaltados(desde, hasta));

    [Fact]
    public void Salirse_del_orden_cuesta_tres_por_momento_y_solo_con_las_dos_fichas()
    {
        var a = T("a.mp3");
        var b = T("b.mp3");

        var siguiente = OrdenMezcla.Coste(a, Ficha(MomentoSet.Pico), b, Ficha(MomentoSet.Cierre));
        var retrocede = OrdenMezcla.Coste(a, Ficha(MomentoSet.Pico), b, Ficha(MomentoSet.Subida));
        var salta = OrdenMezcla.Coste(a, Ficha(MomentoSet.Subida), b, Ficha(MomentoSet.Cierre));
        var sinFicha = OrdenMezcla.Coste(a, Ficha(MomentoSet.Pico), b, null);

        Assert.Equal(OrdenMezcla.CostePorMomento, retrocede - siguiente);
        Assert.Equal(OrdenMezcla.CostePorMomento, salta - siguiente);
        Assert.Equal(siguiente, sinFicha);
    }

    [Fact]
    public void El_coste_no_cambia_para_canciones_sin_ficha()
    {
        // Lo que ya hacía antes de añadir el momento: tono, tempo, energía y el desempate de subir.
        Assert.Equal(0, OrdenMezcla.Coste(T("a.mp3", "8A", 124), null, T("b.mp3", "8A", 124), null));
        Assert.Equal(1 + 2, OrdenMezcla.Coste(T("a.mp3", "8A", 124), null, T("b.mp3", "9A", 126), null));
        Assert.Equal(8 + 6, OrdenMezcla.Coste(T("a.mp3", "8A", 124), null, T("b.mp3", "3B", 0), null));
        Assert.Equal(0 + 1 + 0.5, OrdenMezcla.Coste(T("a.mp3", "8A", 124), null, T("b.mp3", "8A", 123), null));
    }

    [Fact]
    public void Un_tema_de_cierre_no_se_cuela_en_mitad_de_la_subida()
    {
        // El caso del hallazgo: el de Cierre encaja MEJOR de tono (mismo tono) que el de Pico
        // (vecino en la rueda). Sin el momento, entraría justo después de la subida.
        var canciones = new List<(Track, FichaDj?)>
        {
            (T("apertura.mp3", "8A"), Ficha(MomentoSet.Apertura)),
            (T("subida.mp3", "8A"), Ficha(MomentoSet.Subida)),
            (T("cierre.mp3", "8A"), Ficha(MomentoSet.Cierre)),
            (T("pico.mp3", "9A"), Ficha(MomentoSet.Pico)),
        };

        var orden = OrdenMezcla.Ordenar(canciones, primera: canciones[0].Item1)
                               .Select(p => Path.GetFileName(p.Track.FilePath)).ToList();
        var sinMomentos = OrdenMezcla.Ordenar(canciones.Select(c => (c.Item1, (FichaDj?)null)).ToList(), primera: canciones[0].Item1)
                                     .Select(p => Path.GetFileName(p.Track.FilePath)).ToList();

        Assert.Equal(new[] { "apertura.mp3", "subida.mp3", "pico.mp3", "cierre.mp3" }, orden);
        Assert.Equal(new[] { "apertura.mp3", "cierre.mp3", "subida.mp3", "pico.mp3" }, sinMomentos);
    }

    [Fact]
    public void Retroceder_se_acepta_si_lo_demas_entra_mucho_peor()
    {
        // Desde un Pico: la de Subida entra en el mismo tono y tempo; la de Cierre choca de tono (8)
        // y va 4 BPM por encima. Un paso atrás (3) cuesta menos que eso: el orden no es un corsé.
        var pico = (T("pico.mp3", "8A", 124), (FichaDj?)Ficha(MomentoSet.Pico));
        var subida = (T("subida.mp3", "8A", 124), (FichaDj?)Ficha(MomentoSet.Subida));
        var cierre = (T("cierre.mp3", "3B", 128), (FichaDj?)Ficha(MomentoSet.Cierre));

        var orden = OrdenMezcla.Ordenar(new[] { pico, subida, cierre }, primera: pico.Item1);

        Assert.Equal("subida.mp3", Path.GetFileName(orden[1].Track.FilePath));
        Assert.Equal(1, orden[1].PasosAtras);
        Assert.Contains("vuelve a Subida", orden[1].Transicion);
        Assert.Equal(1, OrdenMezcla.Resumir(orden).VueltasAtras);
    }

    [Fact]
    public void Empieza_por_la_elegida_aunque_no_sea_la_mas_tranquila()
    {
        var canciones = new List<(Track, FichaDj?)>
        {
            (T("tranquila.mp3", bpm: 118), new FichaDj { Energia = 2 }),
            (T("fuerte.mp3", bpm: 128), new FichaDj { Energia = 9 }),
            (T("media.mp3", bpm: 124), new FichaDj { Energia = 5 }),
        };

        var sinElegir = OrdenMezcla.Ordenar(canciones);
        // Otra instancia con la misma ruta: la elección viene de la tabla, no de la lista original.
        var elegida = OrdenMezcla.Ordenar(canciones, primera: T("fuerte.mp3"));

        Assert.Equal("tranquila.mp3", Path.GetFileName(sinElegir[0].Track.FilePath));
        Assert.Equal("fuerte.mp3", Path.GetFileName(elegida[0].Track.FilePath));
        Assert.Equal(3, elegida.Count);
        Assert.Equal(new[] { 1, 2, 3 }, elegida.Select(p => p.Posicion));
    }

    [Fact]
    public void Si_la_elegida_no_esta_en_la_lista_se_usa_el_criterio_de_siempre()
    {
        var canciones = new List<(Track, FichaDj?)>
        {
            (T("b.mp3", bpm: 126), null),
            (T("a.mp3", bpm: 120), null),
        };

        var orden = OrdenMezcla.Ordenar(canciones, primera: T("otra.mp3"));

        Assert.Equal("a.mp3", Path.GetFileName(orden[0].Track.FilePath));
    }

    [Fact]
    public void Con_primera_el_orden_sale_igual_cada_vez()
    {
        var canciones = Enumerable.Range(0, 25)
            .Select(i => (T($"t{i:00}.mp3", $"{1 + i % 12}{(i % 2 == 0 ? "A" : "B")}", (uint)(118 + i % 9)),
                          (FichaDj?)Ficha(Fichas.TodosLosMomentos[i % 5], 1 + i % 10)))
            .ToList();
        var primera = canciones[13].Item1;

        var uno = OrdenMezcla.Ordenar(canciones, primera).Select(p => p.Track.FilePath);
        var otro = OrdenMezcla.Ordenar(canciones.AsEnumerable().Reverse().ToList(), primera).Select(p => p.Track.FilePath);

        Assert.Equal(uno, otro);
    }

    [Fact]
    public void Resumen_y_lista_del_orden()
    {
        var orden = OrdenMezcla.Ordenar(new List<(Track, FichaDj?)>
        {
            (T("1.mp3", "8A", 120, "A", "Uno"), null),
            (T("2.mp3", "8A", 121, "B", "Dos"), null),
            (T("3.mp3", "", 122, "C", "Tres"), null),
            (T("4.mp3", "3B", 123, "D", null), null),
        });

        var r = OrdenMezcla.Resumir(orden);
        Assert.Equal(4, r.Canciones);
        Assert.Equal(3, r.Transiciones);
        Assert.Equal(0, r.VueltasAtras);
        Assert.Equal(0, r.Saltos);
        Assert.Equal(r.Transiciones, r.Armonicas + r.Choques + r.SinTonalidad + orden.Skip(1).Count(p => p.Tono == EncajeTono.Salto));
        Assert.StartsWith("4 canciones · ", r.Texto);

        var lista = OrdenMezcla.ParaLista(orden).ToList();
        Assert.Equal(orden.Select(p => p.Track.FilePath), lista.Select(i => i.FilePath));
        Assert.Equal("4", Path.GetFileNameWithoutExtension(lista.Single(i => i.Artist == "D").Title));   // sin título, el archivo
    }

    // --- P2: buscar compatibles ---

    [Fact]
    public void Entran_el_mismo_tono_y_los_vecinos_de_la_rueda_y_no_los_que_chocan()
    {
        var x = T("x.mp3", "8A", 124);
        var bib = new[]
        {
            x,
            T("igual.mp3", "8A", 124), T("vecina.mp3", "9A", 124), T("relativa.mp3", "8B", 124),
            T("salto.mp3", "10A", 124), T("choque.mp3", "3B", 124), T("sin-tono.mp3", "", 124),
        };

        var r = Compatibles.Buscar(x, bib);
        var conSaltos = Compatibles.Buscar(x, bib, new OpcionesCompatibles { IncluirSaltos = true });

        Assert.Equal(new[] { "igual.mp3", "relativa.mp3", "vecina.mp3" },
                     r.Canciones.Select(c => Path.GetFileName(c.Track.FilePath)));
        Assert.Contains(conSaltos.Canciones, c => c.Track.FileName == "salto.mp3" && c.Tono == EncajeTono.Salto);
        Assert.DoesNotContain(conSaltos.Canciones, c => c.Track.FileName is "choque.mp3" or "sin-tono.mp3" or "x.mp3");
        Assert.Equal("", r.Aviso);
    }

    [Theory]
    [InlineData(124u, 131u, true)]    // 7 de 131: 5,3 %
    [InlineData(124u, 117u, true)]    // 7 de 124: 5,6 %
    [InlineData(124u, 132u, false)]   // 8 de 132: 6,1 %, más de un semitono
    [InlineData(124u, 116u, false)]
    [InlineData(86u, 172u, true)]     // doble tempo exacto
    [InlineData(85u, 174u, true)]     // 4 de 174 a doble tempo: 2,3 %
    public void El_margen_de_tempo_es_del_seis_por_ciento_contando_doble_y_mitad(uint bpmX, uint bpmC, bool entra)
    {
        var x = T("x.mp3", "8A", bpmX);
        var c = T("c.mp3", "8A", bpmC);

        var r = Compatibles.Buscar(x, new[] { x, c });

        Assert.Equal(entra, r.Canciones.Count == 1);
    }

    [Fact]
    public void La_diferencia_dice_si_va_a_doble_o_a_mitad_de_tempo()
    {
        var x = T("x.mp3", "8A", 86);
        var r = Compatibles.Buscar(x, new[] { x, T("doble.mp3", "8A", 174), T("igual.mp3", "8A", 88) });

        Assert.Equal("+2 BPM (a doble tempo)", r.Canciones.Single(c => c.Track.FileName == "doble.mp3").TextoDiferencia);
        Assert.Equal("+2 BPM", r.Canciones.Single(c => c.Track.FileName == "igual.mp3").TextoDiferencia);

        var rapida = T("rapida.mp3", "8A", 172);
        var mitad = Compatibles.Buscar(rapida, new[] { rapida, T("lenta.mp3", "8A", 85) }).Canciones.Single();
        Assert.Equal(RelacionTempo.Mitad, mitad.Relacion);
        Assert.Equal("-2 BPM (a mitad de tempo)", mitad.TextoDiferencia);
    }

    [Fact]
    public void Ordenadas_de_la_que_mejor_entra_a_la_que_peor()
    {
        var x = T("x.mp3", "8A", 124);
        var bib = new[]
        {
            x, T("a-vecina-lejos.mp3", "9A", 129), T("b-igual-cerca.mp3", "8A", 125),
            T("c-igual-exacta.mp3", "8A", 124), T("d-vecina-exacta.mp3", "9A", 124),
        };

        var orden = Compatibles.Buscar(x, bib).Canciones.Select(c => c.Track.FileName).ToList();

        Assert.Equal(new[] { "c-igual-exacta.mp3", "b-igual-cerca.mp3", "d-vecina-exacta.mp3", "a-vecina-lejos.mp3" }, orden);
    }

    [Fact]
    public void La_energia_de_la_ficha_entra_en_el_orden()
    {
        var x = T("x.mp3");
        var baja = T("a-baja.mp3");
        var sube = T("b-sube.mp3");
        var fichas = new Dictionary<string, FichaDj>
        {
            [x.FilePath] = new() { Energia = 6 },
            [baja.FilePath] = new() { Energia = 3 },
            [sube.FilePath] = new() { Energia = 7 },
        };

        var r = Compatibles.Buscar(x, new[] { x, baja, sube }, fichaDe: t => fichas.GetValueOrDefault(t.FilePath));

        Assert.Equal("b-sube.mp3", r.Canciones[0].Track.FileName);   // bajar energía rompe la subida
    }

    [Fact]
    public void Las_que_no_tienen_bpm_no_entran_y_se_cuentan()
    {
        var x = T("x.mp3", "8A", 124);
        var r = Compatibles.Buscar(x, new[] { x, T("sin-bpm.mp3", "8A", 0), T("choca-sin-bpm.mp3", "3B", 0) });

        Assert.Empty(r.Canciones);
        Assert.Equal(1, r.SinBpm);   // la que choca de tono no habría entrado de todas formas
    }

    [Fact]
    public void Sin_bpm_en_la_consultada_solo_se_compara_el_tono()
    {
        var x = T("x.mp3", "8A", 0);
        var r = Compatibles.Buscar(x, new[] { x, T("a.mp3", "8A", 90), T("b.mp3", "9A", 140), T("c.mp3", "2A", 90) });

        Assert.Equal(2, r.Canciones.Count);
        Assert.Contains("BPM", r.Aviso);
        Assert.All(r.Canciones, c => Assert.Equal("Sin BPM", c.TextoDiferencia));   // no «Mismo tempo»: no se sabe
    }

    [Fact]
    public void Sin_tonalidad_en_la_consultada_no_hay_lista_y_se_dice_por_que()
    {
        var x = T("x.mp3", "", 124);
        var r = Compatibles.Buscar(x, new[] { x, T("a.mp3", "8A", 124) });

        Assert.Empty(r.Canciones);
        Assert.Contains("tonalidad", r.Aviso);
    }

    [Fact]
    public void No_propone_la_propia_cancion_ni_otras_versiones_suyas()
    {
        var x = T("Karol G - Bichota.mp3", "8A", 96, "Karol G", "Bichota");
        var bib = new[]
        {
            x,
            T("Karol G - Bichota (copia).mp3", "8A", 96, "Karol G", "Bichota"),
            T("Karol G - Bichota (Hype Intro).mp3", "8A", 96, "Karol G", "Bichota (Hype Intro)"),
            T("Karol G, Nicki Minaj - Bichota (Remix).mp3", "8A", 96, "Karol G, Nicki Minaj", "Bichota (Remix)"),
            T("sin etiquetas/Karol G - Bichota (Acapella).mp3", "8A", 96),
            T("Karol G - Tusa.mp3", "8A", 96, "Karol G", "Tusa"),
        };

        var r = Compatibles.Buscar(x, bib);

        Assert.Equal(new[] { "Karol G - Tusa.mp3" }, r.Canciones.Select(c => c.Track.FileName));
    }

    [Fact]
    public void Desde_un_mashup_no_propone_las_canciones_que_ya_lleva_dentro()
    {
        var x = T("Rauw Alejandro, Eiffel 65 - Blue Vs Baby Hello (DJ Mashup).mp3", "1A", 130, "Rauw Alejandro, Eiffel 65", "Blue Vs Baby Hello");
        var bib = new[]
        {
            x,
            T("Rauw Alejandro, Bizarrap - Baby Hello.mp3", "1A", 130, "Rauw Alejandro, Bizarrap", "Baby Hello"),
            T("Eiffel 65 - Blue.mp3", "1A", 130, "Eiffel 65", "Blue (Da Ba Dee)"),
            T("Otro - Otra.mp3", "1A", 130, "Otro", "Otra"),
        };

        var r = Compatibles.Buscar(x, bib);

        Assert.Equal(new[] { "Otro - Otra.mp3" }, r.Canciones.Select(c => c.Track.FileName));
    }

    [Fact]
    public void Para_una_acapella_busca_bases_con_margen_estrecho()
    {
        var x = T("Daddy Yankee - Gasolina (Acapella).mp3", "8A", 96, "Daddy Yankee", "Gasolina (Acapella)");
        var bib = new[]
        {
            x,
            T("A - Base cerca.mp3", "8A", 98, "A", "Base cerca"),                 // 2 de 98: 2 %
            T("B - Base lejos.mp3", "8A", 100, "B", "Base lejos"),                // 4 de 100: 4 %, vale para un tema pero no aquí
            T("C - Otra (Acapella).mp3", "8A", 96, "C", "Otra (Acapella)"),
            T("Acapellas/D - Voz.mp3", "8A", 96, "D", "Voz"),                      // acapella por la carpeta
            T("E x F - Uno x Dos (DJ Mashup).mp3", "8A", 96, "E x F", "Uno x Dos"),
        };

        var r = Compatibles.Buscar(x, bib);

        Assert.True(r.EsAcapella);
        Assert.Equal(0.03, r.Margen);
        Assert.Equal(new[] { "A - Base cerca.mp3" }, r.Canciones.Select(c => c.Track.FileName));

        // La misma base, consultada desde un tema normal, sí entra: el margen ancho es del 6 %.
        var tema = T("G - Tema.mp3", "8A", 96, "G", "Tema");
        Assert.Contains(Compatibles.Buscar(tema, bib).Canciones, c => c.Track.FileName == "B - Base lejos.mp3");
    }

    [Fact]
    public void El_margen_estrecho_de_las_acapellas_vale_en_los_dos_sentidos()
    {
        var acapella = T("C - Voz (Acapella).mp3", "8A", 96, "C", "Voz (Acapella)");
        var cerca = T("A - Base cerca.mp3", "8A", 98, "A", "Base cerca");      // 2 %
        var lejos = T("B - Base lejos.mp3", "8A", 100, "B", "Base lejos");     // 4 %
        var bib = new[] { acapella, cerca, lejos };

        // Desde la acapella solo entra la base cercana...
        Assert.Equal(new[] { "A - Base cerca.mp3" }, Compatibles.Buscar(acapella, bib).Canciones.Select(c => c.Track.FileName));
        // ...y desde las bases, la acapella sale solo en la cercana: antes salía también en la lejana.
        Assert.Contains(Compatibles.Buscar(cerca, bib).Canciones, c => c.Track == acapella);
        Assert.DoesNotContain(Compatibles.Buscar(lejos, bib).Canciones, c => c.Track == acapella);

        // A la misma distancia, un tema normal sí entra: el margen ancho no cambia para el resto.
        var tema = T("D - Tema.mp3", "8A", 96, "D", "Tema");
        Assert.Contains(Compatibles.Buscar(lejos, new[] { lejos, tema }).Canciones, c => c.Track == tema);
    }

    [Fact]
    public void Para_una_acapella_a_igual_coste_van_primero_las_instrumentales()
    {
        var x = T("Voz (Acapella).mp3", "8A", 96, "V", "Voz (Acapella)");
        var bib = new[]
        {
            x,
            T("a - Tema.mp3", "8A", 96, "a", "Tema"),
            T("b - Base (Instrumental).mp3", "8A", 96, "b", "Base (Instrumental)"),
        };

        var r = Compatibles.Buscar(x, bib);

        Assert.Equal(TipoPieza.Instrumental, r.Canciones[0].Tipo);
    }

    [Fact]
    public void La_busqueda_sale_igual_cada_vez()
    {
        var x = T("x.mp3", "5A", 120);
        var bib = Enumerable.Range(0, 60).Select(i => T($"t{i:00}.mp3", $"{1 + i % 12}{(i % 3 == 0 ? "B" : "A")}", (uint)(114 + i % 13))).Append(x).ToList();

        var uno = Compatibles.Buscar(x, bib).Canciones.Select(c => c.Track.FilePath);
        var otro = Compatibles.Buscar(x, bib.AsEnumerable().Reverse()).Canciones.Select(c => c.Track.FilePath);

        Assert.Equal(uno, otro);
    }
}
