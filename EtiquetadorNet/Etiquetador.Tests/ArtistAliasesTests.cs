using Etiquetador.Core;

namespace Etiquetador.Tests;

// Alias = mismo artista con otro nombre. Distinto de ArtistExceptions, que solo corrige la grafía.
public class ArtistAliasesTests
{
    private static string K(string s) => TextUtils.Nk(TextUtils.ToAscii(s));

    // El caso que motivó la función: Deezer publica "Tumbao" bajo el nombre antiguo del artista.
    [Fact]
    public void Cruz_Cafune_y_Cruzzi_son_el_mismo_artista()
    {
        var a = new ArtistAliases();
        Assert.True(a.SameArtist(K("Cruz Cafuné"), K("Cruzzi")));
        Assert.True(a.SameArtist(K("Cruzzi"), K("Cruz Cafune")));   // sin acento, igual
    }

    [Fact]
    public void ArtistMatch_acepta_el_alias()
    {
        var previo = ArtistAliases.Current;
        try
        {
            ArtistAliases.Current = new ArtistAliases();
            Assert.True(Matching.ArtistMatch(K("Cruz Cafuné"), K("Cruzzi")));
        }
        finally { ArtistAliases.Current = previo; }
    }

    // El catálogo suele devolver varios intérpretes juntos: "Cruzzi, Hoke".
    [Fact]
    public void Reconoce_el_alias_dentro_de_una_lista_de_interpretes()
    {
        var a = new ArtistAliases();
        Assert.True(a.SameArtist(K("Cruz Cafuné"), K("Cruzzi, Hoke")));
    }

    [Fact]
    public void No_confunde_artistas_distintos()
    {
        var a = new ArtistAliases();
        Assert.False(a.SameArtist(K("Shakira"), K("Rosalia")));
        Assert.False(a.SameArtist(K("Cruz Cafuné"), K("Quevedo")));
    }

    // Al escribir se usa el nombre canónico (el primero del grupo), no el alias del catálogo.
    [Fact]
    public void Se_escribe_el_nombre_canonico()
    {
        var alias = new ArtistAliases(new[] { new[] { "Cruz Cafuné", "Cruzzi" } });
        var exc = new ArtistExceptions(null, alias);
        Assert.Equal("Cruz Cafuné", exc.NormalizeArtists("Cruzzi"));
        Assert.Equal("Cruz Cafuné, Hoke", exc.NormalizeArtists("Cruzzi, Hoke"));
    }

    [Fact]
    public void Grupo_de_un_solo_nombre_se_ignora()
    {
        var a = new ArtistAliases(new[] { new[] { "Solo Uno" } });
        Assert.Null(a.Canonical(K("Solo Uno")));
    }

    [Fact]
    public void Archivo_inexistente_crea_uno_de_arranque()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-alias-" + Guid.NewGuid().ToString("N"));
        var json = Path.Combine(dir, "ArtistAliases.json");
        try
        {
            var a = ArtistAliases.Load(json);
            Assert.True(File.Exists(json));                          // queda editable para el usuario
            Assert.True(a.SameArtist(K("Cruz Cafuné"), K("Cruzzi")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Archivo_corrupto_no_rompe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var json = Path.Combine(dir, "ArtistAliases.json");
        try
        {
            File.WriteAllText(json, "{ esto no es json valido ");
            var a = ArtistAliases.Load(json);                        // no lanza: cae a la lista por defecto
            Assert.True(a.SameArtist(K("Cruz Cafuné"), K("Cruzzi")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
