using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// Lectura del chart de Spotify por pais. Se prueba contra HTML REAL guardado (Fixtures), porque lo
/// que hay que fijar es que sabemos leer lo que el sitio publica de verdad, no lo que yo suponga que
/// publica.
///
/// Es HTML de un tercero: puede cambiar sin avisar. Por eso el parseo se salta lo que no encaja en
/// vez de reventar, y estas pruebas comprueban tambien ese comportamiento.
/// </summary>
public class SpotifyChartsParserTests
{
    private static string Fixture(string nombre)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6 && dir != null; i++, dir = Path.GetDirectoryName(dir))
        {
            var p = Path.Combine(dir, "Fixtures", nombre);
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        throw new FileNotFoundException($"No se encuentra la muestra {nombre}");
    }

    [Fact]
    public void Lee_el_chart_real_de_espana()
    {
        var filas = SpotifyChartsParser.ParseTracks(Fixture("kworb_es_daily.html"));

        Assert.NotEmpty(filas);
        Assert.Equal(1, filas[0].Position);
        Assert.Equal("BbY WOW", filas[0].Title);
        Assert.StartsWith("KAROL G", filas[0].Artist);
    }

    // Los colaboradores cuentan: para buscar en una biblioteca de DJ, "KAROL G, Judeline, rusowsky"
    // acierta mucho mas que solo "KAROL G".
    [Fact]
    public void Los_colaboradores_van_en_el_artista()
    {
        var filas = SpotifyChartsParser.ParseTracks(Fixture("kworb_es_daily.html"));
        Assert.Contains("Judeline", filas[0].Artist);
        Assert.Contains("rusowsky", filas[0].Artist);
    }

    // El ID de Spotify es lo que permite pedirle la ficha exacta a Spotify: sin el, esto seria solo
    // texto raspado de una pagina.
    [Fact]
    public void Cada_fila_trae_su_id_de_spotify()
    {
        var filas = SpotifyChartsParser.ParseTracks(Fixture("kworb_es_daily.html"));
        Assert.All(filas, f => Assert.Matches("^[A-Za-z0-9]{22}$", f.SpotifyId));
        Assert.All(filas, f => Assert.StartsWith("https://open.spotify.com/track/", f.Link));
    }

    [Fact]
    public void Las_posiciones_van_en_orden()
    {
        var filas = SpotifyChartsParser.ParseTracks(Fixture("kworb_es_daily.html"));
        Assert.Equal(Enumerable.Range(1, filas.Count), filas.Select(f => f.Position));
    }

    [Fact]
    public void Se_respeta_el_limite_pedido()
        => Assert.Single(SpotifyChartsParser.ParseTracks(Fixture("kworb_es_daily.html"), limite: 1));

    // --- Robustez: es HTML ajeno ---

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>nada que ver</body></html>")]
    [InlineData("<table><tbody><tr><td>roto")]
    public void Un_html_que_no_encaja_no_revienta(string html)
        => Assert.Empty(SpotifyChartsParser.ParseTracks(html));

    // Una fila sin enlace de pista se salta, pero las demas se leen igual: que el sitio cambie una
    // fila no puede dejar la pestana en blanco.
    [Fact]
    public void Una_fila_rota_no_se_lleva_las_demas()
    {
        var html = """
            <tbody>
            <tr><td class="np">1</td><td class="text mp"><div>sin enlaces</div></td></tr>
            <tr><td class="np">2</td><td class="text mp"><div><a href="../artist/AAA.html">Quevedo</a> - <a href="../track/BBB.html">Columbia</a></div></td></tr>
            </tbody>
            """;
        var filas = SpotifyChartsParser.ParseTracks(html);
        Assert.Single(filas);
        Assert.Equal("Columbia", filas[0].Title);
        Assert.Equal(2, filas[0].Position);
    }

    // Las entidades HTML se decodifican: si no, apareceria "Tit&iacute;" en la tabla.
    [Fact]
    public void Las_entidades_html_se_decodifican()
    {
        var html = """<tr><td class="np">1</td><td class="text mp"><div><a href="../artist/A.html">Bad Bunny</a> - <a href="../track/B.html">Tit&iacute; Me Pregunt&oacute;</a></div></td></tr>""";
        Assert.Equal("Tití Me Preguntó", SpotifyChartsParser.ParseTracks(html)[0].Title);
    }

    // --- Indice de paises ---

    // El nombre del pais esta en la PRIMERA CELDA de la fila, no en el texto del enlace: ahi pone
    // "Daily". Confundirlos deja el selector lleno de paises llamados "Daily", que fue justo lo que
    // paso la primera vez.
    [Fact]
    public void El_nombre_del_pais_sale_de_la_celda_no_del_enlace()
    {
        var html = """
            <tr><td class="mp text">Spain</td>
            <td class="mp text"><a href="country/es_daily.html">Daily</a> (<a href="country/es_daily_totals.html">Totals</a>) |
            <a href="country/es_weekly.html">Weekly</a></td>
            </tr>
            """;
        var paises = SpotifyChartsParser.ParseCountries(html);

        Assert.Single(paises);
        Assert.Equal("Spain", paises[0].Name);
        Assert.Equal("es", paises[0].Code);
    }

    // Global tambien vale, y su codigo no es de dos letras: exigir dos lo dejaba fuera, y para un DJ
    // el chart global es de los mas utiles.
    [Fact]
    public void El_chart_global_no_se_queda_fuera()
    {
        var html = """
            <tr><td>Global</td><td><a href="country/global_daily.html">Daily</a></td></tr>
            <tr><td>Spain</td><td><a href="country/es_daily.html">Daily</a></td></tr>
            <tr><td>Argentina</td><td><a href="country/ar_daily.html">Daily</a></td></tr>
            """;
        var paises = SpotifyChartsParser.ParseCountries(html);

        Assert.Equal(3, paises.Count);
        Assert.Equal("Argentina", paises[0].Name);           // ordenados por nombre
        Assert.Contains(paises, p => p.Code == "global");
    }

    [Fact]
    public void Un_pais_no_se_repite()
    {
        var html = """
            <tr><td>Spain</td><td><a href="country/es_daily.html">Daily</a></td></tr>
            <tr><td>Spain</td><td><a href="country/es_daily.html">Daily</a></td></tr>
            """;
        Assert.Single(SpotifyChartsParser.ParseCountries(html));
    }
}
