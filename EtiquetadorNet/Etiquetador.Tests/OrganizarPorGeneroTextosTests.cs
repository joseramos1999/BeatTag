using System.Reflection;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Organizar por género (B8 y B9): los textos con su número bien concordado, las opciones que se
/// recuerdan, la página con un motor de prueba y lo que la Ayuda cita de la pantalla. Las pruebas de
/// la Ayuda que son de todas las páginas (una sección por pestaña, sin coloquialismos) siguen en
/// AyudaTests; aquí solo lo propio de esta página.
/// </summary>
public class OrganizarPorGeneroTextosTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // --- Caso 23: singular y plural ---

    [Theory]
    [InlineData(1, 1, "Se moverá 1 canción a 1 carpeta")]
    [InlineData(2, 1, "Se moverán 2 canciones a 1 carpeta")]
    [InlineData(10896, 181, "Se moverán 10.896 canciones a 181 carpetas")]
    [InlineData(0, 0, "No hay ninguna canción que mover")]
    public void Caso_23_lo_que_se_mueve_concuerda_en_numero(int canciones, int carpetas, string texto)
        => Assert.Equal(texto, OrganizarPorGenero.TextoMovimientos(canciones, carpetas));

    [Theory]
    [InlineData(MotivoOmision.Colision, 1, "1 se omite porque ya hay un archivo con ese nombre")]
    [InlineData(MotivoOmision.Colision, 66, "66 se omiten porque ya hay un archivo con ese nombre")]
    [InlineData(MotivoOmision.SinGenero, 1, "1 se queda por no tener género")]
    [InlineData(MotivoOmision.SinGenero, 2456, "2.456 se quedan por no tener género")]
    [InlineData(MotivoOmision.YaEnSuSitio, 1, "1 ya está en la carpeta de su género")]
    [InlineData(MotivoOmision.YaEnSuSitio, 3, "3 ya están en la carpeta de su género")]
    [InlineData(MotivoOmision.OtraUnidad, 2, "2 se omiten porque están en otra unidad")]
    [InlineData(MotivoOmision.Colision, 0, "")]
    public void Caso_23_lo_que_no_se_mueve_concuerda_en_numero(MotivoOmision motivo, int n, string texto)
        => Assert.Equal(texto, OrganizarPorGenero.TextoOmision(motivo, n));

    [Fact]
    public void Cada_motivo_tiene_su_texto_en_singular_y_en_plural()
    {
        foreach (var m in Enum.GetValues<MotivoOmision>())
        {
            Assert.StartsWith("1 ", OrganizarPorGenero.TextoOmision(m, 1));
            Assert.StartsWith("5 ", OrganizarPorGenero.TextoOmision(m, 5));
            Assert.NotEqual(OrganizarPorGenero.TextoOmision(m, 1)[2..], OrganizarPorGenero.TextoOmision(m, 5)[2..]);
            Assert.NotEqual("", OrganizarPorGenero.TextoMotivo(m));
        }
    }

    [Fact]
    public void El_resumen_junta_lo_que_se_mueve_lo_que_se_queda_y_el_reparto()
    {
        var plan = new PlanOrganizar
        {
            Movimientos = new[] { new MovimientoOrganizar("a", "b", "Pop", "Pop") },
            PorDestino = new[] { new CarpetaDestinoPlan("Pop", "Pop", 1, false) },
            Omitidas = new[] { new OmisionOrganizar("c", MotivoOmision.Colision, ""), new OmisionOrganizar("d", MotivoOmision.SinGenero, "") },
            MediaGenerosPorOrigen = 24.4,
        };

        Assert.Equal("Se moverá 1 canción a 1 carpeta · 1 se queda por no tener género · 1 se omite porque ya hay un archivo con ese nombre"
                     + " · De media, cada carpeta de origen se repartirá entre 24 géneros", OrganizarPorGenero.Resumen(plan));
        Assert.Equal("1 canción", OrganizarPorGenero.TextoCanciones(1));
        Assert.Equal("1.234 canciones", OrganizarPorGenero.TextoCanciones(1234));
    }

    [Fact]
    public void La_confirmacion_y_el_resultado_dicen_lo_que_pasa_con_rekordbox_y_los_demas_programas()
    {
        var plan = new PlanOrganizar
        {
            Movimientos = new[] { new MovimientoOrganizar("a", "b", "Pop", "Pop"), new MovimientoOrganizar("c", "d", "Pop", "Pop") },
            PorDestino = new[] { new CarpetaDestinoPlan("Pop", "Pop", 2, false) },
        };
        var (titulo, cuerpo, nota) = OrganizarPorGeneroViewModel.TextosConfirmacion(plan);
        Assert.Equal("Organizar por género", titulo);
        Assert.StartsWith("Se moverán 2 canciones a 1 carpeta (1 se crea).", cuerpo);
        Assert.Contains("Ningún archivo se sobrescribe ni se renombra", cuerpo);
        foreach (var aviso in new[] { "«Rutas rotas»", "Serato, Traktor, Engine DJ y VirtualDJ", "M3U8 y XML", "Enriquecer, vuelve a analizar", "«Deshacer última»" })
            Assert.Contains(aviso, nota);

        var uno = OrganizarPorGeneroViewModel.TextoResultado(new ResultadoOrganizar(new[] { ("a", "b") }, Array.Empty<string>(), false, "", ""), 1);
        Assert.StartsWith("Se ha movido 1 canción a 1 carpeta.", uno);
        Assert.Contains("«Rutas rotas»", uno);
        var varios = OrganizarPorGeneroViewModel.TextoResultado(
            new ResultadoOrganizar(new[] { ("a", "b"), ("c", "d") }, new[] { "e.mp3: " + OrganizadorPorGenero.Abierto, "f.mp3: x" }, true, "", ""), 2);
        Assert.StartsWith("Cancelado. Se han movido 2 canciones a 2 carpetas; el resto se queda donde estaba.", varios);
        Assert.Contains("2 no se han podido mover (por ejemplo, e.mp3: Está abierto en otro programa)", varios);
        var nada = OrganizarPorGeneroViewModel.TextoResultado(new ResultadoOrganizar(Array.Empty<(string, string)>(), Array.Empty<string>(), true, "No se ha tocado ningún archivo.", ""), 0);
        Assert.Equal("No se ha movido ninguna canción. No se ha tocado ningún archivo.", nada);
    }

    // --- Las opciones se recuerdan ---

    [Fact]
    public void Las_opciones_se_guardan_en_la_configuracion_y_se_vuelven_a_leer()
    {
        var paths = new AppPaths(Path.Combine(_dir, "datos"));
        var cfg = new AppConfig
        {
            OrganizarDestino = "carpeta", OrganizarCarpeta = Path.Combine(_dir, "Musica", "Organizada"), OrganizarConservarEstructura = true,
            OrganizarSinGenero = true, OrganizarDejarHerramientas = false, OrganizarAgruparMenosDe = 5,
        };
        Assert.True(cfg.Save(paths, out var error), error);

        var leida = AppConfig.Load(paths);

        Assert.Equal(("carpeta", cfg.OrganizarCarpeta, true, true, false, 5),
                     (leida.OrganizarDestino, leida.OrganizarCarpeta, leida.OrganizarConservarEstructura, leida.OrganizarSinGenero,
                      leida.OrganizarDejarHerramientas, leida.OrganizarAgruparMenosDe));
        var fabrica = new AppConfig();
        Assert.Equal(("porgenero", true, 0), (fabrica.OrganizarDestino, fabrica.OrganizarDejarHerramientas, fabrica.OrganizarAgruparMenosDe));
    }

    // --- La página, con un motor de prueba ---

    private (AppEngine Motor, LibraryStore Biblioteca, string Musica) Motor(AppConfig? config = null)
    {
        var musica = Directory.CreateDirectory(Path.Combine(_dir, "Musica")).FullName;
        var datos = Path.Combine(_dir, "datos");
        var biblioteca = new LibraryStore(new AppConfig { Folders = new() { musica } }, () => { }, new ScanCache(Path.Combine(datos, "scan.json")));
        var motor = MotorDePrueba.Crear(datos, new Logger(), biblioteca);
        void Poner(string campo, object valor) => typeof(AppEngine).GetField(campo, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(motor, valor);
        Poner("<Fichas>k__BackingField", new AlmacenFichas(Path.Combine(datos, "fichas.json")));
        if (config != null) Poner("<Config>k__BackingField", config);
        return (motor, biblioteca, musica);
    }

    private static string Cancion(string carpeta, string nombre, string? genero)
    {
        Directory.CreateDirectory(carpeta);
        var ruta = Path.Combine(carpeta, nombre);
        Mp3Fixture.WriteMinMp3(ruta);
        if (genero != null)
        {
            using var f = TagLib.File.Create(ruta);
            f.Tag.Genres = new[] { genero };
            f.Save();
        }
        return ruta;
    }

    private static async Task Esperar(Func<bool> listo)
    {
        for (var i = 0; i < 200 && !listo(); i++) await Task.Delay(25);
        Assert.True(listo(), "la página no terminó a tiempo");
    }

    [Fact]
    public async Task La_pagina_calcula_la_vista_previa_y_organiza_con_deshacer()
    {
        var (motor, biblioteca, musica) = Motor();
        var descargas = Path.Combine(musica, "Descargas");
        var warmUp = Path.Combine(musica, "WarmUp");
        var a = Cancion(descargas, "a.mp3", "Pop");
        var b = Cancion(descargas, "b.mp3", "Reggaeton");
        var c = Cancion(warmUp, "c.mp3", "Pop");
        var d = Cancion(descargas, "d.mp3", null);
        var vm = new OrganizarPorGeneroViewModel(motor);
        Assert.False(vm.PuedeOrganizar);

        await vm.CalcularCommand.ExecuteAsync(null);

        Assert.True(vm.HayPlan);
        Assert.False(vm.IsBusy);
        Assert.Equal(3, vm.Plan!.Movimientos.Count);
        Assert.Equal(2, vm.Destinos.Count);
        Assert.Equal(OrganizarPorGeneroViewModel.Recomendar, vm.Recomendacion);   // hay una sin género
        Assert.StartsWith("Se moverán 3 canciones a 2 carpetas · 1 se queda por no tener género", vm.Status);
        Assert.Equal("Carpetas de destino (2)", vm.TituloDestinos);
        Assert.True(vm.PuedeOrganizar);

        // Desmarcar una carpeta de origen recalcula en segundo plano.
        vm.Origenes.Single(o => o.Carpeta == warmUp).Incluir = false;
        await Esperar(() => !vm.Calculando && vm.Plan!.Movimientos.Count == 2);
        Assert.Equal("Se omiten (2)", vm.TituloOmitidas);
        Assert.False(vm.Origenes.Single(o => o.Carpeta == warmUp).Incluir);

        await vm.OrganizarAsync();

        var porGenero = Path.Combine(musica, OrganizarPorGenero.CarpetaPorGenero);
        Assert.True(File.Exists(Path.Combine(porGenero, "Pop", "a.mp3")));
        Assert.True(File.Exists(Path.Combine(porGenero, "Reggaeton", "b.mp3")));
        Assert.True(File.Exists(c));
        Assert.True(File.Exists(d));
        Assert.StartsWith("Se han movido 2 canciones a 2 carpetas.", vm.Resultado);
        Assert.False(vm.HayPlan);
        Assert.Contains(biblioteca.Tracks, t => t.FilePath == Path.Combine(porGenero, "Pop", "a.mp3"));

        var ejecucion = Assert.Single(new HistorialDeshacer(motor.Paths.UndoDir).Listar());
        Assert.Equal(TipoEjecucion.OrganizarPorGenero, ejecucion.Tipo);
        new UndoEngine(motor.Paths).UndoLastRun();
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.False(Directory.Exists(porGenero));
    }

    [Fact]
    public async Task La_vista_previa_se_desfasa_si_la_biblioteca_cambia_por_otro_lado()
    {
        var (motor, biblioteca, musica) = Motor();
        Cancion(Path.Combine(musica, "A"), "a.mp3", "Pop");
        var vm = new OrganizarPorGeneroViewModel(motor);
        await vm.CalcularCommand.ExecuteAsync(null);
        Assert.True(vm.PuedeOrganizar);

        Cancion(Path.Combine(musica, "A"), "b.mp3", "Pop");
        await biblioteca.ScanAsync();   // por ejemplo, desde la Bandeja

        Assert.True(vm.Desfasado);
        Assert.False(vm.PuedeOrganizar);
        Assert.Contains("vuelve a calcularla", vm.Aviso);
    }

    // Revisión 1, bloqueante: cambiar una opción o una casilla «Incluir» volvía a planear con la
    // biblioteca de antes y quitaba el aviso; «Organizar» se reactivaba y movía según la biblioteca
    // vieja. REPRODUCIDO: a.mp3 pasaba de Pop a Rock en otra pestaña y acababa en «Por género/Pop».
    [Fact]
    public async Task Cambiar_una_opcion_no_da_por_buena_una_vista_previa_desfasada()
    {
        var (motor, biblioteca, musica) = Motor();
        var a = Cancion(Path.Combine(musica, "Descargas"), "a.mp3", "Pop");
        var vm = new OrganizarPorGeneroViewModel(motor);
        await vm.CalcularCommand.ExecuteAsync(null);
        Assert.True(vm.PuedeOrganizar);

        // Otra pestaña cambia el género y la biblioteca se vuelve a leer.
        using (var f = TagLib.File.Create(a)) { f.Tag.Genres = new[] { "Rock" }; f.Save(); }
        await biblioteca.ScanAsync();
        Assert.True(vm.Desfasado);

        vm.ConservarEstructura = true;
        await Esperar(() => !vm.Calculando);
        vm.Origenes.Single().Incluir = false;
        await Esperar(() => !vm.Calculando);
        vm.Origenes.Single().Incluir = true;
        await Esperar(() => !vm.Calculando);

        Assert.True(vm.Desfasado);
        Assert.False(vm.PuedeOrganizar);
        Assert.Contains("vuelve a calcularla", vm.Aviso);

        // Y si aun así llega a «Organizar» (la confirmación ya estaba abierta), no mueve nada y lo dice.
        await vm.OrganizarAsync();
        Assert.True(File.Exists(a));
        Assert.Contains("vuelve a calcularla", vm.Status);

        // Solo «Calcular la vista previa» la pone al día, ya con el género nuevo.
        await vm.CalcularCommand.ExecuteAsync(null);
        Assert.False(vm.Desfasado);
        Assert.Equal("Rock", Assert.Single(vm.Plan!.Movimientos).Genero);
    }

    [Fact]
    public async Task Una_carpeta_con_ajustes_nueva_o_una_carpeta_desmarcada_tras_calcular_desfasan_la_vista_previa()
    {
        var config = new AppConfig();
        var (motor, biblioteca, musica) = Motor(config);
        var a = Cancion(Path.Combine(musica, "Descargas"), "a.mp3", "Pop");
        var vm = new OrganizarPorGeneroViewModel(motor);
        await vm.CalcularCommand.ExecuteAsync(null);
        Assert.True(vm.PuedeOrganizar);

        // En Duplicados se excluye la carpeta: sus canciones ya no deben moverse.
        config.ExcludedDupFolders.Add(Path.Combine(musica, "Descargas"));
        Assert.False(vm.ComprobarVistaPrevia());
        Assert.True(vm.Desfasado);
        Assert.False(vm.PuedeOrganizar);
        await vm.OrganizarAsync();
        Assert.True(File.Exists(a));

        await vm.CalcularCommand.ExecuteAsync(null);
        Assert.False(vm.PuedeOrganizar);   // ahora se queda por la carpeta con ajustes
        config.ExcludedDupFolders.Clear();
        await vm.CalcularCommand.ExecuteAsync(null);
        Assert.True(vm.PuedeOrganizar);

        // Una carpeta de la biblioteca que se desmarca después de calcular.
        biblioteca.Folders.Single().Enabled = false;
        vm.MoverSinGenero = true;   // y una opción que vuelve a planear
        await Esperar(() => !vm.Calculando);
        Assert.True(vm.Desfasado);
        Assert.False(vm.PuedeOrganizar);
    }

    // Revisión 1: «Calcular la vista previa» no se podía cancelar y mantenía el bloqueo global mientras
    // escaneaba (un primer escaneo real tardó 340 s).
    [Fact]
    public async Task Calcular_la_vista_previa_se_puede_cancelar_mientras_se_escanea()
    {
        var musica = Directory.CreateDirectory(Path.Combine(_dir, "Musica")).FullName;
        Cancion(Path.Combine(musica, "A"), "a.mp3", "Pop");
        var datos = Path.Combine(_dir, "datos");
        using var soltar = new ManualResetEventSlim(false);
        var biblioteca = new LibraryStore(new AppConfig { Folders = new() { musica } }, () => { }, new ScanCache(Path.Combine(datos, "scan.json")),
                                          r => { soltar.Wait(TimeSpan.FromSeconds(30)); return LibraryScanner.Recorrer(r, recursive: true); });
        var motor = MotorDePrueba.Crear(datos, new Logger(), biblioteca);
        var vm = new OrganizarPorGeneroViewModel(motor);

        var calculo = vm.CalcularCommand.ExecuteAsync(null);
        await Esperar(() => vm.IsBusy && biblioteca.IsScanning);
        vm.Cancelar();
        var termino = await Task.WhenAny(calculo, Task.Delay(TimeSpan.FromSeconds(10)));
        soltar.Set();

        Assert.Same(calculo, termino);   // no espera a que termine el escaneo
        Assert.False(vm.IsBusy);
        Assert.False(vm.HayPlan);
        Assert.StartsWith("Cancelado", vm.Status);
    }

    [Fact]
    public void Las_opciones_de_la_pagina_salen_de_la_configuracion_y_se_guardan_al_cambiar()
    {
        var config = new AppConfig { OrganizarDestino = "raiz", OrganizarAgruparMenosDe = 4, OrganizarDejarHerramientas = false };
        var (motor, _, musica) = Motor(config);
        var vm = new OrganizarPorGeneroViewModel(motor);
        Assert.True(vm.DestinoRaiz);
        Assert.False(vm.DestinoPorGenero);
        Assert.True(vm.Agrupar);
        Assert.Equal(4, vm.MinimoGrupo);
        Assert.False(vm.DejarHerramientas);

        vm.DestinoPorGenero = true;
        vm.DestinoRaiz = false;
        vm.Agrupar = false;
        vm.ConservarEstructura = true;

        Assert.Equal("porgenero", config.OrganizarDestino);
        Assert.Equal(0, config.OrganizarAgruparMenosDe);
        Assert.True(config.OrganizarConservarEstructura);

        // Una carpeta fuera de la biblioteca no se acepta como destino, y se dice por qué.
        var fuera = Directory.CreateDirectory(Path.Combine(_dir, "Fuera")).FullName;
        Assert.Contains("tiene que ser una carpeta de la biblioteca", vm.ElegirCarpeta(fuera));
        Assert.False(vm.DestinoCarpeta);
        Assert.Equal("", vm.ElegirCarpeta(Path.Combine(musica, "Organizada")));
        Assert.True(vm.DestinoCarpeta);
        Assert.Equal("carpeta", config.OrganizarDestino);

        // Ni una carpeta con ajustes propios de Volumen: lo que entrase ahí ganaría la exclusión.
        config.VolumenCarpetasExcluidas.Add(Path.Combine(musica, "Acapellas"));
        Assert.Contains("ajustes propios en Duplicados o en Volumen", vm.ElegirCarpeta(Path.Combine(musica, "Acapellas", "Sub")));
        Assert.Equal(Path.Combine(musica, "Organizada"), vm.CarpetaDestino);
    }

    // --- B9: lo que la Ayuda cita existe en la pantalla ---

    private static string Fuente(params string[] tramos)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Etiquetador.App", "Views", "HelpView.axaml")))
            dir = dir.Parent;
        Assert.True(dir != null, "no se encontró el árbol de fuentes desde " + AppContext.BaseDirectory);
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(tramos)));
    }

    /// <summary>La sección de la Ayuda, desde su cabecera hasta la siguiente (sin los escapes XML).</summary>
    private static string SeccionDeLaAyuda()
    {
        var doc = System.Xml.Linq.XDocument.Parse(Fuente("Etiquetador.App", "Views", "HelpView.axaml"));
        System.Xml.Linq.XNamespace av = "https://github.com/avaloniaui";
        var textos = doc.Descendants(av + "TextBlock")
                        .Select(t => ((string?)t.Attribute("Classes") ?? "", (string?)t.Attribute("Text") ?? "")).ToList();
        var inicio = textos.FindIndex(t => t.Item1 == "h" && t.Item2 == "📂 Organizar por género");
        Assert.True(inicio >= 0, "no hay sección «📂 Organizar por género» en la Ayuda");
        var fin = textos.FindIndex(inicio + 1, t => t.Item1 == "h");
        return string.Join("\n", textos.Skip(inicio).Take((fin < 0 ? textos.Count : fin) - inicio).Select(t => t.Item2));
    }

    [Theory]
    [InlineData("Calcular la vista previa", "Views/OrganizarPorGeneroView.axaml")]
    [InlineData("«Organizar»", "Views/OrganizarPorGeneroView.axaml")]
    [InlineData("Cancelar el proceso", "Views/OrganizarPorGeneroView.axaml")]
    [InlineData("una carpeta «Por género» dentro de cada carpeta de la biblioteca", "Views/OrganizarPorGeneroView.axaml")]
    [InlineData("directamente en cada carpeta de la biblioteca", "Views/OrganizarPorGeneroView.axaml")]
    [InlineData("Conservar las subcarpetas de origen dentro de cada género", "Views/OrganizarPorGeneroView.axaml")]
    [InlineData("Mover también las canciones sin género a «Sin género»", "Views/OrganizarPorGeneroView.axaml")]
    [InlineData("Dejar en su sitio los mashups, las transiciones, las acapellas y los instrumentales", "Views/OrganizarPorGeneroView.axaml")]
    [InlineData("Agrupar en «Otros géneros» los que tengan menos de", "Views/OrganizarPorGeneroView.axaml")]
    [InlineData("«Incluir»", "Views/OrganizarPorGeneroView.axaml")]
    [InlineData("Carpetas de destino", "ViewModels/OrganizarPorGeneroViewModel.cs")]
    [InlineData("Por carpeta de origen", "ViewModels/OrganizarPorGeneroViewModel.cs")]
    [InlineData("Se omiten", "ViewModels/OrganizarPorGeneroViewModel.cs")]
    [InlineData("Rutas rotas", "ViewModels/RekordboxViewModel.cs")]
    [InlineData("Unificar géneros", "Views/AsistenteView.axaml")]
    [InlineData("Género por carpeta y artista", "Views/AsistenteView.axaml")]
    public void Lo_que_cita_la_Ayuda_de_esta_pagina_existe_en_la_pantalla(string rotulo, string archivo)
    {
        var enPantalla = rotulo.Trim('«', '»');
        Assert.True(SeccionDeLaAyuda().Contains(rotulo, StringComparison.OrdinalIgnoreCase), $"la Ayuda no cita «{rotulo}»");
        Assert.True(Fuente(new[] { "Etiquetador.App" }.Concat(archivo.Split('/')).ToArray()).Contains(enPantalla, StringComparison.OrdinalIgnoreCase),
                    $"«{enPantalla}» no aparece en {archivo}: actualiza la Ayuda");
    }

    [Fact]
    public void La_Ayuda_cita_el_tipo_del_historial_las_cifras_del_codigo_y_lo_que_no_se_actualiza_solo()
    {
        var seccion = SeccionDeLaAyuda();
        Assert.Contains("«" + HistorialDeshacer.Texto(TipoEjecucion.OrganizarPorGenero) + "»", seccion);
        Assert.Contains($"{OrganizarPorGenero.LargoMaximoRuta} caracteres o más", seccion);
        Assert.Contains($"({OrganizarPorGenero.AgruparPorDefecto}, de serie)", seccion);
        foreach (var programa in new[] { "Serato", "Traktor", "Engine DJ", "VirtualDJ", "M3U8 y XML" })
            Assert.Contains(programa, seccion);
        Assert.Contains("Nunca se sobrescribe ni se renombra", seccion);
        // Y el tipo, también en la lista de ejemplos del historial.
        Assert.Contains("u «Organización por género»", Fuente("Etiquetador.App", "Views", "HelpView.axaml"));
    }

    [Fact]
    public void El_README_describe_la_pagina_entre_la_Bandeja_y_la_Ficha_DJ()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var lineas = File.ReadAllLines(Path.Combine(dir!.FullName, "README.md"));
        int Linea(string inicio) => Array.FindIndex(lineas, l => l.StartsWith(inicio, StringComparison.Ordinal));
        var organizar = Linea("- **Organizar por género**:");
        Assert.True(organizar > Linea("- **Bandeja de entrada**:"));
        Assert.True(organizar < Linea("- **Ficha DJ**:"));
    }
}
