using Etiquetador.Core.Analysis;

namespace Etiquetador.Tests;

/// <summary>
/// De varias copias de la misma canción, cuál se lleva la lista.
///
/// Importa más de lo que parece porque no es solo lo que se ve en la tabla: es el archivo que acaba
/// en el M3U8 y en la carpeta que genera «Crear carpeta con las disponibles». Llevarse un remix
/// teniendo la extended al lado se descubre en cabina.
/// </summary>
public class PrioridadVersionTests
{
    // Lo que un DJ quiere primero: la versión larga o con entrada para mezclar encima.
    [Theory]
    [InlineData("Bad Bunny - Monaco (Extended Mix).mp3")]
    [InlineData("Bad Bunny - Monaco (Extended).mp3")]
    [InlineData("Bad Bunny - Monaco (Hype Intro).mp3")]
    [InlineData("Bad Bunny - Monaco (IVAN RF Hype Intro) 106 BPM.mp3")]
    [InlineData("Bad Bunny - Monaco (Melodic Intro).mp3")]
    [InlineData("Bad Bunny - Monaco (Break Intro).mp3")]
    [InlineData("Bad Bunny - Monaco (Open Show).mp3")]
    [InlineData("Bad Bunny - Monaco (Starter).mp3")]
    public void Las_ediciones_para_mezclar_van_primero(string nombre)
        => Assert.Equal(TipoVersion.Extendida, PrioridadVersion.De(nombre));

    // La canción tal cual se publicó. «Original Mix» y «Radio Edit» son eso: el tema, sin más.
    [Theory]
    [InlineData("Bad Bunny - Monaco.mp3")]
    [InlineData("Bad Bunny - Monaco (Original Mix).mp3")]
    [InlineData("Bad Bunny - Monaco (Radio Edit).mp3")]
    [InlineData("Bad Bunny - Monaco (Clean).mp3")]
    [InlineData("Bad Bunny - Monaco (Dirty).mp3")]
    [InlineData("Bad Bunny - Monaco (Album Version).mp3")]
    public void La_cancion_publicada_es_la_original(string nombre)
        => Assert.Equal(TipoVersion.Original, PrioridadVersion.De(nombre));

    // Otro artista la ha rehecho: suena distinta, así que es lo último que se quiere.
    [Theory]
    [InlineData("Bad Bunny - Monaco (Yaniss Remix).mp3")]
    [InlineData("Bad Bunny - Monaco (RMX).mp3")]
    [InlineData("Bad Bunny - Monaco (Bootleg).mp3")]
    [InlineData("Bad Bunny - Monaco (Rework).mp3")]
    [InlineData("Bad Bunny - Monaco (Flip).mp3")]
    [InlineData("Bad Bunny - Monaco (VIP Mix).mp3")]
    public void Los_remixes_van_los_ultimos(string nombre)
        => Assert.Equal(TipoVersion.Remix, PrioridadVersion.De(nombre));

    // EL CASO QUE OBLIGA A MIRAR PRIMERO SI ES REMIX: «Extended Remix» es un remix largo, no la
    // versión larga del original. Comprobando «extended» antes, esta se colaría la primera.
    [Theory]
    [InlineData("Bad Bunny - Monaco (Extended Remix).mp3")]
    [InlineData("Bad Bunny - Monaco (Yaniss Remix) (Hype Intro).mp3")]
    public void Un_remix_largo_sigue_siendo_un_remix(string nombre)
        => Assert.Equal(TipoVersion.Remix, PrioridadVersion.De(nombre));

    // El orden que pidió el usuario, comprobado de una pieza.
    [Fact]
    public void El_orden_es_extendida_original_y_por_ultimo_remix()
    {
        var extendida = PrioridadVersion.OrdenDe("Tema (Extended).mp3");
        var original = PrioridadVersion.OrdenDe("Tema.mp3");
        var remix = PrioridadVersion.OrdenDe("Tema (Remix).mp3");

        Assert.True(extendida < original, "la extendida va antes que la original");
        Assert.True(original < remix, "la original va antes que el remix");
    }

    // Ordenar una lista de copias reales deja arriba la que hay que pinchar.
    [Fact]
    public void Teniendo_varias_copias_gana_la_extendida()
    {
        var copias = new[]
        {
            "Bad Bunny - Monaco (Yaniss Remix).mp3",
            "Bad Bunny - Monaco.mp3",
            "Bad Bunny - Monaco (Hype Intro).mp3",
        };

        var elegida = copias.OrderBy(PrioridadVersion.OrdenDe).First();

        Assert.Equal("Bad Bunny - Monaco (Hype Intro).mp3", elegida);
    }

    // Y sin la extendida, la original antes que el remix.
    [Fact]
    public void Sin_extendida_gana_la_original_y_no_el_remix()
    {
        var copias = new[]
        {
            "Bad Bunny - Monaco (Yaniss Remix).mp3",
            "Bad Bunny - Monaco.mp3",
        };

        Assert.Equal("Bad Bunny - Monaco.mp3", copias.OrderBy(PrioridadVersion.OrdenDe).First());
    }
}
