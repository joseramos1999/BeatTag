using Etiquetador.Core;
using Etiquetador.Core.Analysis;

namespace Etiquetador.Tests;

public class AnalysisTests
{
    private static Track T(string path, string artist, string title, int bitrate = 320, int dur = 200)
        => new() { FilePath = path, Artist = artist, Title = title, Bitrate = bitrate, DurationSeconds = dur };

    [Fact]
    public void Duplicados_modo_solo_titulo_ignora_artista()
    {
        var list = new[]
        {
            T(@"C:\a\1.mp3", "Artista X", "Gasolina"),
            T(@"C:\b\2.mp3", "Artista Y", "Gasolina"),   // distinto artista, mismo título
        };
        Assert.Empty(DuplicateFinder.Find(list, DuplicateMode.ArtistTitle));      // por artista+título: no
        Assert.Single(DuplicateFinder.Find(list, DuplicateMode.TitleOnly));       // solo título: sí
    }

    [Fact]
    public void Duplicados_modo_con_duracion_separa_por_longitud()
    {
        var corta = T(@"C:\a\1.mp3", "Quevedo", "Columbia", dur: 180);
        var larga = T(@"C:\b\2.mp3", "Quevedo", "Columbia", dur: 400);   // misma canción, duración muy distinta
        var lista = new[] { corta, larga };
        Assert.Single(DuplicateFinder.Find(lista, DuplicateMode.ArtistTitle));            // sin duración: duplicados
        Assert.Empty(DuplicateFinder.Find(lista, DuplicateMode.ArtistTitleDuration));     // con duración: no
    }

    [Fact]
    public void Duplicados_duracion_agrupa_por_cercania_no_por_bloque()
    {
        // 1 s de diferencia -> duplicados (aunque caigan en "bloques de 5 s" distintos)
        var cerca = new[] { T(@"C:\a\1.mp3", "Q", "Columbia", dur: 204), T(@"C:\b\2.mp3", "Q", "Columbia", dur: 205) };
        Assert.Single(DuplicateFinder.Find(cerca, DuplicateMode.ArtistTitleDuration));
        // 6 s de diferencia -> NO duplicados
        var lejos = new[] { T(@"C:\a\1.mp3", "Q", "Columbia", dur: 200), T(@"C:\b\2.mp3", "Q", "Columbia", dur: 206) };
        Assert.Empty(DuplicateFinder.Find(lejos, DuplicateMode.ArtistTitleDuration));
    }

    [Fact]
    public void Duplicados_agrupa_misma_cancion_variando_grafia()
    {
        var list = new[]
        {
            T(@"C:\a\1.mp3", "Bad Bunny", "Titi Me Pregunto"),
            T(@"C:\b\2.mp3", "BAD BUNNY", "Tití Me Preguntó"),   // acentos/mayúsculas -> misma clave
            T(@"C:\c\3.mp3", "Feid", "Classy 101"),
        };
        var groups = DuplicateFinder.Find(list);
        Assert.Single(groups);
        Assert.Equal(2, groups[0].Tracks.Count);
    }

    [Fact]
    public void Duplicados_original_y_edit_no_son_duplicados()
    {
        var list = new[]
        {
            T(@"C:\a\1.mp3", "Daddy Yankee", "Gasolina"),
            T(@"C:\b\2.mp3", "Daddy Yankee", "Gasolina (Extended)"),
        };
        Assert.Empty(DuplicateFinder.Find(list));
    }

    [Fact]
    public void Duplicados_ignora_sin_titulo()
    {
        var list = new[] { T(@"C:\a\1.mp3", "X", ""), T(@"C:\b\2.mp3", "X", "") };
        Assert.Empty(DuplicateFinder.Find(list));
    }

    [Theory]
    [InlineData(@"C:\x.flac", 0, QualityTier.Lossless)]
    [InlineData(@"C:\x.wav", 1411, QualityTier.Lossless)]
    [InlineData(@"C:\x.mp3", 320, QualityTier.High)]
    [InlineData(@"C:\x.mp3", 192, QualityTier.Medium)]
    [InlineData(@"C:\x.mp3", 96, QualityTier.Low)]
    [InlineData(@"C:\x.mp3", 0, QualityTier.Unknown)]
    public void Calidad_clasifica(string path, int bitrate, QualityTier expected)
        => Assert.Equal(expected, AudioQuality.Rate(new Track { FilePath = path, Bitrate = bitrate }));

    [Fact]
    public void Calidad_pobre_por_debajo_de_192()
    {
        Assert.True(AudioQuality.IsPoor(new Track { FilePath = "a.mp3", Bitrate = 128 }));
        Assert.False(AudioQuality.IsPoor(new Track { FilePath = "a.mp3", Bitrate = 320 }));
        Assert.False(AudioQuality.IsPoor(new Track { FilePath = "a.flac", Bitrate = 0 }));
    }
}
