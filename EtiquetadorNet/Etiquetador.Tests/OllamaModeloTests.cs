using System.Net;
using System.Text;
using System.Text.Json;
using Etiquetador.Core;
using Etiquetador.Core.Ai;

namespace Etiquetador.Tests;

/// <summary>
/// La IA local tiene que saber decir si el modelo elegido está DESCARGADO, no solo si Ollama
/// responde, y por qué se ha desactivado.
///
/// El caso real: se elige «llama3.1:8b» en Ajustes (el desplegable ofrece también los sugeridos sin
/// descargar) y no se pulsa «Descargar modelo». El Asistente decía «IA local: llama3.1:8b»,
/// «Renombrar con IA» acababa con «la IA no respondió» en cada canción, y Enriquecer analizaba la
/// biblioteca entera sin IA diciéndolo solo en el log.
///
/// Ollama se sustituye por un manejador HTTP falso: estas pruebas no necesitan red ni Ollama.
/// </summary>
public class OllamaModeloTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Un Ollama de mentira: lista los modelos que se le digan y responde a /api/generate como se le diga.</summary>
    private sealed class OllamaFalso : HttpMessageHandler
    {
        public List<string>? Modelos { get; set; } = new() { "llama3.2:latest" };   // null = no responde
        public Func<CancellationToken, Task<HttpResponseMessage>> Generar { get; set; } = _ => Task.FromResult(Respuesta("{}"));
        public int Generaciones;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (Modelos == null) throw new HttpRequestException("No se puede establecer una conexión.");
            if (req.RequestUri!.AbsolutePath == "/api/tags")
                return Json(JsonSerializer.Serialize(new { models = Modelos.Select(m => new { name = m }) }));
            Interlocked.Increment(ref Generaciones);
            return await Generar(ct);
        }

        public static HttpResponseMessage Json(string cuerpo, HttpStatusCode codigo = HttpStatusCode.OK)
            => new(codigo) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") };

        /// <summary>Lo que devuelve /api/generate: el texto del modelo dentro de «response».</summary>
        public static HttpResponseMessage Respuesta(string textoDelModelo)
            => Json(JsonSerializer.Serialize(new { response = textoDelModelo, done = true }));
    }

    private OllamaClient Cliente(OllamaFalso ollama, TimeSpan? espera = null)
        => new(new ApiClient(new AppPaths(_dir)), http: new HttpClient(ollama) { Timeout = espera ?? TimeSpan.FromSeconds(30) });

    // --- Saber si el modelo elegido está descargado ---

    [Fact]
    public async Task Un_modelo_elegido_sin_descargar_no_se_da_por_disponible()
    {
        var ia = Cliente(new OllamaFalso());

        var estado = await ia.ComprobarModeloAsync("llama3.1:8b");

        Assert.False(estado.Disponible);
        Assert.Equal("", estado.Modelo);
        Assert.Contains("«llama3.1:8b» no está descargado", estado.Motivo);
        Assert.Contains("llama3.2:latest", estado.Motivo);   // y ofrece el que sí está
        Assert.Equal("", await ia.ModeloEfectivoAsync("llama3.1:8b"));
    }

    // Ollama lista «llama3.2:latest» aunque se descargara como «llama3.2»: es el mismo modelo.
    [Theory]
    [InlineData("llama3.2", "llama3.2")]
    [InlineData("llama3.2:latest", "llama3.2:latest")]
    [InlineData("", "llama3.2:latest")]                 // automático
    public async Task Un_modelo_descargado_esta_disponible(string elegido, string esperado)
    {
        var estado = await Cliente(new OllamaFalso()).ComprobarModeloAsync(elegido);

        Assert.True(estado.Disponible);
        Assert.Equal(esperado, estado.Modelo);
        Assert.Equal("", estado.Motivo);
    }

    [Fact]
    public async Task Distingue_ollama_parado_de_ollama_sin_modelos()
    {
        var parado = await Cliente(new OllamaFalso { Modelos = null }).ComprobarModeloAsync("llama3.2");
        var vacio = await Cliente(new OllamaFalso { Modelos = new() }).ComprobarModeloAsync("llama3.2");

        Assert.False(parado.Disponible);
        Assert.Contains("no responde", parado.Motivo);
        Assert.False(vacio.Disponible);
        Assert.Contains("ningún modelo", vacio.Motivo);
    }

    // --- Desactivarse diciendo por qué ---

    // Enriquecer: con el modelo sin descargar se desactiva al primer sondeo, sin llegar a pedir
    // nada, y el motivo queda a la vista para la interfaz.
    [Fact]
    public async Task Con_el_modelo_sin_descargar_se_desactiva_diciendo_por_que()
    {
        var ollama = new OllamaFalso();
        var ia = Cliente(ollama);

        Assert.Null(await ia.ParseAsync("Bad Bunny - Monaco (Intro).mp3", "", "", "llama3.1:8b"));

        Assert.True(ia.AiBlocked);
        Assert.Contains("llama3.1:8b", ia.MotivoBloqueo);
        Assert.Equal(0, ollama.Generaciones);

        ia.Reset();
        Assert.Equal("", ia.MotivoBloqueo);
    }

    // Una primera carga lenta agota el tiempo de espera y la siguiente ya responde: eso no puede
    // dejar sin IA el resto de la tirada.
    [Fact]
    public async Task Un_tiempo_agotado_suelto_no_desactiva_la_ia()
    {
        var primera = true;
        var ollama = new OllamaFalso();
        ollama.Generar = async ct =>
        {
            if (primera) { primera = false; await Task.Delay(Timeout.Infinite, ct); }
            return OllamaFalso.Respuesta("""{"artist":"Bad Bunny","title":"Monaco","version":"Intro","is_mashup":false,"confidence":0.9}""");
        };
        var ia = Cliente(ollama, TimeSpan.FromMilliseconds(200));

        Assert.Null(await ia.ParseAsync("Bad Bunny - Monaco (Intro).mp3", "", "", "llama3.2"));
        Assert.False(ia.AiBlocked);

        var p = await ia.ParseAsync("Feid - Classy 101 (Intro).mp3", "", "", "llama3.2");
        Assert.NotNull(p);
        Assert.Equal("Monaco", p!.Title);
    }

    [Fact]
    public async Task Si_el_tiempo_se_agota_otra_vez_seguida_se_desactiva_diciendo_por_que()
    {
        var ollama = new OllamaFalso { Generar = async ct => { await Task.Delay(Timeout.Infinite, ct); return null!; } };
        var ia = Cliente(ollama, TimeSpan.FromMilliseconds(200));

        for (var i = 0; i < OllamaClient.TiemposAgotadosParaDesactivar; i++)
            Assert.Null(await ia.ParseAsync($"Tema {i}.mp3", "", "", "llama3.2"));

        Assert.True(ia.AiBlocked);
        Assert.Contains("no ha respondido a tiempo", ia.MotivoBloqueo);
    }

    // --- Las herramientas del Asistente ---

    // Un JSON ilegible es una mala respuesta del modelo, no un problema de conexión.
    [Fact]
    public async Task Un_json_ilegible_no_se_informa_como_falta_de_conexion()
    {
        var ia = Cliente(new OllamaFalso { Generar = _ => Task.FromResult(OllamaFalso.Respuesta("{ idioma: es, ")) });
        var ia2 = Cliente(new OllamaFalso { Generar = _ => Task.FromResult(OllamaFalso.Respuesta("{ idioma: es }")) });

        var (json, error) = await ia.PedirJsonAsync("sistema", "peticion", "llama3.2");
        var (json2, error2) = await ia2.PedirJsonAsync("sistema", "peticion", "llama3.2");

        Assert.Null(json);
        Assert.Null(json2);
        Assert.DoesNotContain("contactar", error);
        Assert.DoesNotContain("contactar", error2);
        Assert.Contains("legible", error2);
    }

    // Proponer fichas llega a 5000 canciones: sin Ollama, se repetía el fallo en cada una. Tras
    // varios fallos seguidos se deja de preguntar un rato y se devuelve el mismo motivo al momento.
    [Fact]
    public async Task Tras_varios_fallos_de_conexion_seguidos_deja_de_preguntar()
    {
        var ollama = new OllamaFalso { Generar = _ => throw new HttpRequestException("Conexión rechazada.") };
        var ia = Cliente(ollama);

        for (var i = 0; i < OllamaClient.FallosSeguidosParaPausar + 5; i++)
        {
            var (_, error) = await ia.PedirJsonAsync("sistema", $"peticion {i}", "llama3.2");
            Assert.Contains("No se pudo contactar con Ollama", error);
        }

        Assert.Equal(OllamaClient.FallosSeguidosParaPausar, ollama.Generaciones);

        // Pasada la pausa (o tras Reset, que es lo que hace la aplicación al arrancar Ollama), se vuelve a preguntar.
        ia.Reset();
        await ia.PedirJsonAsync("sistema", "otra", "llama3.2");
        Assert.Equal(OllamaClient.FallosSeguidosParaPausar + 1, ollama.Generaciones);
    }

    [Fact]
    public async Task Una_respuesta_buena_reinicia_la_cuenta_de_fallos()
    {
        var n = 0;
        var ollama = new OllamaFalso
        {
            // Falla, falla, responde, falla, falla: nunca hay tres fallos seguidos.
            Generar = _ => ++n == 3
                ? Task.FromResult(OllamaFalso.Respuesta("""{"idioma":"ES"}"""))
                : throw new HttpRequestException("Conexión rechazada."),
        };
        var ia = Cliente(ollama);

        for (var i = 0; i < 5; i++) await ia.PedirJsonAsync("sistema", $"peticion {i}", "llama3.2");

        Assert.Equal(5, ollama.Generaciones);
    }
}
