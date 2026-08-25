using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// Al agrupar duplicados por AUDIO, un grupo reune el original con las ediciones de DJ del mismo
/// tema: comparten la grabacion pero NO son copias sobrantes. Lo que las distingue es el descriptor
/// del nombre, y de eso se encarga RemixParser. Los casos salen de grupos reales encontrados en la
/// biblioteca del usuario.
/// </summary>
public class VersionDeDuplicadosTests
{
    private static string Version(string archivo)
    {
        var info = RemixParser.Parse(archivo);
        return info.IsVersion ? info.Label.Trim() : "";
    }

    // El original no lleva descriptor: es su propia "version".
    [Fact]
    public void El_original_no_tiene_descriptor()
        => Assert.Equal("", Version("Justin Quiles, Chimbala, Zion & Lennox - Loco"));

    // Cada edicion debe quedar en su propia version, o "Marcar sobrantes" se las llevaria.
    [Theory]
    [InlineData("Justin Quiles Ft Chimbala - Loco (F3LY Melodic Intro) [129 Bpm]")]
    [InlineData("Justin Quiles Ft Chimbala - Loco (F3LY Aca Out) [128 Bpm]")]
    [InlineData("Bad Gyal, Chencho Corleone - Choque (Extended)")]
    [InlineData("Daddy Yankee - Limbo (Sergio Blasco Extended)")]
    public void Las_ediciones_de_dj_si(string archivo)
        => Assert.NotEqual("", Version(archivo));

    // Lo esencial: dos ediciones DISTINTAS del mismo tema no pueden compartir version, porque
    // entonces uno de los dos archivos se marcaria como sobrante.
    [Fact]
    public void Dos_ediciones_distintas_no_se_confunden()
    {
        var intro = Version("Justin Quiles Ft Chimbala - Loco (F3LY Melodic Intro) [129 Bpm]");
        var aca = Version("Justin Quiles Ft Chimbala - Loco (F3LY Aca Out) [128 Bpm]");
        var original = Version("Justin Quiles, Chimbala, Zion & Lennox - Loco");

        Assert.NotEqual(intro, aca);
        Assert.NotEqual(intro, original);
        Assert.NotEqual(aca, original);
    }

    // Y al reves: dos copias de la MISMA edicion si deben compartir version, que es justo el caso
    // en el que una sobra de verdad.
    [Fact]
    public void Dos_copias_de_la_misma_edicion_comparten_version()
    {
        var a = Version("Justin Quiles Ft Chimbala - Loco (F3LY Aca Out) [128 Bpm]");
        var b = Version("Justin Quiles Ft Chimbala - Loco (F3LY Aca Out) [128 Bpm] V2");
        Assert.Equal(a, b);
    }
}
