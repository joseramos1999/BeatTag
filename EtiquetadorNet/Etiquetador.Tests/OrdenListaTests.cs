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

        Assert.Equal(2, prep.NoLasTienes);
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
        Assert.Contains("Fuera del orden: 2 que no tienes, 1 sin BPM ni tonalidad, 1 que ya no está en la biblioteca", resumen);
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

    [Fact]
    public void Sin_nada_fuera_no_se_dice_nada_de_fuera()
    {
        var a = Pista("a.mp3", 120, "8A");
        var prep = ListaFrenteBibliotecaViewModel.PrepararOrden(new[] { Fila(1, a.FilePath) }, new[] { a }, _ => null);
        var resumen = ListaFrenteBibliotecaViewModel.ResumenOrden(OrdenMezcla.Ordenar(prep.ParaOrdenar), prep);
        Assert.DoesNotContain("Fuera del orden", resumen);
        Assert.StartsWith("1 canción en orden · 0 de 0 transiciones armónicas", resumen);
    }
}
