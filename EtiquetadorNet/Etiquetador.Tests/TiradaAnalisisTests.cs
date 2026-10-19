using System.Net;
using System.Text;
using System.Text.Json;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// C11 e I9 en la interfaz: lo que se pone a cero al empezar cada análisis (Enriquecer y No
/// encontradas) y el aviso cuando la IA local se desactiva sola. Con un Spotify y un Ollama de
/// mentira: sin red ni Ollama.
/// </summary>
public class TiradaAnalisisTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Un Ollama que solo tiene «llama3.2» descargado.</summary>
    private sealed class OllamaConUnModelo : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var cuerpo = req.RequestUri!.AbsolutePath == "/api/tags"
                ? JsonSerializer.Serialize(new { models = new[] { new { name = "llama3.2:latest" } } })
                : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") });
        }
    }

    private OllamaClient Ia() => new(new ApiClient(new AppPaths(_dir)), http: new HttpClient(new OllamaConUnModelo()));

    /// <summary>
    /// El caso real de I9: se elige «llama3.1:8b» en Ajustes y no se descarga. La IA se desactiva
    /// al primer intento y Enriquecer seguía analizando la biblioteca entera sin decirlo.
    /// </summary>
    private async Task<OllamaClient> IaDesactivada()
    {
        var ia = Ia();
        Assert.Null(await ia.ParseAsync("Bad Bunny - Monaco (Intro).mp3", "", "", "llama3.1:8b"));
        Assert.True(ia.AiBlocked);
        return ia;
    }

    [Fact]
    public async Task Si_la_ia_se_desactiva_se_dice_por_que()
    {
        var aviso = TiradaAnalisis.AvisoIa(await IaDesactivada(), usaIa: true);

        Assert.StartsWith("La IA local se ha desactivado durante este análisis", aviso);
        Assert.Contains("«llama3.1:8b» no está descargado", aviso);
        Assert.Contains("llama3.2:latest", aviso);   // y cuál sí lo está
    }

    // Quien tiene la IA desactivada en Opciones no necesita ningún aviso sobre ella.
    [Fact]
    public async Task Sin_la_ia_activada_no_hay_aviso()
        => Assert.Equal("", TiradaAnalisis.AvisoIa(await IaDesactivada(), usaIa: false));

    [Fact]
    public void Con_la_ia_funcionando_no_hay_aviso()
        => Assert.Equal("", TiradaAnalisis.AvisoIa(Ia(), usaIa: true));

    // Cada análisis vuelve a probar la IA: si entretanto se descargó el modelo o se arrancó
    // Ollama, no puede seguir apagada hasta cerrar la aplicación.
    [Fact]
    public async Task Empezar_un_analisis_vuelve_a_probar_la_ia()
    {
        var ia = await IaDesactivada();
        using var m = new MontajeApi();

        TiradaAnalisis.Empezar(new SpotifyProvider(m.Api), ia, usaIa: true);

        Assert.False(ia.AiBlocked);
        Assert.Equal("", TiradaAnalisis.AvisoIa(ia, usaIa: true));
    }

    // C11: tras ocho 429 seguidos Spotify se bloqueaba; al empezar el siguiente análisis vuelve.
    [Fact]
    public async Task Empezar_un_analisis_reinicia_el_bloqueo_de_Spotify()
    {
        using var m = new MontajeApi();
        m.Red.Responde("accounts.spotify.com/api/token", "{\"access_token\":\"tok\",\"token_type\":\"Bearer\",\"expires_in\":3600}")
             .Responde("api.spotify.com/v1/search", "{}", HttpStatusCode.TooManyRequests);
        var sp = new SpotifyProvider(m.Api);
        for (var i = 0; i < SpotifyProvider.LimiteDe429; i++)
            await sp.SearchAsync("Bad Bunny", $"Tema {i}", "id", "secreto", false, false);
        Assert.True(sp.SpBlocked);

        TiradaAnalisis.Empezar(sp, Ia(), usaIa: false);

        Assert.False(sp.SpBlocked);
        Assert.Equal(0, sp.Sp429);
    }
}

/// <summary>
/// E12 · Los cuadros Buscar recalculaban miles de filas dos veces por tecla y normalizaban cada fila
/// con expresiones regulares. La consulta preparada y el texto guardado por fila tienen que dar
/// EXACTAMENTE lo mismo que BusquedaTexto.Coincide.
/// </summary>
public class BusquedaTablasTests
{
    [Theory]
    [InlineData("bunny titi", "Bad Bunny - Tití Me Preguntó")]
    [InlineData("bad bunny", "Bad_Bunny-Titi.mp3")]
    [InlineData("rosalia", "ROSALÍA - DESPECHÁ")]
    [InlineData("despecha rosalia", "ROSALÍA - DESPECHÁ")]
    [InlineData("djcity 128", "Bad_Bunny-Tití_Me_Preguntó (Extended) [Djcity.com] 128 8A.mp3")]
    [InlineData("", "cualquier cosa")]
    [InlineData("monaco", "Bad Bunny - Tití Me Preguntó")]
    [InlineData("bunny monaco", "Bad Bunny - Tití Me Preguntó")]
    public void Da_lo_mismo_que_BusquedaTexto(string consulta, string texto)
    {
        var esperado = BusquedaTexto.Coincide(consulta, texto, @"E:\Musica\COMERCIAL");
        var preparado = ConsultaTabla.Preparar(texto, @"E:\Musica\COMERCIAL");
        Assert.Equal(esperado, ConsultaTabla.De(consulta).Coincide(preparado));
    }

    [Fact]
    public void Una_consulta_de_espacios_esta_vacia()
    {
        Assert.True(ConsultaTabla.De("   ").Vacia);
        Assert.True(ConsultaTabla.De("—_-").Vacia);
        Assert.False(ConsultaTabla.De("a").Vacia);
    }

    // El texto de la fila se guarda, pero una celda editada en Enriquecer tiene que encontrarse.
    [Fact]
    public void Editar_una_celda_cambia_lo_que_se_encuentra()
    {
        var r = new Etiquetador.Core.Pipeline.ProcessResult { FilePath = @"E:\m\a.mp3", Old = "pista01.mp3", New = "Bad Bunny - Monaco.mp3" };
        var fila = new PreviewRow { Result = r, Old = r.Old };
        fila.UpdateFrom(r);
        var extended = ConsultaTabla.De("monaco extended");
        Assert.False(extended.Coincide(fila));

        fila.New = "Bad Bunny - Monaco (Extended).mp3";

        Assert.True(extended.Coincide(fila));
    }

    [Fact]
    public void Cada_tabla_busca_en_sus_campos()
    {
        var calidad = new QualRow { FileName = "Bad_Bunny-Monaco.mp3", Folder = @"E:\Musica\Reggaeton", QualityLabel = "128 kbps" };
        var incompleta = new IncRow { FileName = "pista07.mp3", Folder = @"E:\Musica", Missing = "Género, Año", Artist = "Rosalía" };
        var noEncontrada = new NotFoundRow { FileName = "track07_final_master.mp3", Query = "track07", Suggestion = "Bad Bunny - Monaco", Folder = @"E:\Musica" };

        Assert.True(ConsultaTabla.De("reggaeton 128").Coincide(calidad));
        Assert.True(ConsultaTabla.De("rosalia genero").Coincide(incompleta));
        Assert.True(ConsultaTabla.De("monaco track07").Coincide(noEncontrada));
        Assert.False(ConsultaTabla.De("rosalia").Coincide(calidad));
    }
}
