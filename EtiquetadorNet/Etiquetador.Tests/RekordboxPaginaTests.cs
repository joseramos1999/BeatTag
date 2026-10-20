using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// La página rekordbox (equipo «rekordbox», tanda 4): los textos que se enseñan tras cruzar y reparar
/// (P3) y cómo se reúnen las listas que se exportan (P11). La lógica de verdad está en Core
/// (RekordboxCruceTests, RekordboxExportacionTests); aquí, lo que decide la página.
/// </summary>
public class RekordboxPaginaTests
{
    private static string R(string nombre) => Path.Combine(Path.GetTempPath(), "rb-pagina", nombre);

    private static PistaRekordbox Pista(string ruta) => new() { Ruta = ruta, Location = RekordboxRelocator.PathToLocation(ruta) };

    private static PistaRota Rota(ComoReparar como) => new(Pista(R(Guid.NewGuid().ToString("N") + ".mp3")), como,
        como is ComoReparar.RenombradaPorBeatTag or ComoReparar.MismoNombreYTamano or ComoReparar.MismoNombreYDuracion ? R("nueva.mp3") : "");

    // ---------------------------------------------------------------------------------------------
    // P3: textos del cruce y de la reparación
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void El_resumen_de_rotas_dice_cuantas_se_reparan_y_por_que_no_las_demas()
    {
        var rotas = new[]
        {
            Rota(ComoReparar.RenombradaPorBeatTag), Rota(ComoReparar.RenombradaPorBeatTag),
            Rota(ComoReparar.MismoNombreYDuracion),
            Rota(ComoReparar.VariasCandidatas),
            Rota(ComoReparar.SinEncontrar), Rota(ComoReparar.SinEncontrar),
        };

        var texto = RekordboxViewModel.TextoRotas(rotas);

        Assert.Equal("Se pueden reparar 3 de 6: 2 renombradas o movidas por BeatTag, 1 encontrada por nombre y duración. "
                   + "De las demás, 1 tiene varios archivos posibles, 2 no se encuentran en la biblioteca.", texto);
    }

    // Sin ninguna reparable no se habla de «las demás»: se dice por qué no se repara ninguna.
    [Fact]
    public void Sin_reparables_se_dice_el_motivo_de_cada_una()
    {
        Assert.Equal("La entrada rota no se puede reparar desde aquí: 1 está en un disco no conectado.",
                     RekordboxViewModel.TextoRotas(new[] { Rota(ComoReparar.DiscoNoConectado) }));
        Assert.Equal("Ninguna de las 3 entradas rotas se puede reparar desde aquí: 1 ya está en rekordbox con la ruta nueva, 2 comparten archivo con otras entradas.",
                     RekordboxViewModel.TextoRotas(new[] { Rota(ComoReparar.YaEnRekordbox), Rota(ComoReparar.MismoDestino), Rota(ComoReparar.MismoDestino) }));
        Assert.Equal("Ninguna entrada de la colección apunta a un archivo que no existe.", RekordboxViewModel.TextoRotas(Array.Empty<PistaRota>()));
    }

    [Fact]
    public void El_estado_tras_cruzar_concuerda_en_numero()
    {
        var una = new ResultadoCruce
        {
            FaltanEnRekordbox = new[] { new Track { FilePath = R("a.mp3") } },
            Rotas = new[] { Rota(ComoReparar.SinEncontrar) },
            Presentes = new[] { new PistaPresente(Pista(R("fuera.mp3")), null) },
        };
        Assert.Equal("1 canción de la biblioteca no está en rekordbox; 1 entrada de rekordbox apunta a un archivo que no existe. "
                   + "1 pista de rekordbox está fuera de las carpetas de la biblioteca.", RekordboxViewModel.TextoCruce(una));

        var ninguna = new ResultadoCruce();
        Assert.Equal("Todas las canciones de la biblioteca están en rekordbox; ninguna entrada de rekordbox apunta a un archivo que no existe.",
                     RekordboxViewModel.TextoCruce(ninguna));
    }

    [Fact]
    public void La_cabecera_de_la_coleccion_dice_de_cuando_es()
    {
        var c = new ColeccionRekordbox
        {
            Producto = "rekordbox 7.2.17",
            Pistas = new[] { Pista(R("a.mp3")), Pista(R("b.mp3")) },
            SinArchivo = 1,
        };
        var texto = RekordboxViewModel.TextoColeccion(R("musica.xml"), c, new DateTime(2026, 8, 15, 22, 0, 0), new DateTime(2026, 10, 20));
        Assert.Equal("«musica.xml» · rekordbox 7.2.17 · exportada el 15/08/2026 (hace 66 días) · 2 pistas · 1 pista de streaming", texto);
        Assert.Contains("(ayer)", RekordboxViewModel.TextoColeccion(R("x.xml"), c, new DateTime(2026, 10, 19), new DateTime(2026, 10, 20)));
    }

    // Abrir por error la exportación de listas de BeatTag no se toma por la colección de rekordbox.
    [Fact]
    public void La_exportacion_de_BeatTag_no_pasa_por_la_coleccion()
    {
        Assert.True(RekordboxViewModel.EsExportacionDeBeatTag(new ColeccionRekordbox { Producto = "BeatTag 2.4.0" }));
        Assert.False(RekordboxViewModel.EsExportacionDeBeatTag(new ColeccionRekordbox { Producto = "rekordbox 7.2.17" }));
        Assert.False(RekordboxViewModel.EsExportacionDeBeatTag(new ColeccionRekordbox { Producto = "" }));
    }

    [Fact]
    public void Tras_reparar_se_explica_como_importarla()
    {
        var texto = RekordboxViewModel.TextoReparada(3, "musica reparada.xml");
        Assert.StartsWith("3 rutas reparadas en «musica reparada.xml».", texto);
        Assert.Contains("Preferencias → Avanzado → rekordbox xml", texto);
        Assert.Contains(RekordboxRelocator.ListaReparadas, texto);
        Assert.StartsWith("1 ruta reparada", RekordboxViewModel.TextoReparada(1, "x.xml"));
    }

    // MEDIDO: en una colección real solo 11 de 19.690 pistas tenían alguna reproducción; «Nunca
    // pinchadas» sería la colección entera, y la página lo advierte en lugar de presentarlo sin más.
    [Fact]
    public void Si_rekordbox_casi_no_cuenta_reproducciones_se_advierte()
    {
        ColeccionRekordbox Con(int total, int pinchadas) => new()
        {
            Pistas = Enumerable.Range(0, total).Select(i => Pista(R($"{i}.mp3")) with { Reproducciones = i < pinchadas ? 3 : 0 }).ToList(),
        };

        Assert.StartsWith("Ninguna pista de esta colección tiene reproducciones anotadas en rekordbox.", RekordboxViewModel.AvisoReproducciones(Con(200, 0)));
        Assert.StartsWith("Solo 1 pista de 200 tiene reproducciones", RekordboxViewModel.AvisoReproducciones(Con(200, 1)));
        Assert.Equal("", RekordboxViewModel.AvisoReproducciones(Con(200, 2)));     // una de cada cien: el contador sirve
        Assert.Equal("", RekordboxViewModel.AvisoReproducciones(Con(50, 0)));      // con tan pocas no se concluye nada
    }

    // rekordbox distingue mayúsculas en la Location: con dos entradas del mismo archivo, la exportación
    // usa la que se escribe como el archivo de hoy.
    [Fact]
    public void Con_dos_entradas_del_mismo_archivo_se_usa_la_escrita_como_hoy()
    {
        var vieja = R("Billy Ocean - Caribbean Queen (No More Love On the Run).mp3");
        var hoy = R("Billy Ocean - Caribbean Queen (No More Love on the Run).mp3");
        var otra = R("otra.mp3");
        var porRuta = new Dictionary<string, Track>(ComparadorRutas.Instancia) { [hoy] = new Track { FilePath = hoy } };

        var d = RekordboxViewModel.LocationsDeRekordbox(new[] { Pista(vieja), Pista(hoy), Pista(otra) }, porRuta);

        Assert.Equal(RekordboxRelocator.PathToLocation(hoy), d[hoy]);
        Assert.Equal(RekordboxRelocator.PathToLocation(hoy), d[vieja]);   // es el mismo archivo
        Assert.Equal(RekordboxRelocator.PathToLocation(otra), d[otra]);
        // Si la buena va primero, no la sustituye la vieja.
        Assert.Equal(RekordboxRelocator.PathToLocation(hoy), RekordboxViewModel.LocationsDeRekordbox(new[] { Pista(hoy), Pista(vieja) }, porRuta)[hoy]);
    }

    // La importación de BPM y tonalidad de Biblioteca ya no se queda en «0 coincidencias».
    [Fact]
    public void La_importacion_de_Biblioteca_explica_por_que_no_encuentra_nada()
    {
        Assert.Contains("no trae ninguna pista con archivo", LibraryViewModel.TextoImportacionRekordbox(0, 0, 0, 0));
        Assert.Equal("Ninguna de las 120 pistas de la colección de rekordbox está en las carpetas de la biblioteca. "
                   + "En la página rekordbox se ve qué falta en cada lado y qué rutas están rotas.",
                     LibraryViewModel.TextoImportacionRekordbox(120, 0, 0, 0));
        Assert.Equal("rekordbox: 1 canción encontrada · 1 actualizada · 1 fallo (el detalle está en el registro). Se puede deshacer desde Enriquecer.",
                     LibraryViewModel.TextoImportacionRekordbox(5, 1, 1, 1));
        Assert.Equal("rekordbox: 12 canciones encontradas · 0 actualizadas.", LibraryViewModel.TextoImportacionRekordbox(20, 12, 0, 0));
    }

    // ---------------------------------------------------------------------------------------------
    // P11: qué se exporta
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void El_resumen_de_la_exportacion_concuerda_en_numero()
    {
        Assert.Equal("Con lo marcado no hay nada que exportar.", RekordboxViewModel.TextoResumenExportacion(0, 0, 0, 0));
        Assert.Equal("Se exportará: 1 colección.", RekordboxViewModel.TextoResumenExportacion(1, 0, 0, 0));
        Assert.Equal("Se exportará: 3 colecciones, 1 lista abierta en Tendencias, Playlists o el Asistente, "
                   + "2 listas que sigues (se vuelven a leer de internet) y la canción pendiente ya conseguida.",
                     RekordboxViewModel.TextoResumenExportacion(3, 1, 2, 1));
        Assert.Equal("Se exportará: 2 listas abiertas en Tendencias, Playlists o el Asistente y las 4 canciones pendientes ya conseguidas.",
                     RekordboxViewModel.TextoResumenExportacion(0, 2, 0, 4));
    }

    [Fact]
    public void Tras_exportar_se_dice_como_importar_la_carpeta()
    {
        var t = RekordboxViewModel.TextoExportado(new ResultadoExportacionRekordbox(1, 1, 1), "BeatTag.xml");
        Assert.StartsWith("1 lista exportada con 1 canción a «BeatTag.xml» (1 lista sin canciones de la biblioteca no se incluye).", t);
        Assert.Contains("carpeta «BeatTag»", t);
        Assert.StartsWith("4 listas exportadas con 30 canciones a «x.xml».", RekordboxViewModel.TextoExportado(new ResultadoExportacionRekordbox(4, 30, 0), "x.xml"));
    }

    // La lista abierta en Tendencias entra con las canciones que tienes, en su orden, y sin la fecha en
    // el nombre; si se ordenó para mezclar, el orden va aparte.
    [Fact]
    public void Una_lista_abierta_y_su_orden_para_mezclar()
    {
        var filas = new[]
        {
            new TrendRow { Position = 3, Artist = "C", Title = "c", FilePath = R("c.mp3") },
            new TrendRow { Position = 1, Artist = "A", Title = "a", FilePath = R("a.mp3") },
            new TrendRow { Position = 2, Artist = "B", Title = "b", FilePath = "" },          // no la tienes
        };

        var listas = RekordboxViewModel.DeListaAbierta("Tendencias Spain 2026-10-20", filas, new[] { R("c.mp3"), R("a.mp3") }).ToList();

        Assert.Equal(2, listas.Count);
        Assert.Equal(new[] { R("a.mp3"), R("c.mp3") }, listas[0].Rutas);
        Assert.Equal("Tendencias Spain", listas[0].Nombre);
        Assert.Equal(RekordboxViewModel.CarpetaListas, listas[0].Carpeta);
        Assert.Equal(RekordboxViewModel.CarpetaOrdenes, listas[1].Carpeta);
        Assert.Equal(new[] { R("c.mp3"), R("a.mp3") }, listas[1].Rutas);

        // Sin nada abierto (o sin ninguna en la biblioteca), nada.
        Assert.Empty(RekordboxViewModel.DeListaAbierta("Playlist 2026-10-20", new[] { filas[2] }, Array.Empty<string>()));
    }

    [Theory]
    [InlineData("Tendencias Spain 2026-10-20", "Tendencias Spain")]
    [InlineData("REGUETÓN 2026 2026-10-20", "REGUETÓN 2026")]
    [InlineData("Sin fecha", "Sin fecha")]
    public void El_nombre_va_sin_la_fecha(string nombre, string esperado) => Assert.Equal(esperado, RekordboxViewModel.SinFecha(nombre));

    // La misma lista vista en Tendencias y seguida no sale dos veces; se queda la primera (la seguida).
    [Fact]
    public void Las_listas_repetidas_salen_una_vez()
    {
        var a = R("a.mp3");
        var b = R("b.mp3");
        var listas = RekordboxViewModel.SinRepetidas(new[]
        {
            new ListaParaRekordbox("Listas", "Top 50 España", new[] { a, b }),
            new ListaParaRekordbox("Listas", "Tendencias Spain", new[] { a.ToUpperInvariant(), b }),
            new ListaParaRekordbox("Órdenes para mezclar", "Tendencias Spain", new[] { b, a }),
        });
        Assert.Equal(new[] { "Top 50 España", "Tendencias Spain" }, listas.Select(l => l.Nombre));
        Assert.Equal("Órdenes para mezclar", listas[1].Carpeta);
    }

    // Lo que salió de la biblioteca desde que se abrió la lista no viaja a rekordbox como pista perdida.
    [Fact]
    public void Solo_entran_los_archivos_que_siguen_en_la_biblioteca()
    {
        var a = R("a.mp3");
        var porRuta = new Dictionary<string, Track>(ComparadorRutas.Instancia) { [a] = new Track { FilePath = a } };
        var listas = RekordboxViewModel.SoloDeLaBiblioteca(new[]
        {
            new ListaParaRekordbox("", "Orden", new[] { R("borrada.mp3"), a }),
            new ListaParaRekordbox("", "Toda fuera", new[] { R("borrada.mp3") }),
        }, porRuta);
        Assert.Equal(new[] { a }, listas[0].Rutas);
        Assert.Empty(listas[1].Rutas);   // la cuenta el exportador como lista sin canciones
    }

    [Fact]
    public void Las_pendientes_conseguidas_que_siguen_en_la_biblioteca_de_la_mas_reciente()
    {
        var a = R("a.mp3");
        var b = R("b.mp3");
        var canciones = new[] { new Track { FilePath = a }, new Track { FilePath = b } };
        var pendientes = new[]
        {
            new Pendiente { Artista = "A", Titulo = "a", Conseguida = new DateTime(2026, 9, 1), Archivo = a },
            new Pendiente { Artista = "B", Titulo = "b", Conseguida = new DateTime(2026, 10, 1), Archivo = b },
            new Pendiente { Artista = "C", Titulo = "c", Conseguida = new DateTime(2026, 10, 2), Archivo = R("ya-no-esta.mp3") },
            new Pendiente { Artista = "D", Titulo = "d" },                                                   // sigue pendiente
        };

        var lista = Assert.Single(RekordboxViewModel.PendientesConseguidas(pendientes, canciones));

        Assert.Equal("Pendientes conseguidas", lista.Nombre);
        Assert.Equal(new[] { b, a }, lista.Rutas);
    }

    // Las colecciones salen con sus canciones ordenadas como en su página: por BPM, y sin BPM al final.
    [Fact]
    public void Cada_coleccion_con_sus_canciones_por_bpm()
    {
        var lenta = new Track { FilePath = R("lenta house.mp3"), Title = "Lenta house", Artist = "X", Bpm = 120 };
        var rapida = new Track { FilePath = R("rapida house.mp3"), Title = "Rápida house", Artist = "Y", Bpm = 128 };
        var sinBpm = new Track { FilePath = R("sin bpm house.mp3"), Title = "Sin BPM house", Artist = "Z" };
        var otra = new Track { FilePath = R("techno.mp3"), Title = "Techno", Artist = "W", Bpm = 130 };
        var coleccion = new ColeccionInteligente { Nombre = "House", Texto = "house" };

        var listas = RekordboxViewModel.ListasDeColecciones(new[] { coleccion }, new[] { sinBpm, rapida, otra, lenta },
                                                            new Dictionary<string, FichaDj>());

        var l = Assert.Single(listas);
        Assert.Equal(RekordboxViewModel.CarpetaColecciones, l.Carpeta);
        Assert.Equal("House", l.Nombre);
        Assert.Equal(new[] { lenta.FilePath, rapida.FilePath, sinBpm.FilePath }, l.Rutas);
    }

    // «Reparar colección de rekordbox» en Ajustes salta a la página por su número: si alguien renumera
    // las páginas y deja la constante, el botón llevaría a otra sin que nada falle al compilar.
    [Fact]
    public void Ajustes_salta_a_la_pagina_rekordbox()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Etiquetador.App", "ViewModels", "MainViewModel.cs")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var vm = File.ReadAllText(Path.Combine(dir!.FullName, "Etiquetador.App", "ViewModels", "MainViewModel.cs"));

        var constante = System.Text.RegularExpressions.Regex.Match(vm, @"const int RekordboxTabIndex = (\d+);");
        var enLaBarra = System.Text.RegularExpressions.Regex.Match(vm, @"Indice = (\d+),[^\n]*Nombre = ""rekordbox""");
        Assert.True(constante.Success && enLaBarra.Success);
        Assert.Equal(enLaBarra.Groups[1].Value, constante.Groups[1].Value);
        Assert.Contains("Settings.AbrirRekordboxPedido += () => SelectedTabIndex = RekordboxTabIndex;", vm);
    }

    // La colección exportada que se ha abierto no se sustituye por las listas, la escriba quien la escriba.
    [Fact]
    public void No_se_exporta_encima_de_la_coleccion_abierta()
    {
        var coleccion = R("musica.xml");
        Assert.Contains("elige otro nombre", RekordboxViewModel.MotivoDestinoNoValido(coleccion.ToUpperInvariant(), coleccion));
        Assert.Equal("", RekordboxViewModel.MotivoDestinoNoValido(R("BeatTag.xml"), coleccion));
        Assert.Equal("", RekordboxViewModel.MotivoDestinoNoValido(R("BeatTag.xml"), ""));
    }
}
