using Etiquetador.Core.Providers;
using Xunit;

namespace Etiquetador.Tests;

// Reconocimiento de enlaces de cancion pegados por el usuario (puro, sin red).
public class TrackLinkTests
{
    [Theory]
    [InlineData("https://www.deezer.com/track/3135556", "3135556")]
    [InlineData("https://www.deezer.com/es/track/3135556", "3135556")]
    [InlineData("https://deezer.com/track/3135556?utm_source=x", "3135556")]
    [InlineData("deezer.com/track/3135556", "3135556")]
    public void Reconoce_deezer(string url, string id)
    {
        var l = TrackLinkParser.Parse(url);
        Assert.NotNull(l);
        Assert.Equal("Deezer", l!.Value.Source);
        Assert.Equal(id, l.Value.Id);
    }

    [Theory]
    [InlineData("https://open.spotify.com/track/0eGsygTp906u18L0Oimnem", "0eGsygTp906u18L0Oimnem")]
    [InlineData("https://open.spotify.com/intl-es/track/0eGsygTp906u18L0Oimnem?si=abc", "0eGsygTp906u18L0Oimnem")]
    [InlineData("spotify:track:0eGsygTp906u18L0Oimnem", "0eGsygTp906u18L0Oimnem")]
    public void Reconoce_spotify(string url, string id)
    {
        var l = TrackLinkParser.Parse(url);
        Assert.NotNull(l);
        Assert.Equal("Spotify", l!.Value.Source);
        Assert.Equal(id, l.Value.Id);
    }

    [Fact]
    public void Reconoce_apple_music_con_id_de_cancion()
    {
        var l = TrackLinkParser.Parse("https://music.apple.com/es/album/one-more-time/697194953?i=697194975");
        Assert.NotNull(l);
        Assert.Equal("iTunes", l!.Value.Source);
        Assert.Equal("697194975", l.Value.Id);   // el de la cancion (i=), no el del album
    }

    [Fact]
    public void Enlace_de_album_de_apple_no_vale()   // sin ?i= no apunta a una cancion
        => Assert.Null(TrackLinkParser.Parse("https://music.apple.com/es/album/discovery/697194953"));

    // Forma /song/ : la que da el boton "Compartir canción" de Apple Music.
    [Theory]
    [InlineData("https://music.apple.com/py/song/x-100/1774016747", "1774016747")]
    [InlineData("https://music.apple.com/es/song/1774016747", "1774016747")]
    public void Reconoce_apple_music_enlace_de_cancion(string url, string id)
    {
        var l = TrackLinkParser.Parse(url);
        Assert.NotNull(l);
        Assert.Equal("iTunes", l!.Value.Source);
        Assert.Equal(id, l.Value.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Daft Punk - One More Time")]
    [InlineData("https://youtube.com/watch?v=abc")]
    public void Lo_que_no_es_enlace_de_cancion_se_rechaza(string s)
        => Assert.Null(TrackLinkParser.Parse(s));

    [Theory]
    [InlineData("https://algo.com/x", true)]
    [InlineData("spotify:track:abc", true)]
    [InlineData("Daft Punk", false)]
    public void Detecta_si_parece_un_enlace(string s, bool expected)
        => Assert.Equal(expected, TrackLinkParser.LooksLikeUrl(s));
}
