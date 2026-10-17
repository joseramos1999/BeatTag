using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// N5 · RemixParser se quedaba con el PRIMER paréntesis que fuera una versión, aunque no tuviera autor
/// («(Remix)») o fuera la versión que ya trae el catálogo. Los pools añaden su edición al final del
/// nombre, así que el editor que firma el archivo se perdía. Casos reales de los registros.
/// </summary>
public class RemixUltimoEditorTests
{
    [Theory]
    [InlineData("Gaby Music, Anuel AA, Luar La L, Lunay, Bryant Myers - No Te Quieren Conmigo (Remix) (Miguel Roma Hype Intro)", "Miguel Roma", "Hype Intro")]
    [InlineData("Cyril Kamer, RVFV, Polima WestCoast - Thalia (Remix) (Pablo Maggio Acapella Intro)", "Pablo Maggio", "Acapella Intro")]
    [InlineData("David Guetta Ft Sia - Titanium (Remix) (Bayne Remix 128bpm)", "Bayne", "Remix")]
    [InlineData("Black Eyed Peas, J Balvin, Steve Aoki - RITMO (Bad Boys For Life) (Steve Aoki Remix) (Yaniss X Greg Lassierra Remix)", "Yaniss X Greg Lassierra", "Remix")]
    [InlineData("CKay, Franglish - love nwantiti (French Remix) (Yaniss Remix)", "Yaniss", "Remix")]
    public void Manda_el_ultimo_parentesis_con_autor(string nombre, string editor, string tipo)
    {
        var r = RemixParser.Parse(nombre);
        Assert.Equal(editor, r.Remixer);
        Assert.Equal(tipo, r.Kind);
    }

    // Sin ningún autor, sigue valiendo el primer paréntesis que sea versión.
    [Fact]
    public void Sin_autor_se_queda_el_primer_tipo()
    {
        var r = RemixParser.Parse("Bad Bunny - Chambea (Remix) (Extended)");
        Assert.False(r.HasRemixer);
        Assert.Equal("Remix", r.Kind);
    }

    // Todas las candidatas, de la más probable a la menos: FileProcessor pasa a la siguiente cuando
    // el catálogo ya trae la primera en su título.
    [Fact]
    public void Las_candidatas_van_de_la_ultima_a_la_primera()
    {
        var c = RemixParser.Candidates("RITMO (Bad Boys For Life) (Steve Aoki Remix) (Yaniss X Greg Lassierra Remix)")
                           .Where(r => r.HasRemixer).Select(r => r.Remixer).ToList();
        Assert.Equal(new[] { "Yaniss X Greg Lassierra", "Steve Aoki" }, c);
    }

    // Al preferir el último paréntesis aparecen listas de descriptores pegadas al autor. En el nombre
    // del archivo se conserva el texto del pool tal cual; para el tag Remixer, solo la persona.
    [Theory]
    [InlineData("Darell, Ozuna, Maluma - Lollipop (Remix) (Intro, DJ Nev Intro Acapella)", "DJ Nev")]
    [InlineData("Bad Bunny - Efecto (Extended, Luigi Beltran Extended Edit)", "Luigi Beltran")]
    [InlineData("Bad Bunny, El Alfa - La Romana (Saul Gallego Hype Intro, Extended)", "Saul Gallego")]
    [InlineData("Musicologo Y Menes, Farruko - Romper La Disco (feat. Lenox) (Juankii Hype Intro Acapella Out)", "Juankii")]
    [InlineData("Armando Calderon - Tumbao (Antonio Guevara Acapella Break Intro)", "Antonio Guevara")]
    [InlineData("Musicologo - The-Libro (Tea-Chan Intro Outro)", "Tea-Chan")]
    public void El_autor_para_el_tag_es_solo_la_persona(string nombre, string editor)
        => Assert.Equal(editor, RemixParser.Autor(RemixParser.Parse(nombre).Remixer));

    // Palabras que parecen de versión pero son parte del nombre de un artista real.
    [Theory]
    [InlineData("Tema (James Hype Remix)", "James Hype")]
    [InlineData("Tema (DJ Nano Bootleg)", "DJ Nano")]
    [InlineData("Ana Mena, Belinda - Las 12 (Manu Vip Remix 128bpm)", "Manu Vip")]
    [InlineData("Lykke Li - I Follow Rivers (Magician remix) (The Magician Remix)", "The Magician")]
    public void Los_nombres_reales_no_se_recortan(string nombre, string editor)
    {
        var r = RemixParser.Parse(nombre);
        Assert.Equal(editor, r.Remixer);
        Assert.Equal(editor, RemixParser.Autor(r.Remixer));
    }

    // «Coro» o «Break Out» describen la parte del tema, no a quien lo firma: en el nombre se
    // conservan, pero no van al tag Remixer.
    [Theory]
    [InlineData("Ozuna - Te Sone de Nuevo (Coro Acapella Intro)")]
    [InlineData("Feid - Velocidad Crucero (Break Out Acapella)")]
    public void Lo_que_describe_la_parte_del_tema_no_es_un_autor(string nombre)
    {
        var r = RemixParser.Parse(nombre);
        Assert.True(r.HasRemixer);
        Assert.Equal("", RemixParser.Autor(r.Remixer));
    }

    // El BPM detrás del tipo impedía reconocerlo en el formato de doble espacio.
    [Fact]
    public void El_bpm_al_final_no_impide_leer_el_editor_del_doble_espacio()
    {
        var r = RemixParser.Parse("Chris Brown - Look At Me Now  Beauz Hard Techno Remix 154bpm ");
        Assert.Equal("Beauz Hard Techno", r.Remixer);
        Assert.Equal("Remix", r.Kind);
    }

    // Sin límite de palabra, el final de «Firestarter» o de «@SAMUVIP» se tomaba por el tipo.
    [Theory]
    [InlineData("The Prodigy - Firestarter")]
    [InlineData("Envolver...AcpO1Flow... @SAMUVIP")]
    public void El_tipo_tiene_que_ser_una_palabra_entera(string nombre)
        => Assert.False(RemixParser.Parse(nombre).HasRemixer);

    // De punta a punta (solo limpieza, sin red): todas las firmas del nombre se conservan, en su
    // orden, y la del archivo (la última) es la que va al tag Remixer.
    [Theory]
    [InlineData("Aria Vega, Ryan Castro - CHEVERE (premium remix) (Davidmusic Slam Acapella).mp3",
                "Aria Vega, Ryan Castro - CHEVERE (Premium Remix, Davidmusic Slam Acapella).mp3", "Davidmusic Slam")]
    [InlineData("Gaby Music, Anuel AA, Luar La L, Lunay, Bryant Myers - No Te Quieren Conmigo (Remix) (Miguel Roma Hype Intro).mp3",
                "Gaby Music, Anuel AA, Luar La L, Lunay, Bryant Myers - No Te Quieren Conmigo (Remix, Miguel Roma Hype Intro).mp3", "Miguel Roma")]
    [InlineData("Black Eyed Peas, J Balvin, Steve Aoki - RITMO (Bad Boys For Life) (Steve Aoki Remix) (Yaniss X Greg Lassierra Remix).mp3",
                "Black Eyed Peas, J Balvin, Steve Aoki - RITMO (Steve Aoki Remix, Yaniss X Greg Lassierra Remix).mp3", "Yaniss X Greg Lassierra")]
    [InlineData("The Prodigy - Firestarter.mp3", "The Prodigy - Firestarter.mp3", "")]
    public async Task El_nombre_conserva_todas_las_firmas(string archivo, string esperado, string remixer)
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, archivo);
            Mp3Fixture.WriteMinMp3(p);
            var r = await new CatalogoFalso().Procesador(dir).ProcessAsync(p, false, new Etiquetador.Core.Pipeline.ProcessOptions { CleanOnly = true });
            Assert.Equal(esperado, r.New);
            Assert.Equal(remixer, r.Remixer);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // N7 · «Mashboot» es una versión con autor.
    [Fact]
    public void Mashboot_es_una_version_con_autor()
    {
        var r = RemixParser.Parse("FIEL (Ayuxx Mashboot) - Wisin, Jhay Cortez");
        Assert.Equal("Ayuxx", r.Remixer);
        Assert.Equal("Mashboot", r.Kind);
    }
}
