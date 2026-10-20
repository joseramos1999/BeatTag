using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// P1 · Ordenar para mezclar las listas de Tendencias y Playlists: lo que tienes de la lista, en el
/// orden de «Ordenar para mezclar», diciendo qué se queda fuera y por qué.
/// </summary>
public class OrdenListaTests
{
    private static Track Pista(string nombre, uint bpm, string? tono)
        => new() { FilePath = Rutas.Archivo(nombre), Folder = Rutas.Carpeta, Artist = "A", Title = nombre, Bpm = bpm, Key = tono, DurationSeconds = 200 };

    private static TrendRow Fila(int posicion, string ruta = "")
        => new() { Position = posicion, Artist = "A", Title = "T" + posicion, FilePath = ruta };

    [Fact]
    public void Se_ordena_lo_que_tienes_y_se_cuenta_lo_que_queda_fuera()
    {
        var lenta = Pista("lenta.mp3", 92, "8A");
        var media = Pista("media.mp3", 95, "9A");
        var soloTono = Pista("solo tono.mp3", 0, "8B");
        var soloBpm = Pista("solo bpm.mp3", 100, null);
        var sinNada = Pista("sin nada.mp3", 0, null);
        var biblioteca = new[] { lenta, media, soloTono, soloBpm, sinNada };

        var filas = new[]
        {
            Fila(1, media.FilePath), Fila(2), Fila(3, lenta.FilePath), Fila(4, soloTono.FilePath),
            Fila(5, soloBpm.FilePath), Fila(6, sinNada.FilePath), Fila(7), Fila(8, media.FilePath),   // la 8 repite archivo
            Fila(9, Rutas.Archivo("borrada.mp3")),                                                  // ya no está en la biblioteca
        };

        var prep = ListaFrenteBibliotecaViewModel.PrepararOrden(filas, biblioteca, _ => null);

        Assert.Equal(2, prep.NoEnBiblioteca);
        Assert.Equal(1, prep.SinDatos);
        Assert.Equal(1, prep.Repetidas);
        Assert.Equal(1, prep.FueraDeLaBiblioteca);
        // Con solo BPM o solo tonalidad SÍ entra: el orden la coloca con lo que tiene.
        Assert.Equal(new[] { media, lenta, soloTono, soloBpm }.Select(t => t.FilePath).OrderBy(x => x),
                     prep.ParaOrdenar.Select(x => x.Track.FilePath).OrderBy(x => x));
        Assert.Equal(1, prep.PosicionEnLista[media.FilePath]);   // la primera vez que sale en la lista

        var orden = OrdenMezcla.Ordenar(prep.ParaOrdenar);
        Assert.Equal(lenta.FilePath, orden[0].Track.FilePath);   // empieza por la más tranquila
        Assert.Equal(EncajeTono.Compatible, orden[1].Tono);
        Assert.Equal(media.FilePath, orden[1].Track.FilePath);

        var resumen = ListaFrenteBibliotecaViewModel.ResumenOrden(orden, prep);
        Assert.StartsWith("4 canciones en orden", resumen);
        Assert.Contains("Fuera del orden: 2 que no están en la biblioteca, 1 sin BPM ni tonalidad, 1 cuyo archivo ya ha salido de la biblioteca", resumen);
        Assert.Contains("Una fila apunta al mismo archivo", resumen);
        Assert.Contains("Sin fichas de DJ", resumen);
    }

    // Con fichas, la energía entra en el orden como en el Asistente.
    [Fact]
    public void Las_fichas_de_dj_entran_en_el_orden()
    {
        var a = Pista("a.mp3", 100, "8A");
        var b = Pista("b.mp3", 100, "8A");
        var fichas = new Dictionary<string, FichaDj> { [a.FilePath] = new() { Energia = 8 }, [b.FilePath] = new() { Energia = 3 } };

        var prep = ListaFrenteBibliotecaViewModel.PrepararOrden(new[] { Fila(1, a.FilePath), Fila(2, b.FilePath) }, new[] { a, b },
                                                                fichas.GetValueOrDefault);
        var orden = OrdenMezcla.Ordenar(prep.ParaOrdenar);

        Assert.Equal(b.FilePath, orden[0].Track.FilePath);   // la de menos energía abre
        Assert.DoesNotContain("Sin fichas de DJ", ListaFrenteBibliotecaViewModel.ResumenOrden(orden, prep));
    }

    // La fila de la tabla enseña lo que se exporta al M3U8, en ese orden y con la duración.
    [Fact]
    public void La_fila_del_orden_cuenta_la_transicion_y_su_puesto_en_la_lista()
    {
        var a = Pista("a.mp3", 120, "8A");
        var b = Pista("b.mp3", 122, "3B");
        var prep = ListaFrenteBibliotecaViewModel.PrepararOrden(new[] { Fila(7, a.FilePath), Fila(2, b.FilePath) }, new[] { a, b }, _ => null);
        var orden = OrdenMezcla.Ordenar(prep.ParaOrdenar);

        var filas = orden.Select(p => new PasoListaFila(p, prep.PosicionEnLista[p.Track.FilePath])).ToList();
        Assert.Equal(7, filas[0].EnLista);
        Assert.Equal("", filas[0].Transicion);
        Assert.Equal("Choque de tono · +2 BPM", filas[1].Transicion);
        Assert.True(filas[1].Choca);
        Assert.Equal("122", filas[1].Bpm);

        var m3u = PlaylistWriter.Build(filas.Select(f => new PlaylistItem(f.FilePath, f.Artist, f.Title, f.Paso.Track.DurationSeconds)));
        Assert.True(m3u.IndexOf(a.FilePath, StringComparison.Ordinal) < m3u.IndexOf(b.FilePath, StringComparison.Ordinal));
        Assert.Contains("#EXTINF:200,", m3u);
    }

    // Con una sola canción no hay transiciones: «0 de 0 transiciones armónicas · 0 choques de tono» no
    // informaba de nada.
    [Fact]
    public void Sin_nada_fuera_no_se_dice_nada_de_fuera()
    {
        var a = Pista("a.mp3", 120, "8A");
        var prep = ListaFrenteBibliotecaViewModel.PrepararOrden(new[] { Fila(1, a.FilePath) }, new[] { a }, _ => null);
        var resumen = ListaFrenteBibliotecaViewModel.ResumenOrden(OrdenMezcla.Ordenar(prep.ParaOrdenar), prep);
        Assert.DoesNotContain("Fuera del orden", resumen);
        Assert.StartsWith("1 canción en orden.", resumen);
        Assert.DoesNotContain("transicion", resumen);
        Assert.DoesNotContain("choque", resumen);
    }

    // Revisión: «1 choques de tono» salía siempre en plural.
    [Fact]
    public void Cada_cifra_del_resumen_concuerda_en_numero()
    {
        var a = Pista("a.mp3", 120, "8A");
        var b = Pista("b.mp3", 122, "3B");
        var prep = ListaFrenteBibliotecaViewModel.PrepararOrden(new[] { Fila(1, a.FilePath), Fila(2, b.FilePath), Fila(3) }, new[] { a, b }, _ => null);
        var resumen = ListaFrenteBibliotecaViewModel.ResumenOrden(OrdenMezcla.Ordenar(prep.ParaOrdenar), prep);

        Assert.StartsWith("2 canciones en orden · 0 de 1 transición armónica · 1 choque de tono.", resumen);
        Assert.Contains("Fuera del orden: 1 que no está en la biblioteca.", resumen);

        var c = Pista("c.mp3", 121, "8A");
        var prep2 = ListaFrenteBibliotecaViewModel.PrepararOrden(new[] { Fila(1, a.FilePath), Fila(2, c.FilePath) }, new[] { a, c }, _ => null);
        Assert.StartsWith("2 canciones en orden · 1 de 1 transición armónica · ningún choque de tono.",
                          ListaFrenteBibliotecaViewModel.ResumenOrden(OrdenMezcla.Ordenar(prep2.ParaOrdenar), prep2));
    }

    // interfaz-6: el resumen de Tendencias y Playlists cuenta, como el del Asistente, las transiciones
    // que vuelven a un momento anterior de la sesión o se lo saltan. Antes la columna lo decía («vuelve
    // a…») y el resumen no.
    [Fact]
    public void El_resumen_cuenta_los_momentos_de_la_sesion()
    {
        var pico = Pista("pico.mp3", 124, "8A");
        var subida = Pista("subida.mp3", 124, "8A");
        var fichas = new Dictionary<string, FichaDj>
        {
            [pico.FilePath] = new() { Momentos = MomentoSet.Pico },
            [subida.FilePath] = new() { Momentos = MomentoSet.Subida },
        };
        var prep = ListaFrenteBibliotecaViewModel.PrepararOrden(new[] { Fila(1, pico.FilePath), Fila(2, subida.FilePath) },
                                                                new[] { pico, subida }, fichas.GetValueOrDefault);
        var orden = OrdenMezcla.Ordenar(prep.ParaOrdenar, pico);   // empezar por el pico obliga a volver atrás

        var resumen = ListaFrenteBibliotecaViewModel.ResumenOrden(orden, prep);
        Assert.Contains("1 vuelve a un momento anterior de la sesión", resumen);
        // Las fichas solo con momento también son fichas: el orden no sale «solo por tono y BPM».
        Assert.DoesNotContain("Sin fichas de DJ", resumen);

        var apertura = Pista("apertura.mp3", 124, "8A");
        var cierre = Pista("cierre.mp3", 124, "8A");
        var fichas2 = new Dictionary<string, FichaDj>
        {
            [apertura.FilePath] = new() { Momentos = MomentoSet.Apertura, Energia = 2 },
            [cierre.FilePath] = new() { Momentos = MomentoSet.Cierre, Energia = 3 },
        };
        var prep2 = ListaFrenteBibliotecaViewModel.PrepararOrden(new[] { Fila(1, apertura.FilePath), Fila(2, cierre.FilePath) },
                                                                 new[] { apertura, cierre }, fichas2.GetValueOrDefault);
        Assert.Contains("1 se salta un momento de la sesión",
                        ListaFrenteBibliotecaViewModel.ResumenOrden(OrdenMezcla.Ordenar(prep2.ParaOrdenar), prep2));
    }

    // «Empezar por esta» (I13) en Tendencias y Playlists: el orden se rehace abriendo con la elegida, y
    // si ya no está en la biblioteca no se puede abrir con ella.
    [Fact]
    public void Empezar_por_esta_abre_el_orden_con_la_elegida()
    {
        var lenta = Pista("lenta.mp3", 92, "8A");
        var rapida = Pista("rapida.mp3", 128, "9A");
        var media = Pista("media.mp3", 110, "8B");
        var prep = ListaFrenteBibliotecaViewModel.PrepararOrden(
            new[] { Fila(1, lenta.FilePath), Fila(2, rapida.FilePath), Fila(3, media.FilePath) }, new[] { lenta, rapida, media }, _ => null);

        Assert.Equal(lenta.FilePath, OrdenMezcla.Ordenar(prep.ParaOrdenar)[0].Track.FilePath);   // sin elegir, la más tranquila

        var primera = ListaFrenteBibliotecaViewModel.PrimeraDelOrden(prep, rapida.FilePath.ToUpperInvariant());
        Assert.Same(rapida, primera);
        var orden = OrdenMezcla.Ordenar(prep.ParaOrdenar, primera);
        Assert.Equal(rapida.FilePath, orden[0].Track.FilePath);
        Assert.Equal(3, orden.Count);

        // Enviada a la papelera desde otra página: la biblioteca de ahora ya no la tiene.
        var sinRapida = ListaFrenteBibliotecaViewModel.PrepararOrden(
            new[] { Fila(1, lenta.FilePath), Fila(2, rapida.FilePath), Fila(3, media.FilePath) }, new[] { lenta, media }, _ => null);
        Assert.Null(ListaFrenteBibliotecaViewModel.PrimeraDelOrden(sinRapida, rapida.FilePath));
    }

    // «Buscar compatibles» y «Ver sus versiones» del menú del orden reciben la fila: tiene que dar su archivo.
    [Fact]
    public void La_fila_del_orden_da_su_archivo_a_las_acciones_de_cancion()
    {
        var a = Pista("a.mp3", 120, "8A");
        var paso = OrdenMezcla.Ordenar(new List<(Track, FichaDj?)> { (a, null) })[0];
        var fila = new PasoListaFila(paso, 4);

        Assert.IsAssignableFrom<IFilaConArchivo>(fila);
        Assert.Equal(a.FilePath, Etiquetador.App.Acciones.AccionesCancion.RutaDe(fila));
    }

    // Plurales de las líneas de estado de Tendencias y Playlists.
    [Theory]
    [InlineData(0, "No falta ninguna.")]
    [InlineData(1, "Falta 1.")]
    [InlineData(7, "Faltan 7.")]
    public void Lo_que_falta_concuerda_en_numero(int faltan, string esperado)
        => Assert.Equal(esperado, ListaFrenteBibliotecaViewModel.TextoFaltan(faltan));

    [Fact]
    public void El_resumen_del_cruce_no_depende_del_numero()
    {
        Assert.Equal("49 de 50 en la biblioteca (98 %)", ListaFrenteBibliotecaViewModel.TextoResumen(49, 50));
        Assert.Equal("1 de 1 en la biblioteca (100 %)", ListaFrenteBibliotecaViewModel.TextoResumen(1, 1));
        Assert.Equal("0 de 0 en la biblioteca (0 %)", ListaFrenteBibliotecaViewModel.TextoResumen(0, 0));
        Assert.Equal("1 canción", ListaFrenteBibliotecaViewModel.Canciones(1));
        Assert.Equal("3 canciones", ListaFrenteBibliotecaViewModel.Canciones(3));
    }
}
