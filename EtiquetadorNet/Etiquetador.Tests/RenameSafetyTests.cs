using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

public class RenameSafetyTests
{
    private static readonly string Dir = Path.GetTempPath();

    [Theory]
    [InlineData(@"..\evil.mp3")]
    [InlineData(@"sub\evil.mp3")]
    [InlineData("sub/evil.mp3")]
    [InlineData(@"C:\evil.mp3")]
    [InlineData(@"\evil.mp3")]
    [InlineData("")]
    public void Rechaza_rutas_peligrosas(string proposed)
        => Assert.False(RenameSafety.TryResolveTarget(proposed, "orig.mp3", Dir, out _, out _));

    [Fact]
    public void Fuerza_extension_origen()
    {
        Assert.True(RenameSafety.TryResolveTarget("Cancion.txt", "orig.mp3", Dir, out var target, out _));
        Assert.Equal(".mp3", Path.GetExtension(target));
        Assert.Equal("Cancion", Path.GetFileNameWithoutExtension(target));
    }

    [Fact]
    public void Sanea_caracteres_ilegales()
    {
        Assert.True(RenameSafety.TryResolveTarget("A B?.mp3", "orig.mp3", Dir, out var target, out _));
        Assert.Equal("A B.mp3", target);
    }

    [Fact]
    public void Rechaza_dos_puntos_de_unidad()   // "A:" parece unidad de Windows -> se rechaza
        => Assert.False(RenameSafety.TryResolveTarget("A: B.mp3", "orig.mp3", Dir, out _, out _));

    [Fact]
    public void Nombre_valido_pasa_tal_cual()
    {
        Assert.True(RenameSafety.TryResolveTarget("Bad Bunny - Titi.mp3", "orig.mp3", Dir, out var target, out _));
        Assert.Equal("Bad Bunny - Titi.mp3", target);
    }
}
