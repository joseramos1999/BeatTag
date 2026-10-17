using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// Hallazgos A8 y T6: fpcalc sin límite de tiempo y su descarga sin protección ante llamadas en
/// paralelo.
/// </summary>
public class FingerprintFpcalcTests
{
    // ---------- La descarga, de una en una ----------

    /// <summary>
    /// Servidor de mentira: cuenta cuántas descargas hay en curso a la vez y entrega unos bytes que
    /// no son el zip oficial (así el SHA-256 no cuadra y nunca se ejecuta nada).
    /// </summary>
    private sealed class DescargaFalsa : HttpMessageHandler
    {
        private int _enCurso;
        public int Maximo;
        public int Peticiones;
        public Action? AlEmpezar;
        public readonly ConcurrentBag<string> Servidores = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Servidores.Add(request.RequestUri?.Host ?? "");
            if (Interlocked.Increment(ref Peticiones) == 1) AlEmpezar?.Invoke();
            var n = Interlocked.Increment(ref _enCurso);
            lock (this) Maximo = Math.Max(Maximo, n);
            try { await Task.Delay(150, ct); }
            finally { Interlocked.Decrement(ref _enCurso); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
        }
    }

    // «Comprobar audio» identifica 4 canciones a la vez. La primera vez en un equipo sin fpcalc, las
    // 4 se ponían a descargar al mismo fpcalc.zip y a descomprimir en la misma carpeta; las que
    // perdían la carrera fallaban y, con el motor de pago activo, esas canciones se pagaban en AudD.
    [Fact]
    public async Task Cuatro_canciones_a_la_vez_no_descargan_fpcalc_a_la_vez()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            var fp = new Fingerprint(paths);
            var servidor = new DescargaFalsa();
            using var http = new HttpClient(servidor);

            var r = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => fp.EnsureFpcalcAsync(http))));

            Assert.All(r, x => Assert.Null(x));         // la descarga de mentira no vale: ninguna lo consigue
            Assert.Equal(1, servidor.Maximo);    // pero nunca hubo dos descargas a la vez
            // Y no queda ningún resto (comprimidos ni carpetas temporales) en la carpeta lib.
            Assert.Empty(Directory.GetFileSystemEntries(paths.LibDir));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Las que esperan turno miran otra vez al entrar: si la primera ya lo dejó listo, no se descarga
    // de nuevo.
    [Fact]
    public async Task Si_otra_llamada_lo_deja_listo_las_demas_no_lo_descargan_otra_vez()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            var fp = new Fingerprint(paths);
            // Mientras dura la primera descarga, fpcalc aparece en su sitio (como si otra parte de la
            // aplicación lo hubiera terminado de instalar).
            var servidor = new DescargaFalsa
            {
                AlEmpezar = () => { Directory.CreateDirectory(paths.LibDir); File.WriteAllText(fp.FpcalcPath, "fpcalc"); },
            };
            using var http = new HttpClient(servidor);

            var r = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => fp.EnsureFpcalcAsync(http))));

            Assert.Equal(1, servidor.Peticiones);
            Assert.Equal(3, r.Count(x => x == fp.FpcalcPath));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // El caso que costaba dinero: primera vez en un equipo sin fpcalc, sin poder descargarlo, y con
    // AudD activo. Antes cada canción contaba como «el gratuito no lo sabe» y se pagaba en AudD.
    // Ahora «Comprobar audio» prepara fpcalc una vez antes de empezar y, si no puede, se detiene
    // diciéndolo, sin gastar ninguna consulta.
    [Fact]
    public async Task Sin_fpcalc_Comprobar_audio_se_detiene_sin_gastar_consultas_de_pago()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            paths.EnsureDirectories();
            var servidor = new DescargaFalsa();   // la descarga de fpcalc no vale (SHA-256 distinto)
            using var http = new HttpClient(servidor);
            var scanner = new IdentificationScanner(Path.Combine(dir, "identificacion.json"), new Fingerprint(paths),
                                                    new AcoustIdProvider(new ApiClient(paths)), new AuddProvider(http), http);
            var cancion = Path.Combine(dir, "Quevedo - Columbia.mp3");
            Mp3Fixture.WriteMinMp3(cancion, frames: 30);

            var r = await scanner.ScanAsync(new[] { cancion },
                new IdentificationScanner.Opciones("clave-acoustid", "clave-audd", UsarPago: true, MaxPago: 10),
                force: true, progreso: null);

            Assert.Equal(0, r.ConsultasDePago);
            Assert.Contains("fpcalc", r.Error);
            Assert.False(r.Cancelada);
            Assert.DoesNotContain(servidor.Servidores, s => s.Contains("audd", StringComparison.OrdinalIgnoreCase));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- fpcalc con plazo ----------

    /// <summary>
    /// Un programa que tarda ~2 s y, si nadie lo para, deja una marca en disco al terminar. Si la
    /// marca no aparece, es que se le paró de verdad (a él y a lo que lanzó).
    /// </summary>
    private static ProcessStartInfo Lento(string marca)
        => OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { Arguments = $"/c ping -n 3 127.0.0.1 >nul & echo listo> \"{marca}\"" }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", $"sleep 2; echo listo > '{marca}'" } };

    // Antes no había límite: un archivo dañado que dejara a fpcalc sin terminar paraba la pasada de
    // Enriquecer en esa canción hasta que el usuario cancelaba.
    [Fact]
    public async Task Un_programa_que_no_termina_se_para_al_vencer_el_plazo()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var marca = Path.Combine(dir, "termino.txt");
            var reloj = Stopwatch.StartNew();

            var r = await Fingerprint.EjecutarConLimiteAsync(Lento(marca), TimeSpan.FromMilliseconds(500));

            Assert.NotNull(r);
            Assert.True(r!.Colgado);
            Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(10));
            await Task.Delay(3500);
            Assert.False(File.Exists(marca), "el programa siguió vivo después de vencer el plazo");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Cancelar también lo para: antes, al cancelar, fpcalc seguía corriendo por su cuenta.
    [Fact]
    public async Task Cancelar_para_el_programa_y_se_comunica_como_cancelacion()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var marca = Path.Combine(dir, "termino.txt");
            using var cts = new CancellationTokenSource(300);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Fingerprint.EjecutarConLimiteAsync(Lento(marca), TimeSpan.FromSeconds(60), cts.Token));

            await Task.Delay(3500);
            Assert.False(File.Exists(marca), "el programa siguió vivo después de cancelar");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Lo normal sigue igual: un programa que termina devuelve su código y lo que escribió.
    [Fact]
    public async Task Un_programa_que_termina_devuelve_lo_que_escribio()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { Arguments = "/c echo hola" }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "echo hola" } };

        var r = await Fingerprint.EjecutarConLimiteAsync(psi, TimeSpan.FromSeconds(30));

        Assert.NotNull(r);
        Assert.False(r!.Colgado);
        Assert.Equal(0, r.Codigo);
        Assert.Equal("hola", r.Salida.Trim());
    }

    // El plazo por defecto es el mismo que usa el análisis de duplicados por huella.
    [Fact]
    public void El_plazo_de_fpcalc_es_de_un_minuto()
    {
        var dir = Mp3Fixture.NewTempDir();
        try { Assert.Equal(TimeSpan.FromSeconds(60), new Fingerprint(new AppPaths(dir)).TiempoLimite); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
