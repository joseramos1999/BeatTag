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

    // --- Puntos suspensivos: son legítimos en los títulos y NO son un riesgo de ruta ---

    [Theory]
    [InlineData("Britney Spears - Oops!... I Did It Again.mp3", "Britney Spears - Oops!... I Did It Again.mp3")]
    [InlineData("Artista - Espera... (Extended Mix).mp3", "Artista - Espera... (Extended Mix).mp3")]
    [InlineData("Grupo - Y entonces.. paro.mp3", "Grupo - Y entonces.. paro.mp3")]
    public void Acepta_puntos_suspensivos_en_el_titulo(string proposed, string expected)
    {
        Assert.True(RenameSafety.TryResolveTarget(proposed, "orig.mp3", Dir, out var target, out var err), err);
        Assert.Equal(expected, target);
    }

    [Fact]
    public void Puntos_finales_se_recortan()   // Windows no admite nombres acabados en punto
    {
        Assert.True(RenameSafety.TryResolveTarget("Cancion sin final....mp3", "orig.mp3", Dir, out var target, out _));
        Assert.Equal("Cancion sin final.mp3", target);
    }

    // El campo "Nombre del archivo" del Editor pasa por esta misma validación.
    [Fact]
    public void Editor_no_deja_cambiar_la_extension()
    {
        Assert.True(RenameSafety.TryResolveTarget("Nuevo nombre.wav", "cancion.mp3", Dir, out var target, out _));
        Assert.Equal("Nuevo nombre.mp3", target);   // se conserva la extensión original
    }

    [Fact]
    public void Editor_no_deja_escapar_de_la_carpeta()
        => Assert.False(RenameSafety.TryResolveTarget(@"..\fuera.mp3", "cancion.mp3", Dir, out _, out _));

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("...")]
    [InlineData("  ..  ")]
    public void Rechaza_el_nombre_que_es_solo_puntos(string proposed)
        => Assert.False(RenameSafety.TryResolveTarget(proposed, "orig.mp3", Dir, out _, out _));
}
