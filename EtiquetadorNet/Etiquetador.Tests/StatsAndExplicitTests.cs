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
    [InlineData("Artista - Tema (Radio Edit).mp3", Explicitness.Clean)]
    [InlineData("Artista - Tema.mp3", Explicitness.Unknown)]
    public void Detecta_explicito(string file, Explicitness expected)
        => Assert.Equal(expected, ExplicitDetector.Detect(Tk(file)));

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
