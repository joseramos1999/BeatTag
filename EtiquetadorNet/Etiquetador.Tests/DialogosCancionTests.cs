using Etiquetador.App.Acciones;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// Lo que hay alrededor de las funciones de mezcla en la aplicación: los diálogos de compatibles,
/// versiones y ordenar una selección, el menú de la Biblioteca y la papelera desde ella. Sin ventanas:
/// se prueban los ViewModels con una biblioteca de mentira (<see cref="EntornoCanciones"/>).
/// </summary>
public class DialogosCancionTests
{
    private static readonly string Raiz = Path.Combine(Path.GetTempPath(), "MusicaDialogos");

    private static Track T(string archivo, string key = "8A", uint bpm = 124, string? artista = null, string? titulo = null) => new()
    {
        FilePath = Path.Combine(Raiz, archivo),
        Key = key,
        Bpm = bpm,
        Artist = artista,
        Title = titulo,
    };

    // --- Compatibles ---

    [Fact]
    public async Task Compatibles_rellena_la_tabla_y_dice_cuantas_hay()
    {
        var x = T("X - Uno.mp3", artista: "X", titulo: "Uno");
        var bib = new List<Track> { x, T("A - Dos.mp3", "9A", 124, "A", "Dos"), T("B - Tres.mp3", "3B", 124, "B", "Tres") };
        var vm = new CompatiblesViewModel(new EntornoCanciones(bib), x);

        await vm.BuscarAsync();

        Assert.Single(vm.Filas);
        Assert.Equal("Dos", vm.Filas[0].Titulo);
        Assert.Equal("Armónica", vm.Filas[0].Encaje);
        Assert.Equal("1 compatible", vm.Resumen);
        Assert.Contains("según las etiquetas", vm.Detalle);
        Assert.StartsWith("Compatibles con «X – Uno»", vm.Titulo);
        Assert.True(vm.HayFilas);
    }

    [Fact]
    public async Task Compatibles_corta_la_lista_por_abajo_y_lo_dice()
    {
        var x = T("x.mp3");
        var bib = Enumerable.Range(0, CompatiblesViewModel.Maximo + 20).Select(i => T($"c{i:000}.mp3")).Append(x).ToList();
        var vm = new CompatiblesViewModel(new EntornoCanciones(bib), x);

        await vm.BuscarAsync();

        Assert.Equal(CompatiblesViewModel.Maximo, vm.Filas.Count);
        Assert.Contains($"se muestran las {CompatiblesViewModel.Maximo}", vm.Resumen);
    }

    [Fact]
    public async Task Compatibles_filtra_por_tipo_sin_volver_a_buscar()
    {
        var x = T("x.mp3");
        var bib = new List<Track> { x, T("a - Tema.mp3"), T("b - Voz (Acapella).mp3") };
        var vm = new CompatiblesViewModel(new EntornoCanciones(bib), x);
        await vm.BuscarAsync();
        Assert.Equal(2, vm.Filas.Count);

        vm.Filtro = vm.Filtros.Single(f => f.Valor == FiltroCompatibles.SoloAcapellas);
        Assert.Equal(new[] { "Acapella" }, vm.Filas.Select(f => f.Tipo));

        vm.Filtro = vm.Filtros.Single(f => f.Valor == FiltroCompatibles.SinAcapellas);
        Assert.Equal(new[] { "a - Tema.mp3" }, vm.Filas.Select(f => f.Archivo));
    }

    [Fact]
    public async Task Compatibles_para_una_acapella_habla_de_bases()
    {
        var x = T("Voz (Acapella).mp3", bpm: 100);
        var vm = new CompatiblesViewModel(new EntornoCanciones(new List<Track> { x, T("base.mp3", bpm: 102) }), x);

        await vm.BuscarAsync();

        Assert.True(vm.EsAcapella);
        Assert.StartsWith("Bases para", vm.Titulo);
        Assert.Contains("3 %", vm.Detalle);
    }

    [Fact]
    public async Task Compatibles_sigue_la_cadena_desde_la_seleccionada()
    {
        var x = T("x.mp3", "8A");
        var y = T("y.mp3", "9A");
        var z = T("z.mp3", "10A");
        var vm = new CompatiblesViewModel(new EntornoCanciones(new List<Track> { x, y, z }), x);
        await vm.BuscarAsync();
        Assert.Equal(new[] { "y.mp3" }, vm.Filas.Select(f => f.Archivo));

        vm.Seleccionada = vm.Filas[0];
        await vm.BuscarDesdeEstaCommand.ExecuteAsync(null);

        Assert.Same(y, vm.Consultada);
        Assert.Equal(new[] { "x.mp3", "z.mp3" }, vm.Filas.Select(f => f.Archivo));
    }

    [Fact]
    public async Task Compatibles_de_una_cancion_que_ya_no_esta_lo_dice()
    {
        var vm = new CompatiblesViewModel(new EntornoCanciones(new List<Track>()), null);

        await vm.BuscarAsync();

        Assert.Empty(vm.Filas);
        Assert.Contains("ya no está en la biblioteca", vm.Aviso);
    }

    [Fact]
    public async Task Compatibles_guarda_la_lista_con_la_consultada_delante()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var x = T("x.mp3", artista: "X", titulo: "Uno");
            var bib = new List<Track> { x, T("b.mp3", "9A", 124, "B", "Dos"), T("a.mp3", "8A", 124, "A", "Tres") };
            var vm = new CompatiblesViewModel(new EntornoCanciones(bib), x);
            await vm.BuscarAsync();

            var destino = Path.Combine(dir, "lista.m3u8");
            Assert.Equal("", vm.ExportarM3u(destino));

            var rutas = File.ReadAllLines(destino).Where(l => !l.StartsWith('#')).ToList();
            Assert.Equal(new[] { x.FilePath, bib[2].FilePath, bib[1].FilePath }, rutas);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Theory]
    [InlineData(0, 0, "Ninguna compatible")]
    [InlineData(1, 0, "1 compatible")]
    [InlineData(5, 1, "5 compatibles · 1 con tono compatible no tiene BPM en las etiquetas y no se ha tenido en cuenta")]
    [InlineData(5, 3, "5 compatibles · 3 con tono compatible no tienen BPM en las etiquetas y no se han tenido en cuenta")]
    public void Resumen_de_compatibles(int total, int sinBpm, string esperado)
        => Assert.Equal(esperado, CompatiblesViewModel.TextoResumen(total, sinBpm));

    // --- Versiones ---

    [Fact]
    public async Task Versiones_rellena_la_tabla_y_marca_la_consultada()
    {
        var original = T("Karol G - Bichota.mp3", artista: "Karol G", titulo: "Bichota");
        var bib = new List<Track>
        {
            original,
            T("Karol G - Bichota (Acapella).mp3", artista: "Karol G", titulo: "Bichota (Acapella)"),
            T("Karol G - Bichota (Hype Intro).mp3", artista: "Karol G", titulo: "Bichota (Hype Intro)"),
        };
        var vm = new VersionesViewModel(new EntornoCanciones(bib), original);

        await vm.CargarAsync();

        Assert.Equal(3, vm.Filas.Count);
        Assert.Equal("●", vm.Filas[0].Esta);
        Assert.Equal(new[] { "Original", "Extended / Intro", "Acapella" }, vm.Filas.Select(f => f.Tipo));
        Assert.False(vm.EsMashup);
        Assert.Equal("3 piezas · Original: 1 · Extended / Intro: 1 · Acapella: 1", vm.Resumen);
    }

    [Fact]
    public async Task Versiones_de_un_mashup_enseñan_la_cancion_de_cada_pieza()
    {
        var mashup = T("Mashups/Karol G - Bichota x Tusa (DJ Mashup).mp3");
        var bib = new List<Track>
        {
            mashup,
            T("Karol G - Bichota.mp3", artista: "Karol G", titulo: "Bichota"),
            T("Karol G - Tusa.mp3", artista: "Karol G", titulo: "Tusa"),
        };
        var vm = new VersionesViewModel(new EntornoCanciones(bib), mashup);

        await vm.CargarAsync();

        Assert.True(vm.EsMashup);
        Assert.Contains(vm.Filas, f => f.Cancion == "Tusa");
    }

    [Fact]
    public void Sin_otras_versiones_se_dice_asi()
        => Assert.Equal("No hay otras versiones de esta canción en la biblioteca.",
                        VersionesViewModel.TextoResumen(new[] { new PiezaVersion(T("a.mp3"), TipoPieza.Original, true, "") }));

    [Fact]
    public void El_indice_de_versiones_es_uno_por_biblioteca()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var una = new LibraryStore(new AppConfig(), () => { }, new ScanCache(Path.Combine(dir, "scan.json")));
            var otra = new LibraryStore(new AppConfig(), () => { }, new ScanCache(Path.Combine(dir, "scan.json")));
            Assert.Same(VersionesViewModel.CacheDe(una), VersionesViewModel.CacheDe(una));
            Assert.NotSame(VersionesViewModel.CacheDe(una), VersionesViewModel.CacheDe(otra));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // --- Ordenar la selección de la Biblioteca (P1) ---

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(MezclaViewModel.Maximo, true)]
    [InlineData(MezclaViewModel.Maximo + 1, false)]
    public void Se_ordenan_de_dos_canciones_al_maximo(int seleccionadas, bool sePuede)
        => Assert.Equal(sePuede, MezclaSeleccionViewModel.MotivoParaNoOrdenar(seleccionadas).Length == 0);

    [Fact]
    public async Task Ordena_la_seleccion_como_la_tabla_de_mezcla_y_empieza_por_la_elegida()
    {
        var seleccion = new List<Track>
        {
            T("c.mp3", "8A", 126), T("a.mp3", "8A", 120), T("b.mp3", "9A", 122),
            T("a.mp3", "8A", 120),   // la misma seleccionada dos veces: se pincha una
        };
        var fichas = new Dictionary<string, FichaDj>(StringComparer.OrdinalIgnoreCase) { [seleccion[0].FilePath] = new() { Energia = 7 } };
        var vm = new MezclaSeleccionViewModel(new EntornoCanciones(seleccion, fichas), seleccion);

        await vm.OrdenarAsync();
        var esperado = OrdenMezcla.Ordenar(seleccion.Take(3).Select(t => (t, fichas.GetValueOrDefault(t.FilePath))).ToList());

        Assert.Equal(3, vm.Canciones);
        Assert.Equal(esperado.Select(p => p.Track.FilePath), vm.Pasos.Select(p => p.FilePath));
        Assert.Equal("7", vm.Pasos.Single(p => p.FilePath == seleccion[0].FilePath).Energia);
        Assert.StartsWith("3 canciones", vm.Resumen);

        vm.Seleccionada = vm.Pasos.Single(p => p.FilePath == seleccion[0].FilePath);
        await vm.EmpezarPorEstaCommand.ExecuteAsync(null);

        Assert.Equal(seleccion[0].FilePath, vm.Pasos[0].FilePath);
        Assert.Contains("empezando por", vm.Status);
    }

    [Fact]
    public async Task La_lista_de_la_seleccion_se_guarda_en_ese_orden()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var seleccion = new List<Track> { T("b.mp3", "8A", 126, "B", "Dos"), T("a.mp3", "8A", 120, "A", "Uno") };
            var vm = new MezclaSeleccionViewModel(new EntornoCanciones(seleccion), seleccion);
            await vm.OrdenarAsync();

            var destino = Path.Combine(dir, "sesion.m3u8");
            vm.ExportarM3u(destino);

            var rutas = File.ReadAllLines(destino).Where(l => !l.StartsWith('#')).ToList();
            Assert.Equal(vm.Pasos.Select(p => p.FilePath), rutas);
            Assert.Contains("Lista guardada", vm.Status);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void El_estado_tras_ordenar_avisa_si_falta_la_tonalidad()
    {
        var conTono = new ResumenMezcla(4, 3, 3, 0, 0, 0, 0);
        var sinTono = new ResumenMezcla(4, 3, 0, 0, 2, 0, 0);

        Assert.EndsWith("sin tonalidad.", MezclaViewModel.TextoEstado(conTono));
        Assert.Contains("impórtala de rekordbox", MezclaViewModel.TextoEstado(sinTono));
    }

    // --- Menú de la Biblioteca ---

    [Fact]
    public void Menu_de_la_biblioteca_segun_la_seleccion()
    {
        var una = LibraryViewModel.CalcularMenu(1, ocupada: false);
        Assert.True(una.Escuchar && una.Editar && una.Buscar && una.Carpeta && una.Papelera);
        Assert.False(una.Ordenar);                         // ordenar pide al menos dos
        Assert.Equal("Enviar a la papelera", una.TextoPapelera);

        var varias = LibraryViewModel.CalcularMenu(12, ocupada: false);
        Assert.True(varias.Ordenar);
        Assert.Equal("Ordenar para mezclar la selección (12)…", varias.TextoOrdenar);
        Assert.Equal("Enviar a la papelera (12)", varias.TextoPapelera);

        // Mientras escanea o importa de rekordbox, solo se puede abrir la carpeta.
        var ocupada = LibraryViewModel.CalcularMenu(12, ocupada: true);
        Assert.False(ocupada.Escuchar || ocupada.Editar || ocupada.Buscar || ocupada.Ordenar || ocupada.Papelera);
        Assert.True(ocupada.Carpeta);
        Assert.Contains("espera", ocupada.Espera);

        var nada = LibraryViewModel.CalcularMenu(0, ocupada: false);
        Assert.False(nada.Escuchar || nada.Carpeta || nada.Papelera || nada.Ordenar);
    }

    [Fact]
    public void Papelera_desde_la_biblioteca_quita_solo_las_que_se_enviaron()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var store = new LibraryStore(new AppConfig(), () => { }, new ScanCache(Path.Combine(dir, "scan.json")));
            var a = T("a.mp3");
            var b = T("b.mp3");
            var c = T("c.mp3");
            foreach (var t in new[] { a, b, c }) store.Tracks.Add(t);
            var avisos = 0;
            store.Changed += () => avisos++;

            var estado = LibraryViewModel.PapeleraYQuitar(store, new[] { a.FilePath, b.FilePath },
                ruta => ruta == b.FilePath ? (false, "La unidad no tiene papelera.") : (true, ""));

            Assert.Equal(new[] { b, c }, store.Tracks);                         // la rechazada se queda
            Assert.Equal(1, avisos);                                             // un solo aviso a las pestañas
            Assert.Equal("1 enviados a la papelera, 1 con problemas. El primero: b.mp3: La unidad no tiene papelera.", estado);

            Assert.Equal("Enviado a la papelera: c.mp3.", LibraryViewModel.PapeleraYQuitar(store, new[] { c.FilePath }, _ => (true, "")));
            Assert.Equal("No se pudo enviar a la papelera. b.mp3: motivo desconocido.",
                         LibraryViewModel.PapeleraYQuitar(store, new[] { b.FilePath }, _ => (false, "")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // --- La opción de menú de las demás tablas ---

    [Fact]
    public void La_opcion_de_menu_saca_la_ruta_de_cualquier_fila()
    {
        // Las filas de Tendencias y Playlists (TrendRow) llevan un pincel de Avalonia, que no se puede
        // crear fuera de la aplicación; su caso es el mismo: la ruta, o vacío si no la tienes.
        var t = T("a.mp3");
        Assert.Equal(t.FilePath, AccionesCancion.RutaDe(t));                                // Biblioteca
        Assert.Equal(t.FilePath, AccionesCancion.RutaDe(new FichaRow(t, null)));            // Colecciones y Ficha DJ
        Assert.Equal(t.FilePath, AccionesCancion.RutaDe(new PasoFila(new PasoMezcla(1, t, null, EncajeTono.Igual, 0))));
        Assert.Equal("", AccionesCancion.RutaDe(""));
        Assert.Equal("", AccionesCancion.RutaDe(null));

        Assert.False(AccionesCancion.BuscarCompatibles.CanExecute(null));
        Assert.True(AccionesCancion.VerVersiones.CanExecute(t));
    }
}
