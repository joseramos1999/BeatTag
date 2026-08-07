using Etiquetador.Core;

namespace Etiquetador.Tests;

public class GenreNormalizerTests
{
    [Theory]
    [InlineData("hip-hop", "Hip Hop")]
    [InlineData("Rap", "Hip Hop")]
    [InlineData("HIPHOP", "Hip Hop")]
    [InlineData("rnb", "R&B")]
    [InlineData("R&B/Soul", "R&B")]
    [InlineData("Reguetón", "Reggaeton")]
    [InlineData("tech-house", "Tech House")]
    [InlineData("Drum & Bass", "Drum & Bass")]
    [InlineData("dnb", "Drum & Bass")]
    [InlineData("EDM", "EDM")]
    [InlineData("Pop/Rock", "Pop")]          // toma el segmento principal
    [InlineData("indie rock", "Indie Rock")] // desconocido -> Formato Título
    [InlineData("", "")]
    public void Canoniza_generos(string input, string expected)
        => Assert.Equal(expected, GenreNormalizer.Canonical(input));
}
