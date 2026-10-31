using System.Diagnostics;
using System.Text.Json;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// Hallazgos A3, T11 y A7: la caché de huellas de Duplicados.
///
/// Visto en los registros reales: «Huellas de audio: 15241 leídas de la caché en 1341 / 1387 /
/// 1393 / 1775 / 1890 / 3453 ms», también en sesiones que no tocaron las huellas. Abrir Duplicados
/// leía en el hilo de la interfaz los ~160 MB de huellas.json (pasando por un texto de ~320 MB),
/// porque el enlace del resumen de huellas se evalúa aunque su recuadro esté oculto. Y la lectura
/// perezosa se daba por hecha ANTES de leer, así que una consulta a la vez veía la caché vacía.
///
/// A7: cancelar «Analizar audio» dejaba cada fpcalc en marcha esperando su minuto de plazo.
/// </summary>
public class HuellasDuplicadosTests
{
    // ---------- Ayudantes ----------

    /// <summary>Escribe una caché con el MISMO formato que usaban las versiones anteriores.</summary>
    private static void EscribirCache(string archivo, IEnumerable<(string Ruta, int[] F)> huellas)
    {
        var d = new Dictionary<string, object>();
        foreach (var (ruta, f) in huellas)
        {
            var fi = new FileInfo(ruta);
            d[ruta] = new
            {
                M = fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0,
                S = fi.Exists ? fi.Length : 0,
                D = 215.0,
                F = f,
            };
        }
        File.WriteAllText(archivo, JsonSerializer.Serialize(d));
    }

    /// <summary>
    /// Un «fpcalc» de mentira: un .cmd en Windows y un guion de shell en el resto. Recibe los mismos
    /// argumentos que el de verdad y no los mira.
    /// </summary>
    private static string ProgramaFalso(string dir, string nombre, string cuerpoCmd, string cuerpoSh)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(dir, nombre + ".cmd");
            File.WriteAllText(cmd, "@echo off\r\n" + cuerpoCmd + "\r\n");
            return cmd;
        }
        var sh = Path.Combine(dir, nombre);
        File.WriteAllText(sh, "#!/bin/sh\n" + cuerpoSh + "\n");
#pragma warning disable CA1416   // solo se llega aquí fuera de Windows
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        return sh;
    }

    private static string Cancion(string dir, string nombre)
    {
        var ruta = Path.Combine(dir, nombre);
        Mp3Fixture.WriteMinMp3(ruta, frames: 10);
        return ruta;
    }

    // ---------- Carga perezosa, fuera del hilo de la interfaz y segura entre hilos ----------

    // Montar el escáner y preguntar si fpcalc existe no puede leer los 160 MB: es lo que hace la
    // aplicación al arrancar y al abrir Duplicados con el criterio por nombre.
    [Fact]
    public void Crear_el_escaner_no_lee_la_cache()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var cache = Path.Combine(dir, "huellas.json");
            EscribirCache(cache, new[] { (Path.Combine(dir, "a.mp3"), new[] { 1, 2, 3 }) });

            var fp = new FingerprintScanner(cache, Path.Combine(dir, "fpcalc.exe"));
            _ = fp.FpcalcDisponible;

            Assert.False(fp.EstaCargada);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Antes la carga se marcaba como hecha ANTES de leer: la segunda consulta simultánea encontraba
    // la caché vacía y daba por «sin calcular» canciones que sí lo estaban. Ahora todas esperan a la
    // misma lectura.
    [Fact]
    public async Task Varias_consultas_a_la_vez_esperan_a_la_misma_lectura()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var cancion = Cancion(dir, "Quevedo - Columbia.mp3");
            var cache = Path.Combine(dir, "huellas.json");

            // Relleno para que la lectura dure algo: con una caché de juguete acabaría antes de que
            // llegara la segunda consulta y la prueba no demostraría nada.
            var relleno = Enumerable.Range(0, 4000)
                .Select(i => (Path.Combine(dir, $"falta_{i}.mp3"), Enumerable.Range(i, 200).ToArray()));
            EscribirCache(cache, relleno.Append((cancion, new[] { 7, 8, 9 })));

            var fp = new FingerprintScanner(cache, Path.Combine(dir, "fpcalc.exe"));
            const int hilos = 8;
            using var salida = new Barrier(hilos);
            var resultados = new int[]?[hilos];
            var tareas = Enumerable.Range(0, hilos).Select(i => Task.Factory.StartNew(() =>
            {
                salida.SignalAndWait();
                resultados[i] = fp.Get(cancion);
            }, TaskCreationOptions.LongRunning)).ToArray();
            await Task.WhenAll(tareas);

            Assert.All(resultados, r => Assert.Equal(new[] { 7, 8, 9 }, r));
            Assert.True(fp.EstaCargada);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // La lectura por flujo entiende las cachés que ya hay en disco: no se pierde ninguna huella al
    // actualizar.
    [Fact]
    public async Task La_cache_de_versiones_anteriores_se_lee_igual()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var a = Cancion(dir, "Bad Bunny - Titi Me Pregunto.mp3");
            var b = Cancion(dir, "Karol G - Provenza.mp3");
            var cache = Path.Combine(dir, "huellas.json");
            EscribirCache(cache, new[] { (a, new[] { 1, -2, int.MaxValue }) });

            var fp = new FingerprintScanner(cache, Path.Combine(dir, "fpcalc.exe"));
            await fp.CargarAsync();

            Assert.Equal(new[] { 1, -2, int.MaxValue }, fp.Get(a));
            Assert.Null(fp.Get(b));
            Assert.Equal(1, fp.Contar(new[] { a, b }));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Una caché truncada (un corte a media escritura) se aparta con otro nombre en vez de tratarse
    // como vacía y pisarse en el siguiente guardado: son horas de cálculo.
    [Fact]
    public async Task Una_cache_danada_se_aparta_en_vez_de_pisarla()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var cache = Path.Combine(dir, "huellas.json");
            File.WriteAllText(cache, "{\"C:\\\\m\\\\a.mp3\":{\"M\":1,\"S\":2,\"F\":[1,2,");

            var fp = new FingerprintScanner(cache, Path.Combine(dir, "fpcalc.exe"));
            await fp.CargarAsync();

            Assert.True(fp.EstaCargada);
            Assert.False(File.Exists(cache));
            Assert.Single(Directory.GetFiles(dir, "huellas.json.ilegible_*"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- Una caché que no se puede abrir (revisión de A3) ----------
    //
    // Un bloqueo pasajero (un antivirus, una copia de seguridad con el archivo abierto) dejaba la
    // caché dada por leída y VACÍA para toda la sesión: Duplicados decía «Sin calcular… las 15000
    // canciones» y «Analizar audio» calculaba durante horas para luego negarse a guardar. Un fallo
    // al leer tampoco es un archivo dañado: no se aparta.

    /// <summary>Abre el archivo sin compartirlo, como un programa que lo tiene bloqueado.</summary>
    private static FileStream Bloquear(string archivo)
        => new(archivo, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    [WindowsFact]
    public async Task Una_cache_bloqueada_no_se_da_por_leida_ni_se_aparta_y_se_reintenta()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var a = Cancion(dir, "Quevedo - Columbia.mp3");
            var cache = Path.Combine(dir, "huellas.json");
            EscribirCache(cache, new[] { (a, new[] { 1, 2, 3 }) });
            var fp = new FingerprintScanner(cache, Path.Combine(dir, "fpcalc.exe"));

            using (Bloquear(cache))
            {
                await fp.CargarAsync();
                Assert.False(fp.EstaCargada);
                Assert.StartsWith("No se han podido leer las huellas ya calculadas", fp.AvisoCarga);
            }
            Assert.True(File.Exists(cache));
            Assert.Empty(Directory.GetFiles(dir, "huellas.json.ilegible_*"));

            // Ya libre: la siguiente operación lo vuelve a intentar y lo lee entero.
            await fp.CargarAsync();
            Assert.True(fp.EstaCargada);
            Assert.Equal("", fp.AvisoCarga);
            Assert.Equal(new[] { 1, 2, 3 }, fp.Get(a));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // El reintento lo hace quien empieza una operación, no cada consulta: contar 200 canciones con
    // el archivo aún bloqueado no puede convertirse en 200 intentos y 200 líneas en el registro.
    [WindowsFact]
    public async Task Con_la_cache_bloqueada_contar_no_reintenta_por_cada_cancion()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var cache = Path.Combine(dir, "huellas.json");
            EscribirCache(cache, new[] { (Path.Combine(dir, "a.mp3"), new[] { 1, 2, 3 }) });
            var log = new Logger();
            var errores = 0;
            log.OnLog += e => { if (e.Kind == LogKind.Err) Interlocked.Increment(ref errores); };
            var fp = new FingerprintScanner(cache, Path.Combine(dir, "fpcalc.exe"), log);
            var rutas = Enumerable.Range(0, 200).Select(i => Path.Combine(dir, $"c{i}.mp3")).ToList();

            using (Bloquear(cache))
            {
                await fp.CargarAsync();
                Assert.Equal(0, fp.Contar(rutas));
                Assert.Equal(1, errores);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Lo que importa al usuario: con las huellas sin leer no se calcula nada (horas de trabajo que no
    // se podrían guardar) y el archivo del disco sigue tal cual.
    [WindowsFact]
    public async Task Con_la_cache_bloqueada_no_se_calcula_nada_ni_se_pisa()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var a = Cancion(dir, "Quevedo - Columbia.mp3");
            var b = Cancion(dir, "Karol G - Provenza.mp3");
            var cache = Path.Combine(dir, "huellas.json");
            EscribirCache(cache, new[] { (a, new[] { 1, 2, 3 }) });
            var antes = File.ReadAllBytes(cache);
            var marca = Path.Combine(dir, "fpcalc_llamado.txt");
            const string json = "{\"duration\":200,\"fingerprint\":[9,9,9]}";
            var fpcalc = ProgramaFalso(dir, "fpcalc",
                $"echo si>> \"{marca}\"\r\necho {json}", $"echo si >> '{marca}'; echo '{json}'");
            var fp = new FingerprintScanner(cache, fpcalc);

            string motivo;
            using (Bloquear(cache))
                motivo = await fp.ScanAsync(new[] { a, b }, force: false, progreso: null);

            Assert.Equal(fp.AvisoCarga, motivo);
            Assert.NotEqual("", motivo);
            Assert.False(File.Exists(marca), "se llamó a fpcalc con las huellas guardadas sin leer");
            Assert.Equal(antes, File.ReadAllBytes(cache));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Y Duplicados lo dice en lugar de «Sin calcular. Hay que analizar el audio de las N canciones».
    [WindowsFact]
    public async Task El_resumen_dice_que_no_se_pueden_leer_las_huellas_en_vez_de_sin_calcular()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var a = Cancion(dir, "a.mp3");
            var cache = Path.Combine(dir, "huellas.json");
            EscribirCache(cache, new[] { (a, new[] { 1, 2, 3 }) });
            var fpcalc = Path.Combine(dir, "fpcalc.exe");
            File.WriteAllText(fpcalc, "");   // basta con que exista
            var fp = new FingerprintScanner(cache, fpcalc);

            string texto;
            using (Bloquear(cache))
                texto = await DuplicatesViewModel.CalcularResumenHuellasAsync(fp, new[] { a });

            Assert.DoesNotContain("Sin calcular", texto);
            Assert.StartsWith("No se han podido leer las huellas ya calculadas", texto);

            // Cuando se libera, el siguiente recuento ya es el de verdad.
            Assert.Equal("Las 1 analizadas.", await DuplicatesViewModel.CalcularResumenHuellasAsync(fp, new[] { a }));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- Calcular con fpcalc ----------

    // Lo calculado se guarda (por flujo, de forma atómica) y otra sesión lo vuelve a leer.
    [Fact]
    public async Task Las_huellas_calculadas_se_guardan_y_otra_sesion_las_lee()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var cancion = Cancion(dir, "Quevedo - Columbia.mp3");
            var cache = Path.Combine(dir, "huellas.json");
            const string json = "{\"duration\":215.5,\"fingerprint\":[1,2,3,4294967295]}";
            var fpcalc = ProgramaFalso(dir, "fpcalc", "echo " + json, "echo '" + json + "'");

            var fp = new FingerprintScanner(cache, fpcalc);
            await fp.ScanAsync(new[] { cancion }, force: false, progreso: null);

            Assert.True(File.Exists(cache));
            Assert.False(File.Exists(cache + ".tmp"));

            var otra = new FingerprintScanner(cache, fpcalc);
            Assert.Equal(new[] { 1, 2, 3, -1 }, otra.Get(cancion));   // fpcalc los da sin signo
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Con «force» no se leía la caché antes de calcular, y el guardado del final escribía SOLO lo
    // calculado en esa pasada: se perdían las huellas de todas las demás canciones.
    [Fact]
    public async Task Recalcular_unas_pocas_no_borra_las_huellas_de_las_demas()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var a = Cancion(dir, "a.mp3");
            var b = Cancion(dir, "b.mp3");
            var cache = Path.Combine(dir, "huellas.json");
            EscribirCache(cache, new[] { (b, new[] { 4, 5, 6 }) });
            const string json = "{\"duration\":200,\"fingerprint\":[9,9,9]}";
            var fpcalc = ProgramaFalso(dir, "fpcalc", "echo " + json, "echo '" + json + "'");

            var fp = new FingerprintScanner(cache, fpcalc);
            await fp.ScanAsync(new[] { a }, force: true, progreso: null);

            var otra = new FingerprintScanner(cache, fpcalc);
            Assert.Equal(new[] { 9, 9, 9 }, otra.Get(a));
            Assert.Equal(new[] { 4, 5, 6 }, otra.Get(b));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // A7. Cancelar tiene que parar los fpcalc en marcha EN EL ACTO. Antes se dejaba de vaciar su
    // salida y se esperaba su minuto de plazo con WaitForExit, que no mira el token: la barra seguía
    // ocupada y el registro se llenaba de falsos «no respondió».
    //
    // El fpcalc de mentira no termina por su cuenta (ver ProgramaQueEspera): la pasada solo vuelve si
    // alguien lo para, la cancelación enseguida o el plazo de fpcalc al minuto, y la línea va en la
    // mitad del plazo. Antes eran 1,8 s contra un fpcalc que acababa solo a los 2 s, y en 4 núcleos
    // con la CPU ocupada la pasada llegó a volver a los 1,7 s. MEDIDO: la cancelación pedida para los
    // 400 ms llegó a los 1,3 s, porque su temporizador corre en el pool, y lanzar cada fpcalc costó
    // hasta 0,7 s.
    [Fact]
    public async Task Cancelar_el_analisis_detiene_fpcalc_al_momento()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var fpcalc = new ProgramaQueEspera(dir, "fpcalc");
            var canciones = Enumerable.Range(0, 3).Select(i => Cancion(dir, $"c{i}.mp3")).ToList();
            var fp = new FingerprintScanner(Path.Combine(dir, "huellas.json"), fpcalc.Ruta);

            using var cts = new CancellationTokenSource(400);
            var reloj = Stopwatch.StartNew();
            await fp.ScanAsync(canciones, force: false, progreso: null, cts.Token);
            reloj.Stop();

            Assert.True(reloj.Elapsed < TimeSpan.FromMilliseconds(FingerprintScanner.TiempoLimiteMs / 2),
                $"la pasada tardó {reloj.Elapsed.TotalSeconds:0.0} s en volver tras cancelar: esperó al plazo de fpcalc en vez de pararlo");
            Assert.False(await fpcalc.SiguioVivoAsync(), "algún fpcalc siguió vivo después de cancelar");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- El resumen de Duplicados ----------

    [Fact]
    public void El_resumen_de_huellas_dice_cuantas_faltan()
    {
        Assert.Equal("No está disponible el analizador de audio (fpcalc).",
                     DuplicatesViewModel.TextoResumenHuellas(false, 15241, 0));
        Assert.Equal("", DuplicatesViewModel.TextoResumenHuellas(true, 0, 0));
        Assert.StartsWith("Sin calcular.", DuplicatesViewModel.TextoResumenHuellas(true, 15241, 0));
        Assert.Equal("15000 de 15241 analizadas. Faltan 241.",
                     DuplicatesViewModel.TextoResumenHuellas(true, 15241, 15000));
        Assert.Equal("Las 15241 analizadas.", DuplicatesViewModel.TextoResumenHuellas(true, 15241, 15241));
    }

    // El recuento se hace en segundo plano y la propia lectura de la caché va dentro: la vista ya no
    // la provoca al crearse.
    [Fact]
    public async Task El_resumen_se_calcula_en_segundo_plano_leyendo_la_cache()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var a = Cancion(dir, "a.mp3");
            var b = Cancion(dir, "b.mp3");
            var cache = Path.Combine(dir, "huellas.json");
            EscribirCache(cache, new[] { (a, new[] { 1, 2, 3 }) });
            var fpcalc = Path.Combine(dir, "fpcalc.exe");
            File.WriteAllText(fpcalc, "");   // basta con que exista

            var fp = new FingerprintScanner(cache, fpcalc);
            var texto = await DuplicatesViewModel.CalcularResumenHuellasAsync(fp, new[] { a, b });

            Assert.Equal("1 de 2 analizadas. Faltan 1.", texto);
            Assert.True(fp.EstaCargada);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
