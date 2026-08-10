using Etiquetador.Core;
using Xunit;

namespace Etiquetador.Tests;

// Detección de la versión y de QUIÉN la firma (el remixer no debe perderse al limpiar el nombre).
public class RemixParserTests
{
    [Theory]
    [InlineData("Adele - Hello (Tiesto Remix)", "Tiesto", "Remix")]
    [InlineData("Bad Bunny - Titi Me Pregunto (Pedro Cabrera Remix)", "Pedro Cabrera", "Remix")]
    [InlineData("Rosalia - Despecha (DJ Nano Bootleg)", "DJ Nano", "Bootleg")]
    [InlineData("Karol G - Provenza (Sammy Deejay Flip)", "Sammy Deejay", "Flip")]
    [InlineData("Tema (David Guetta & Morten Remix)", "David Guetta & Morten", "Remix")]
    [InlineData("Tema (Joe Berte Rmx)", "Joe Berte", "Remix")]          // rmx -> Remix
    [InlineData("Track (Dimitri Vegas VIP Mix)", "Dimitri Vegas", "VIP")]
    [InlineData("Cancion - Pedro Cabrera Remix", "Pedro Cabrera", "Remix")]   // sin paréntesis
    [InlineData("Song (Remixed by Calvin Harris)", "Calvin Harris", "Remix")] // "remixed by"
    public void Detecta_al_remixer(string input, string remixer, string kind)
    {
        var r = RemixParser.Parse(input);
        Assert.True(r.IsVersion);
        Assert.Equal(remixer, r.Remixer);
        Assert.Equal(kind, r.Kind);
    }

    [Theory]
    [InlineData("Shakira - Monotonia (Extended Mix)")]
    [InlineData("Song (Original Mix)")]
    [InlineData("Song (Club Mix)")]
    [InlineData("Artist - Cancion normal")]
    [InlineData("Song (Acapella)")]
    public void No_inventa_remixer_en_versiones_genericas(string input)
        => Assert.False(RemixParser.Parse(input).HasRemixer);

    [Fact]
    public void Radio_edit_es_edit_pero_sin_autor()
    {
        var r = RemixParser.Parse("Daft Punk - One More Time (Radio Edit)");
        Assert.False(r.HasRemixer);   // no lo firma nadie
        Assert.Equal("Edit", r.Kind);
    }

    [Fact]
    public void La_etiqueta_junta_autor_y_tipo()
        => Assert.Equal("Tiesto Remix", RemixParser.Parse("Hello (Tiesto Remix)").Label);

    [Fact]
    public void Ignora_el_bpm_pegado_al_autor()
    {
        var r = RemixParser.Parse("Quevedo - Columbia (Alex Ferrer Edit) 128 BPM");
        Assert.Equal("Alex Ferrer", r.Remixer);
    }

    // --- Edits de pool: también llevan firma y no debe perderse ---

    [Theory]
    [InlineData("Quevedo - Columbia (Alex Ferrer Hype Intro)", "Alex Ferrer", "Hype Intro")]
    [InlineData("Bizarrap - Session 52 (DJ Nano Quick Hit)", "DJ Nano", "Quick Hit")]
    [InlineData("Rauw Alejandro - Todo De Ti (Pedro Cabrera Intro)", "Pedro Cabrera", "Intro")]
    [InlineData("Feid - Normal (Jose Ramos Acapella Out)", "Jose Ramos", "Acapella Out")]
    [InlineData("Shakira - TQG (Sammy Deejay Short Edit)", "Sammy Deejay", "Short Edit")]
    [InlineData("Bad Bunny - Moscow Mule (Juanjo Garcia Open Show)", "Juanjo Garcia", "Open Show")]
    [InlineData("Karol G - Provenza (David Marley Break Intro)", "David Marley", "Break Intro")]
    [InlineData("Tema (DJ Sanchez Transition)", "DJ Sanchez", "Transition")]
    [InlineData("Tema (Myke Roldan Acapella Starter)", "Myke Roldan", "Acapella Starter")]
    [InlineData("Tema (DJ Eros Acapella Break)", "DJ Eros", "Acapella Break")]
    [InlineData("Tema - Alex Ferrer Redrum", "Alex Ferrer", "Redrum")]
    public void Detecta_al_editor_en_los_edits_de_pool(string input, string editor, string kind)
    {
        var r = RemixParser.Parse(input);
        Assert.Equal(editor, r.Remixer);
        Assert.Equal(kind, r.Kind);
    }

    [Fact]
    public void El_doble_espacio_separa_titulo_y_editor()
    {
        // Patrón típico de pool: "Titulo  Editor Mashup" (el separador se perdió y quedó doble espacio).
        var r = RemixParser.Parse("Don Omar - Taboo  Pedro Cabrera Mashup");
        Assert.Equal("Pedro Cabrera", r.Remixer);   // NO "Taboo Pedro Cabrera"
        Assert.Equal("Mashup", r.Kind);
    }

    [Theory]
    [InlineData("Cancion (Hype Intro)", "Hype Intro")]
    [InlineData("Cancion (Intro)", "Intro")]
    public void Edit_de_pool_sin_firma_conserva_el_tipo(string input, string kind)
    {
        var r = RemixParser.Parse(input);
        Assert.False(r.HasRemixer);
        Assert.Equal(kind, r.Kind);
    }

    // --- Los record pools distribuyen, no firman: nunca deben salir como autor ---

    [Theory]
    [InlineData("Los Yakis (Unlimited Latin Extended 108bpm) - Mamita Molona - 108bpm - DJTOOLSVIP")]
    [InlineData("SHAKE BODY - SKALES (Unlimited Latin Extended 131Bpm) - 131bpm - DJTOOLSVIP")]
    [InlineData("Tema (Latin Box Extended)")]
    [InlineData("Cancion - BRGS 2023 Remix")]
    public void Un_record_pool_no_es_el_autor(string input)
        => Assert.False(RemixParser.Parse(input).HasRemixer);

    // "Try It" NO es un pool: es un editor que firma sus edits, y debe conservarse.
    [Theory]
    [InlineData("El Alfa - Banda De Camion (Try It Hype Intro)", "Hype Intro")]
    [InlineData("Myke Towers - DEGENERE (Try It Acapella Intro)", "Acapella Intro")]
    [InlineData("Tchalalala (Try It Extended)", "Extended")]
    [InlineData("Calabria x Jordan (Try It Mashup)", "Mashup")]
    public void Try_it_es_un_editor_no_un_pool(string input, string kind)
    {
        var r = RemixParser.Parse(input);
        Assert.Equal("Try It", r.Remixer);
        Assert.Equal(kind, r.Kind);
    }

    [Fact]
    public void Extended_a_secas_no_inventa_autor()
    {
        var r = RemixParser.Parse("Shakira - Monotonia (Extended Mix)");
        Assert.False(r.HasRemixer);   // lo firma la discográfica, no una persona
    }

    [Fact]
    public void El_editor_sobrevive_aunque_el_pool_venga_detras()
    {
        // El pool se quita antes; el editor de dentro del paréntesis debe conservarse.
        var limpio = System.Text.RegularExpressions.Regex.Replace(
            "Hay Lupita - Lomiiel (Myke Roldan Acapella Intro 145Bpm) - 145bpm - DJTOOLSVIP",
            Descriptors.PoolRe, " ");
        var r = RemixParser.Parse(limpio);
        Assert.Equal("Myke Roldan", r.Remixer);
        Assert.Equal("Acapella Intro", r.Kind);
    }

    [Fact]
    public void El_bpm_final_no_impide_reconocer_el_tipo()
    {
        var r = RemixParser.Parse("Tema (Alex Selas Hype Intro 128 Bpm)");
        Assert.Equal("Alex Selas", r.Remixer);
        Assert.Equal("Hype Intro", r.Kind);
    }

    [Fact]
    public void Vacio_no_es_remix()
    {
        Assert.False(RemixParser.Parse("").IsVersion);
        Assert.False(RemixParser.Parse(null).IsVersion);
        Assert.Equal("", RemixParser.Parse(null).Label);   // no debe reventar con nulos
    }
}
