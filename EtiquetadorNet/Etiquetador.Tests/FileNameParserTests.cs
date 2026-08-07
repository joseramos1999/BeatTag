using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

public class FileNameParserTests
{
    [Fact]
    public void Separa_artista_y_titulo()
    {
        var r = FileNameParser.Parse("Quevedo - Columbia.mp3");
        Assert.Equal("Quevedo", r.FnArtist);
        Assert.Equal("Columbia", r.FnTitle);
    }

    [Fact]
    public void Normaliza_en_dash_a_guion()
    {
        var r = FileNameParser.Parse("Rauw Alejandro – Todo De Ti.mp3");   // en-dash
        Assert.Equal("Rauw Alejandro", r.FnArtist);
        Assert.Equal("Todo De Ti", r.FnTitle);
    }

    [Fact]
    public void Quita_prefijo_de_pool_entre_corchetes()
    {
        var r = FileNameParser.Parse("[Deezer] Feid - Feliz Cumpleanos.mp3");
        Assert.Equal("Feid", r.FnArtist);
        Assert.Equal("Feliz Cumpleanos", r.FnTitle);
    }

    [Fact]
    public void Sin_separador_todo_es_titulo()
    {
        var r = FileNameParser.Parse("Party Rock Anthem.mp3");
        Assert.Equal("", r.FnArtist);
        Assert.Equal("Party Rock Anthem", r.FnTitle);
    }

    [Fact]
    public void Colapsa_artista_duplicado()
    {
        var r = FileNameParser.Parse("Shakira - Shakira - Monotonia.mp3");
        Assert.Equal("Shakira", r.FnArtist);
        Assert.Equal("Monotonia", r.FnTitle);
    }
}
