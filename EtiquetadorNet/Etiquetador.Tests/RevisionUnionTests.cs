using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Revisión de la unión del pulido de la tanda 3: lo que la unión eligió (una sola papelera cancelable
/// por página, un solo texto para el envío, el tipo de cada herramienta en el historial) recorrido por
/// el camino de verdad. Antes solo había funciones puras, y una regresión en la conexión (un
/// CancelarPapelera que apunta a un campo que nadie asigna, un envío que deja de ocupar la página)
/// pasaba con todas las pruebas en verde.
/// </summary>
public class RevisionUnionTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Un envío de mentira que se queda parado en el primer archivo hasta que la prueba lo suelta.</summary>
    private sealed class EnvioRetenido
    {
        public readonly SemaphoreSlim Dentro = new(0);
        public readonly ManualResetEventSlim Soltar = new(false);
        public int Llamadas;

        public (bool Ok, string Error) Enviar(string ruta)
        {
            if (Interlocked.Increment(ref Llamadas) == 1)
            {
                Dentro.Release();
                Soltar.Wait(TimeSpan.FromSeconds(20));
            }
            return (true, "");
        }

        public async Task EsperarDentroAsync()
            => Assert.True(await Dentro.WaitAsync(TimeSpan.FromSeconds(20)), "el envío no llegó a empezar");
    }

    // ---------------------------------------------------------------- union-5 · la papelera de la Biblioteca

    // Lo que hace el cierre de la ventana (PaginaNav.Cancelar = Library.CancelarPapelera) a mitad de
    // un envío: se para entre un archivo y el siguiente, lo ya enviado sale de la biblioteca y la
    // página queda libre. Si CancelarPapelera volviera a apuntar a otro campo, cerrar esperaría 15 s.
    [Fact]
    public async Task Cerrar_a_mitad_del_envio_de_la_biblioteca_lo_para_y_quita_lo_enviado()
    {
        var store = new LibraryStore(new AppConfig(), () => { }, new ScanCache(Path.Combine(_dir, "scan.json")));
        var rutas = Enumerable.Range(0, 10).Select(i => Path.Combine(_dir, $"tema {i}.mp3")).ToList();
        foreach (var r in rutas) store.Tracks.Add(new Track { FilePath = r });
        var envio = new EnvioRetenido();

        await UnSoloHilo.Ejecutar(async () =>
        {
            var vm = new LibraryViewModel(store) { EnviarUno = envio.Enviar };
            var tarea = vm.EnviarAPapeleraAsync(rutas);
            await envio.EsperarDentroAsync();

            Assert.True(vm.IsBusy);             // mientras dura, las demás pestañas esperan
            Assert.True(vm.EnviandoPapelera);   // y se enseña «Cancelar el envío»

            vm.CancelarPapelera();
            envio.Soltar.Set();
            await tarea.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.False(vm.IsBusy);
            Assert.False(vm.EnviandoPapelera);
            Assert.Equal(1, envio.Llamadas);                    // el segundo ya no se envía
            Assert.Equal(rutas.Skip(1), store.Tracks.Select(t => t.FilePath));
            Assert.Equal("Cancelado: se ha enviado 1 de los 10 archivos a la papelera; el resto no se ha tocado.", vm.Status);
        });
    }

    // Sin envío en marcha, cancelar no hace nada (ni lanza): el cierre la llama siempre.
    [Fact]
    public void Cancelar_la_papelera_sin_envio_no_hace_nada()
    {
        var store = new LibraryStore(new AppConfig(), () => { }, new ScanCache(Path.Combine(_dir, "scan.json")));
        new LibraryViewModel(store).CancelarPapelera();
    }

    // ---------------------------------------------------------------- union-1 · catalogo-9 · Duplicados ocupada mientras envía

    /// <summary>Tres copias de la misma canción (mismo artista y título), ya escaneadas.</summary>
    private async Task<(LibraryStore Store, AppConfig Config)> TresCopiasAsync()
    {
        var carpeta = Directory.CreateDirectory(Path.Combine(_dir, "Musica")).FullName;
        foreach (var nombre in new[] { "a.mp3", "b.mp3", "c.mp3" })
        {
            var ruta = Path.Combine(carpeta, nombre);
            Mp3Fixture.WriteMinMp3(ruta);
            Mp3Fixture.SetTags(ruta, "Losing It", "Fisher", null);
        }
        var config = new AppConfig { Folders = new() { carpeta } };
        var store = new LibraryStore(config, () => { }, new ScanCache(Path.Combine(_dir, "scan.json")));
        await store.ScanAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(3, store.Tracks.Count);
        return (store, config);
    }

    private DuplicatesViewModel Duplicados(LibraryStore store, AppConfig config, Func<string, (bool, string)>? enviar = null,
                                           ComparacionRetenida? comparacion = null)
    {
        var huellas = new FingerprintScanner(Path.Combine(_dir, "huellas.json"), Path.Combine(_dir, "no-hay-fpcalc.exe"));
        return new DuplicatesViewModel(store, config, () => { }, huellas, new AudioPreview())
        {
            EnviarUno = enviar ?? Papelera.Enviar,
            AgruparPorAudio = comparacion == null ? null : comparacion.Agrupar,
        };
    }

    /// <summary>
    /// Una comparación por audio que, una vez retenida, se queda parada hasta que la prueba la suelta.
    /// Agrupa de verdad, sin huellas (estas canciones no las tienen).
    /// </summary>
    private sealed class ComparacionRetenida
    {
        private readonly ManualResetEventSlim _soltar = new(true);   // abierta: lo de antes de retenerla pasa
        private readonly SemaphoreSlim _dentro = new(0);

        public void Retener() => _soltar.Reset();
        public void Soltar() => _soltar.Set();

        public ResultadoPorAudio Agrupar(List<Track> lista, IProgress<(int Hechas, int Total)> avance, CancellationToken ct)
        {
            if (!_soltar.IsSet)
            {
                _dentro.Release();
                Assert.True(_soltar.Wait(TimeSpan.FromSeconds(20)), "la prueba no soltó la comparación");
            }
            return FingerprintDuplicates.Agrupar(lista, _ => null, incluirEdiciones: true, progreso: avance, ct: ct);
        }

        public async Task EsperarDentroAsync()
            => Assert.True(await _dentro.WaitAsync(TimeSpan.FromSeconds(20)), "la comparación no llegó a empezar");
    }

    // La página está OCUPADA mientras envía (PaginaNav de Duplicados solo mira IsBusy: sin esto, las
    // demás pestañas quedarían libres y cerrar no preguntaría), y «Cancelar» del cierre (CancelarTodo)
    // lo para entre un archivo y el siguiente; lo enviado sale de la tabla y de la biblioteca.
    [Fact]
    public async Task Duplicados_sigue_ocupada_mientras_envia_y_cerrar_lo_para()
    {
        var (store, config) = await TresCopiasAsync();
        var envio = new EnvioRetenido();

        await UnSoloHilo.Ejecutar(async () =>
        {
            var vm = Duplicados(store, config, envio.Enviar);
            Assert.Equal(3, vm.Rows.Count);
            vm.MarkAllButBestCommand.Execute(null);
            Assert.Equal(2, vm.Marcadas);

            var tarea = vm.SendMarkedToRecycleBinAsync();
            await envio.EsperarDentroAsync();
            Assert.True(vm.IsBusy);
            Assert.True(vm.EnviandoAPapelera);
            Assert.True(MainViewModel.CambiaLaOcupacion(nameof(vm.IsBusy)));   // y el shell lo oye

            vm.CancelarTodo();
            envio.Soltar.Set();
            await tarea.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.False(vm.IsBusy);
            Assert.Equal(1, envio.Llamadas);
            Assert.Equal(2, store.Tracks.Count);   // la enviada sale de la biblioteca
            Assert.Equal(2, vm.Rows.Count);        // y de la tabla; la otra marcada sigue
            Assert.Equal(1, vm.Marcadas);
            Assert.Equal("Cancelado: se ha enviado 1 de las 2 copias a la papelera; el resto no se ha tocado.", vm.Status);
        });
    }

    // ---------------------------------------------------------------- catalogo-9 · la propuesta de una copia sustituida

    // El aviso de «Sustituir» (AppEngine.ArchivosSustituidos, que lanza la Bandeja) llega a Enriquecer
    // y retira la propuesta de la copia antigua: «Aplicar marcadas» la escribiría en la descarga. Antes
    // solo se probaba FilasRevertidas, que ya existía, y no esta conexión.
    [Fact]
    public void El_aviso_de_sustituir_retira_la_propuesta_en_Enriquecer()
    {
        Action<IReadOnlyCollection<string>>? aviso = null;
        var vm = new EnrichViewModel(null, new AppConfig(), retirar => aviso += retirar);
        PreviewRow Fila(string ruta)
        {
            var r = new ProcessResult { FilePath = ruta, Old = Path.GetFileName(ruta), New = Path.GetFileName(ruta), Found = true };
            return new PreviewRow { Result = r, Old = r.Old, Apply = true };
        }
        var vieja = Fila(@"E:\Musica\Fisher - Losing It.mp3");
        var otra = Fila(@"E:\Musica\Fisher - Stop It.mp3");
        vm.Rows.Add(vieja);
        vm.Rows.Add(otra);

        Assert.NotNull(aviso);   // la página se ha suscrito
        aviso!(new[] { @"E:\Musica\Fisher - Losing It.mp3", @"E:\Musica\Fisher - Losing It.aiff" });   // lo que avisa la Bandeja

        Assert.Equal(new[] { otra }, vm.Rows);
    }

    // ---------------------------------------------------------------- catalogo-5 · «Analizar» por audio

    // «Analizar» en el modo por audio vuelve enseguida y la comparación de huellas sigue aparte. La
    // base soltaba IsBusy al volver y desbloqueaba las demás pestañas con la comparación en marcha.
    //
    // La comparación se retiene hasta haber mirado la página. Con tres canciones sin huella, la de
    // verdad termina en el acto, y en la CI llegó a terminar antes de la comprobación: la página
    // quedaba libre, lo correcto, y la prueba fallaba.
    [Fact]
    public async Task Analizar_por_audio_no_suelta_la_pagina_con_la_comparacion_en_marcha()
    {
        var (store, config) = await TresCopiasAsync();
        var comparacion = new ComparacionRetenida();

        await UnSoloHilo.Ejecutar(async () =>
        {
            var vm = Duplicados(store, config, comparacion: comparacion);

            // Elegir el criterio por audio ya compara, sin pulsar «Analizar»: también ocupa la página.
            comparacion.Retener();
            try
            {
                vm.SelectedMode = vm.ModeOptions.Single(m => m.Value == DuplicateMode.Fingerprint);
                await comparacion.EsperarDentroAsync();
                Assert.True(vm.IsBusy);
            }
            finally { comparacion.Soltar(); }
            await UnSoloHilo.HastaQue(() => !vm.IsBusy, "la primera comparación termina");

            comparacion.Retener();
            try
            {
                var analizar = vm.AnalyzeCommand.ExecuteAsync(null);
                Assert.True(analizar.IsCompleted);    // «Analizar» ya ha vuelto…
                await comparacion.EsperarDentroAsync();
                Assert.True(vm.IsBusy);               // …y la página sigue ocupada mientras compara
            }
            finally { comparacion.Soltar(); }   // también si falla: que no quede un hilo parado 20 s
            await UnSoloHilo.HastaQue(() => !vm.IsBusy, "la comparación termina y suelta la página");
        });
    }

    // ---------------------------------------------------------------- union-2 · un solo texto para el envío

    private static EnvioPapeleraResultado Res(int enviadas, int fallos = 0, bool cancelado = false, string motivo = "En uso.")
        => new(Enumerable.Range(0, enviadas).Select(i => $"e{i}.mp3").ToList(),
               Enumerable.Range(0, fallos).Select(i => ($"f{i}.mp3", motivo)).ToList(), cancelado);

    // Biblioteca, el Asistente, Duplicados y la Bandeja dicen lo mismo, con su sustantivo. Antes
    // Biblioteca y el Asistente daban «Cancelado: 0 de 10 enviados» y «1 de 10 enviados».
    [Fact]
    public void El_envio_cancelado_concuerda_con_0_con_1_y_con_varias()
    {
        Assert.Equal("Cancelado: no se ha enviado ningún archivo a la papelera.", EnvioPapelera.Resumen(Res(0, cancelado: true), 10, Sustantivo.Archivo));
        Assert.Equal("Cancelado: se ha enviado 1 de los 10 archivos a la papelera; el resto no se ha tocado.",
                     EnvioPapelera.Resumen(Res(1, cancelado: true), 10, Sustantivo.Archivo));
        Assert.Equal("Cancelado: se han enviado 3 de las 10 descargas a la papelera; el resto no se ha tocado.",
                     EnvioPapelera.Resumen(Res(3, cancelado: true), 10, Sustantivo.Descarga));
        Assert.Equal("Cancelado: no se ha enviado ninguna copia a la papelera.", EnvioPapelera.Resumen(Res(0, cancelado: true), 5, Sustantivo.Copia));
        Assert.Equal($"Cancelado: se han enviado {1200:N0} de los {15000:N0} archivos a la papelera; el resto no se ha tocado.",
                     EnvioPapelera.Resumen(Res(1200, cancelado: true), 15000, Sustantivo.Archivo));
    }

    [Fact]
    public void Los_fallos_dicen_el_motivo_comun_una_vez_y_el_nombre_si_es_uno()
    {
        Assert.Equal("No se ha podido enviar f0.mp3 a la papelera: En uso.", EnvioPapelera.Resumen(Res(0, 1), 1, Sustantivo.Copia));
        Assert.Equal("2 archivos enviados a la papelera. No se ha enviado f0.mp3: En uso.", EnvioPapelera.Resumen(Res(2, 1), 3, Sustantivo.Archivo));
        Assert.Equal("2 archivos enviados a la papelera. 4 no se han enviado: En uso.", EnvioPapelera.Resumen(Res(2, 4), 6, Sustantivo.Archivo));
        Assert.Equal("Cancelado: se han enviado 2 de los 9 archivos a la papelera; el resto no se ha tocado. 3 no se han enviado: En uso.",
                     EnvioPapelera.Resumen(Res(2, 3, cancelado: true), 9, Sustantivo.Archivo));
        // Un motivo sin punto final (los de macOS) no deja la frase sin cerrar.
        Assert.Equal("No se ha podido enviar f0.mp3 a la papelera: no se pudo lanzar osascript.",
                     EnvioPapelera.Resumen(Res(0, 1, motivo: "no se pudo lanzar osascript"), 1, Sustantivo.Archivo));
    }

    // ---------------------------------------------------------------- union-5 · el tipo de cada herramienta en el historial

    // Si «Género por carpeta y artista» volviera a anotar Generos, el historial lo llamaría «Géneros»
    // como a «Unificar géneros». Las dos herramientas de reglas pasan su constante a la escritura.
    [Fact]
    public void Cada_herramienta_de_reglas_se_anota_con_su_tipo()
    {
        Assert.Equal(TipoEjecucion.GeneroPorReglas, GeneroPorReglasViewModel.TipoEnElHistorial);
        Assert.Equal("Género por carpeta y artista", HistorialDeshacer.Texto(GeneroPorReglasViewModel.TipoEnElHistorial));
        Assert.Equal(TipoEjecucion.LimpiezaPacks, LimpiarPacksViewModel.TipoEnElHistorial);
        Assert.Equal("Limpieza de publicidad de packs", HistorialDeshacer.Texto(LimpiarPacksViewModel.TipoEnElHistorial));

        // Y AplicarAsync usa la constante, no un tipo escrito a mano.
        foreach (var vista in new[] { "GeneroPorReglasViewModel", "LimpiarPacksViewModel" })
        {
            var codigo = File.ReadAllText(Fuente("Etiquetador.App", "ViewModels", vista + ".cs"));
            Assert.Contains("TipoEnElHistorial));", codigo);
            Assert.DoesNotContain("TipoEjecucion.Generos", codigo);
        }
    }

    /// <summary>Sube desde el binario de pruebas hasta la raíz de la solución (como MenusCancionTests).</summary>
    internal static string Fuente(params string[] tramos)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Etiquetador.App", "Views", "MainWindow.axaml")))
            dir = dir.Parent;
        Assert.True(dir != null, "no se encontró el árbol de fuentes desde " + AppContext.BaseDirectory);
        return Path.Combine(dir!.FullName, Path.Combine(tramos));
    }

    // ---------------------------------------------------------------- catalogo-8 · singular y plural

    [Fact]
    public void La_biblioteca_concuerda_carpetas_y_canciones()
    {
        Assert.Equal("1 carpeta sin escanear. Pulsa Escanear.", LibraryViewModel.TextoEstado(1, 0, 0, false));
        Assert.Equal("3 carpetas sin escanear. Pulsa Escanear.", LibraryViewModel.TextoEstado(3, 0, 0, false));
        Assert.Equal("1 canción · 1 incompleta · 1 carpeta", LibraryViewModel.TextoEstado(1, 1, 1, true));
        Assert.Equal($"{1200:N0} canciones · 0 incompletas · 2 carpetas · hay carpetas por escanear, pulsa Escanear",
                     LibraryViewModel.TextoEstado(2, 1200, 0, false));
        Assert.Equal("Añade una o varias carpetas y pulsa Escanear.", LibraryViewModel.TextoEstado(0, 0, 0, false));
    }

    [Fact]
    public async Task Duplicados_concuerda_grupos_y_carpetas()
    {
        Assert.Equal("Carpetas prioritarias y excluidas  ·  1 carpeta, ninguna configurada", DuplicatesViewModel.TextoCarpetas(1, 0, 0));
        Assert.Equal("Carpetas prioritarias y excluidas  ·  4 carpetas, ninguna configurada", DuplicatesViewModel.TextoCarpetas(4, 0, 0));
        Assert.Equal("Carpetas prioritarias y excluidas  ·  1 prioritaria · 2 excluidas", DuplicatesViewModel.TextoCarpetas(4, 1, 2));

        // El recuento de la búsqueda: «1 grupo», no «1 grupos».
        var (store, config) = await TresCopiasAsync();
        var vm = Duplicados(store, config);
        vm.Busqueda = "a.mp3";
        Assert.Equal("1 grupo", vm.FiltroInfo);
    }

    [Fact]
    public void Pasar_a_la_biblioteca_una_sola_cancion_habla_en_singular()
    {
        Assert.StartsWith("El archivo se mueve conservando su nombre.", BandejaViewModel.NotaPasar(1, 0));
        Assert.StartsWith("Los archivos se mueven conservando su nombre.", BandejaViewModel.NotaPasar(3, 0));
        Assert.StartsWith("⚠ Tiene avisos sin resolver (duplicados, calidad baja o etiquetas incompletas). El archivo se mueve",
                          BandejaViewModel.NotaPasar(1, 1));
        Assert.StartsWith("⚠ 2 de ellas tienen avisos sin resolver", BandejaViewModel.NotaPasar(5, 2));
    }

    // ---------------------------------------------------------------- listas-6 · la caché de descartados ya buscados

    // Un descartado ya buscado que vuelve a la biblioteca sale de la caché, también cuando no hay
    // ninguna pendiente que buscar: dentro de la biblioteca, BeatTag puede renombrarlo.
    [Fact]
    public void Los_descartados_que_vuelven_a_la_biblioteca_salen_de_los_ya_buscados()
    {
        IReadOnlySet<string> buscados = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"E:\Remix.mp3", @"E:\Otro.mp3" };

        var quedan = SeguimientoListas.SinLosQueHanVuelto(buscados, r => r == @"E:\Remix.mp3");

        Assert.Equal(new[] { @"E:\Otro.mp3" }, quedan);
        Assert.Same(buscados, SeguimientoListas.SinLosQueHanVuelto(buscados, _ => false));   // sin cambios, el mismo

        // Y TacharUnaVezAsync lo hace también antes de la salida «Nada que buscar».
        var codigo = File.ReadAllText(Fuente("Etiquetador.App", "Services", "SeguimientoListas.cs"));
        var nada = codigo.IndexOf("Nada que buscar", StringComparison.Ordinal);
        var seguir = codigo.IndexOf("await SeguirDescartadosAsync", StringComparison.Ordinal);
        Assert.InRange(codigo.IndexOf("SinLosQueHanVuelto(_descartadosBuscados", StringComparison.Ordinal), nada, seguir);
    }

    // ---------------------------------------------------------------- asistente_mezcla-8 · textos del Asistente

    [Fact]
    public void Quitar_la_regla_se_lee_bien()
    {
        var vista = File.ReadAllText(Fuente("Etiquetador.App", "Views", "AsistenteView.axaml"));
        Assert.Contains("Quita el género asignado a las carpetas seleccionadas o hace que dejen de estar excluidas.", vista);
        Assert.DoesNotContain("(o que dejen de estar excluidas)", vista);
    }

    // ---------------------------------------------------------------- asistente_mezcla-2 · Cambios fuera de la interfaz

    // La instantánea da lo mismo que las filas, y cada género distinto se resuelve una vez.
    [Fact]
    public void Los_cambios_de_genero_se_calculan_igual_desde_la_instantanea()
    {
        var generos = new[] { "Reggaeton", "Salsa" };
        var filas = new List<(string, string)> { ("a.mp3", "reggaeton"), ("b.mp3", "  "), ("c.mp3", "Salsa"), ("d.mp3", "reggaeton") };

        var cambios = GeneroPorReglasViewModel.CambiosDe(filas, generos);

        Assert.Equal(new[] { "a.mp3", "c.mp3", "d.mp3" }, cambios.Select(c => c.Ruta));
        Assert.Equal(new[] { "Reggaeton", "Salsa", "Reggaeton" }, cambios.Select(c => c.GeneroNuevo));
    }
}
