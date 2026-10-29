using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Revisión final de la mejora de Volumen: la pestaña de punta a punta con el motor real (sin su
/// constructor, ver <see cref="MotorDePrueba"/>) y MP3 sintéticos que el ajuste sí cambia.
///   · La última barrera decide con la medida vigente del archivo, no con la fila (tras un deshacer
///     se subía con el pico de antes y el audio saturaba).
///   · Si el historial no se puede escribir, lo ya ajustado queda anotado y no se repite.
///   · Un archivo que no se puede leer no se atribuye al pico real.
///   · Rehacer la tabla avisa una sola vez.
///   · «Limpiar» borra las mediciones también en memoria y conserva la ganancia aplicada.
///   · El deshacer pone al día la medida con los pasos que devuelve.
/// </summary>
public class VolumenRevisionFinalTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Datos => Directory.CreateDirectory(Path.Combine(_dir, "datos")).FullName;
    private string Musica => Directory.CreateDirectory(Path.Combine(_dir, "Musica")).FullName;

    /// <summary>Un MP3 que el ajuste de volumen sí cambia: tramas con audio y margen en los dos sentidos.</summary>
    private string Mp3(string nombre)
    {
        var ruta = Path.Combine(Musica, nombre);
        File.WriteAllBytes(ruta, Mp3GainSintetico.Tramas(30, _ => 150));
        return ruta;
    }

    private static LoudnessRow Fila(string ruta, double lufs, double pico, double? picoReal, double target, double aplicada = 0)
        => new()
        {
            FileName = Path.GetFileName(ruta), Folder = Path.GetDirectoryName(ruta)!, FilePath = ruta,
            Lufs = lufs, PeakDb = pico, TruePeakDb = picoReal, GananciaAplicada = aplicada, Target = target,
        };

    private static LoudnessRow EnLaTabla(LoudnessViewModel vm, string ruta) => vm.Rows.Single(r => r.FilePath == ruta);

    // ---------------------------------------------------------------------------------------------
    // La barrera del pico real, con la medida vigente
    // ---------------------------------------------------------------------------------------------

    // El caso de la revisión, de punta a punta: la fila dice −13,0 LUFS y −3,3 dBTP (antes del deshacer),
    // el archivo está ya a −10,0 y −0,3 (lo que dice su medida guardada, que sigue valiendo), y se pide
    // «Nivel de club». Con la fila se subía un paso y el archivo acababa a +1,2 dBTP. Además, una fila
    // cuyo archivo cambió sin medida vigente ya no sale como candidata al rehacer la tabla.
    [Fact]
    public async Task Tras_un_deshacer_el_ajuste_no_sube_con_la_medida_de_antes()
    {
        var motor = MotorDePrueba.Crear(Datos, new Logger());
        var vm = new LoudnessViewModel(motor) { Modo = "Nivel de club" };
        var a = Mp3("a.mp3");
        var c = Mp3("c.mp3");
        motor.Loudness.Update(a, -10.0, -0.5, -0.3);   // el archivo tal como está ahora
        var filaA = Fila(a, -13.0, -3.5, -3.3, vm.Target, aplicada: -3);
        Assert.True(filaA.Candidata);                  // con la fila, +1 paso
        vm.Rows.Add(filaA);
        vm.Rows.Add(Fila(c, -16, -8, -7, vm.Target));  // sin medida guardada que valga
        var antes = File.ReadAllBytes(a);

        await vm.ApplyAsync(new[] { filaA });

        Assert.Equal(antes, File.ReadAllBytes(a));
        Assert.Contains(AjusteVolumenSeleccion.TextoYaEnNivel(1), vm.Status);
        Assert.Equal(-10.0, EnLaTabla(vm, a).Lufs);   // la tabla, con la medida de ahora

        var filaC = EnLaTabla(vm, c);
        Assert.False(filaC.Candidata);
        Assert.False(filaC.Ajustar);
        Assert.StartsWith("Ha cambiado desde que se midió", filaC.Aviso);
        Assert.Equal("Por medir", filaC.Estado);
    }

    // Una subida sin pico real cuyo archivo ya no está (Enriquecer lo renombró después del análisis):
    // antes el estado decía «su pico real no deja margen para aumentarla», que era falso.
    [Fact]
    public async Task Si_no_se_puede_leer_el_estado_no_culpa_al_pico_real()
    {
        var log = new Logger();
        var vm = new LoudnessViewModel(MotorDePrueba.Crear(Datos, log)) { Modo = "Nivel de club" };
        var fila = Fila(Path.Combine(Musica, "renombrada.mp3"), -16, -6, null, vm.Target);
        Assert.True(fila.Candidata);
        vm.Rows.Add(fila);

        await vm.ApplyAsync(new[] { fila });

        Assert.DoesNotContain("pico real no deja margen", vm.Status);
        Assert.Contains(AjusteVolumenSeleccion.TextoSinLeer(1), vm.Status);
    }

    // ---------------------------------------------------------------------------------------------
    // El historial falla: lo ya ajustado queda anotado
    // ---------------------------------------------------------------------------------------------

    // Antes la medida se ponía al día DESPUÉS de anotar el manifiesto. Si este fallaba, la fila volvía
    // con el nivel de antes y marcada: un segundo «Ajustar» bajaba el archivo otra vez, y la ganancia
    // aplicada no quedaba anotada.
    [Fact]
    public async Task Si_falla_el_historial_lo_ajustado_queda_anotado_y_no_se_vuelve_a_marcar()
    {
        var motor = MotorDePrueba.Crear(Datos, new Logger());
        var vm = new LoudnessViewModel(motor) { Modo = "Nivel de club" };
        var b = Mp3("b.mp3");
        motor.Loudness.Update(b, -7.0, -0.2, -0.1);
        var fila = Fila(b, -7.0, -0.2, -0.1, vm.Target);
        vm.Rows.Add(fila);

        // El manifiesto no se deja escribir: en su sitio hay una carpeta con su nombre (en la vida real,
        // la carpeta de datos sin permiso). Se cubren los segundos que puede tardar la prueba.
        var ahora = DateTime.Now;
        for (var s = -2; s <= 120; s++)
            Directory.CreateDirectory(Path.Combine(motor.Paths.UndoDir, $"run_{ahora.AddSeconds(s):yyyyMMdd_HHmmss}.jsonl"));

        await vm.ApplyAsync(new[] { fila });

        Assert.Contains("SE DETUVO", vm.Status);
        var guardada = motor.Loudness.Get(b);
        Assert.NotNull(guardada);
        Assert.Equal(-11.5, guardada!.Value.Lufs, 9);              // −7 − 3 pasos
        Assert.Equal(-4.5, guardada.Value.GananciaAplicada, 9);
        var enTabla = EnLaTabla(vm, b);
        Assert.Equal(-11.5, enTabla.Lufs, 9);
        Assert.False(enTabla.Ajustar);                               // ya está en nivel: no se repite
    }

    // ---------------------------------------------------------------------------------------------
    // Rehacer la tabla, con un solo aviso
    // ---------------------------------------------------------------------------------------------

    // Excluir puede mover la referencia automática, y eso rehace la tabla. Antes se vaciaba y se volvía
    // a llenar fila a fila: con 20.000 filas agrupadas, casi un segundo en el hilo de la interfaz.
    [Fact]
    public void Excluir_o_cambiar_el_nivel_rehace_la_tabla_con_un_solo_aviso()
    {
        var biblioteca = new LibraryStore(new AppConfig(), () => { }, new ScanCache(Path.Combine(Datos, "scan.json")));
        var vm = new LoudnessViewModel(MotorDePrueba.Crear(Datos, new Logger(), biblioteca));
        var altas = Enumerable.Range(0, 20).Select(i => Fila(Path.Combine(_dir, "M", $"alta{i}.mp3"), -9, -6, -5.5, vm.Target)).ToList();
        var bajas = Enumerable.Range(0, 10).Select(i => Fila(Path.Combine(_dir, "M", $"baja{i}.mp3"), -15, -0.1, -0.05, vm.Target)).ToList();
        foreach (var f in altas.Concat(bajas)) vm.Rows.Add(f);

        var avisos = new List<NotifyCollectionChangedAction>();
        vm.Rows.CollectionChanged += (_, e) => avisos.Add(e.Action);
        var antes = vm.Target;
        vm.ExcluirCanciones(bajas);

        Assert.NotEqual(antes, vm.Target);   // la referencia se ha movido: la tabla se ha rehecho
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, avisos);
        Assert.Equal(30, vm.Rows.Count);

        avisos.Clear();
        vm.Modo = "Nivel de club";
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, avisos);
        Assert.All(vm.Rows, r => Assert.Equal(-11, r.Target));
    }

    // ---------------------------------------------------------------------------------------------
    // «Limpiar carpeta de datos»
    // ---------------------------------------------------------------------------------------------

    // Antes borraba sonoridad.json del disco pero no de la memoria: la pestaña seguía usando las
    // mediciones y el siguiente guardado las volvía a escribir, o no, según lo que se hiciera antes de
    // cerrar. La ganancia aplicada se conserva: no se regenera.
    [Fact]
    public void Limpiar_borra_las_mediciones_tambien_en_memoria_y_conserva_la_ganancia_aplicada()
    {
        var biblioteca = new LibraryStore(new AppConfig(), () => { }, new ScanCache(Path.Combine(Datos, "scan.json")));
        var motor = MotorDePrueba.Crear(Datos, new Logger(), biblioteca);
        var ajustada = Path.Combine(Musica, "ajustada.wav");
        var otra = Path.Combine(Musica, "otra.wav");
        Tono(ajustada, 0.3);
        Tono(otra, 0.2);
        var m = motor.Loudness.Measure(ajustada, force: true)!.Value;
        motor.Loudness.Measure(otra, force: true);
        Tono(ajustada, 0.3 * Math.Pow(2, -0.25));   // se baja un paso…
        motor.Loudness.Ajustado(ajustada, m.Lufs, m.PeakDb, m.TruePeakDb, -1.5);   // …y se anota
        motor.Loudness.Save();
        Assert.True(File.Exists(motor.Paths.LoudnessCachePath));
        Assert.NotNull(motor.Loudness.Get(ajustada));
        Assert.NotNull(motor.Loudness.Get(otra));

        var ajustes = (SettingsViewModel)RuntimeHelpers.GetUninitializedObject(typeof(SettingsViewModel));
        typeof(SettingsViewModel).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ajustes, motor);
        ajustes.LimpiarCarpetaDatos();

        Assert.Null(motor.Loudness.Get(otra));
        Assert.Null(motor.Loudness.Get(ajustada));
        motor.Loudness.Save();   // lo que antes reescribía el archivo entero
        if (File.Exists(motor.Paths.LoudnessCachePath))
            Assert.DoesNotContain(Path.GetFileName(otra), File.ReadAllText(motor.Paths.LoudnessCachePath));
        Assert.True(File.Exists(motor.Paths.VolumenAplicadoPath));
        Assert.Equal(-1.5, motor.Loudness.Measure(ajustada)!.Value.GananciaAplicada, 9);
    }

    private static void Tono(string ruta, double amplitud)
    {
        const int fs = 48000;
        var x = new float[fs * 3 * 2];
        for (int i = 0; i < fs * 3; i++)
            x[2 * i] = x[2 * i + 1] = (float)(amplitud * Math.Sin(2 * Math.PI * 997 * i / fs));
        using var w = new NAudio.Wave.WaveFileWriter(ruta, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(fs, 2));
        w.WriteSamples(x, 0, x.Length);
    }

    // ---------------------------------------------------------------------------------------------
    // El deshacer pone al día la medida
    // ---------------------------------------------------------------------------------------------

    // Antes la medida solo caducaba por la fecha: la pestaña seguía con el nivel de antes del deshacer y
    // la ganancia aplicada dependía de volver a medir. Ahora se desplaza con los pasos devueltos.
    [Fact]
    public void Deshacer_un_ajuste_pone_al_dia_la_medida_y_la_ganancia_aplicada()
    {
        var motor = MotorDePrueba.Crear(Datos, new Logger());
        var d = Mp3("d.mp3");
        motor.Loudness.Update(d, -9.0, -0.6, -0.4);
        Assert.True(Mp3Gain.Apply(d, -2).Ok);   // la pestaña Volumen lo baja 3 dB…
        motor.Loudness.Ajustado(d, -9.0, -0.6, -0.4, -3.0);
        Directory.CreateDirectory(motor.Paths.UndoDir);
        File.WriteAllText(Path.Combine(motor.Paths.UndoDir, "run_20261029_120000.jsonl"), JsonSerializer.Serialize(new UndoRecord
        {
            OrigPath = d, FinalPath = d,
            Fields = new() { [UndoEngine.VolumeField] = FieldChange.Num(2, 0) },
            Origen = nameof(TipoEjecucion.Volumen),
            GananciaAnotada = true,
        }) + "\n");

        Assert.Equal(1, Deshacedor(motor).UndoLastRun().Reverted);   // …y «Deshacer última» lo devuelve

        var r = motor.Loudness.Get(d);
        Assert.NotNull(r);
        Assert.Equal(-9.0, r!.Value.Lufs, 9);
        Assert.Equal(-0.4, r.Value.TruePeakDb!.Value, 9);
        Assert.Equal(0, r.Value.GananciaAplicada);
    }

    /// <summary>Un UndoEngine que avisa al motor como el de la aplicación (AppEngine.SeguirDeshecho).</summary>
    private static UndoEngine Deshacedor(AppEngine motor)
    {
        var seguir = typeof(AppEngine).GetMethod("SeguirDeshecho", BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(ArchivoDeshecho) })!;
        return new UndoEngine(motor.Paths) { AlDeshacer = a => seguir.Invoke(motor, new object[] { a }) };
    }

    // ---------------------------------------------------------------------------------------------
    // Revisión de las correcciones
    // ---------------------------------------------------------------------------------------------

    // El caso de la revisión, de punta a punta: un MP3 a −10,76 que BeatTag 1.10 bajó 2 pasos (la medida
    // quedó a −13,76 sin ganancia anotada, y el manifiesto sin la marca) se deshace tras actualizar.
    // Antes quedaba con +3 dB «aplicados»: su nivel original calculado era −13,76 y se guardaba en
    // volumen-aplicado.json, que «Analizar de nuevo» conserva y «Limpiar» no borra.
    [Fact]
    public void Deshacer_un_ajuste_de_la_version_anterior_no_inventa_ganancia_aplicada()
    {
        var motor = MotorDePrueba.Crear(Datos, new Logger());
        var d = Mp3("v110.mp3");
        Assert.True(Mp3Gain.Apply(d, -2).Ok);
        motor.Loudness.Update(d, -13.76, -3.4, -3.2);   // lo que dejaba 1.10: la medida desplazada, sin G
        Directory.CreateDirectory(motor.Paths.UndoDir);
        File.WriteAllText(Path.Combine(motor.Paths.UndoDir, "run_20261022_161531.jsonl"), JsonSerializer.Serialize(new UndoRecord
        {
            OrigPath = d, FinalPath = d,
            Fields = new() { [UndoEngine.VolumeField] = FieldChange.Num(2, 0) },
            Origen = nameof(TipoEjecucion.Volumen),
        }) + "\n");

        Assert.Equal(1, Deshacedor(motor).UndoLastRun().Reverted);

        var r = motor.Loudness.Get(d)!.Value;
        Assert.Equal(-10.76, r.Lufs, 9);
        Assert.Equal(0, r.GananciaAplicada);
        motor.Loudness.Save();
        Assert.False(File.Exists(motor.Paths.VolumenAplicadoPath) && File.ReadAllText(motor.Paths.VolumenAplicadoPath).Contains("v110.mp3"));
    }

    // Lo que escribe la pestaña sí lleva la marca: ajustar y deshacer deja la ganancia aplicada en 0.
    [Fact]
    public async Task Ajustar_y_deshacer_con_la_pestana_deja_la_ganancia_aplicada_en_cero()
    {
        var motor = MotorDePrueba.Crear(Datos, new Logger());
        var vm = new LoudnessViewModel(motor) { Modo = "Nivel de club" };
        var b = Mp3("b.mp3");
        motor.Loudness.Update(b, -7.0, -0.2, -0.1);
        var fila = Fila(b, -7.0, -0.2, -0.1, vm.Target);
        vm.Rows.Add(fila);

        await vm.ApplyAsync(new[] { fila });
        Assert.Equal(-4.5, motor.Loudness.Get(b)!.Value.GananciaAplicada, 9);
        var manifiesto = Assert.Single(Directory.GetFiles(motor.Paths.UndoDir, "run_*.jsonl"));
        Assert.Contains("\"GananciaAnotada\":true", File.ReadAllText(manifiesto));

        Assert.Equal(1, Deshacedor(motor).UndoLastRun().Reverted);
        var r = motor.Loudness.Get(b)!.Value;
        Assert.Equal(-7.0, r.Lufs, 9);
        Assert.Equal(0, r.GananciaAplicada);
    }

    // El caso de la revisión: un MP3 se subió 2 pasos hasta −12,5 y «Deshacer última» lo devolvió a −15,5
    // sin que la tabla se enterase. Con «Nivel de plataformas» (−14) la fila decía «Reducir 1,5 dB» y la
    // confirmación «Se reducirá su nivel», pero con la medida de ahora el archivo se AUMENTABA. Ahora no
    // se toca, el resultado lo dice y la tabla lo vuelve a mostrar con su nivel de ahora.
    [Fact]
    public async Task Si_ahora_requiere_el_ajuste_contrario_no_se_toca_y_se_dice()
    {
        var motor = MotorDePrueba.Crear(Datos, new Logger());
        var vm = new LoudnessViewModel(motor) { Modo = "Nivel de plataformas de streaming" };
        var a = Mp3("a.mp3");
        motor.Loudness.Update(a, -15.5, -6.0, -6.0);   // el archivo tal como está ahora
        var fila = Fila(a, -12.5, -3.0, -3.0, vm.Target);
        Assert.True(fila.Candidata);
        Assert.True(fila.Gain < 0);                    // se confirmó para reducir
        Assert.StartsWith("Se reducirá su nivel.", AjusteVolumenSeleccion.TextosConfirmacion(
            new SeleccionAjuste(new[] { fila }, 0, 0, 0), porSeleccion: false, parciales: 0).Cuerpo);
        vm.Rows.Add(fila);
        var antes = File.ReadAllBytes(a);

        await vm.ApplyAsync(new[] { fila });

        Assert.Equal(antes, File.ReadAllBytes(a));
        Assert.StartsWith("Ajustadas 0 de 1", vm.Status);
        Assert.Contains(AjusteVolumenSeleccion.TextoSentidoContrario(1), vm.Status);
        var enTabla = EnLaTabla(vm, a);
        Assert.Equal(-15.5, enTabla.Lufs, 9);
        Assert.StartsWith("Aumentar", enTabla.Accion);
        Assert.True(enTabla.Ajustar);   // con su nivel de ahora, para que el usuario la vea y la confirme
    }

    // Las «Por medir» no cuentan como «requieren ajuste» en el resumen, ni en el filtro «Solo
    // desviadas», ni en la referencia automática, ni entre las que faltan por medir el pico real. Antes,
    // tras un ajuste en el que un archivo no se pudo leer, el estado decía «1 requiere ajuste» y
    // «Ajustar volumen» respondía «No hay ninguna grabación que requiera ajuste».
    [Fact]
    public void Las_filas_por_medir_no_cuentan_como_desviadas()
    {
        var vm = new LoudnessViewModel(MotorDePrueba.Crear(Datos, new Logger()));
        var medidas = Enumerable.Range(0, 30)
            .Select(i => Fila(Path.Combine(_dir, "M", $"n{i}.mp3"), -10, -8, -7.5, vm.Target)).ToList();
        LoudnessRow PorMedir(string nombre, double lufs, double pico) => new()
        {
            FileName = nombre, Folder = Path.Combine(_dir, "M"), FilePath = Path.Combine(_dir, "M", nombre),
            Lufs = lufs, PeakDb = pico, Target = vm.Target, MedidaCaducada = true,
        };
        // Bajas y sin margen: con sus valores de antes arrastrarían la referencia hacia abajo.
        var caducadas = Enumerable.Range(0, 6).Select(i => PorMedir($"c{i}.mp3", -16, -0.2)).ToList();
        var conMargen = PorMedir("m.mp3", -16, -8);   // se aumentaría y no tiene pico real
        var flac = PorMedir("d.flac", -20, -9);
        foreach (var f in medidas.Concat(caducadas).Append(conMargen).Append(flac)) vm.Rows.Add(f);

        vm.Modo = "Nivel de club";
        vm.Modo = LoudnessViewModel.ModoAutomatico;   // se calcula la referencia con la tabla

        var soloMedidas = LoudnessViewModel.ReferenciaAutomatica(medidas).Referencia;
        Assert.Equal(soloMedidas, vm.Target);
        Assert.NotEqual(soloMedidas, LoudnessViewModel.ReferenciaAutomatica(medidas.Concat(caducadas.Select(c => new LoudnessRow
        {
            FilePath = c.FilePath, Lufs = c.Lufs, PeakDb = c.PeakDb, Target = c.Target,
        })).ToList()).Referencia);

        Assert.Equal(AjusteVolumenSeleccion.TextoResumen(38, 30, 0, 0, 0, porMedir: 8), vm.Resumen);
        Assert.DoesNotContain("requieren ajuste", vm.Resumen);
        vm.SoloDesviadas = true;
        Assert.False(vm.RowsView.Filter!(EnLaTabla(vm, caducadas[0].FilePath)));
        Assert.True(vm.RowsView.Filter!(Fila(Path.Combine(_dir, "M", "x.mp3"), -16, -8, -7.5, vm.Target)));
        Assert.Empty(LoudnessViewModel.SinPicoReal(vm.Rows));
        Assert.Equal(0, vm.DesviadasNoMp3);
    }

    // «Limpiar carpeta de datos» borra las mediciones: la pestaña ya no las enseña como medidas y
    // marcadas (antes «Ajustar volumen» decodificaba una a una las marcadas, sin avisar).
    [Fact]
    public void Limpiar_vacia_la_tabla_de_volumen()
    {
        var biblioteca = new LibraryStore(new AppConfig(), () => { }, new ScanCache(Path.Combine(Datos, "scan.json")));
        var motor = MotorDePrueba.Crear(Datos, new Logger(), biblioteca);
        var vm = new LoudnessViewModel(motor) { Modo = "Nivel de club" };
        var a = Mp3("a.mp3");
        motor.Loudness.Update(a, -16, -8, -7);
        motor.Loudness.Save();
        vm.Rows.Add(Fila(a, -16, -8, -7, vm.Target));
        vm.Rows[0].Marcar(true);
        Assert.Single(vm.ParaAjustar());

        var ajustes = (SettingsViewModel)RuntimeHelpers.GetUninitializedObject(typeof(SettingsViewModel));
        typeof(SettingsViewModel).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ajustes, motor);
        ajustes.LimpiarCarpetaDatos();

        Assert.Empty(vm.Rows);
        Assert.Empty(vm.ParaAjustar());
        Assert.Equal("", vm.Resumen);
        Assert.Contains("Pulsa Analizar", vm.Status);
    }

    // ---------------------------------------------------------------------------------------------
    // Las medidas de la tabla se leen fuera del hilo de la interfaz
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Ejecuta <paramref name="accion"/> con un contexto de un solo hilo, como el de la interfaz: todas
    /// sus continuaciones vuelven a ese hilo, que se pasa a la acción.
    /// </summary>
    private static void EnElHiloDeLaInterfaz(Func<Thread, Task> accion)
    {
        var cola = new System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback D, object? S)>();
        Exception? error = null;
        var hilo = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new ContextoDeUnHilo(cola));
            var tarea = accion(Thread.CurrentThread);
            tarea.ContinueWith(_ => cola.CompleteAdding(), TaskScheduler.Default);
            foreach (var (d, s) in cola.GetConsumingEnumerable()) d(s);
            try { tarea.GetAwaiter().GetResult(); } catch (Exception e) { error = e; }
        });
        hilo.Start();
        Assert.True(hilo.Join(TimeSpan.FromSeconds(60)), "la acción no terminó");
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class ContextoDeUnHilo(System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback D, object? S)> cola) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            try { cola.Add((d, state)); } catch (InvalidOperationException) { /* ya terminó */ }
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
    }

    // Al terminar de analizar, la tabla se monta con las medidas guardadas: con 20.000 pistas, leerlas
    // en el hilo de la interfaz (cada una mira la fecha y el tamaño del archivo) la bloqueaba 0,2-0,3 s.
    [Fact]
    public async Task Al_terminar_de_analizar_las_medidas_no_se_leen_en_el_hilo_de_la_interfaz()
    {
        Tono(Path.Combine(Musica, "tono.wav"), 0.3);
        var biblioteca = new LibraryStore(new AppConfig { Folders = new List<string> { Musica } }, () => { },
                                          new ScanCache(Path.Combine(Datos, "scan.json")));
        await biblioteca.ScanAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(biblioteca.Tracks);
        var motor = MotorDePrueba.Crear(Datos, new Logger(), biblioteca);
        var vm = new LoudnessViewModel(motor);
        var enLaInterfaz = 0;

        EnElHiloDeLaInterfaz(async hilo =>
        {
            motor.Loudness.AlConsultar = () => { if (Thread.CurrentThread == hilo) Interlocked.Increment(ref enLaInterfaz); };
            await vm.MeasureCommand.ExecuteAsync(null);
        });

        Assert.Single(vm.Rows);
        Assert.Equal(0, enLaInterfaz);
    }

    // Lo mismo al fallar a mitad del ajuste: la tabla se rehacía leyendo cada medida en el hilo de la
    // interfaz, cuando en el camino normal ya se leían fuera.
    [Fact]
    public void Al_fallar_a_mitad_del_ajuste_las_medidas_no_se_leen_en_el_hilo_de_la_interfaz()
    {
        var motor = MotorDePrueba.Crear(Datos, new Logger());
        var vm = new LoudnessViewModel(motor) { Modo = "Nivel de club" };
        var filas = new[] { Mp3("a.mp3"), Mp3("b.mp3") }.Select(ruta =>
        {
            motor.Loudness.Update(ruta, -7.0, -0.2, -0.1);
            return Fila(ruta, -7.0, -0.2, -0.1, vm.Target);
        }).ToList();
        foreach (var f in filas) vm.Rows.Add(f);
        // Un fallo inesperado con la primera ya ajustada (aquí, al anunciar la segunda).
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LoudnessViewModel.Status) && vm.Status.StartsWith("Ajustando 2 de", StringComparison.Ordinal))
                throw new InvalidOperationException("fallo de prueba");
        };
        var enLaInterfaz = 0;

        EnElHiloDeLaInterfaz(async hilo =>
        {
            motor.Loudness.AlConsultar = () => { if (Thread.CurrentThread == hilo) Interlocked.Increment(ref enLaInterfaz); };
            await vm.ApplyAsync(filas);
        });

        Assert.StartsWith("Error durante el ajuste", vm.Status);
        Assert.Equal(-11.5, EnLaTabla(vm, filas[0].FilePath).Lufs, 9);   // la tabla, rehecha con lo ajustado
        Assert.Equal(0, enLaInterfaz);
    }
}
