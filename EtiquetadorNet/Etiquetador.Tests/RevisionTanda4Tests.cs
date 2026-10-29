using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// Revisión de la unión de la tanda 4: las cachés de audio y las exclusiones de Volumen siguen al
/// archivo por todos los caminos que escriben en él, y la caché de la calidad real (P4) se carga y se
/// guarda como las demás.
/// </summary>
public class RevisionTanda4Tests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Ruta(params string[] partes)
    {
        var r = Path.Combine(new[] { _dir }.Concat(partes).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(r)!);
        return r;
    }

    /// <summary>
    /// El motor real sin su constructor (que solo trabaja sobre la carpeta de datos de Documentos), con
    /// las cachés de audio, la configuración y las colas que usan SeguirArchivo y los guardados.
    /// </summary>
    private AppEngine Motor(Logger log, LibraryStore? biblioteca = null)
    {
        var motor = (AppEngine)RuntimeHelpers.GetUninitializedObject(typeof(AppEngine));
        void Poner(string campo, object valor)
        {
            var f = typeof(AppEngine).GetField(campo, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(f != null, $"AppEngine ya no tiene el campo {campo}: actualiza Motor");
            f!.SetValue(motor, valor);
        }
        var datos = Ruta("datos", "x");
        var paths = new AppPaths(Path.GetDirectoryName(datos)!);
        var api = new ApiClient(paths);
        var huellas = new Fingerprint(paths, log);
        Poner("<Paths>k__BackingField", paths);
        Poner("<Logger>k__BackingField", log);
        Poner("<Avisos>k__BackingField", new AvisosApp());
        Poner("<Config>k__BackingField", new AppConfig());
        Poner("<Api>k__BackingField", api);
        Poner("<AudioAceptadas>k__BackingField", new IgnoreList(paths.AudioAceptadasPath));
        Poner("<Loudness>k__BackingField", new LoudnessScanner(paths.LoudnessCachePath, log));
        Poner("<Fingerprints>k__BackingField", new FingerprintScanner(paths.FingerprintCachePath, huellas.FpcalcPath, log));
        Poner("<Identificacion>k__BackingField", new IdentificationScanner(paths.IdentificacionCachePath, huellas, new AcoustIdProvider(api),
                                                                          new AuddProvider(new HttpClient(), log), new HttpClient(), log));
        Poner("<Analysis>k__BackingField", new AnalysisCache(paths.AnalysisCachePath));
        Poner("<Marks>k__BackingField", new ApplyMarks(paths.ApplyMarksPath));
        Poner("<Applied>k__BackingField", new IgnoreList(paths.AppliedPath));
        Poner("<Ignored>k__BackingField", new IgnoreList(paths.IgnoredPath));
        Poner("_cerrojoSustituciones", new object());
        Poner("_sustituciones", new AlmacenSustituciones(AlmacenSustituciones.RutaEn(paths.DataDir)));
        Poner("_cerrojoHuellas", new object());
        Poner("_colaHuellas", Task.CompletedTask);
        Poner("_cerrojoEspectro", new object());
        Poner("_cerrojoCargaEspectro", new object());
        Poner("_relojPunto", System.Diagnostics.Stopwatch.StartNew());
        if (biblioteca != null) Poner("<Library>k__BackingField", biblioteca);
        motor.EnLaInterfaz = a => a();   // en la aplicación, Dispatcher.UIThread.Post
        return motor;
    }

    private static object? Privado(AppEngine motor, string metodo, params object[] args)
    {
        var m = typeof(AppEngine).GetMethod(metodo, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(m != null, $"AppEngine ya no tiene el método {metodo}");
        return m!.Invoke(motor, args);
    }

    private static object? Campo(AppEngine motor, string campo)
        => typeof(AppEngine).GetField(campo, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(motor);

    /// <summary>Deja en espectro.json una medida válida para cada archivo (con su fecha y tamaño de ahora).</summary>
    private static void MedidasGuardadas(AppPaths paths, params string[] archivos)
    {
        var mapa = archivos.ToDictionary(a => a, a =>
        {
            var s = SelloArchivo.De(a);
            return new Dictionary<string, long>
            {
                ["M"] = s.M, ["S"] = s.S, ["E"] = (int)EstadoCorte.SinCorte, ["C"] = 0, ["T"] = 21_000, ["V"] = EspectroScanner.VersionMetodo,
            };
        });
        File.WriteAllText(paths.EspectroCachePath, JsonSerializer.Serialize(mapa));
    }

    private static MedidaEspectro? MedidaEnDisco(AppPaths paths, string archivo)
        => new EspectroScanner(paths.EspectroCachePath).Get(archivo);

    // ---------------------------------------------------------------------------------------------
    // datos-2: importar BPM y tonalidad de rekordbox
    // ---------------------------------------------------------------------------------------------

    // Importar escribe etiquetas, y la calidad real (como las huellas, la sonoridad y las
    // identificaciones de pago) caducaba por la fecha: importar 9.213 canciones obligaba a medirlas otra
    // vez. Ahora la medida sigue al archivo, queda guardada y el manifiesto deshace la importación.
    [Fact]
    public void Importar_de_rekordbox_conserva_la_calidad_real_y_se_puede_deshacer()
    {
        var log = new Logger();
        var motor = Motor(log);
        var tema = Ruta("Musica", "Tema uno.mp3");
        var otra = Ruta("Musica", "Otra.mp3");
        Mp3Fixture.WriteMinMp3(tema);
        Mp3Fixture.WriteMinMp3(otra);
        File.SetLastWriteTimeUtc(tema, DateTime.UtcNow.AddDays(-3));   // para que escribir cambie la fecha
        MedidasGuardadas(motor.Paths, tema, otra);
        var antes = SelloArchivo.De(tema);
        var xml = Ruta("coleccion.xml");
        File.WriteAllText(xml, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <DJ_PLAYLISTS Version="1.0.0">
              <COLLECTION Entries="1">
                <TRACK TrackID="1" Name="Tema uno" AverageBpm="124.00" Tonality="8A" Location="{RekordboxRelocator.PathToLocation(tema)}"/>
              </COLLECTION>
            </DJ_PLAYLISTS>
            """);
        var manifiesto = motor.Paths.NuevoManifiesto();

        var r = LibraryViewModel.ImportarConHistorial(xml, new[] { (tema, "Tema uno.mp3"), (otra, "Otra.mp3") }, overwrite: true,
                                                      manifiesto, (ruta, sello) => motor.SeguirArchivo(ruta, ruta, sello));
        motor.GuardarCachesLigeras();

        Assert.Equal((1, 1, 1), (r.Entradas, r.Coincidencias, r.Actualizadas));
        Assert.Empty(r.Fallos);
        Assert.NotEqual(antes, SelloArchivo.De(tema));             // el archivo sí cambió
        Assert.NotNull(MedidaEnDisco(motor.Paths, tema));          // y su medida sigue valiendo, ya guardada
        Assert.NotNull(MedidaEnDisco(motor.Paths, otra));

        // El manifiesto lleva el nombre que reconoce el historial y deshace la importación.
        Assert.Matches(@"^run_\d{8}_\d{6}(_\d+)?\.jsonl$", Path.GetFileName(manifiesto));
        Assert.Equal(1, new UndoEngine(motor.Paths).UndoLastRun(manifiesto).Reverted);
        using var f = TagLib.File.Create(tema);
        Assert.Equal(0u, f.Tag.BeatsPerMinute);
    }

    // interfaz-11: el texto concuerda en número también con una sola pista.
    [Fact]
    public void La_importacion_con_una_sola_pista_que_no_esta_lo_dice_en_singular()
    {
        Assert.Equal("La única pista de la colección de rekordbox no está en las carpetas de la biblioteca. "
                   + "En la página rekordbox se ve qué falta en cada lado y qué rutas están rotas.",
                     LibraryViewModel.TextoImportacionRekordbox(1, 0, 0, 0));
    }

    /// <summary>Un historial que deja de poder escribirse en la anotación número <c>fallaEn</c> (el disco, lleno a mitad).</summary>
    private sealed class HistorialQueFalla : IHistorialDeshacer
    {
        private readonly int _fallaEn;
        private int _llamadas;
        public readonly List<UndoRecord> Anotados = new();
        public HistorialQueFalla(int fallaEn) => _fallaEn = fallaEn;

        public string Anotar(UndoRecord registro)
        {
            if (++_llamadas >= _fallaEn) return "No hay espacio suficiente en el disco.";
            Anotados.Add(registro);
            return "";
        }
    }

    /// <summary>Canciones sin BPM ni tonalidad y una colección de rekordbox que trae ambos para todas.</summary>
    private (string Xml, (string Ruta, string Nombre)[] Canciones) ColeccionDe(int cuantas)
    {
        var canciones = Enumerable.Range(1, cuantas).Select(i =>
        {
            var ruta = Ruta("Musica", $"Tema {i}.mp3");
            Mp3Fixture.WriteMinMp3(ruta);
            return (ruta, Path.GetFileName(ruta));
        }).ToArray();
        var pistas = string.Join("\n", canciones.Select((c, i) =>
            $"""<TRACK TrackID="{i + 1}" Name="Tema {i + 1}" AverageBpm="124.00" Tonality="8A" Location="{RekordboxRelocator.PathToLocation(c.ruta)}"/>"""));
        var xml = Ruta("coleccion.xml");
        File.WriteAllText(xml, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <DJ_PLAYLISTS Version="1.0.0">
              <COLLECTION Entries="{cuantas}">
            {pistas}
              </COLLECTION>
            </DJ_PLAYLISTS>
            """);
        return (xml, canciones);
    }

    private static uint BpmDe(string ruta)
    {
        using var f = TagLib.File.Create(ruta);
        return f.Tag.BeatsPerMinute;
    }

    // datos-2 (segunda revisión): la importación anotaba DESPUÉS de guardar, con un StreamWriter propio,
    // y si una anotación fallaba seguía escribiendo en las canciones siguientes, que quedaban cambiadas
    // sin poder deshacerse. Ahora anota antes de guardar y se para en la primera que no puede anotar.
    [Fact]
    public void Si_el_historial_deja_de_escribirse_a_mitad_la_importacion_se_para_y_no_toca_ninguna_cancion_mas()
    {
        var (xml, canciones) = ColeccionDe(5);
        var historial = new HistorialQueFalla(fallaEn: 3);
        var seguidas = new List<string>();

        var r = LibraryViewModel.ImportarBpmYTonalidad(xml, canciones, overwrite: true, historial, "",
                                                       (ruta, _) => seguidas.Add(ruta));

        // Las dos primeras, escritas y anotadas; la tercera (la que no se pudo anotar) y las siguientes, intactas.
        Assert.Equal(new uint[] { 124, 124, 0, 0, 0 }, canciones.Select(c => BpmDe(c.Ruta)).ToArray());
        Assert.Equal(canciones.Take(2).Select(c => c.Ruta), historial.Anotados.Select(a => a.OrigPath));
        Assert.Equal(canciones.Take(2).Select(c => c.Ruta), seguidas);
        Assert.Equal((5, 5, 2), (r.Entradas, r.Coincidencias, r.Actualizadas));
        // Ninguna sale contada a la vez como actualizada y como fallo.
        Assert.Empty(r.Fallos);
        Assert.Contains("Se ha detenido en «Tema 3.mp3»", r.Detenido);
        Assert.Contains("No hay espacio suficiente en el disco.", r.Detenido);

        var estado = LibraryViewModel.TextoImportacionRekordbox(r.Entradas, r.Coincidencias, r.Actualizadas, r.Fallos.Count, r.Detenido);
        Assert.StartsWith("rekordbox: 5 canciones encontradas · 2 actualizadas · ⚠ Se ha detenido en «Tema 3.mp3»", estado);
        Assert.Contains("lo anterior sí se puede deshacer", estado);
        Assert.DoesNotContain("..", estado);
    }

    // Lo que sí se escribió antes de la parada queda anotado en el manifiesto de verdad y se deshace.
    [Fact]
    public void Lo_importado_antes_de_la_parada_se_puede_deshacer()
    {
        var (xml, canciones) = ColeccionDe(3);
        var paths = new AppPaths(Path.GetDirectoryName(Ruta("datos", "x"))!);
        var manifiesto = paths.NuevoManifiesto();
        LibraryViewModel.ImportacionRekordbox r;
        using (var m = ManifiestoDeshacer.Abrir(manifiesto, out var error))
        {
            Assert.NotNull(m);
            // El manifiesto de verdad deja de aceptar anotaciones tras la primera (como si se llenara el disco).
            var unaYNada = new HistorialTrasN(m!, 1);
            r = LibraryViewModel.ImportarBpmYTonalidad(xml, canciones, overwrite: true, unaYNada, error, (_, _) => { });
        }

        Assert.Equal(1, r.Actualizadas);
        Assert.NotEqual("", r.Detenido);
        Assert.Equal(1, new UndoEngine(paths).UndoLastRun(manifiesto).Reverted);
        Assert.All(canciones, c => Assert.Equal(0u, BpmDe(c.Ruta)));
    }

    /// <summary>Pasa las <c>n</c> primeras anotaciones al historial de verdad y rechaza las demás.</summary>
    private sealed class HistorialTrasN(IHistorialDeshacer real, int n) : IHistorialDeshacer
    {
        private int _hechas;
        public string Anotar(UndoRecord registro) => _hechas++ < n ? real.Anotar(registro) : "No hay espacio suficiente en el disco.";
    }

    // Si el historial no se puede ni abrir (carpeta de datos sin permiso o sin espacio), no se toca nada.
    [Fact]
    public void Sin_historial_para_deshacer_la_importacion_no_toca_ninguna_cancion()
    {
        var (xml, canciones) = ColeccionDe(2);
        var ocupado = Ruta("soy-un-archivo");
        File.WriteAllText(ocupado, "x");   // la «carpeta» del manifiesto es un archivo: no se puede crear dentro

        var r = LibraryViewModel.ImportarConHistorial(xml, canciones, overwrite: true, Path.Combine(ocupado, "run.jsonl"), (_, _) => { });

        Assert.All(canciones, c => Assert.Equal(0u, BpmDe(c.Ruta)));
        Assert.Equal((2, 2, 0), (r.Entradas, r.Coincidencias, r.Actualizadas));
        Assert.StartsWith("No se ha tocado ningún archivo: no se pudo crear el historial para deshacer", r.Detenido);
        Assert.Contains("⚠ No se ha tocado ningún archivo",
                        LibraryViewModel.TextoImportacionRekordbox(r.Entradas, r.Coincidencias, r.Actualizadas, r.Fallos.Count, r.Detenido));
    }

    // Una importación sin nada que cambiar no deja un manifiesto vacío en el historial.
    [Fact]
    public void Una_importacion_sin_cambios_no_deja_manifiesto()
    {
        var (xml, canciones) = ColeccionDe(1);
        var paths = new AppPaths(Path.GetDirectoryName(Ruta("datos", "x"))!);
        LibraryViewModel.ImportarConHistorial(xml, canciones, overwrite: true, paths.NuevoManifiesto(), (_, _) => { });
        var segundo = paths.NuevoManifiesto();

        var r = LibraryViewModel.ImportarConHistorial(xml, canciones, overwrite: false, segundo, (_, _) => { });

        Assert.Equal(0, r.Actualizadas);
        Assert.False(File.Exists(segundo));
    }

    // ---------------------------------------------------------------------------------------------
    // menor-volumen_copias-1: las exclusiones de Volumen siguen al archivo
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Una_cancion_excluida_del_ajuste_de_volumen_sigue_excluida_al_renombrarla()
    {
        var motor = Motor(new Logger());
        var acapella = Ruta("Musica", "acapella.mp3");
        var renombrada = Ruta("Musica", "Artista - Acapella (Intro).mp3");
        motor.Config.VolumenCancionesExcluidas = new List<string> { Ruta("Musica", "otra.mp3"), acapella };

        motor.SeguirArchivo(acapella, renombrada, default);

        Assert.Equal(new[] { Ruta("Musica", "otra.mp3"), renombrada }, motor.Config.VolumenCancionesExcluidas);
        // Queda guardado: al volver a abrir BeatTag sigue excluida.
        Assert.Contains(renombrada, AppConfig.Load(motor.Paths, out _).VolumenCancionesExcluidas);

        // Lo que no está excluido no toca la configuración.
        var guardada = File.GetLastWriteTimeUtc(motor.Paths.ConfigPath);
        motor.SeguirArchivo(Ruta("Musica", "x.mp3"), Ruta("Musica", "y.mp3"), default);
        Assert.Equal(guardada, File.GetLastWriteTimeUtc(motor.Paths.ConfigPath));
    }

    [Fact]
    public void Cambiar_una_ruta_excluida_respeta_mayusculas_y_duplicados()
    {
        var lista = new List<string> { @"D:\M\a.mp3", @"D:\M\b.mp3" };
        Assert.True(AppEngine.CambiarRutaExcluida(lista, @"D:\M\A.MP3", @"D:\M\A.mp3"));   // solo cambian las mayúsculas
        Assert.Equal(new[] { @"D:\M\A.mp3", @"D:\M\b.mp3" }, lista);
        Assert.True(AppEngine.CambiarRutaExcluida(lista, @"D:\M\A.mp3", @"D:\M\B.mp3"));   // el destino ya estaba
        Assert.Equal(new[] { @"D:\M\b.mp3" }, lista);
        Assert.False(AppEngine.CambiarRutaExcluida(lista, @"D:\M\z.mp3", @"D:\M\w.mp3"));
        Assert.False(AppEngine.CambiarRutaExcluida(lista, @"D:\M\b.mp3", @"D:\M\b.mp3"));
    }

    // Revisión V3: elegir «Nivel de club» lanzaba sin avisar la medición del pico real de las que se
    // aumentarían (en una biblioteca real, unas 1.500 decodificaciones con todos los núcleos menos uno)
    // y Analizar y Ajustar quedaban desactivados. Ahora no se mide nada: se dice cuántas faltan.
    [Fact]
    public void Cambiar_el_nivel_de_referencia_no_mide_nada_y_dice_cuantas_faltan()
    {
        var vm = new LoudnessViewModel(Motor(new Logger()));
        vm.Rows.Add(new LoudnessRow { FilePath = Ruta("Musica", "baja.mp3"), Lufs = -16, PeakDb = -8, Target = -14 });
        vm.Rows.Add(new LoudnessRow { FilePath = Ruta("Musica", "alta.mp3"), Lufs = -6, PeakDb = 0, Target = -14 });

        vm.Modo = "Nivel de club";

        Assert.False(vm.IsBusy);
        Assert.Equal(-11, vm.Target);
        Assert.Equal(AjusteVolumenSeleccion.TextoPicoRealPendiente(1), vm.Status);
    }

    // Revisión V3: excluir lanzaba esa misma medición, que tapaba la confirmación de la exclusión (y el
    // aviso de que no se pudo guardar). Ahora la confirmación queda, con lo que falta detrás.
    [Fact]
    public void Al_excluir_la_confirmacion_no_la_tapa_ninguna_medicion()
    {
        var biblioteca = new LibraryStore(new AppConfig(), () => { }, new ScanCache(Ruta("datos", "scan.json")));
        var vm = new LoudnessViewModel(Motor(new Logger(), biblioteca));
        vm.Modo = "Nivel de club";
        var baja = new LoudnessRow { FilePath = Ruta("Musica", "baja.mp3"), Lufs = -16, PeakDb = -8, Target = -11 };
        var otra = new LoudnessRow { FilePath = Ruta("Musica", "otra.mp3"), Lufs = -15, PeakDb = -9, Target = -11 };
        vm.Rows.Add(baja);
        vm.Rows.Add(otra);

        vm.ExcluirCanciones(new[] { otra });

        Assert.False(vm.IsBusy);
        Assert.Equal(AjusteVolumenSeleccion.TextoCancionesExcluidas(1, 1) + " " + AjusteVolumenSeleccion.TextoPicoRealPendiente(1), vm.Status);
    }

    // ---------------------------------------------------------------------------------------------
    // rendimiento-4 y rendimiento-5: la caché del espectro, fuera del hilo que llama y guardada
    // ---------------------------------------------------------------------------------------------

    // El Editor y Volumen llaman a SeguirArchivo desde el hilo de la interfaz: si la caché no estaba
    // cargada, se leía allí (1,5-3 MB). Ahora la carga va a la cola de las huellas, en orden.
    [Fact]
    public void Sin_la_cache_cargada_el_espectro_se_sigue_en_segundo_plano_y_en_orden()
    {
        var motor = Motor(new Logger());
        var a = Ruta("Musica", "a.mp3");
        Mp3Fixture.WriteMinMp3(a);
        MedidasGuardadas(motor.Paths, a);
        var b = Ruta("Musica", "b.mp3");
        var c = Ruta("Musica", "c.mp3");

        using var suelta = new ManualResetEventSlim();
        Privado(motor, "EnColaDeHuellas", (Action)(() => suelta.Wait(TimeSpan.FromSeconds(30))));   // la cola, ocupada
        File.Move(a, b);
        motor.SeguirArchivo(a, b, default);

        Assert.Null(Campo(motor, "_espectro"));   // no se ha leído en este hilo

        // Alguien carga la caché mientras tanto (la página Calidad) y llega otro renombrado: va detrás
        // del primero, no se le adelanta.
        _ = motor.Espectro;
        File.Move(b, c);
        motor.SeguirArchivo(b, c, default);
        suelta.Set();
        motor.GuardarCachesLigeras();

        Assert.Null(motor.Espectro.Get(b));
        Assert.NotNull(motor.Espectro.Get(c));
        Assert.NotNull(MedidaEnDisco(motor.Paths, c));
        Assert.Equal(0, (int)Campo(motor, "_espectroEnCola")!);
    }

    // AplicarGeneros, el renombrado con IA y el volcado de fichas guardan sus cachés con
    // GuardarCachesDeAudio, y Enriquecer con el punto de control: la calidad real se guarda con ellas.
    [Fact]
    public async Task Los_guardados_tras_un_lote_incluyen_la_calidad_real()
    {
        var motor = Motor(new Logger());
        var a = Ruta("Musica", "a.mp3");
        var b = Ruta("Musica", "b.mp3");
        var c = Ruta("Musica", "c.mp3");
        Mp3Fixture.WriteMinMp3(a);
        MedidasGuardadas(motor.Paths, a);
        _ = motor.Espectro;   // ya cargada: se sigue al momento

        File.Move(a, b);
        motor.SeguirArchivo(a, b, default);
        Privado(motor, "GuardarCachesDeAudio");
        Assert.NotNull(MedidaEnDisco(motor.Paths, b));

        File.Move(b, c);
        motor.SeguirArchivo(b, c, default);
        typeof(AppEngine).GetField("_desdeElUltimoPunto", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(motor, 10_000);
        Assert.True(await motor.PuntoDeControlAsync());
        Assert.NotNull(MedidaEnDisco(motor.Paths, c));
    }

    // ---------------------------------------------------------------------------------------------
    // interfaz-1: la página rekordbox no ofrece editar lo que no está en la biblioteca
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void En_rekordbox_lo_que_no_esta_en_la_biblioteca_no_se_ofrece_para_editar()
    {
        // «Sin preparar» con «Solo canciones de la biblioteca» desmarcada: una pista de otra carpeta.
        var fuera = new FilaRekordbox(@"D:\rekordbox\Sampler\loop.wav", "", "loop", 4, null, enBiblioteca: false);
        var dentro = new FilaRekordbox(@"E:\Musica\a.mp3", "A", "a", 200, null, enBiblioteca: true);
        Assert.Equal("", fuera.RutaEnBiblioteca);
        Assert.Equal(@"D:\rekordbox\Sampler\loop.wav", fuera.RutaArchivo);   // escuchar y abrir la carpeta, sí
        Assert.Equal(@"E:\Musica\a.mp3", dentro.RutaEnBiblioteca);
        Assert.False(Etiquetador.App.Acciones.AccionesCancion.PuedeEditar(
            Etiquetador.App.Acciones.AccionesCancion.RutaDe(fuera.RutaEnBiblioteca), ocupada: false));

        // «Rutas rotas»: el destino del historial puede existir fuera de la biblioteca. Si está en ella,
        // se da tal como la biblioteca lo tiene (con los acentos en su forma), que es lo que busca el Editor.
        var biblioteca = RekordboxViewModel.RutasDeLaBiblioteca(new[] { "E:\\Musica\\Canci\u00F3n.mp3" });
        var pista = new PistaRekordbox { Ruta = @"E:\Viejo\x.mp3", Nombre = "x" };
        var enBiblioteca = RekordboxViewModel.FilaDeRota(new PistaRota(pista, ComoReparar.RenombradaPorBeatTag, "E:\\Musica\\Cancio\u0301n.mp3"), biblioteca);
        var fueraDeElla = RekordboxViewModel.FilaDeRota(new PistaRota(pista, ComoReparar.RenombradaPorBeatTag, @"F:\Otra\x.mp3"), biblioteca);
        Assert.Equal("E:\\Musica\\Canci\u00F3n.mp3", enBiblioteca.RutaEnBiblioteca);
        Assert.Equal("", fueraDeElla.RutaEnBiblioteca);
        Assert.Equal(@"F:\Otra\x.mp3", fueraDeElla.RutaArchivo);
    }

    // ---------------------------------------------------------------------------------------------
    // interfaz-4, -5 y -6: los avisos remiten a la página rekordbox, y Ajustes lo explica
    // ---------------------------------------------------------------------------------------------

    private static string Fuentes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Etiquetador.App", "Views", "MainWindow.axaml"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Etiquetador.App");
    }

    // La reparación se trasladó de Ajustes a la página rekordbox. Los avisos de Enriquecer, el
    // historial y el Asistente seguían diciendo «Ajustes → Reparar colección de rekordbox». La Ayuda la
    // pone al día su propio responsable.
    [Fact]
    public void Ningun_aviso_manda_a_reparar_la_coleccion_en_Ajustes()
    {
        var restos = Directory.EnumerateFiles(Fuentes(), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && Path.GetFileName(f) != "HelpView.axaml")
            .Where(f =>
            {
                var t = File.ReadAllText(f);
                return t.Contains("Ajustes → Reparar colección de rekordbox", StringComparison.Ordinal)
                       || t.Contains("colección en Ajustes", StringComparison.Ordinal)
                       || t.Contains("«Reparar colección de rekordbox», en Ajustes", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName).ToList();
        Assert.Empty(restos);
        Assert.EndsWith("en la página rekordbox (pestaña «Rutas rotas»).", AsistenteViewModel.TrasRenombrar);
        Assert.Contains(RekordboxViewModel.DondeSeRepara, AsistenteViewModel.NotaRenombrar);
    }

    [Fact]
    public void La_banda_de_renombrados_de_Enriquecer_concuerda_en_numero()
    {
        Assert.StartsWith("Se va a renombrar 1 archivo. Si usas rekordbox", EnrichViewModel.TextoRenombrados(1));
        Assert.StartsWith("Se van a renombrar 3 archivos. Si usas rekordbox", EnrichViewModel.TextoRenombrados(3));
        Assert.EndsWith("Puedes devolvérselos después en la página rekordbox (pestaña «Rutas rotas»).", EnrichViewModel.TextoRenombrados(3));
    }

    [Fact]
    public void Ajustes_dice_cuantos_renombrados_anota_BeatTag_sin_prometer_que_se_reparen()
    {
        Assert.Equal("1 renombrado anotado por BeatTag", SettingsViewModel.TextoRenombrados(1));
        Assert.Equal("0 renombrados anotados por BeatTag", SettingsViewModel.TextoRenombrados(0));
        Assert.Matches(@"^7.?412 renombrados anotados por BeatTag$", SettingsViewModel.TextoRenombrados(7412));
    }

    // Sin biblioteca preparada la página rekordbox no está disponible: el botón de Ajustes saltaba a
    // ella, el salto se deshacía y se acababa en Biblioteca sin saber por qué.
    [Fact]
    public void Reparar_desde_Ajustes_sale_desactivado_y_dice_por_que_cuando_la_pagina_no_esta_disponible()
    {
        var ajustes = (SettingsViewModel)RuntimeHelpers.GetUninitializedObject(typeof(SettingsViewModel));

        ajustes.PonerDisponibilidadRekordbox(disponible: false, hayProceso: false);
        Assert.False(ajustes.AbrirRekordboxCommand.CanExecute(null));
        Assert.Contains("cuando la biblioteca esté preparada", ajustes.AyudaAbrirRekordbox);

        ajustes.PonerDisponibilidadRekordbox(disponible: false, hayProceso: true);
        Assert.Contains("cuando termine el proceso en marcha", ajustes.AyudaAbrirRekordbox);

        ajustes.PonerDisponibilidadRekordbox(disponible: true, hayProceso: false);
        Assert.True(ajustes.AbrirRekordboxCommand.CanExecute(null));
        Assert.StartsWith("Abre la página rekordbox", ajustes.AyudaAbrirRekordbox);

        var vista = File.ReadAllText(Path.Combine(Fuentes(), "Views", "SettingsView.axaml"));
        Assert.Matches(@"<Button Content=""Reparar colección de rekordbox""[^>]*ToolTip\.Tip=""\{Binding AyudaAbrirRekordbox\}"" ToolTip\.ShowOnDisabled=""True""", vista);
    }

    // rendimiento-7: mientras se prepara otra restauración (se borra y se reescribe la misma carpeta),
    // cancelar no puede borrarla a medias.
    [Fact]
    public async Task Cancelar_la_restauracion_espera_a_que_termine_de_prepararse_otra()
    {
        var ajustes = (SettingsViewModel)RuntimeHelpers.GetUninitializedObject(typeof(SettingsViewModel));
        ajustes.CopiaOcupada = true;

        // Sin motor: si no esperase, fallaría al buscar la carpeta de datos.
        await ajustes.CancelarRestauracionCommand.ExecuteAsync(null);

        Assert.True(ajustes.CopiaOcupada);
        var vista = File.ReadAllText(Path.Combine(Fuentes(), "Views", "SettingsView.axaml"));
        Assert.Matches(@"<Button Content=""Cancelar la restauración""[^>]*IsEnabled=""\{Binding !CopiaOcupada\}""", vista);
    }

    // ---------------------------------------------------------------------------------------------
    // interfaz-7: copiar a una carpeta (Tendencias, Playlists y Novedades) se cancela y no deja archivos cortados
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Copiar_a_una_carpeta_se_detiene_entre_archivos_y_no_deja_archivos_cortados()
    {
        var a = Ruta("Musica", "a.mp3");
        var b = Ruta("Musica", "b.mp3");
        File.WriteAllText(a, "audio a");
        File.WriteAllText(b, "audio b");
        var usb = Path.Combine(_dir, "USB");
        var archivos = new[] { (a, "01 - a.mp3"), (b, "02 - b.mp3") };

        // «Cancelar» (o cerrar la ventana) durante la primera: la segunda ya no se copia.
        using (var cts = new CancellationTokenSource())
        {
            var r = ListaFrenteBibliotecaViewModel.CopiarNumeradas(archivos, usb, cts.Token, (o, d) => { File.Copy(o, d, true); cts.Cancel(); });
            Assert.Equal(new ListaFrenteBibliotecaViewModel.ResultadoCopia(1, 0, 0, true), r);
        }
        Assert.Equal("audio a", File.ReadAllText(Path.Combine(usb, "01 - a.mp3")));
        Assert.False(File.Exists(Path.Combine(usb, "02 - b.mp3")));

        // Un fallo a mitad de una copia no deja nada con el nombre definitivo ni el parcial.
        var cortada = ListaFrenteBibliotecaViewModel.CopiarNumeradas(archivos, usb, CancellationToken.None,
            (o, d) => { File.WriteAllText(d, "aud"); throw new IOException("el USB se ha desconectado"); });
        Assert.Equal(new ListaFrenteBibliotecaViewModel.ResultadoCopia(0, 1, 1, false), cortada);
        Assert.Empty(Directory.GetFiles(usb, "02*"));

        // Lo que dejó un cierre a la fuerza es el parcial, no el archivo: la copia siguiente lo repone.
        var parcial = Path.Combine(usb, "02 - b.mp3" + ListaFrenteBibliotecaViewModel.SufijoParcial);
        File.WriteAllText(parcial, "aud");
        var otra = ListaFrenteBibliotecaViewModel.CopiarNumeradas(archivos, usb, CancellationToken.None);
        Assert.Equal(new ListaFrenteBibliotecaViewModel.ResultadoCopia(1, 1, 0, false), otra);
        Assert.Equal("audio b", File.ReadAllText(Path.Combine(usb, "02 - b.mp3")));
        Assert.False(File.Exists(parcial));
    }

    // ---------------------------------------------------------------------------------------------
    // rendimiento-3: corregir un artista de Novedades no se solapa con una búsqueda
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Mientras_se_comprueba_una_correccion_de_Novedades_no_se_busca_ni_se_toca_otro_artista()
    {
        var motor = Motor(new Logger(), new LibraryStore(new AppConfig(), () => { }, new ScanCache(Ruta("scan.json"))));
        var vm = new NovedadesViewModel(motor);
        var respuesta = new TaskCompletionSource<(ArtistaDeezer? Artista, bool FalloDeRed)>();
        vm.ConsultaArtistaDePrueba = _ => respuesta.Task;
        vm.ArtistaElegido = new FilaArtistaNovedades("bad bunny", "Bad Bunny", 12, 0, "", 0, null, 0, -1, false, false);
        vm.Correccion = "https://www.deezer.com/artist/10583405";
        Assert.True(vm.PuedeBuscar);

        var corregir = vm.CorregirArtistaCommand.ExecuteAsync(null);   // Deezer tarda en contestar

        Assert.True(vm.Corrigiendo);
        Assert.False(vm.PuedeBuscar);
        Assert.False(vm.PuedeTocarArtista);
        await vm.ExcluirArtistaCommand.ExecuteAsync(null);
        Assert.Equal("Espera a que termine la comprobación del artista en Deezer.", vm.Status);

        respuesta.SetResult((new ArtistaDeezer(10583405, "Bad Bunny", 1_000_000, 50), false));
        await corregir;

        Assert.False(vm.Corrigiendo);
        Assert.True(vm.PuedeBuscar);
        Assert.StartsWith("«Bad Bunny» queda asociado a «Bad Bunny» de Deezer.", vm.Status);
        var guardado = new AlmacenArtistasDeezer(Path.Combine(motor.Paths.DataDir, AlmacenArtistasDeezer.NombreArchivo)).Buscar("bad bunny");
        Assert.Equal(EstadoArtistaDeezer.Manual, guardado?.Estado);
    }

    // El guardado toma la foto dentro de su turno: con varios guardados a la vez, el último en escribir
    // lleva lo último hecho.
    [Fact]
    public void Los_guardados_a_la_vez_de_los_artistas_de_Novedades_dejan_lo_ultimo()
    {
        var archivo = Ruta("datos", AlmacenArtistasDeezer.NombreArchivo);
        var almacen = new AlmacenArtistasDeezer(archivo);
        Parallel.For(0, 40, i =>
        {
            almacen.Corregir($"artista {i}", $"Artista {i}", new ArtistaDeezer(i + 1, $"Artista {i}", 10, 1), DateTime.Now);
            Assert.Equal("", almacen.Guardar());
        });
        Assert.Equal(40, new AlmacenArtistasDeezer(archivo).Count);
    }

    // ---------------------------------------------------------------------------------------------
    // menor-audio_medido-2 y -4: la página Calidad
    // ---------------------------------------------------------------------------------------------

    private sealed class Pagina : IEstadoPagina, IProgresoPagina
    {
        public string Status => "";
        public bool IsBusy { get; init; }
        public double Progress => 0;
    }

    private sealed class PaginaConTrabajosSinAvance : IEstadoPagina, IProgresoPagina
    {
        public string Status => "";
        public bool IsBusy { get; init; }
        public double Progress => 0;
        public bool ConAvance { get; init; }
    }

    [Fact]
    public void La_barra_del_pie_sale_solo_si_lo_que_se_hace_tiene_avance()
    {
        Assert.True(MainViewModel.HayBarra(new Pagina { IsBusy = true }));   // lo de siempre
        Assert.False(MainViewModel.HayBarra(new Pagina { IsBusy = false }));
        Assert.False(MainViewModel.HayBarra(new PaginaConTrabajosSinAvance { IsBusy = true, ConAvance = false }));
        Assert.True(MainViewModel.HayBarra(new PaginaConTrabajosSinAvance { IsBusy = true, ConAvance = true }));
        Assert.False(MainViewModel.HayBarra(null));
    }

    // «Analizar calidad real» con la biblioteca sin escanear: mientras escanea, la barra sin avance de
    // la página y ninguna en el pie (antes, la de avance parada en 0 y la del pie con el valor viejo).
    [Fact]
    public async Task Calidad_no_ensena_una_barra_de_avance_parada_mientras_escanea()
    {
        var musica = Directory.CreateDirectory(Path.Combine(_dir, "Musica")).FullName;
        var store = new LibraryStore(new AppConfig { Folders = new() { musica } }, () => { }, new ScanCache(Ruta("scan.json")));
        var vm = new QualityViewModel(Motor(new Logger(), store));
        var alEscanear = new List<(bool Analizando, bool SinAvance, bool Pie)>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Status) && vm.Status == "Escaneando biblioteca…")
                alEscanear.Add((vm.AnalizandoEspectro, vm.OcupadaSinAvance, MainViewModel.HayBarra(vm)));
        };
        var conAvance = new List<bool>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.ConAvance)) conAvance.Add(vm.ConAvance); };

        await vm.AnalizarEspectroCommand.ExecuteAsync(null);

        Assert.Equal(new[] { (false, true, false) }, alEscanear);
        Assert.Equal(new[] { true, false }, conAvance);   // la barra de avance, solo mientras analiza
        Assert.StartsWith("Análisis de la calidad real terminado", vm.Status);
    }

    [Fact]
    public void Solo_sospechosos_sin_nada_medido_dice_que_hay_que_analizar()
    {
        Assert.Equal("0 de 15000 · Pulsa «Analizar calidad real» para medir los archivos.",
                     QualityViewModel.TextoFiltro(hayBusqueda: false, soloSospechosos: true, 0, 15000, hayMedidas: false));
        Assert.Equal("12 de 15000", QualityViewModel.TextoFiltro(false, true, 12, 15000, hayMedidas: true));
        Assert.Equal("3 de 15000", QualityViewModel.TextoFiltro(true, false, 3, 15000, hayMedidas: false));
        Assert.Equal("", QualityViewModel.TextoFiltro(false, false, 15000, 15000, hayMedidas: false));
        // Mientras se releen las medidas guardadas no se pide analizar lo que ya está medido.
        Assert.Equal("0 de 15000 · Leyendo las medidas guardadas…",
                     QualityViewModel.TextoFiltro(false, true, 0, 15000, hayMedidas: false, leyendoMedidas: true));
        Assert.Equal("3 de 15000", QualityViewModel.TextoFiltro(true, false, 3, 15000, hayMedidas: false, leyendoMedidas: true));
    }

    // =============================================================================================
    // Segunda revisión de la tanda 4
    // =============================================================================================

    /// <summary>
    /// Retiene un cerrojo desde otro hilo hasta que se suelta: hace de «carga de la caché en marcha»
    /// sin depender de lo que tarde de verdad leer espectro.json.
    /// </summary>
    private sealed class CerrojoRetenido : IDisposable
    {
        private readonly ManualResetEventSlim _tomado = new(), _soltar = new();
        private readonly Thread _hilo;

        public CerrojoRetenido(object cerrojo)
        {
            _hilo = new Thread(() => { lock (cerrojo) { _tomado.Set(); _soltar.Wait(TimeSpan.FromSeconds(30)); } }) { IsBackground = true };
            _hilo.Start();
            Assert.True(_tomado.Wait(TimeSpan.FromSeconds(10)), "no se pudo tomar el cerrojo");
        }

        public void Dispose()
        {
            _soltar.Set();
            _hilo.Join(TimeSpan.FromSeconds(10));
            _tomado.Dispose();
            _soltar.Dispose();
        }
    }

    private static object CerrojoDeCarga(AppEngine motor) => Campo(motor, "_cerrojoCargaEspectro")!;

    private static EspectroScanner? EspectroCargado(AppEngine motor)
        => (EspectroScanner?)typeof(AppEngine).GetProperty("EspectroCargado", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(motor);

    // rendimiento-4: la carga de espectro.json ya no va bajo el cerrojo que toman SeguirEspectro y
    // EspectroCargado. Un seguimiento del Editor o de Volumen (hilo de la interfaz) que llega mientras
    // la página Calidad carga la caché vuelve al momento, y se aplica cuando la carga termina.
    [Fact]
    public async Task Mientras_se_carga_la_cache_del_espectro_seguir_un_archivo_no_espera_a_la_carga()
    {
        var motor = Motor(new Logger());
        var a = Ruta("Musica", "a.mp3");
        Mp3Fixture.WriteMinMp3(a);
        MedidasGuardadas(motor.Paths, a);
        var b = Ruta("Musica", "b.mp3");

        EspectroScanner? deLaPagina = null;
        Task carga;
        using (new CerrojoRetenido(CerrojoDeCarga(motor)))   // la carga, en marcha
        {
            carga = Task.Run(() => deLaPagina = motor.Espectro);   // la página Calidad, esperando a esa carga
            File.Move(a, b);
            var seguir = Task.Run(() => motor.SeguirArchivo(a, b, default));
            Assert.Same(seguir, await Task.WhenAny(seguir, Task.Delay(TimeSpan.FromSeconds(5))));   // no esperó a la carga
            await seguir;
            Assert.Null(EspectroCargado(motor));                   // tampoco espera: aún no hay nada cargado
            Assert.False(carga.IsCompleted);
        }

        Assert.Same(carga, await Task.WhenAny(carga, Task.Delay(TimeSpan.FromSeconds(10))));
        await carga;
        motor.GuardarCachesLigeras();   // espera a la cola, donde quedó el seguimiento
        Assert.Same(deLaPagina, motor.Espectro);   // una sola carga para los dos
        Assert.NotNull(motor.Espectro.Get(b));
        Assert.Null(motor.Espectro.Get(a));
        Assert.NotNull(MedidaEnDisco(motor.Paths, b));
    }

    // menor-audio_medido-4: con «Solo sospechosos» marcada, mientras se releen las medidas guardadas el
    // pie lo dice, en lugar de pedir que se analice lo que ya está medido.
    [Fact]
    public async Task Con_solo_sospechosos_mientras_se_releen_las_medidas_el_pie_no_pide_analizar()
    {
        var musica = Directory.CreateDirectory(Path.Combine(_dir, "Musica")).FullName;
        var a = Path.Combine(musica, "a.mp3");
        Mp3Fixture.WriteMinMp3(a);
        var store = new LibraryStore(new AppConfig { Folders = new() { musica } }, () => { }, new ScanCache(Ruta("scan.json")));
        var motor = Motor(new Logger(), store);
        await store.ScanAsync();
        MedidasGuardadas(motor.Paths, store.Tracks.Single().FilePath);
        var vm = new QualityViewModel(motor) { SoloSospechosos = true };
        var rehacer = typeof(QualityViewModel).GetMethod("Recompute", BindingFlags.Instance | BindingFlags.NonPublic)!;

        using (new CerrojoRetenido(CerrojoDeCarga(motor)))   // la lectura de las medidas tarda
        {
            rehacer.Invoke(vm, null);
            Assert.Equal("0 de 1 · Leyendo las medidas guardadas…", vm.FiltroInfo);
        }

        var reloj = System.Diagnostics.Stopwatch.StartNew();
        while (vm.FiltroInfo != "0 de 1" && reloj.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        Assert.Equal("0 de 1", vm.FiltroInfo);   // medida y coherente: ningún sospechoso, sin pedir que se analice
        Assert.NotEqual("", vm.Rows.Single().CorteMedido);
    }

    // interfaz-7: «Cancelar» de Novedades también detiene la copia a una carpeta. Iba con Buscando, que
    // es false mientras se copia: el botón salía desactivado y la copia bloqueaba la aplicación entera.
    [Fact]
    public void En_Novedades_se_puede_cancelar_la_copia_a_una_carpeta()
    {
        var motor = Motor(new Logger(), new LibraryStore(new AppConfig(), () => { }, new ScanCache(Ruta("scan.json"))));
        var vm = new NovedadesViewModel(motor);
        var avisos = new List<string?>();
        vm.PropertyChanged += (_, e) => avisos.Add(e.PropertyName);
        Assert.False(vm.PuedeCancelar);

        vm.IsBusy = true;   // copiando a una carpeta (Buscando sigue en false)
        Assert.False(vm.Buscando);
        Assert.True(vm.PuedeCancelar);
        Assert.Contains(nameof(vm.PuedeCancelar), avisos);

        vm.IsBusy = false;
        vm.Buscando = true;
        Assert.True(vm.PuedeCancelar);

        var vista = File.ReadAllText(Path.Combine(Fuentes(), "Views", "NovedadesView.axaml"));
        Assert.Matches(@"<Button Content=""Cancelar"" Command=""\{Binding CancelCommand\}"" IsEnabled=""\{Binding PuedeCancelar\}""", vista);
    }

    // interfaz-7: el estado de la copia sigue en minúscula detrás de «Copia cancelada:» y concuerda en número.
    [Fact]
    public void El_estado_de_la_copia_a_una_carpeta_se_escribe_bien()
    {
        Assert.Equal("Copia cancelada: copiadas 3 en «USB» · 2 ya estaban. La biblioteca original no se modifica.",
                     ListaFrenteBibliotecaViewModel.TextoCopia(new(3, 2, 0, true), "USB"));
        Assert.Equal("Copiadas 12 en «USB» · 1 ya estaba · 2 con error. La biblioteca original no se modifica.",
                     ListaFrenteBibliotecaViewModel.TextoCopia(new(12, 1, 2, false), "USB"));
        Assert.Equal("Copiada 1 en «USB». La biblioteca original no se modifica.",
                     ListaFrenteBibliotecaViewModel.TextoCopia(new(1, 0, 0, false), "USB"));
        Assert.Equal("Copia cancelada: copiada 1 en «USB». La biblioteca original no se modifica.",
                     ListaFrenteBibliotecaViewModel.TextoCopia(new(1, 0, 0, true), "USB"));
    }
}
