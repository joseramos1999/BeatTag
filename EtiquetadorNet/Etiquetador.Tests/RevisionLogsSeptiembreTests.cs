using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Fallos encontrados revisando los registros de uso normal de septiembre de 2026 (300 renombrados
/// aplicados el día 22 y 24 el día 25). Cada caso es un archivo real de esa biblioteca.
/// </summary>
public class RevisionLogsSeptiembreTests
{
    private static string[] Versiones(string archivo)
    {
        var p = FileNameParser.Parse(archivo);
        return Descriptors.ExtractOtros(Descriptors.CompleteTruncated(p.RawForOtros), "");
    }

    // --- Los corchetes que son versión ya no se tiran ---

    [Theory]
    [InlineData("Toxic (Steve Aoki & KAAZE Remix) [Intro Clean] - Britney Spears - bpm - DJTOOLSVIP.mp3", "Intro")]
    [InlineData("Fisher - Losing It (CHALANT & DEDRO Remix) [Extended Mix].wav", "Extended Mix")]
    [InlineData("La Pantera - Cayó La Noche (feat. Cruz Cafuné, Abhir Hathi, Bejo, EL IMA) [Remix].mp3", "Remix")]
    public void Un_corchete_con_la_version_se_conserva(string archivo, string version)
        => Assert.Contains(version, Versiones(archivo));

    // Las firmas de pool y de quien lo subió siguen fuera.
    [Theory]
    [InlineData("Igual Que Ayer RKM y Ken (Acap) [MaxxYsla]..mp3", "MaxxYsla")]
    [InlineData("Die Young (Angelo The Kid Split Edit)[EdmPacks.com].mp3", "EdmPacks")]
    [InlineData("Yo Voy (Zion _ Lennox)...IOAcp [TheMaLcA].mp3", "TheMaLcA")]
    public void Un_corchete_con_una_firma_se_sigue_quitando(string archivo, string firma)
        => Assert.DoesNotContain(firma, FileNameParser.Parse(archivo).RawForOtros);

    // --- Abreviaturas de acapella ---

    [Theory]
    [InlineData("[DJ Luis Orihuela] - Eh Oh Eh Oh - Jowell & Randy (In Acp).mp3", "Intro Acapella")]
    [InlineData("Yo Voy (Zion _ Lennox)...IOAcp [TheMaLcA].mp3", "Intro Outro Acapella")]
    [InlineData("Igual Que Ayer RKM y Ken (Acap) [MaxxYsla]..mp3", "Acapella")]
    [InlineData("Daddy Yankee - Gasolina (Out Acp).mp3", "Outro Acapella")]
    [InlineData("Ojos Brujos (IntroOutro Dirty) - Clarent, Omar Courtz.mp3", "Intro Outro")]
    public void Las_abreviaturas_de_acapella_son_una_version(string archivo, string version)
        => Assert.Contains(version, Versiones(archivo));

    // «acp» dentro de otra palabra no es una acapella.
    [Fact]
    public void Acp_dentro_de_una_palabra_no_cuenta()
        => Assert.Empty(Versiones("Kacper - Placebo.mp3"));

    // --- Mashups que el catálogo convierte en un solo tema ---

    [Theory]
    [InlineData("A Tu Merced X Pierdo La Cabeza (Lois Nietto Hype Intro) (Extended) 93", "Bad Bunny - A Tu Merced (Lois Nietto Hype Intro, Extended)")]
    [InlineData("Lovumba X Bby Wow (Saul Gallego Hype Intro) (Extended) 120", "Daddy Yankee - Lovumba (Saul Gallego Hype Intro, Extended)")]
    [InlineData("Betoven X Panti Y Colale 2.0 (Hype Intro) (Extended) 127", "El Alfa, De La Ghetto - Panti Y Colale 2.0 (Hype Intro, Extended)")]
    [InlineData("Me Reclama X Zizi (Ruben Rey Hype Intro) (Extended) 90", "Mambo Kingz, DJ Luian - Me Reclama (Ruben Rey Hype Intro, Extended)")]
    public void Perder_una_de_las_canciones_se_detecta(string titulo, string propuesto)
        => Assert.True(Matching.PierdeUnaCancion(titulo, propuesto));

    [Theory]
    [InlineData("Loco x verte", "Nicky Jam - Loco x Verte")]                                       // un tema con « x » en el título
    [InlineData("Bichota (Dion Dobbe x Taron Mashup)", "Karol G - Bichota (Dion Dobbe x Taron Mashup)")] // la « x » de los autores
    [InlineData("Perro Negro", "Bad Bunny, Feid - Perro Negro")]                                   // sin « x »
    [InlineData("Estamos Bien x Right Round", "Bad Bunny - Estamos Bien x Right Round")]            // conserva las dos
    public void Si_no_se_pierde_nada_no_salta(string titulo, string propuesto)
        => Assert.False(Matching.PierdeUnaCancion(titulo, propuesto));
}
