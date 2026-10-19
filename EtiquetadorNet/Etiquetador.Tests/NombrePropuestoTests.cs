using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// N15 · Comprobación final del nombre propuesto: las huellas de una regla que ha fallado se ven
/// mirando solo el resultado, y la propuesta llega «para revisar» en vez de aplicarse en bloque.
/// Los nombres son propuestas reales de los registros, todas marcadas para aplicar en su día.
/// </summary>
public class NombrePropuestoTests
{
    [Fact]
    public void Guion_colgando_delante_de_la_version()
        => Assert.Contains("guion o coma colgando en el nombre",
            NombrePropuesto.Anomalias("Gusty DJ, Lolo OG, DobleP - El Motorcito - (Remix)", "Gusty DJ, Lolo OG, DobleP", "El Motorcito -"));

    [Fact]
    public void Coma_colgando_y_titulo_borrado_al_pasar_a_ascii()
    {
        // Lo que salía antes de N8: el título en cirílico desaparecía.
        var m = NombrePropuesto.Anomalias("KARTASHOW, - (Rakurs & Ramirez Remix)", "KARTASHOW,", "");
        Assert.Contains("guion o coma colgando en el nombre", m);
        Assert.Contains("no hay título", m);
    }

    [Fact]
    public void Titulo_que_se_queda_sin_letras()
        => Assert.Contains("el título se queda sin letras al pasarlo a ASCII",
            NombrePropuesto.Anomalias("Damso - .", "Damso", "★."));

    [Fact]
    public void Titulo_igual_que_el_artista()
        => Assert.Contains("el título es igual que el artista",
            NombrePropuesto.Anomalias("Eric Prydz - Eric Prydz", "Eric Prydz", "Eric Prydz"));

    [Fact]
    public void Artista_repetido()
        => Assert.Contains("artista repetido: «Bad Bunny»",
            NombrePropuesto.Anomalias("Bad Bunny, Bad Bunny - Monaco", "Bad Bunny, Bad Bunny", "Monaco"));

    [Fact]
    public void Parentesis_sin_cerrar()
        => Assert.Contains("paréntesis sin cerrar o sin abrir",
            NombrePropuesto.Anomalias("Gayle - abcdepjanoo (DJs from M", "Gayle", "abcdepjanoo (DJs from M"));

    [Fact]
    public void Titulo_que_solo_es_un_descriptor()
        => Assert.Contains("el título es solo un descriptor de versión («Extended Mix»)",
            NombrePropuesto.Anomalias("Shakira - Extended Mix", "Shakira", "Extended Mix"));

    [Fact]
    public void Bpm_distinto_del_que_declara_el_nombre()
        => Assert.Contains("el BPM propuesto (135) no cuadra con el que declara el nombre (155)",
            NombrePropuesto.Anomalias("KAROL G, Bad Bunny - Ahora Me Llama (Adrukid Remix)", "KAROL G, Bad Bunny", "Ahora Me Llama",
                "Ahora Me Llama (ADRUKID Remix) - KAROL G, Bad Bunny - 155bpm - DJTOOLSVIP.mp3", "135"));

    // Lo normal no se marca: ni los títulos que empiezan por paréntesis ni los que no son latinos.
    [Theory]
    [InlineData("Bad Bunny - Monaco (Alvaro Varen Open Show)", "Bad Bunny", "Monaco")]
    [InlineData("Bill Medley, Jennifer Warnes - (I've Had) The Time of My Life", "Bill Medley, Jennifer Warnes", "(I've Had) The Time of My Life")]
    [InlineData("KARTASHOW - Золото (Rakurs & Ramirez Remix)", "KARTASHOW", "Золото")]
    [InlineData("Кино - Группа крови", "Кино", "Группа крови")]
    [InlineData("Teriyaki Boyz - Tokyo Drift (Fast & Furious) (Sam Collins Remix)", "Teriyaki Boyz", "Tokyo Drift (Fast & Furious)")]
    [InlineData("Lo Que Paso, Paso", "", "Lo Que Paso, Paso")]
    [InlineData("Salvatore Ganacci, Tokischa, Rosalia - Dirty", "Salvatore Ganacci, Tokischa, Rosalia", "Dirty")]
    public void Un_nombre_correcto_no_tiene_anomalias(string nombre, string artista, string titulo)
        => Assert.Empty(NombrePropuesto.Anomalias(nombre, artista, titulo, nombre + ".mp3", ""));

    // De punta a punta: lo que solo encuentra MusicBrainz y además tiene el título igual al artista sale
    // para revisar, con los dos motivos a la vista. (El nombre de archivo ya trae ese título para que
    // la prueba no dependa de cuánto exige MusicBrainzProvider al título.)
    [Fact]
    public async Task La_propuesta_anomala_sale_para_revisar()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Eric Prydz - Eric Prydz.mp3");
            Mp3Fixture.WriteMinMp3(p);
            var cat = new CatalogoFalso()
                .Responde(CatalogoFalso.BusquedaMusicBrainz(), CatalogoFalso.MusicBrainz(95, "Eric Prydz", "Eric Prydz"));

            var r = await cat.Procesador(dir).ProcessAsync(p, false, new ProcessOptions { Deezer = true, MusicBrainz = true });

            Assert.False(r.Skip);
            Assert.Equal("Eric Prydz - Eric Prydz.mp3", r.New);
            Assert.Contains("identificada solo por MusicBrainz", r.Revisar);
            Assert.Contains("el título es igual que el artista", r.Revisar);
            Assert.StartsWith("revisar:", r.Why);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // El caso real del que salió aquel «Eric Prydz - Eric Prydz» era un mashup («abcdefu» de Gayle con
    // «Pjanoo» de Eric Prydz, hecho por DJs from Mars). Con N7 la red de mashups mira la mitad que casa
    // con el título propuesto, y ya no se renombra.
    [Fact]
    public void El_caso_real_de_Gayle_y_Eric_Prydz_es_una_mezcla()
    {
        var p = FileNameParser.Parse("Gayle x Eric Prydz - abcdepjanoo (DJs from M.mp3");
        var mitad = Etiquetador.Core.Matching.MitadConElTitulo(p.FnArtist, p.FnTitle, "Eric Prydz");
        Assert.Equal("Gayle x Eric Prydz", mitad);
        Assert.True(Etiquetador.Core.Matching.PierdeUnaCancion(mitad, "Eric Prydz - Eric Prydz"));
    }
}
