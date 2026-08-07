using System.Reflection;
using System.Text.Json.Nodes;
using Etiquetador.Core.Ai;

namespace Etiquetador.Tests;

// El parseo de la respuesta de Gemini (extraer el objeto JSON del texto) es la parte con lógica pura.
public class GeminiParseTests
{
    // Llama a los métodos privados estáticos FromNode/ExtractText por reflexión (lógica pura, sin red).
    private static string ExtractText(string raw)
        => (string)typeof(GeminiClient).GetMethod("ExtractText", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { raw })!;

    private static AiParse FromNode(JsonNode? j)
        => (AiParse)typeof(GeminiClient).GetMethod("FromNode", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { j })!;

    [Fact]
    public void Extrae_texto_de_candidates()
    {
        var raw = """
        {"candidates":[{"content":{"parts":[{"text":"{\"artist\":\"Bad Bunny\",\"title\":\"Titi Me Pregunto\"}"}]}}]}
        """;
        var txt = ExtractText(raw);
        Assert.Contains("Bad Bunny", txt);
    }

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
}
