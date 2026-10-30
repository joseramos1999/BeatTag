using System.Text.Json;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// E14/M14 · Lo que enseña y pregunta el diálogo «Historial de cambios»: qué se deshace, qué no se
/// puede deshacer todavía y por qué, y qué se pisaría. Y que deshacer pasa lo elegido a quien deshace.
/// </summary>
public class HistorialDeshacerVistaTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    private readonly AppPaths _paths;

    public HistorialDeshacerVistaTests()
    {
        _paths = new AppPaths(_dir);
        _paths.EnsureDirectories();
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static EjecucionDeshacer Ejecucion(string id, TipoEjecucion tipo, int pendientes = 3)
        => new(id, HistorialDeshacer.FechaDelId(id)!.Value, tipo, pendientes, 0, 0, 0, Rutas.Archivo(id + ".jsonl"), null);

    private static CambioDeshacer Cambio(string nombre, bool renombra = false, params string[] campos)
        => new(nombre, Rutas.Archivo("antes-" + nombre), Rutas.Archivo(nombre), renombra, campos, EstadoCambio.Pendiente);

    private static readonly EjecucionDeshacer Lote = Ejecucion("run_20261009_160301", TipoEjecucion.AplicarPropuestas);
    private static readonly EjecucionDeshacer Editor = Ejecucion("run_20261010_180200", TipoEjecucion.EdicionEtiquetas, 1);
    private static readonly EjecucionDeshacer Ia = Ejecucion("run_20261011_090000", TipoEjecucion.Renombrado, 1);

    [Fact]
    public void La_ejecucion_entera_sin_conflictos_se_deshace_toda()
    {
        var a = new AnalisisDeshacer(Lote, new[] { Cambio("a.mp3", true, "Title"), Cambio("b.mp3", false, "Genre") },
                                     Array.Empty<BloqueoDeshacer>(), Array.Empty<SolapeDeshacer>(), Array.Empty<CambioDeshacer>());

        var p = HistorialDeshacerViewModel.Planificar(a, soloMarcadas: false);

        Assert.True(p.Posible);
        Assert.Null(p.Huellas);   // todo: también se archivan las líneas que no se dejan leer
        Assert.Equal("Deshacer «Aplicar propuestas» del 09/10/2026 16:03", p.Titulo);
        Assert.Equal("2 archivos volverán a como estaban antes de esa ejecución; 1 recupera su nombre anterior.", p.Cuerpo);
        Assert.Contains("rekordbox", p.Nota);
        Assert.Equal("Deshacer (2)", p.Aceptar);
    }

    [Fact]
    public void Solo_las_marcadas_pasan_sus_huellas()
    {
        var a = new AnalisisDeshacer(Lote, new[] { Cambio("b.mp3", false, "Genre") },
                                     Array.Empty<BloqueoDeshacer>(), Array.Empty<SolapeDeshacer>(), Array.Empty<CambioDeshacer>());

        var p = HistorialDeshacerViewModel.Planificar(a, soloMarcadas: true);

        Assert.Equal(new[] { "b.mp3" }, p.Huellas);
        Assert.Equal("Deshacer 1 canción de «Aplicar propuestas» del 09/10/2026 16:03", p.Titulo);
        Assert.DoesNotContain("rekordbox", p.Nota);   // nada cambia de nombre
    }

    [Fact]
    public void Si_todo_esta_bloqueado_no_se_pregunta_y_se_dice_que_deshacer_antes()
    {
        var bloqueado = Cambio("a.mp3", true, "Title");
        var a = new AnalisisDeshacer(Lote, Array.Empty<CambioDeshacer>(),
                                     new[] { new BloqueoDeshacer(bloqueado, Ia, Rutas.Archivo("a (Extended).mp3"), false) },
                                     Array.Empty<SolapeDeshacer>(), Array.Empty<CambioDeshacer>());

        var p = HistorialDeshacerViewModel.Planificar(a, soloMarcadas: false);

        Assert.False(p.Posible);
        Assert.Equal("1 archivo no se puede deshacer todavía: después lo renombró o lo movió «Renombrado» del 11/10/2026 09:00. "
                     + "Para deshacerlo, deshaz antes esa ejecución.", p.Motivo);
    }

    [Fact]
    public void Lo_bloqueado_se_queda_fuera_y_se_avisa_de_lo_que_se_pisaria()
    {
        var libre = Cambio("b.mp3", false, "Genre");
        var pisado = Cambio("c.mp3", true, "Title", "Genre");
        var a = new AnalisisDeshacer(Lote, new[] { libre, pisado },
            new[]
            {
                new BloqueoDeshacer(Cambio("a.mp3", true), Ia, Rutas.Archivo("x.mp3"), false),
                new BloqueoDeshacer(Cambio("d.mp3", true), Ia, Rutas.Archivo("d.mp3"), true),
            },
            new[] { new SolapeDeshacer(pisado, new[] { Editor }, new[] { "Title" }, PierdeDeshacer: true) },
            new[] { libre });

        var p = HistorialDeshacerViewModel.Planificar(a, soloMarcadas: false);

        Assert.True(p.Posible);
        Assert.Equal(new[] { "b.mp3", "c.mp3" }, p.Huellas!.OrderBy(h => h));   // lo bloqueado no se pasa
        Assert.Contains("1 archivo no se puede deshacer todavía: después lo renombró o lo movió «Renombrado» del 11/10/2026 09:00.", p.Cuerpo);
        Assert.Contains("otro archivo ocupa ahora el nombre que le dejó esta ejecución", p.Cuerpo);
        Assert.Contains("Se quedan pendientes.", p.Cuerpo);
        Assert.Contains("1 de estos archivos se volvió a cambiar después en «Edición de etiquetas» del 10/10/2026 18:02.", p.Cuerpo);
        Assert.Contains("Lo que cambiaron las dos (título) se queda como lo dejó la posterior en 1 archivo.", p.Cuerpo);
        // Datos-1: la posterior sigue al archivo hasta su nombre anterior, y eso es lo que se promete.
        Assert.Contains("1 archivo vuelve a su nombre anterior, y el historial se pone al día para que la ejecución posterior lo encuentre con ese nombre: "
                        + "lo que cambió en él se queda y se podrá deshacer después.", p.Cuerpo);
        Assert.DoesNotContain("se detendrá", p.Cuerpo);
        Assert.DoesNotContain("volumen", p.Cuerpo);
        Assert.Contains("empieza por la más reciente", p.Cuerpo);
        Assert.Contains("1 archivo no aparece en el disco", p.Cuerpo);
        Assert.Equal("Deshacer (2)", p.Aceptar);
    }

    // Visto en revisión: si lo que bloquea se dio por perdido, «deshaz antes esa ejecución» no tiene
    // salida, porque una ejecución dada por perdida ya no se puede deshacer.
    [Fact]
    public void Si_lo_que_bloquea_se_dio_por_perdido_se_dice_que_solo_queda_darlo_por_perdido()
    {
        var a = new AnalisisDeshacer(Lote, Array.Empty<CambioDeshacer>(),
                                     new[] { new BloqueoDeshacer(Cambio("a.mp3", true, "Title"), Ia, Rutas.Archivo("x.mp3"), false, Perdido: true) },
                                     Array.Empty<SolapeDeshacer>(), Array.Empty<CambioDeshacer>());

        var p = HistorialDeshacerViewModel.Planificar(a, soloMarcadas: false);

        Assert.False(p.Posible);
        Assert.Equal("1 archivo no se puede deshacer: después lo renombró o lo movió «Renombrado» del 11/10/2026 09:00, "
                     + "pero ese cambio se dio por perdido y ya no se puede deshacer. Este archivo solo se puede dar por perdido también: "
                     + "usa «Dar por perdidos…» cuando no quede nada más que deshacer en esta ejecución.", p.Motivo);
        Assert.DoesNotContain("deshaz antes", p.Motivo);
    }

    [Fact]
    public void Lo_perdido_y_lo_que_aun_se_puede_deshacer_antes_se_explican_por_separado()
    {
        var bloqueos = new[]
        {
            new BloqueoDeshacer(Cambio("a.mp3", true), Ia, Rutas.Archivo("x.mp3"), false),
            new BloqueoDeshacer(Cambio("b.mp3", true), Ia, Rutas.Archivo("y.mp3"), false, Perdido: true),
            new BloqueoDeshacer(Cambio("c.mp3", true), Ia, Rutas.Archivo("z.mp3"), false, Perdido: true),
        };

        var texto = HistorialDeshacerViewModel.TextoBloqueados(bloqueos);

        Assert.StartsWith("1 archivo no se puede deshacer todavía: después lo renombró o lo movió «Renombrado» del 11/10/2026 09:00. "
                          + "Para deshacerlo, deshaz antes esa ejecución. ", texto);
        Assert.Contains("2 archivos no se pueden deshacer: después los renombró o los movió «Renombrado» del 11/10/2026 09:00, "
                        + "pero esos cambios se dieron por perdidos y ya no se pueden deshacer. Estos archivos solo se pueden dar por perdidos también", texto);
    }

    // Visto en revisión: la confirmación prometía que «recupera su nombre anterior» aunque ese nombre
    // lo ocupaba ya otro archivo; deshacer fallaba (sin perder nada) y el registro seguía pendiente.
    [Fact]
    public void Un_nombre_anterior_ocupado_se_explica_y_no_se_promete()
    {
        var libre = Cambio("b.mp3", false, "Genre");
        var traido = new BloqueoDeshacer(Cambio("a.mp3", true), Ia, Rutas.Archivo("antes-a.mp3"), true, NombreAnterior: true);
        var enDisco = Cambio("c.mp3", true, "Title");
        var a = new AnalisisDeshacer(Lote, new[] { libre }, new[] { traido }, Array.Empty<SolapeDeshacer>(), Array.Empty<CambioDeshacer>())
        {
            NombreOcupado = new[] { enDisco },
        };

        var p = HistorialDeshacerViewModel.Planificar(a, soloMarcadas: false);

        Assert.True(p.Posible);
        Assert.Equal(new[] { "b.mp3" }, p.Huellas);   // ni lo bloqueado ni lo ocupado se pasan
        Assert.StartsWith("1 archivo volverá a como estaba antes de esa ejecución.", p.Cuerpo);   // sin prometer nombres
        Assert.Contains("1 archivo no se puede deshacer todavía: otro archivo ocupa ahora el nombre que tenía antes, al que volvería "
                        + "(lo llevó ahí «Renombrado» del 11/10/2026 09:00). Para deshacerlo, deshaz antes esa ejecución.", p.Cuerpo);
        Assert.Contains("1 archivo no puede volver a su nombre anterior porque ya hay otro archivo con ese nombre en su carpeta: se queda pendiente. "
                        + "Cambia de nombre o aparta ese otro archivo y vuelve a intentarlo.", p.Cuerpo);

        // Si no queda nada más, es el motivo para no preguntar.
        var solo = new AnalisisDeshacer(Lote, Array.Empty<CambioDeshacer>(), Array.Empty<BloqueoDeshacer>(),
                                        Array.Empty<SolapeDeshacer>(), Array.Empty<CambioDeshacer>()) { NombreOcupado = new[] { enDisco, Cambio("d.mp3", true) } };
        var q = HistorialDeshacerViewModel.Planificar(solo, soloMarcadas: false);
        Assert.False(q.Posible);
        Assert.StartsWith("2 archivos no pueden volver a su nombre anterior porque ya hay otros archivos con esos nombres en sus carpetas: se quedan pendientes.", q.Motivo);
    }

    // Antes se avisaba de que la posterior perdía su archivo (y, con un ajuste de volumen, su
    // ganancia). Desde datos-1 la posterior lo sigue hasta su nombre anterior, también un volumen,
    // así que se promete eso y no se asusta con una pérdida que ya no ocurre.
    [Fact]
    public void Si_vuelve_a_su_nombre_se_dice_que_las_posteriores_lo_siguen()
    {
        var volumen = Ejecucion("run_20261012_120000", TipoEjecucion.Volumen, 2);
        var x = Cambio("x.mp3", true, "Title");
        var y = Cambio("y.mp3", true, "Title");
        var pierden = new[]
        {
            new SolapeDeshacer(x, new[] { volumen }, Array.Empty<string>(), PierdeDeshacer: true, VolumenPosterior: true),
            new SolapeDeshacer(y, new[] { Editor, volumen }, Array.Empty<string>(), PierdeDeshacer: true, VolumenPosterior: true),
        };

        var texto = HistorialDeshacerViewModel.TextoPierdeDeshacer(pierden);

        Assert.Equal(" 2 archivos vuelven a su nombre anterior, y el historial se pone al día para que las ejecuciones posteriores los encuentren con ese nombre: "
                     + "lo que cambiaron en ellos se queda y se podrá deshacer después.", texto);
        Assert.Equal("", HistorialDeshacerViewModel.TextoPierdeDeshacer(Array.Empty<SolapeDeshacer>()));
    }

    // Revisión 3: al deshacer una organización por género, la confirmación decía que los archivos
    // «recuperan su nombre anterior», y lo que recuperan es su carpeta: el nombre no cambia.
    private static readonly EjecucionDeshacer Organizacion = Ejecucion("run_20261030_120000", TipoEjecucion.OrganizarPorGenero, 2);

    private static CambioDeshacer Movido(string carpeta, string nombre)
        => new(nombre, Rutas.De("m", carpeta, nombre), Rutas.De("m", "Por género", "Latin", nombre), true, Array.Empty<string>(), EstadoCambio.Pendiente);

    [Fact]
    public void Deshacer_una_organizacion_dice_que_vuelven_a_su_carpeta()
    {
        var a = new AnalisisDeshacer(Organizacion, new[] { Movido("Descargas", "a.mp3"), Movido("WarmUp", "b.mp3") },
                                     Array.Empty<BloqueoDeshacer>(), Array.Empty<SolapeDeshacer>(), Array.Empty<CambioDeshacer>());

        var p = HistorialDeshacerViewModel.Planificar(a, soloMarcadas: false);

        Assert.Equal("2 archivos volverán a como estaban antes de esa ejecución; 2 vuelven a su carpeta anterior.", p.Cuerpo);
        Assert.DoesNotContain("nombre", p.Cuerpo);
        Assert.Contains("rekordbox", p.Nota);
        Assert.Equal("carpeta", Movido("Descargas", "a.mp3").CamposTexto);

        // Uno solo, en singular.
        var uno = new AnalisisDeshacer(Organizacion, new[] { Movido("Descargas", "a.mp3") },
                                       Array.Empty<BloqueoDeshacer>(), Array.Empty<SolapeDeshacer>(), Array.Empty<CambioDeshacer>());
        Assert.Equal("1 archivo volverá a como estaba antes de esa ejecución; 1 vuelve a su carpeta anterior.",
                     HistorialDeshacerViewModel.Planificar(uno, soloMarcadas: false).Cuerpo);
    }

    [Fact]
    public void Si_unos_cambian_de_nombre_y_otros_de_carpeta_se_dicen_las_dos_cosas()
    {
        var ambos = new CambioDeshacer("c.mp3", Rutas.De("m", "Descargas", "c (1).mp3"), Rutas.De("m", "Pop", "c.mp3"), true,
                                       Array.Empty<string>(), EstadoCambio.Pendiente);
        var mezcla = new AnalisisDeshacer(Lote, new[] { Cambio("a.mp3", true, "Title"), Movido("Descargas", "b.mp3") },
                                          Array.Empty<BloqueoDeshacer>(), Array.Empty<SolapeDeshacer>(), Array.Empty<CambioDeshacer>());
        var solo = new AnalisisDeshacer(Lote, new[] { ambos }, Array.Empty<BloqueoDeshacer>(), Array.Empty<SolapeDeshacer>(), Array.Empty<CambioDeshacer>());

        Assert.EndsWith("; 2 recuperan su nombre o su carpeta anterior.", HistorialDeshacerViewModel.Planificar(mezcla, soloMarcadas: false).Cuerpo);
        Assert.EndsWith("; 1 recupera su nombre y su carpeta anteriores.", HistorialDeshacerViewModel.Planificar(solo, soloMarcadas: false).Cuerpo);
        Assert.Equal("nombre, carpeta", ambos.CamposTexto);
    }

    [Fact]
    public void Si_vuelve_a_su_carpeta_se_dice_que_la_posterior_lo_encuentra_ahi()
    {
        var movido = Movido("Descargas", "a.mp3");
        var pierden = new[] { new SolapeDeshacer(movido, new[] { Editor }, Array.Empty<string>(), PierdeDeshacer: true) };

        Assert.Equal(" 1 archivo vuelve a su carpeta anterior, y el historial se pone al día para que la ejecución posterior lo encuentre ahí: "
                     + "lo que cambió en él se queda y se podrá deshacer después.", HistorialDeshacerViewModel.TextoPierdeDeshacer(pierden));
    }

    // Revisión 3: al bloqueo siempre se le añadía «Se quedan pendientes.», también con un solo archivo.
    [Fact]
    public void Un_solo_archivo_bloqueado_se_queda_pendiente_en_singular()
    {
        var uno = new AnalisisDeshacer(Organizacion, new[] { Movido("Descargas", "a.mp3") },
                                       new[] { new BloqueoDeshacer(Movido("WarmUp", "b.mp3"), Ia, Rutas.Archivo("b.mp3"), false) },
                                       Array.Empty<SolapeDeshacer>(), Array.Empty<CambioDeshacer>());
        var dos = new AnalisisDeshacer(Organizacion, new[] { Movido("Descargas", "a.mp3") },
                                       new[]
                                       {
                                           new BloqueoDeshacer(Movido("WarmUp", "b.mp3"), Ia, Rutas.Archivo("b.mp3"), false),
                                           new BloqueoDeshacer(Movido("WarmUp", "c.mp3"), Ia, Rutas.Archivo("c.mp3"), false),
                                       },
                                       Array.Empty<SolapeDeshacer>(), Array.Empty<CambioDeshacer>());

        var p = HistorialDeshacerViewModel.Planificar(uno, soloMarcadas: false);
        Assert.EndsWith("Para deshacerlo, deshaz antes esa ejecución. Se queda pendiente.", p.Cuerpo);
        Assert.DoesNotContain("Se quedan pendientes", p.Cuerpo);
        Assert.EndsWith("Para deshacerlos, deshaz antes esa ejecución. Se quedan pendientes.", HistorialDeshacerViewModel.Planificar(dos, soloMarcadas: false).Cuerpo);
    }

    [Fact]
    public void Tras_deshacer_se_dice_como_reintentar_lo_pendiente()
    {
        var res = new UndoResult("run_20261009_160301.jsonl", 3, 1, 1, 0, false, false);

        var texto = EnrichViewModel.ResumenDeshacer(res, vuelven: 2, retiradas: 0, nada: "nada", reintento: "se puede reintentar desde el historial.");

        Assert.StartsWith("Deshecho: 3 revertidos · 0 conservados · 1 ausente · 1 error.", texto);   // singular y plural correctos
        Assert.Contains("Quedan 2 pendientes: se puede reintentar desde el historial.", texto);
        Assert.StartsWith("Deshecho: 1 revertido · 1 conservado · 0 ausentes · 1 error. Queda 1 pendiente: x",
                          EnrichViewModel.ResumenDeshacer(new UndoResult("run.jsonl", 1, 1, 0, 1, false, false), 0, 0, "nada", "x"));
        Assert.Contains("2 canciones vuelven a estar pendientes", texto);
        Assert.Equal("nada", EnrichViewModel.ResumenDeshacer(new UndoResult("", 0, 0, 0, 0, false, true), 0, 0, "nada", "x"));
    }

    // --- El diálogo con un historial de verdad ---

    private void Escribir(string nombre, params UndoRecord[] registros)
        => File.WriteAllText(Path.Combine(_paths.UndoDir, nombre), string.Join("\n", registros.Select(r => JsonSerializer.Serialize(r))) + "\n");

    private static UndoRecord Genero(string ruta) => new()
    {
        OrigPath = ruta, FinalPath = ruta, Fields = new() { ["Genre"] = FieldChange.Arr(new[] { "x" }, new[] { "House" }) },
    };

    private static async Task Esperar(Func<bool> condicion)
    {
        for (var i = 0; i < 100 && !condicion(); i++) await Task.Delay(50);
        Assert.True(condicion());
    }

    [Fact]
    public async Task Lista_lo_pendiente_y_deshace_solo_las_marcadas()
    {
        Escribir("run_20261001_100000.deshecho.jsonl", Genero(Rutas.Archivo("viejo.mp3")));
        Escribir("run_20261009_160301.jsonl", Genero(Rutas.Archivo("a.mp3")), Genero(Rutas.Archivo("b.mp3")));

        (EjecucionDeshacer E, IReadOnlyCollection<string>? H)? pedido = null;
        var vm = new HistorialDeshacerViewModel(new HistorialDeshacer(_paths.UndoDir), (e, h) =>
        {
            pedido = (e, h);
            return Task.FromResult<string?>("hecho");
        });

        await vm.CargarAsync();
        Assert.Single(vm.Ejecuciones);   // lo ya deshecho no sale si no se pide
        Assert.Equal("run_20261009_160301", vm.Seleccionada!.E.Id);
        Assert.True(vm.PuedeDeshacerEjecucion);
        await Esperar(() => vm.Cambios.Count == 2);

        vm.MostrarDeshechas = true;
        Assert.Equal(2, vm.Ejecuciones.Count);
        Assert.Equal("run_20261009_160301", vm.Seleccionada!.E.Id);   // la elegida se conserva
        await Esperar(() => vm.Cambios.Count == 2);

        Assert.False(vm.PuedeDeshacerMarcadas);
        vm.MarcarTodasCommand.Execute(null);
        Assert.Equal(2, vm.Marcadas);
        vm.DesmarcarTodasCommand.Execute(null);
        Assert.Equal(0, vm.Marcadas);
        var b = vm.Cambios.Single(c => c.Archivo == "b.mp3");
        b.Marcada = true;
        Assert.Equal(1, vm.Marcadas);
        Assert.True(vm.PuedeDeshacerMarcadas);

        var plan = await vm.PrepararAsync(soloMarcadas: true);
        Assert.NotNull(plan);
        Assert.True(plan!.Posible);
        await vm.EjecutarAsync(plan);

        Assert.NotNull(pedido);
        Assert.Equal("run_20261009_160301", pedido!.Value.E.Id);
        Assert.Equal(new[] { b.C.Huella }, pedido.Value.H);
        Assert.Equal("hecho", vm.Estado);
    }

    // El aviso de un deshacer atascado decía que se podía dar por perdido, pero ninguna pantalla lo
    // ofrecía: la única salida era borrar el manifiesto a mano.
    [Fact]
    public async Task Dar_por_perdidos_saca_la_ejecucion_de_la_cola_sin_tocar_archivos()
    {
        Escribir("run_20261009_160301.jsonl", Genero(Rutas.Archivo("a.mp3")), Genero(Rutas.Archivo("b.mp3")));
        var undo = new UndoEngine(_paths);
        var vm = new HistorialDeshacerViewModel(new HistorialDeshacer(_paths.UndoDir), (_, _) => Task.FromResult<string?>(""), undo.DarPorPerdidos);
        await vm.CargarAsync();
        Assert.True(vm.PuedeDarPorPerdidos);

        var c = HistorialDeshacerViewModel.ConfirmacionPerdidos(vm.Seleccionada!.E);
        Assert.Equal("Dar por perdidos los cambios pendientes de «Géneros» del 09/10/2026 16:03", c.Titulo);
        Assert.StartsWith("2 cambios pendientes dejan de estar por deshacer. No se toca ningún archivo.", c.Cuerpo);

        await vm.DarPorPerdidosAsync();

        Assert.Equal("2 cambios pendientes de «Géneros» del 09/10/2026 16:03 quedan dados por perdidos. No se ha tocado ningún archivo.", vm.Estado);
        Assert.True(File.Exists(Path.Combine(_paths.UndoDir, "run_20261009_160301.perdidos.jsonl")));
        Assert.Empty(vm.Ejecuciones);   // ya no queda nada pendiente
        Assert.True(undo.UndoLastRun().None);
        vm.MostrarDeshechas = true;
        Assert.Equal(EstadoEjecucion.Perdida, Assert.Single(vm.Ejecuciones).E.Estado);
    }

    // Visto en revisión (1 de cada 11 pasadas de la suite): elegir una ejecución lanza la lectura de
    // su manifiesto sin esperarla, y «Dar por perdidos» justo después fallaba por uso compartido del
    // archivo. Se comprueba el orden (el detalle ya está leído cuando se da por perdido), que es lo
    // que lo evita, en vez de fiarse de que las dos lecturas coincidan en el tiempo.
    [Fact]
    public async Task Dar_por_perdidos_nada_mas_abrir_espera_a_que_termine_la_lectura()
    {
        Escribir("run_20261009_160301.jsonl", Enumerable.Range(0, 4000).Select(i => Genero(Rutas.Archivo($"tema {i}.mp3"))).ToArray());
        var undo = new UndoEngine(_paths);
        HistorialDeshacerViewModel? vm = null;
        var cargadosAlDarPorPerdidos = -1;
        vm = new HistorialDeshacerViewModel(new HistorialDeshacer(_paths.UndoDir), (_, _) => Task.FromResult<string?>(""), m =>
        {
            cargadosAlDarPorPerdidos = vm!.Cambios.Count;
            return undo.DarPorPerdidos(m);
        });
        await vm.CargarAsync();

        await vm.DarPorPerdidosAsync();   // sin esperar a que salga el detalle, como un clic muy rápido

        Assert.Equal(4000, cargadosAlDarPorPerdidos);
        Assert.StartsWith("4000 cambios pendientes de «Géneros»", vm.Estado);
        Assert.True(File.Exists(Path.Combine(_paths.UndoDir, "run_20261009_160301.perdidos.jsonl")));
        Assert.False(File.Exists(Path.Combine(_paths.UndoDir, "run_20261009_160301.jsonl")));
    }

    [Fact]
    public async Task Preparar_y_deshacer_esperan_a_que_termine_la_lectura_del_detalle()
    {
        Escribir("run_20261009_160301.jsonl", Enumerable.Range(0, 4000).Select(i => Genero(Rutas.Archivo($"tema {i}.mp3"))).ToArray());
        HistorialDeshacerViewModel? vm = null;
        var cargadosAlDeshacer = -1;
        vm = new HistorialDeshacerViewModel(new HistorialDeshacer(_paths.UndoDir), (_, _) =>
        {
            cargadosAlDeshacer = vm!.Cambios.Count;
            return Task.FromResult<string?>("hecho");
        });
        await vm.CargarAsync();

        var plan = await vm.PrepararAsync(soloMarcadas: false);

        Assert.Equal(4000, vm.Cambios.Count);   // la lectura ya terminó al preparar
        Assert.True(plan!.Posible);
        // Elegir otra vez lanza otra lectura, como al pulsar en la lista: deshacer también la espera.
        var fila = vm.Seleccionada;
        vm.Seleccionada = null;
        vm.Seleccionada = fila;
        await vm.EjecutarAsync(plan);
        Assert.Equal(4000, cargadosAlDeshacer);
    }

    [Fact]
    public async Task Sin_nada_marcado_no_se_prepara_nada()
    {
        Escribir("run_20261009_160301.jsonl", Genero(Rutas.Archivo("a.mp3")));
        var vm = new HistorialDeshacerViewModel(new HistorialDeshacer(_paths.UndoDir), (_, _) => Task.FromResult<string?>(""));
        await vm.CargarAsync();

        Assert.Null(await vm.PrepararAsync(soloMarcadas: true));
        Assert.Equal("Marca antes alguna canción pendiente.", vm.Estado);
    }
}
