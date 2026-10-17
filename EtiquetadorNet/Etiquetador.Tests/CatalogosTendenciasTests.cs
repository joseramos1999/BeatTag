using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// C6 · La lista de éxitos es DIARIA y se servía de caché hasta 30 días. En los registros, el
/// Top 50 de España salió «de caché» el 11, 15, 16, 22 y 25 de septiembre y el 2 de octubre; no
/// volvió a pedirse hasta el 9 de octubre.
/// </summary>
public class CatalogosTendenciasTests
{
    private const string UrlEs = "https://kworb.net/spotify/country/es_daily.html";
    private static readonly ChartCountry Espana = new("España", 0, "es");

    /// <summary>El HTML real del chart de España que ya usan las pruebas del parser.</summary>
    private static string KworbReal()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6 && dir != null; i++, dir = Path.GetDirectoryName(dir))
        {
            var p = Path.Combine(dir, "Fixtures", "kworb_es_daily.html");
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        throw new FileNotFoundException("No se encuentra la muestra kworb_es_daily.html");
    }

    [Fact]
    public async Task Una_lista_de_hace_mas_de_unas_horas_se_vuelve_a_pedir()
    {
        using var m = new MontajeApi(cache: true);
        m.Red.Responde("kworb.net/spotify/country/es_daily", "<html>hoy</html>");
        var cp = m.Api.CachePath(UrlEs)!;
        m.Api.CacheStore(cp, "<html>la del día 11</html>");
        File.SetLastWriteTime(cp, DateTime.Now.AddDays(-4));   // dentro de los 30 días de la caché general

        await new ChartsProvider(m.Api).GetChartAsync(ChartSource.Spotify, Espana);

        Assert.Equal(1, m.Red.Cuantas("es_daily"));
        Assert.Equal("<html>hoy</html>", File.ReadAllText(cp));
    }

    [Fact]
    public async Task Una_lista_reciente_sale_de_la_cache()
    {
        using var m = new MontajeApi(cache: true);
        m.Api.CacheStore(m.Api.CachePath(UrlEs)!, "<html>de hace un rato</html>");
        await new ChartsProvider(m.Api).GetChartAsync(ChartSource.Spotify, Espana);
        Assert.Equal(0, m.Red.Cuantas("es_daily"));
    }

    [Fact]
    public async Task Actualizar_fuerza_la_red_aunque_la_guardada_sea_reciente()
    {
        using var m = new MontajeApi(cache: true);
        m.Red.Responde("kworb.net/spotify/country/es_daily", "<html>hoy</html>");
        m.Api.CacheStore(m.Api.CachePath(UrlEs)!, "<html>de hace un rato</html>");
        await new ChartsProvider(m.Api).GetChartAsync(ChartSource.Spotify, Espana, forzarRed: true);
        Assert.Equal(1, m.Red.Cuantas("es_daily"));
    }

    [Fact]
    public async Task Sin_red_se_ensena_la_ultima_guardada_y_se_sabe_de_cuando_es()
    {
        using var m = new MontajeApi(cache: true);
        m.Red.SinConexion("kworb.net");
        var cp = m.Api.CachePath(UrlEs)!;
        m.Api.CacheStore(cp, KworbReal());
        var fecha = DateTime.Now.AddDays(-2);
        File.SetLastWriteTime(cp, fecha);
        var charts = new ChartsProvider(m.Api);

        var lista = await charts.GetChartAsync(ChartSource.Spotify, Espana);

        Assert.NotEmpty(lista);   // la de antes de ayer, mejor que una pantalla vacía
        Assert.Equal(fecha, charts.FechaDeLaLista(ChartSource.Spotify, Espana)!.Value, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task El_chart_de_Deezer_tambien_caduca_en_horas()
    {
        using var m = new MontajeApi(cache: true);
        const string url = "https://api.deezer.com/playlist/1116189381/tracks?limit=50";
        m.Red.Responde("playlist/1116189381/tracks", """{"data":[{"title":"Hoy","duration":180,"artist":{"name":"A"},"link":"l"}]}""");
        var cp = m.Api.CachePath(url)!;
        m.Api.CacheStore(cp, """{"data":[{"title":"Ayer","duration":180,"artist":{"name":"A"},"link":"l"}]}""");
        File.SetLastWriteTime(cp, DateTime.Now.AddHours(-8));

        var lista = await new ChartsProvider(m.Api).GetChartAsync(ChartSource.Deezer, new ChartCountry("España", 1116189381));

        Assert.Equal("Hoy", Assert.Single(lista).Title);
    }

    [Fact]
    public void La_vigencia_de_una_lista_es_de_horas_y_no_de_dias()
        => Assert.True(ChartsProvider.VigenciaLista <= TimeSpan.FromHours(12));
}
