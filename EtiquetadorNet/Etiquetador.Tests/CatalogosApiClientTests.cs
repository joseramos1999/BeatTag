using System.Net;
using System.Text;
using Etiquetador.Core;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// La capa de red: un tiempo agotado no es «Cancelar» (C7), un fallo de red no es «sin
/// resultados» (C8a) y la caché ni crece sin fin ni sirve entradas rotas (C10). Todo sobre una red
/// falsa: ninguna prueba sale a internet.
/// </summary>
public class CatalogosApiClientTests
{
    private const string BusquedaDeezer = "https://api.deezer.com/search?q=Black%20Eyed%20Peas%20Shut%20Up&limit=8";

    // ------------------------------------------------------------------ C7

    [Fact]
    public async Task Un_tiempo_agotado_no_se_trata_como_cancelar()
    {
        // En una tirada de 15.000 canciones, Deezer se queda colgado una vez: antes la
        // TaskCanceledException del timeout se relanzaba y el análisis entero acababa con
        // «Análisis cancelado por el usuario».
        using var m = new MontajeApi(timeout: TimeSpan.FromMilliseconds(150));
        m.Red.Cuelga("api.deezer.com");

        var r = await m.Api.GetAsync(BusquedaDeezer);   // no lanza

        Assert.Null(r);
        Assert.Contains("tiempo de espera", m.Api.LastApiError);
        Assert.Equal(2, m.Red.Cuantas("api.deezer.com"));   // y se reintentó una vez
        Assert.Equal(1, m.Api.FallosDeRed);
    }

    [Fact]
    public async Task Cancelar_de_verdad_sigue_cancelando()
    {
        using var m = new MontajeApi(timeout: TimeSpan.FromSeconds(30));
        m.Red.Cuelga("api.deezer.com");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => m.Api.GetAsync(BusquedaDeezer, ct: cts.Token));
        Assert.Equal(0, m.Api.FallosDeRed);   // cancelar no es un fallo de red
    }

    [Fact]
    public async Task Los_POST_y_las_descargas_tampoco_confunden_timeout_con_cancelar()
    {
        using var m = new MontajeApi(timeout: TimeSpan.FromMilliseconds(150));
        m.Red.Cuelga("api.acoustid.org").Cuelga("cdn.test");

        Assert.Null(await m.Api.PostFormAsync("https://api.acoustid.org/v2/lookup", "a=b"));
        Assert.Null(await m.Api.PostJsonAsync("https://api.acoustid.org/v2/otro", "{}"));
        Assert.Null(await m.Api.GetBytesAsync("https://cdn.test/portada.jpg"));
        Assert.Equal(3, m.Api.FallosDeRed);
    }

    [Fact]
    public async Task AudD_un_timeout_es_un_error_del_servicio_y_no_una_cancelacion()
    {
        // En IdentificationScanner la excepción se tragaba y la pasada acababa como si nada.
        var red = new RedFalsa().Cuelga("api.audd.io");
        using var http = new HttpClient(red) { Timeout = TimeSpan.FromMilliseconds(150) };
        var audio = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(audio, new byte[64]);
            var r = await new AuddProvider(http).IdentificarAsync(audio, "token-de-prueba");
            Assert.Null(r.Match);
            Assert.False(r.Detener);
            Assert.Contains("a tiempo", r.Error);
        }
        finally { File.Delete(audio); }
    }

    // ------------------------------------------------------------------ C8a

    [Fact]
    public async Task Una_busqueda_vacia_es_una_respuesta_y_no_un_fallo()
    {
        using var m = new MontajeApi();
        m.Red.Responde("api.deezer.com", "{\"data\":[],\"total\":0}");
        var r = await m.Api.GetRespuestaAsync(BusquedaDeezer);
        Assert.False(r.FalloDeRed);
        Assert.Equal(200, r.Codigo);
        Assert.Equal(0, m.Api.FallosDeRed);
    }

    [Fact]
    public async Task Un_404_es_una_respuesta_y_no_un_fallo()
    {
        using var m = new MontajeApi();
        m.Red.Responde("api.deezer.com/track/", "{}", HttpStatusCode.NotFound);
        var r = await m.Api.GetRespuestaAsync("https://api.deezer.com/track/1");
        Assert.False(r.FalloDeRed);
        Assert.Equal(404, r.Codigo);
        Assert.Equal(1, m.Red.Cuantas("api.deezer.com"));   // reintentar daría lo mismo
        Assert.Equal(0, m.Api.FallosDeRed);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]   // el «musicbrainz.org HTTP 503» de los registros
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Un_5xx_o_429_tras_reintentar_cuenta_como_fallo_de_red(HttpStatusCode codigo)
    {
        using var m = new MontajeApi();
        m.Red.Responde("musicbrainz.org", "", codigo);
        var r = await m.Api.GetRespuestaAsync("https://musicbrainz.org/ws/2/recording?query=x&fmt=json");
        Assert.True(r.FalloDeRed);
        Assert.Null(r.Texto);
        Assert.Equal(2, m.Red.Cuantas("musicbrainz.org"));
        Assert.Equal(1, m.Api.FallosDeRed);
    }

    [Fact]
    public async Task Sin_conexion_cuenta_como_fallo_de_red()
    {
        using var m = new MontajeApi();
        m.Red.SinConexion("api.deezer.com");
        Assert.Null(await m.Api.GetAsync(BusquedaDeezer));
        Assert.Equal(1, m.Api.FallosDeRed);
    }

    [Fact]
    public async Task Si_el_reintento_sale_bien_no_hay_fallo()
    {
        using var m = new MontajeApi();
        m.Red.Secuencia("musicbrainz.org", (HttpStatusCode.ServiceUnavailable, ""), (HttpStatusCode.OK, "{\"recordings\":[]}"));
        var r = await m.Api.GetAsync("https://musicbrainz.org/ws/2/recording?query=x&fmt=json");
        Assert.NotNull(r);
        Assert.Equal(0, m.Api.FallosDeRed);
    }

    [Fact]
    public async Task La_cuota_de_Deezer_no_se_guarda_como_respuesta()
    {
        // Deezer avisa de la cuota con un HTTP 200 y {"error":…}. Antes eso se guardaba en caché
        // y esa búsqueda daba «0 resultados» durante 30 días.
        using var m = new MontajeApi(cache: true);
        m.Red.Responde("api.deezer.com", "{\"error\":{\"type\":\"Exception\",\"message\":\"Quota limit exceeded\",\"code\":4}}");
        var r = await m.Api.GetRespuestaAsync(BusquedaDeezer);
        Assert.True(r.FalloDeRed);
        Assert.Equal(1, m.Api.FallosDeRed);
        Assert.False(File.Exists(m.Api.CachePath(BusquedaDeezer)));
    }

    [Fact]
    public async Task Medir_fallos_cuenta_solo_lo_de_su_cancion()
    {
        // El contrato para quien orquesta: mide UNA canción aunque haya otras a la vez.
        using var m = new MontajeApi();
        m.Red.SinConexion("caida.test").Responde("bien.test", "{}");

        async Task<int> Cancion(string servidor)
        {
            using var medicion = m.Api.MedirFallos();
            await m.Api.GetAsync($"https://{servidor}/a");
            await Task.Yield();
            await m.Api.GetAsync($"https://{servidor}/b");
            return medicion.Fallos;
        }

        var resultados = await Task.WhenAll(Cancion("caida.test"), Cancion("bien.test"), Cancion("caida.test"));
        Assert.Equal(new[] { 2, 0, 2 }, resultados);
        Assert.Equal(4, m.Api.FallosDeRed);
    }

    [Fact]
    public async Task Las_mediciones_anidadas_suman_hacia_fuera()
    {
        using var m = new MontajeApi();
        m.Red.SinConexion("caida.test");
        using var fuera = m.Api.MedirFallos();
        using (var dentro = m.Api.MedirFallos())
        {
            await m.Api.GetAsync("https://caida.test/x");
            Assert.True(dentro.HuboFallos);
        }
        await m.Api.GetAsync("https://caida.test/y");
        Assert.Equal(2, fuera.Fallos);
    }

    // ------------------------------------------------------------------ C10

    [Fact]
    public async Task Una_entrada_de_cache_ilegible_vuelve_a_la_red()
    {
        // Una escritura cortada a medias dejaba un JSON truncado que se servía un mes como «sin
        // resultados».
        using var m = new MontajeApi(cache: true);
        var cp = m.Api.CachePath(BusquedaDeezer)!;
        Directory.CreateDirectory(Path.GetDirectoryName(cp)!);
        File.WriteAllText(cp, "{\"data\":[{\"id\":3135556,\"title\":\"Shut", Encoding.UTF8);
        m.Red.Responde("api.deezer.com", "{\"data\":[{\"id\":3135556,\"title\":\"Shut Up\"}]}");

        var r = await m.Api.GetAsync(BusquedaDeezer);

        Assert.Equal("Shut Up", r?["data"]?[0]?["title"]?.ToString());
        Assert.Equal(1, m.Red.Cuantas("api.deezer.com"));
        Assert.Contains("Shut Up\"", File.ReadAllText(cp));   // y la entrada queda reparada
    }

    [Fact]
    public void Guardar_en_cache_no_deja_temporales()
    {
        using var m = new MontajeApi(cache: true);
        var cp = m.Api.CachePath(BusquedaDeezer)!;
        m.Api.CacheStore(cp, "{\"data\":[]}");
        m.Api.CacheStore(cp, "{\"data\":[1]}");   // sobrescribir también
        Assert.Equal("{\"data\":[1]}", File.ReadAllText(cp));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(cp)!, "*.tmp"));
    }

    [Fact]
    public void Podar_borra_lo_caducado_y_conserva_lo_vigente()
    {
        using var m = new MontajeApi(cache: true);
        var vieja = m.Api.CachePath("https://api.deezer.com/search?q=vieja")!;
        var nueva = m.Api.CachePath("https://api.deezer.com/search?q=nueva")!;
        var huerfano = Path.Combine(Path.GetDirectoryName(nueva)!, "x.json.abc.tmp");
        m.Api.CacheStore(vieja, "{\"data\":[]}");
        m.Api.CacheStore(nueva, "{\"data\":[]}");
        File.WriteAllText(huerfano, "{");
        File.SetLastWriteTime(vieja, DateTime.Now.AddDays(-45));     // más de los 30 días
        File.SetLastWriteTime(huerfano, DateTime.Now.AddHours(-3));  // escritura cortada antigua

        var (archivos, bytes) = m.Api.PodarCache();

        Assert.Equal(2, archivos);
        Assert.True(bytes > 0);
        Assert.False(File.Exists(vieja));
        Assert.False(File.Exists(huerfano));
        Assert.True(File.Exists(nueva));
    }

    [Fact]
    public async Task La_poda_automatica_va_en_segundo_plano()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-poda-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(dir);
            using var api = new ApiClient(paths, new HttpClient(new RedFalsa())) { CacheOn = true };
            var vieja = api.CachePath("https://api.deezer.com/search?q=vieja")!;
            Directory.CreateDirectory(Path.GetDirectoryName(vieja)!);
            File.WriteAllText(vieja, "{}");
            File.SetLastWriteTime(vieja, DateTime.Now.AddDays(-60));

            Assert.Null(api.ReadCache("https://api.deezer.com/search?q=otra"));   // primer contacto con la caché

            for (var i = 0; i < 100 && File.Exists(vieja); i++) await Task.Delay(50);
            Assert.False(File.Exists(vieja));
            Assert.True(File.Exists(Path.Combine(paths.CacheDir, ".ultima-poda")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Una_antiguedad_maxima_propia_acorta_la_caducidad()
    {
        using var m = new MontajeApi(cache: true);
        const string url = "https://kworb.net/spotify/country/es_daily.html";
        m.Red.Responde("kworb.net", "<html>lista nueva</html>");
        var cp = m.Api.CachePath(url)!;
        m.Api.CacheStore(cp, "<html>lista vieja</html>");
        File.SetLastWriteTime(cp, DateTime.Now.AddHours(-7));

        Assert.Equal("<html>lista vieja</html>", await m.Api.GetTextAsync(url));                        // 30 días: vale
        Assert.Equal("<html>lista nueva</html>", await m.Api.GetTextAsync(url, maxAge: TimeSpan.FromHours(6)));
        Assert.Equal(1, m.Red.Cuantas("kworb.net"));
    }
}
