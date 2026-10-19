using System.Text;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// P7 · Seguir varias playlists y charts: cada lista guarda lo que tenía y, al volver a leerla, se
/// marcan las canciones que han entrado desde la última revisión de un día anterior.
/// </summary>
public class ListasSeguidasTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Archivo => Path.Combine(_dir, AlmacenListasSeguidas.NombreArchivo);

    private static readonly DateTime Lunes = new(2026, 10, 19, 10, 0, 0);

    private static PistaLista P(string artista, string titulo, string id = "", bool laTienes = false)
        => new() { Artista = artista, Titulo = titulo, IdPista = id, Enlace = id.Length > 0 ? "https://open.spotify.com/track/" + id : "", LaTienes = laTienes };

    private static readonly PistaLista[] Semana1 =
    {
        P("Bad Bunny", "DtMF", "id1"), P("Karol G", "Si Antes Te Hubiera Conocido", "id2"), P("Rosalía", "Despechá", "id3"),
    };

    // --- Identificar la lista ---

    // La misma playlist pegada de tres formas es la misma lista; el ID de Spotify conserva sus mayúsculas.
    [Fact]
    public void La_misma_playlist_pegada_de_formas_distintas_es_la_misma_lista()
    {
        const string id = "37i9dQZF1DXcBWIGoYBM5M";
        var claves = new[]
        {
            $"https://open.spotify.com/playlist/{id}",
            $"https://open.spotify.com/intl-es/playlist/{id}?si=abc123",
            $"spotify:playlist:{id}",
        }.Select(ListasSeguidas.ClavePlaylist).Distinct().ToList();

        Assert.Equal($"spotify:playlist:{id}", Assert.Single(claves));
        Assert.Equal("deezer:playlist:1234", ListasSeguidas.ClavePlaylist("https://www.deezer.com/es/playlist/1234"));
        Assert.Equal(ListasSeguidas.ClavePlaylist("https://www.deezer.com/playlist/1234"), ListasSeguidas.ClavePlaylist("deezer.com/en/playlist/1234"));
    }

    // El Top 100 no es el Top 50: compararlos daría cincuenta «nuevas» que no lo son.
    [Fact]
    public void Un_chart_es_su_fuente_su_pais_y_su_tamano()
    {
        var es = new ChartCountry("Spain", 1116189381, "es");
        Assert.NotEqual(TrendsViewModel.DescribirChart(ChartSource.Spotify, es, 50).Clave,
                        TrendsViewModel.DescribirChart(ChartSource.Spotify, es, 100).Clave);
        Assert.NotEqual(TrendsViewModel.DescribirChart(ChartSource.Spotify, es, 50).Clave,
                        TrendsViewModel.DescribirChart(ChartSource.Deezer, es, 50).Clave);

        var d = TrendsViewModel.DescribirChart(ChartSource.Spotify, es, 50);
        Assert.Equal(TipoListaSeguida.Chart, d.Tipo);
        Assert.Equal("Top 50 de Spain (Spotify)", d.Nombre);
        Assert.Equal("Spotify", d.Fuente);
        Assert.Equal("es", d.CodigoPais);
    }

    [Fact]
    public void Una_playlist_se_describe_con_su_enlace()
    {
        var d = PlaylistViewModel.DescribirPlaylist(" https://www.deezer.com/playlist/1234 ", "");
        Assert.Equal(TipoListaSeguida.Playlist, d.Tipo);
        Assert.Equal("deezer:playlist:1234", d.Clave);
        Assert.Equal("Playlist", d.Nombre);
        Assert.Equal("https://www.deezer.com/playlist/1234", d.Enlace);
    }

    // --- Qué es nuevo ---

    private static ListaSeguida Seguida() => new() { Clave = "chart:spotify:es:50", Nombre = "Top 50 de Spain (Spotify)" };

    // El primer día no hay con qué comparar; abrirla otra vez esa misma tarde tampoco cambia eso.
    [Fact]
    public void El_dia_que_se_empieza_a_seguir_no_hay_novedades()
    {
        var l = Seguida();
        Assert.Equal(NovedadesLista.Ninguna, ListasSeguidas.Registrar(l, Semana1, Lunes, completa: true));

        var tarde = Semana1.Append(P("Quevedo", "Columbia", "id4")).ToList();
        var nov = ListasSeguidas.Registrar(l, tarde, Lunes.AddHours(6), completa: true);
        Assert.Null(nov.Desde);
        Assert.Empty(nov.Nuevas);
    }

    // Al día siguiente se compara con lo que se vio el día antes; volver a abrirla ese día no borra
    // las novedades de la mañana; y al otro día la referencia pasa a ser la del día anterior.
    [Fact]
    public void Las_novedades_son_desde_el_ultimo_dia_que_se_reviso()
    {
        var l = Seguida();
        ListasSeguidas.Registrar(l, Semana1, Lunes, completa: true);

        var martes = new List<PistaLista>(Semana1) { P("Quevedo", "Columbia", "id4") };
        var nov = ListasSeguidas.Registrar(l, martes, Lunes.AddDays(1), completa: true);
        Assert.Equal(Lunes, nov.Desde);
        Assert.Equal(new[] { 3 }, nov.Nuevas);

        // Martes por la tarde: siguen siendo nuevas respecto al lunes.
        var otraVez = ListasSeguidas.Registrar(l, martes, Lunes.AddDays(1).AddHours(8), completa: true);
        Assert.Equal(Lunes, otraVez.Desde);
        Assert.Equal(new[] { 3 }, otraVez.Nuevas);

        // Miércoles: se compara con lo último del martes. Columbia ya no es nueva; esta sí.
        var miercoles = new List<PistaLista>(martes) { P("Myke Towers", "La Falda", "id5") };
        var nov3 = ListasSeguidas.Registrar(l, miercoles, Lunes.AddDays(2), completa: true);
        Assert.Equal(Lunes.AddDays(1).AddHours(8), nov3.Desde);
        Assert.Equal(new[] { 4 }, nov3.Nuevas);
    }

    // Una canción solo es nueva si no estaba ni por su ID ni por su artista y título: así no sale como
    // nueva porque la plataforma le cambió el ID ni porque retocó el título.
    [Fact]
    public void Ni_un_id_nuevo_ni_un_titulo_retocado_la_hacen_nueva()
    {
        var l = Seguida();
        ListasSeguidas.Registrar(l, Semana1, Lunes, completa: true);

        var martes = new[]
        {
            P("Bad Bunny", "DtMF", "otroId"),                                   // reedición: otro ID
            P("Karol G", "Si Antes Te Hubiera Conocido (Remix)", "id2"),        // título retocado, mismo ID
            P("Rosalía", "Despechá", ""),                                        // sin ID
            P("Bad Bunny", "Baile Inolvidable", "id9"),                          // de verdad nueva
        };
        var nov = ListasSeguidas.Registrar(l, martes, Lunes.AddDays(1), completa: true);
        Assert.Equal(new[] { 3 }, nov.Nuevas);
    }

    // Las sesiones numeradas son canciones distintas aunque compartan casi todo el título.
    [Fact]
    public void Una_sesion_nueva_de_una_serie_sale_como_nueva()
    {
        var l = Seguida();
        ListasSeguidas.Registrar(l, new[] { P("Bizarrap, Quevedo", "Bzrp Music Sessions, Vol. 52") }, Lunes, completa: true);
        var nov = ListasSeguidas.Registrar(l, new[] { P("Bizarrap, Quevedo", "Bzrp Music Sessions, Vol. 52"), P("Bizarrap, Shakira", "Bzrp Music Sessions, Vol. 53") },
                                           Lunes.AddDays(1), completa: true);
        Assert.Equal(new[] { 1 }, nov.Nuevas);
    }

    // Deezer dejó de responder a mitad: se compara, pero no se apunta. Si se apuntara, la siguiente
    // lectura entera daría por nuevas las canciones que faltaron esta vez.
    [Fact]
    public void Una_lectura_incompleta_no_cuenta_para_la_siguiente()
    {
        var l = Seguida();
        ListasSeguidas.Registrar(l, Semana1, Lunes, completa: true);

        var cortada = Semana1.Take(1).ToList();
        ListasSeguidas.Registrar(l, cortada, Lunes.AddDays(1), completa: false);
        Assert.Equal(3, l.Ultima!.Canciones);

        var entera = ListasSeguidas.Registrar(l, Semana1, Lunes.AddDays(2), completa: true);
        Assert.Empty(entera.Nuevas);
    }

    // Sin red, Tendencias enseña el chart guardado. Esa lectura, más vieja que la última, no avanza nada.
    [Fact]
    public void Una_lectura_mas_vieja_que_la_ultima_no_avanza()
    {
        var l = Seguida();
        ListasSeguidas.Registrar(l, Semana1, Lunes.AddDays(1), completa: true);
        ListasSeguidas.Registrar(l, Semana1.Take(2).ToList(), Lunes, completa: true);

        Assert.Equal(Lunes.AddDays(1), l.Ultima!.Fecha);
        Assert.Null(l.Referencia);
    }

    // --- Almacén ---

    [Fact]
    public void Seguir_dos_veces_la_misma_lista_la_guarda_una_vez()
    {
        var a = new AlmacenListasSeguidas(Archivo);
        a.Seguir(Seguida(), Semana1, Lunes);
        a.Seguir(Seguida(), Semana1.Take(1).ToList(), Lunes.AddHours(1));

        var l = Assert.Single(a.Todas());
        Assert.Equal(3, l.Ultima!.Canciones);
        Assert.True(a.Sigue("chart:spotify:es:50"));
    }

    [Fact]
    public void Las_nuevas_se_guardan_con_si_las_tienes_y_sobreviven_a_cerrar()
    {
        var a = new AlmacenListasSeguidas(Archivo);
        a.Seguir(Seguida(), Semana1, Lunes);
        var martes = new List<PistaLista>(Semana1) { P("Quevedo", "Columbia", "id4", laTienes: true), P("Myke Towers", "La Falda", "id5") };
        var nov = a.Registrar("chart:spotify:es:50", martes, Lunes.AddDays(1), completa: true, ahora: Lunes.AddDays(1), aviso: "Aviso de prueba");
        Assert.Equal(new[] { 3, 4 }, nov.Nuevas);
        Assert.Equal("", a.Guardar());

        var b = new AlmacenListasSeguidas(Archivo);
        Assert.Equal("", b.AvisoCarga);
        var l = Assert.Single(b.Todas());
        Assert.Equal(new[] { "Columbia", "La Falda" }, l.Nuevas.Select(n => n.Titulo));
        Assert.Equal(new[] { true, false }, l.Nuevas.Select(n => n.LaTienes));
        Assert.Equal(Lunes, l.Referencia!.Fecha);
        Assert.Equal(Lunes.AddDays(1), l.Revisada);
        Assert.Equal("Aviso de prueba", l.Aviso);

        // La tabla de Pendientes lo cuenta así.
        var fila = new ListaSeguidaFila(l);
        Assert.Equal("2 desde 19/10", fila.Nuevas);
        Assert.Equal("1", fila.TeFaltan);
    }

    [Fact]
    public void Registrar_una_lista_que_no_se_sigue_no_hace_nada()
    {
        var a = new AlmacenListasSeguidas(Archivo);
        Assert.Equal(NovedadesLista.Ninguna, a.Registrar("chart:spotify:fr:50", Semana1, Lunes, true, Lunes));
        Assert.Equal(0, a.Count);
    }

    [Fact]
    public void Dejar_de_seguir_la_quita()
    {
        var a = new AlmacenListasSeguidas(Archivo);
        var l = a.Seguir(Seguida(), Semana1, Lunes);
        Assert.Equal(1, a.DejarDeSeguir(new[] { l.Id }));
        Assert.False(a.Sigue(l.Clave));
    }

    // Las copias que se entregan no tocan lo guardado.
    [Fact]
    public void Lo_que_devuelve_es_una_copia()
    {
        var a = new AlmacenListasSeguidas(Archivo);
        var l = a.Seguir(Seguida(), Semana1, Lunes);
        l.Nombre = "Otro";
        l.Ultima!.Claves.Clear();
        var guardada = a.Buscar(l.Clave)!;
        Assert.Equal("Top 50 de Spain (Spotify)", guardada.Nombre);
        Assert.NotEmpty(guardada.Ultima!.Claves);
    }

    [Fact]
    public void Un_archivo_ilegible_se_aparta_y_se_avisa()
    {
        File.WriteAllText(Archivo, "{ no es una lista", Encoding.UTF8);
        var a = new AlmacenListasSeguidas(Archivo);
        Assert.Contains("se ha apartado", a.AvisoCarga);
        Assert.Single(Directory.GetFiles(_dir, AlmacenListasSeguidas.NombreArchivo + ".ilegible_*"));
    }

    // --- Lo que se lee de la red, como lo guarda una lista seguida ---

    [Fact]
    public void Las_pistas_llevan_el_id_de_spotify_o_el_enlace_de_deezer()
    {
        var spotify = new ChartTrack(1, "Bad Bunny", "DtMF", 0, "https://open.spotify.com/track/abc") { SpotifyId = "abc" };
        var deezer = new ChartTrack(2, "Karol G", "Provenza", 0, "https://www.deezer.com/track/123");
        var pistas = SeguimientoListas.PistasDe(new[] { spotify, deezer }, i => i == 1);

        Assert.Equal("abc", pistas[0].IdPista);
        Assert.Equal("https://www.deezer.com/track/123", pistas[1].IdPista);
        Assert.Equal(new[] { false, true }, pistas.Select(p => p.LaTienes));
    }

    // El tope de Spotify deja siempre las mismas 100: la lectura vale para comparar, pero hay que decir
    // que lo que entre por el final no se verá. Un corte de Deezer a mitad no vale para comparar.
    [Fact]
    public void El_tope_de_spotify_se_avisa_y_un_corte_de_deezer_no_cuenta()
    {
        var pistas = Enumerable.Range(1, 100).Select(i => new ChartTrack(i, "A", "T" + i, 0, "")).ToList();
        var spotify = new PlaylistLeida("Lista", PlataformaLista.Spotify, pistas, true, "") { Aviso = "x" };
        var deezer = new PlaylistLeida("Lista", PlataformaLista.Deezer, pistas, true, "") { Aviso = "Deezer dejó de responder en la página 2" };
        var entera = new PlaylistLeida("Lista", PlataformaLista.Deezer, pistas, false, "");

        Assert.True(SeguimientoListas.LecturaCompleta(spotify));
        Assert.Contains("las canciones nuevas no se verán", SeguimientoListas.AvisoLectura(spotify));
        Assert.False(SeguimientoListas.LecturaCompleta(deezer));
        Assert.StartsWith("Deezer dejó de responder", SeguimientoListas.AvisoLectura(deezer));
        Assert.True(SeguimientoListas.LecturaCompleta(entera));
        Assert.Equal("", SeguimientoListas.AvisoLectura(entera));
    }

    // --- Textos ---

    [Fact]
    public void Los_textos_de_novedades()
    {
        Assert.StartsWith("La sigues desde hoy", ListaFrenteBibliotecaViewModel.TextoNovedades(NovedadesLista.Ninguna, true));
        Assert.Equal("Ninguna canción nueva desde el 19/10.", ListaFrenteBibliotecaViewModel.TextoNovedades(new NovedadesLista(Lunes, Array.Empty<int>()), true));
        Assert.Equal("1 canción nueva desde el 19/10.", ListaFrenteBibliotecaViewModel.TextoNovedades(new NovedadesLista(Lunes, new[] { 4 }), true));
        Assert.Contains("no se leyó entera", ListaFrenteBibliotecaViewModel.TextoNovedades(new NovedadesLista(Lunes, new[] { 1, 2 }), false));
    }

    [Fact]
    public void El_resumen_de_actualizar_todas()
    {
        var texto = PendientesViewModel.TextoRevision(new[]
        {
            new SeguimientoListas.Revision("a", "Top 50", 50, 3, 2, Lunes, "", ""),
            new SeguimientoListas.Revision("b", "Mi lista", 120, 4, 3, Lunes, "", ""),
            new SeguimientoListas.Revision("c", "Rota", 0, 0, 0, null, "", "Esa lista no existe en Deezer o es privada."),
        });
        Assert.StartsWith("2 listas revisadas: 7 canciones nuevas, 5 que no tienes.", texto);
        Assert.Contains("1 no se pudo leer", texto);
        Assert.Contains("Añadir a pendientes", texto);

        Assert.Equal("1 lista revisada: ninguna canción nueva.",
            PendientesViewModel.TextoRevision(new[] { new SeguimientoListas.Revision("a", "Top 50", 50, 0, 0, Lunes, "", "") }));
    }

    [Fact]
    public void Sin_referencia_la_tabla_dice_desde_cuando_habra_novedades()
    {
        var fila = new ListaSeguidaFila(new ListaSeguida { Clave = "spotify:playlist:x", Nombre = "Mi lista" });
        Assert.Equal("A partir de mañana", fila.Nuevas);
        Assert.Equal("", fila.TeFaltan);
        Assert.Equal("Playlist de Spotify", fila.Tipo);
        Assert.Equal("Fallo", new ListaSeguidaFila(new ListaSeguida(), "Fallo").Aviso);
    }
}
