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
        Assert.Contains("1 archivo vuelve a su nombre anterior, y la ejecución posterior ya no podrá deshacer sus cambios en él.", p.Cuerpo);
        Assert.Contains("empieza por la más reciente", p.Cuerpo);
        Assert.Contains("1 archivo no aparece en el disco", p.Cuerpo);
        Assert.Equal("Deshacer (2)", p.Aceptar);
    }

    [Fact]
    public void Tras_deshacer_se_dice_como_reintentar_lo_pendiente()
    {
        var res = new UndoResult("run_20261009_160301.jsonl", 3, 1, 1, 0, false, false);

        var texto = EnrichViewModel.ResumenDeshacer(res, vuelven: 2, retiradas: 0, nada: "nada", reintento: "siguen en el historial para reintentarlo.");

        Assert.StartsWith("Deshecho: 3 revertidos · 0 conservados · 1 ausentes · 1 errores.", texto);
        Assert.Contains("Quedan 2 pendientes: siguen en el historial para reintentarlo.", texto);
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
