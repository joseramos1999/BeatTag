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
        }) + "\n");

        var seguir = typeof(AppEngine).GetMethod("SeguirDeshecho", BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(ArchivoDeshecho) })!;
        var undo = new UndoEngine(motor.Paths) { AlDeshacer = a => seguir.Invoke(motor, new object[] { a }) };
        Assert.Equal(1, undo.UndoLastRun().Reverted);   // …y «Deshacer última» lo devuelve

        var r = motor.Loudness.Get(d);
        Assert.NotNull(r);
        Assert.Equal(-9.0, r!.Value.Lufs, 9);
        Assert.Equal(-0.4, r.Value.TruePeakDb!.Value, 9);
        Assert.Equal(0, r.Value.GananciaAplicada);
    }
}
