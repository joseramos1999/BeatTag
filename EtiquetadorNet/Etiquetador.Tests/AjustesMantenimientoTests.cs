using System.Net;
using System.Text;
using System.Text.Json;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// E11 · «Vaciar caché» borraba unas 69.000 respuestas (256 subcarpetas de ~260-280 archivos en la
/// carpeta cache del usuario) sin confirmar y en el hilo de la interfaz. Ahora se cuenta antes, se
/// dice qué se pierde y se borra en segundo plano: aquí, el recuento, el borrado y el texto.
/// </summary>
public class VaciarCacheTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Una carpeta cache como la real, a escala: subcarpetas por prefijo con respuestas.</summary>
    private string CacheDeEjemplo(int carpetas, int porCarpeta)
    {
        var cache = Path.Combine(_dir, "cache");
        for (var c = 0; c < carpetas; c++)
        {
            var sub = Path.Combine(cache, c.ToString("x2"));
            Directory.CreateDirectory(sub);
            for (var i = 0; i < porCarpeta; i++) File.WriteAllText(Path.Combine(sub, $"{c:x2}{i:x6}.json"), "{\"data\":[]}");
        }
        File.WriteAllText(Path.Combine(cache, ".ultima-poda"), "");
        return cache;
    }

    [Fact]
    public void Cuenta_lo_que_hay_antes_de_preguntar()
    {
        var cache = CacheDeEjemplo(4, 260);
        var (n, bytes) = SettingsViewModel.ContarArchivos(cache);
        Assert.Equal(4 * 260 + 1, n);
        Assert.True(bytes > 0);
        Assert.Equal((0, 0L), SettingsViewModel.ContarArchivos(Path.Combine(_dir, "no-existe")));
    }

    [Fact]
    public void Borra_todo_lo_de_dentro_y_dice_por_donde_va()
    {
        var cache = CacheDeEjemplo(4, 260);
        var avances = new List<int>();

        var (borrados, fallidos) = SettingsViewModel.BorrarContenido(cache, new ProgresoInmediato(avances));

        Assert.Equal(1041, borrados);
        Assert.Equal(0, fallidos);
        Assert.True(Directory.Exists(cache));                         // la carpeta se queda
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache));    // vacía
        Assert.Equal(new[] { 500, 1000 }, avances);
    }

    [Fact]
    public void La_confirmacion_dice_que_se_borra_y_que_cuesta_despues()
    {
        var (cuerpo, nota) = SettingsViewModel.TextosVaciarCache(new SettingsViewModel.ContenidoCache(68912, 412L << 20, 13540, true));
        // Los miles se escriben con el separador del sistema (punto, coma o espacio fino).
        var cifras = cuerpo.Replace('\u00A0', '.').Replace('\u202F', '.').Replace(',', '.');

        Assert.Contains("68.912 archivos (412 MB)", cifras);
        Assert.Contains("13.540 canciones", cifras);
        Assert.Contains("Caché de escaneo", cuerpo);
        Assert.Contains("No se tocan tus archivos de música", nota);
        Assert.Contains("el próximo análisis volverá a consultar", nota);
        Assert.True(new SettingsViewModel.ContenidoCache(0, 0, 0, false).Vacia);
    }

    /// <summary>Progress&lt;T&gt; que apunta en el momento (el normal lo manda al contexto de sincronización).</summary>
    private sealed class ProgresoInmediato(List<int> destino) : IProgress<int>
    {
        public void Report(int value) => destino.Add(value);
    }
}

/// <summary>
/// M5 (contador) · «N renombrados anotados» sumaba también los renombrados que se deshicieron.
/// Visto en una biblioteca real: run_20260920_230254.deshecho.jsonl tiene 12 registros con
/// "Renamed":true que se deshicieron esa noche, y el contador los seguía contando.
/// </summary>
public class RenombradosReparablesTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    private string Deshacer => Path.Combine(_dir, "deshacer");
    private string Musica => Path.Combine(_dir, "musica");

    public RenombradosReparablesTests()
    {
        Directory.CreateDirectory(Deshacer);
        Directory.CreateDirectory(Musica);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Linea(string de, string a)
        => JsonSerializer.Serialize(new UndoRecord { OrigPath = de, FinalPath = a, Renamed = true });

    [Fact]
    public void Los_deshechos_no_cuentan()
    {
        // 12 renombrados deshechos: los archivos volvieron a su nombre original.
        var deshechos = new List<string>();
        for (var i = 1; i <= 12; i++)
        {
            var orig = Path.Combine(Musica, $"pista{i:00}.mp3");
            File.WriteAllText(orig, "");
            deshechos.Add(Linea(orig, Path.Combine(Musica, $"Artista {i} - Tema {i}.mp3")));
        }
        var archivado = Path.Combine(Deshacer, "run_20260920_230254.deshecho.jsonl");
        File.WriteAllText(archivado, string.Join("\n", deshechos) + "\n");
        File.SetLastWriteTime(archivado, new DateTime(2026, 9, 20, 23, 30, 0));

        // 3 renombrados vigentes, de una tirada posterior.
        var vigentes = new List<string>();
        for (var i = 1; i <= 3; i++)
        {
            var nuevo = Path.Combine(Musica, $"Bad Bunny - Tema {i}.mp3");
            File.WriteAllText(nuevo, "");
            vigentes.Add(Linea(Path.Combine(Musica, $"bb_{i}.mp3"), nuevo));
        }
        File.WriteAllText(Path.Combine(Deshacer, "run_20261001_101010.jsonl"), string.Join("\n", vigentes) + "\n");

        Assert.Equal(15, RekordboxRelocator.ReadRenames(Deshacer).Count);   // lo que contaba antes
        Assert.Equal(3, SettingsViewModel.RenombradosReparables(Deshacer));
        Assert.Equal(12, SettingsViewModel.NombresDeshechos(Deshacer).Count);
    }

    // Un renombrado cuyo archivo ya no está en la ruta nueva (se movió o se borró) no se puede reparar.
    [Fact]
    public void Si_el_archivo_ya_no_esta_no_cuenta()
    {
        File.WriteAllText(Path.Combine(Deshacer, "run_20261001_101010.jsonl"),
            Linea(Path.Combine(Musica, "a.mp3"), Path.Combine(Musica, "A - B.mp3")) + "\n");

        Assert.Equal(0, SettingsViewModel.RenombradosReparables(Deshacer));
        Assert.Equal(0, SettingsViewModel.RenombradosReparables(Path.Combine(_dir, "no-existe")));
    }

    // Deshecho y vuelto a aplicar más tarde con otro nombre: cuenta una vez, por la ruta original.
    [Fact]
    public void Deshecho_y_vuelto_a_aplicar_cuenta_una_vez()
    {
        var orig = Path.Combine(Musica, "pista01.mp3");
        var primero = Path.Combine(Musica, "Artista - Tema.mp3");
        var segundo = Path.Combine(Musica, "Artista - Tema (Extended).mp3");
        var archivado = Path.Combine(Deshacer, "run_20260920_230254.deshecho.jsonl");
        File.WriteAllText(archivado, Linea(orig, primero) + "\n");
        File.SetLastWriteTime(archivado, new DateTime(2026, 9, 20, 23, 30, 0));
        File.WriteAllText(Path.Combine(Deshacer, "run_20261001_101010.jsonl"), Linea(orig, segundo) + "\n");
        File.WriteAllText(segundo, "");

        Assert.Equal(1, SettingsViewModel.RenombradosReparables(Deshacer));
    }
}

/// <summary>
/// T9 (Ajustes) · Si config.net.json no se podía leer, o traía credenciales cifradas en otro equipo,
/// solo quedaba una línea en el registro. Ahora Ajustes lo enseña y ofrece olvidar las ilegibles.
/// </summary>
public class AvisosConfiguracionTests : IDisposable
{
    // Un secreto DPAPI que no se puede descifrar en este perfil (como en ConfiguracionIlegibleTests).
    private const string BlobAjeno = "01000000d08c9ddf00112233445566778899aabbccddeeff";

    private readonly string _dir = Mp3Fixture.NewTempDir();
    private readonly AppPaths _paths;

    public AvisosConfiguracionTests() => _paths = new AppPaths(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Credenciales_de_otro_equipo_se_avisan_y_se_pueden_olvidar()
    {
        File.WriteAllText(_paths.ConfigPath, $$"""{"SpotifySecretEnc":"{{BlobAjeno}}","DiscogsTokenEnc":"{{BlobAjeno}}","Folders":["E:\\Musica"]}""");
        var cfg = AppConfig.Load(_paths);

        var (archivo, credenciales) = SettingsViewModel.AvisosConfiguracion(cfg);

        Assert.Equal("", archivo);   // el aviso de carga era este mismo: no se repite
        Assert.Contains("Discogs, Spotify", credenciales);
        Assert.Contains("«Olvidar credenciales ilegibles»", credenciales);

        cfg.OlvidarCredencialesIlegibles();
        Assert.Equal(("", ""), SettingsViewModel.AvisosConfiguracion(cfg));
    }

    [Fact]
    public void Una_configuracion_ilegible_se_avisa()
    {
        File.WriteAllText(_paths.ConfigPath, """{"Folders":["E:\\Musica\\COMERCIAL"],"PriorityFolders":["E:\\Mus""");
        var cfg = AppConfig.Load(_paths);

        var (archivo, credenciales) = SettingsViewModel.AvisosConfiguracion(cfg);

        Assert.Equal(cfg.AvisoCarga, archivo);
        Assert.Contains("config.net.json.ilegible_", archivo);
        Assert.Equal("", credenciales);
    }

    [Fact]
    public void Una_configuracion_normal_no_avisa()
    {
        File.WriteAllText(_paths.ConfigPath, """{"Folders":["E:\\Musica"]}""");
        Assert.Equal(("", ""), SettingsViewModel.AvisosConfiguracion(AppConfig.Load(_paths)));
    }
}

/// <summary>
/// I7 · Descargar «llama3.1:8b» (unos 4,7 GB) dejaba BeatTag inutilizable unos 20 minutos, sin forma
/// de cancelar; y si la conexión se quedaba colgada sin cerrar el socket, la lectura esperaba para
/// siempre. Ahora la descarga va en segundo plano, se puede cancelar y se detiene sola si no avanza.
/// </summary>
public class DescargaModeloTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Un Ollama que manda una línea de avance y se queda colgado sin cerrar la conexión.</summary>
    private sealed class OllamaColgado : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FlujoColgado()) });
    }

    /// <summary>Entrega una línea y después no vuelve a dar nada hasta que se cancela la lectura.</summary>
    private sealed class FlujoColgado : Stream
    {
        private readonly byte[] _primera = Encoding.UTF8.GetBytes("{\"status\":\"pulling 6a0746a1ec1a\",\"total\":4920753328,\"completed\":104857600}\n");
        private bool _dada;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_dada) { _dada = true; _primera.CopyTo(buffer); return _primera.Length; }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Lo que hace «Cancelar» (o el vigilante de avance): la descarga colgada termina enseguida.
    [Fact]
    public async Task Una_descarga_colgada_se_puede_cancelar()
    {
        var ia = new OllamaClient(new ApiClient(new AppPaths(_dir)), http: new HttpClient(new OllamaColgado()));
        var avances = new List<double>();
        using var cts = new CancellationTokenSource();

        var descarga = ia.PullModelAsync("llama3.1:8b", new ProgresoInmediato(avances), cts.Token);
        await Task.Delay(200);
        Assert.False(descarga.IsCompleted);   // colgada, como con la conexión que no se cierra

        cts.Cancel();
        var terminada = await Task.WhenAny(descarga, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(descarga, terminada);
        Assert.Equal("Descarga cancelada.", await descarga);
        Assert.Single(avances);
    }

    [Fact]
    public void Sin_avance_cinco_minutos_se_da_por_parada()
    {
        var t0 = new DateTime(2026, 10, 19, 21, 0, 0, DateTimeKind.Utc);
        Assert.False(SettingsViewModel.DescargaParada(t0, t0.AddMinutes(4).AddSeconds(59)));
        Assert.True(SettingsViewModel.DescargaParada(t0, t0.AddMinutes(5)));
    }

    private sealed class ProgresoInmediato(List<double> destino) : IProgress<(string Estado, double Fraccion)>
    {
        public void Report((string Estado, double Fraccion) value) => destino.Add(value.Fraccion);
    }
}
