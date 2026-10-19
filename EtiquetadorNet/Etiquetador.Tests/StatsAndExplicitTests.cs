using Etiquetador.Core;
using Etiquetador.Core.Analysis;

namespace Etiquetador.Tests;

public class StatsAndExplicitTests
{
    private static Track Tk(string file, string title = "", string genre = "", uint bpm = 0, uint year = 0, int bitrate = 320, int dur = 200)
        => new() { FilePath = @"C:\m\" + file, Title = title, Genre = genre, Bpm = bpm, Year = year, Bitrate = bitrate, DurationSeconds = dur };

    [Theory]
    [InlineData("Cancion (Dirty).mp3", Explicitness.Explicit)]
    [InlineData("Cancion (Clean).mp3", Explicitness.Clean)]
    [InlineData("Artista - Tema (Explicit).mp3", Explicitness.Explicit)]
    // Antes contaba como Clean. Cambiado a propósito (hallazgo A12): «Radio Edit» dice cuánto dura
    // la versión, no cómo es la letra, y muchas versiones de radio conservan la letra explícita.
    // Una colección «solo limpias» para una boda no puede fiarse de eso.
    [InlineData("Artista - Tema (Radio Edit).mp3", Explicitness.Unknown)]
    [InlineData("Artista - Tema.mp3", Explicitness.Unknown)]
    public void Detecta_explicito(string file, Explicitness expected)
        => Assert.Equal(expected, ExplicitDetector.Detect(Tk(file)));

    // ---------- Hallazgo A12: los marcadores, solo donde va la versión ----------

    // Nombres reales de la biblioteca en los que «clean» o «dirty» forman parte del artista, del
    // título o del nombre de un DJ. Antes salían como Limpia o Explícita; no dicen nada de la letra.
    [Theory]
    [InlineData("Clean Bandit - Rockabye.mp3")]
    [InlineData("Dirty Vegas - Days Go By.mp3")]
    [InlineData("Rockabye x Light It Up - Clean Bandit Ft. Major Lazer (Elias Mashup 102-108Bpm).mp3")]
    [InlineData("Darell, Clean Bandit x Darell, Ozuna, Maluma - Lambo Sport x Lollipop - Remix (Try It Mashup).mp3")]
    [InlineData("Feid x Jason Derulo - ALAKRAN x Talk Dirty (Try It Mashup).mp3")]
    [InlineData("Santa x Bailame (Dirty Sou Mashup).mp3")]
    [InlineData("Guerrera x Como Tu No Hay Dos x The Nights (Dirty Sou 115-128 Mashup).mp3")]
    [InlineData("Salvatore Ganacci, Tokischa, ROSALIA - Dirty.mp3")]
    [InlineData("Black Eyed Peas - The Time (Dirty Bit).mp3")]
    [InlineData("Skee-Lo - I Wish (Radio Edit).mp3")]
    [InlineData("Ultra Nate - Free (Mood II Swing Radio Edit).mp3")]
    public void El_artista_o_el_titulo_no_dicen_como_es_la_letra(string archivo)
        => Assert.Equal(Explicitness.Unknown, ExplicitDetector.Detect(Tk(archivo)));

    // Y los marcadores de verdad se siguen reconociendo, en todas las formas que traen los pools.
    [Theory]
    [InlineData("Myke Towers - La Falda (Intro, Dirty).mp3", Explicitness.Explicit)]
    [InlineData("Feid - Prohibidox (DJ Ronald Intro Mashup) (Dirty Intro).mp3", Explicitness.Explicit)]
    [InlineData("Quevedo - WANDA (Dirty, Extended).mp3", Explicitness.Explicit)]
    [InlineData("Ojos Brujos (IntroOutro Dirty) - Clarent, Omar Courtz.mp3", Explicitness.Explicit)]
    [InlineData("Christina Perri - a thousand years (Clean).mp3", Explicitness.Clean)]
    [InlineData("Darell, Clean Bandit - Lambo Sport (Clean, Extended).mp3", Explicitness.Clean)]
    [InlineData("Anuel AA - Reggaetonera (Clean Version) (Acapella).mp3", Explicitness.Clean)]
    [InlineData("Toxic (Steve Aoki & KAAZE Remix) [Intro Clean] - Britney Spears - bpm - DJTOOLSVIP.mp3", Explicitness.Clean)]
    [InlineData("Calabria vs. BAILE INoLVIDABLE (Deville 126-90 Toneplay Trans) Clean 8A 126 - Bad Bunny, Enur - bpm - DJTOOLSVIP.mp3", Explicitness.Clean)]
    [InlineData("Paco Amoroso - Betos Horns - fred (Remix, Clean, Radio Edit).mp3", Explicitness.Clean)]
    [InlineData("Juan Magan - Bailando por Ahi (F3LY Melodic Intro) [130 Bpm] [INTRO LIMPIA].mp3", Explicitness.Clean)]
    [InlineData("Daft Punk - One More Time - Clean.mp3", Explicitness.Clean)]
    public void Reconoce_los_marcadores_de_version(string archivo, Explicitness esperado)
        => Assert.Equal(esperado, ExplicitDetector.Detect(Tk(archivo)));

    // El caso peor de antes: «dirty» del título se miraba antes que «clean» de la versión, y la
    // versión limpia de «Talk Dirty» salía como Explícita.
    [Fact]
    public void La_version_limpia_de_Talk_Dirty_es_limpia()
    {
        Assert.Equal(Explicitness.Clean, ExplicitDetector.Detect(Tk("Jason Derulo - Talk Dirty (Clean Version).mp3")));
        Assert.Equal(Explicitness.Clean, ExplicitDetector.Detect(Tk("pista07.mp3", title: "Talk Dirty (Clean Version)")));
    }

    // El título del tag también cuenta, con la misma regla: lo que va tras « - » es la versión.
    [Theory]
    [InlineData("One More Time - Clean", Explicitness.Clean)]
    [InlineData("Soldi (Extended Redrum) 95 Bpm Dirty", Explicitness.Explicit)]
    [InlineData("Come Clean", Explicitness.Unknown)]
    [InlineData("Talk Dirty", Explicitness.Unknown)]
    public void El_titulo_del_tag_sigue_la_misma_regla(string titulo, Explicitness esperado)
        => Assert.Equal(esperado, ExplicitDetector.Detect(Tk("pista07.mp3", title: titulo)));

    // Lo que importa al final: la colección «solo limpias» de una boda.
    [Fact]
    public void Clean_Bandit_no_entra_en_una_coleccion_de_solo_limpias()
    {
        var t = Tk("Clean Bandit - Rockabye.mp3");
        Assert.Equal(Etiquetador.Core.Dj.TipoLetra.SinIndicar, Etiquetador.Core.Dj.Fichas.LetraEfectiva(null, t));
    }

    [Fact]
    public void Stats_resume_biblioteca()
    {
        var tracks = new[]
        {
            Tk("a.mp3", "A", "Reggaeton", bpm: 95, year: 2021, bitrate: 320),
            Tk("b.mp3", "B", "reggaeton", bpm: 128, year: 2019, bitrate: 128),   // género se normaliza -> Reggaeton
            Tk("c.flac", "C", "House", bpm: 0, year: 0, bitrate: 0),             // lossless por extensión
            Tk("d (Dirty).mp3", "D", "", bpm: 175, year: 2024),
        };
        var s = LibraryStats.Compute(tracks);
        Assert.Equal(4, s.Total);
        Assert.Equal(2, s.ByGenre.First(g => g.Label == "Reggaeton").Count);     // agrupa grafías
        Assert.Equal(1, s.ByBpm.First(b => b.Label == "140+").Count);
        Assert.Equal(1, s.ByQuality.First(q => q.Label == "Sin pérdida").Count);
        Assert.Equal(1, s.ByExplicit.First(e => e.Label == "Explícito").Count);
    }
}
