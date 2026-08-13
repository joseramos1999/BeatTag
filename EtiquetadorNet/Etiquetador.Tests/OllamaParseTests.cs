using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etiquetador.Core.Ai;
using Etiquetador.Core;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

// El mapeo de la respuesta de la IA local es la parte con lógica pura (sin red ni Ollama en marcha).
public class OllamaParseTests
{
    private static AiParse FromNode(JsonNode? j)
        => (AiParse)typeof(OllamaClient).GetMethod("FromNode", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { j })!;

    // Réplica de la extracción que hace ParseAsync sobre el cuerpo que devuelve Ollama.
    private static AiParse? ExtraerDeRespuestaOllama(string raw)
    {
        var txt = JsonNode.Parse(raw)?["response"]?.GetValue<string>() ?? "";
        if (txt.Length == 0) return null;
        var m = Regex.Match(txt, @"\{.*\}", RegexOptions.Singleline);
        return m.Success ? FromNode(JsonNode.Parse(m.Value)) : null;
    }

    [Fact]
    public void Extrae_el_json_del_campo_response()
    {
        var raw = """
        {"model":"llama3.2","response":"{\"artist\":\"Bad Bunny\",\"title\":\"Titi Me Pregunto\",\"version\":\"\",\"is_mashup\":false,\"confidence\":0.9}","done":true}
        """;
        var p = ExtraerDeRespuestaOllama(raw);
        Assert.NotNull(p);
        Assert.Equal("Bad Bunny", p!.Artist);
        Assert.Equal("Titi Me Pregunto", p.Title);
    }

    [Fact]
    public void Tolera_texto_alrededor_del_json()
    {
        // Los modelos pequeños a veces añaden explicación pese a pedirles solo JSON.
        var raw = """
        {"response":"Claro, aqui tienes:\n{\"artist\":\"Feid\",\"title\":\"Classy 101\",\"version\":\"\",\"is_mashup\":false,\"confidence\":0.8}\nEspero que sirva.","done":true}
        """;
        var p = ExtraerDeRespuestaOllama(raw);
        Assert.NotNull(p);
        Assert.Equal("Feid", p!.Artist);
    }

    [Fact]
    public void Respuesta_vacia_no_rompe()
        => Assert.Null(ExtraerDeRespuestaOllama("""{"response":"","done":true}"""));

    [Fact]
    public void FromNode_mapea_claves_minusculas()
    {
        var j = JsonNode.Parse("""
        {"artist":"  Feid ","title":" Classy 101 ","version":"Remix","is_mashup":false,"confidence":0.92}
        """);
        var p = FromNode(j);
        Assert.Equal("Feid", p.Artist);       // trim
        Assert.Equal("Classy 101", p.Title);
        Assert.Equal("Remix", p.Version);
        Assert.False(p.IsMashup);
        Assert.Equal(0.92, p.Confidence, 3);
    }

    [Fact]
    public void FromNode_mashup_true()
    {
        var j = JsonNode.Parse("""{"artist":"","title":"","version":"","is_mashup":true,"confidence":0}""");
        Assert.True(FromNode(j).IsMashup);
    }

    // Lo más importante para quien no tenga Ollama: la ausencia no debe romper ni ralentizar el análisis.
    [Fact]
    public async Task Sin_Ollama_no_rompe_y_se_desactiva_sola()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-ollama-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(dir);
        Directory.CreateDirectory(dir);
        try
        {
            // Puerto sin nadie escuchando: simula "Ollama no instalado".
            var cli = new OllamaClient(new ApiClient(paths)) { Host = "http://127.0.0.1:9" };

            Assert.Null(await cli.ListModelsAsync());
            Assert.False(await cli.IsAvailableAsync());

            var p = await cli.ParseAsync("Bichota Karol G Dj Masa Intro 95 Bpm - DJTOOLSVIP.mp3", "", "", "llama3.2");
            Assert.Null(p);
            Assert.True(cli.AiBlocked);      // no se reintenta en cada archivo

            // Ya bloqueado: devuelve null de inmediato, sin volver a intentar la conexión.
            Assert.Null(await cli.ParseAsync("otra cancion.mp3", "", "", "llama3.2"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
