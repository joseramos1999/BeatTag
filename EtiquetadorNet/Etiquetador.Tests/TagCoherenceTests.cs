using Etiquetador.Core;
using Xunit;

namespace Etiquetador.Tests;

// Ajuste de confianza según la coherencia con los tags ya embebidos en el archivo.
public class TagCoherenceTests
{
    [Fact]
    public void Sin_tags_es_neutro()
        => Assert.Equal(0, Matching.TagCoherence("", "", "Daft Punk", "One More Time"));

    [Fact]
    public void Tags_que_coinciden_suman()
    {
        var d = Matching.TagCoherence("Daft Punk", "One More Time", "Daft Punk", "One More Time");
        Assert.Equal(5, d, 3);   // +3 título + +2 artista
    }

    [Fact]
    public void Titulo_con_descriptor_sigue_coincidiendo()
    {
        // El tag trae "(Radio Edit)"; se limpia el descriptor antes de comparar.
        var d = Matching.TagCoherence("Daft Punk", "One More Time (Radio Edit)", "Daft Punk", "One More Time");
        Assert.True(d >= 5);
    }

    [Fact]
    public void Titulo_distinto_penaliza()
    {
        var d = Matching.TagCoherence("Daft Punk", "Harder Better Faster", "Daft Punk", "One More Time");
        Assert.Equal(0, d, 3);   // -2 título + +2 artista
    }

    [Fact]
    public void Ambos_distintos_penalizan_fuerte()
    {
        var d = Matching.TagCoherence("Rick Astley", "Never Gonna Give You Up", "Daft Punk", "One More Time");
        Assert.Equal(-3.5, d, 3);   // -2 título + -1.5 artista
    }

    [Fact]
    public void Solo_titulo_presente_puntua_solo_titulo()
    {
        var d = Matching.TagCoherence("", "One More Time", "Daft Punk", "One More Time");
        Assert.Equal(3, d, 3);
    }
}
