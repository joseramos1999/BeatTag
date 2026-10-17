using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// N8 · Al pasar el nombre a ASCII desaparecían los artistas y títulos en escrituras no latinas.
/// Casos reales de los registros: «Kartashow - Золото (Rakurs &amp; Ramirez Remix)» → «KARTASHOW, -
/// (Rakurs &amp; Ramirez Remix)» y «Damso - Ε. Signaler» → «Damso - . Signaler». Un tema entero en
/// cirílico («Кино - Группа крови») quedaba en «-».
/// </summary>
public class NombreUnicodeTests
{
    [Theory]
    [InlineData("Кино", "Группа крови", "Кино - Группа крови")]
    [InlineData("Damso", "Ε. Signaler", "Damso - Ε. Signaler")]
    [InlineData("KARTASHOW", "Золото", "KARTASHOW - Золото")]
    [InlineData("Rosalía", "Despechá", "Rosalia - Despecha")]          // latinas: ASCII como siempre
    [InlineData("Björk", "Jóga", "Bjork - Joga")]
    public void Cada_parte_pasa_a_ascii_solo_si_no_pierde_letras(string artista, string titulo, string esperado)
        => Assert.Equal(esperado, FileProcessor.ComponerNombre(artista, titulo, Array.Empty<string>()));

    [Fact]
    public void Las_versiones_tambien_se_deciden_aparte()
        => Assert.Equal("KARTASHOW - Золото (Rakurs & Ramirez Remix)",
                        FileProcessor.ComponerNombre("KARTASHOW", "Золото", new[] { "Rakurs & Ramirez Remix" }));

    [Theory]
    [InlineData("Золото", true)]
    [InlineData("Ε. Signaler", true)]
    [InlineData("Rosalía Ñu Bjørk", false)]
    [InlineData("Işık", false)]      // la «ı» turca tiene equivalente
    [InlineData("♥ Love ♥", false)]  // un símbolo no es una letra
    public void Sabe_cuando_el_ascii_borra_letras(string texto, bool pierde)
        => Assert.Equal(pierde, TextUtils.PierdeLetrasEnAscii(texto));

    // Última barrera, aunque el nombre lo edite el usuario: sin una sola letra ni cifra no se renombra.
    [Theory]
    [InlineData("-.mp3", false)]
    [InlineData("- - ().mp3", false)]
    [InlineData("Кино - Группа крови.mp3", true)]
    [InlineData("Taylor Swift - 22.mp3", true)]
    public void Un_nombre_sin_letras_ni_cifras_no_se_acepta(string propuesto, bool valido)
        => Assert.Equal(valido, RenameSafety.TryResolveTarget(propuesto, "orig.mp3", Path.GetTempPath(), out _, out _));

    // El nombre de siempre no cambia: los acentos latinos se siguen quitando.
    [Fact]
    public void El_ascii_de_siempre_no_cambia()
        => Assert.Equal("Nengo Flow - Bellaqueo", FileProcessor.ComponerNombre("Ñengo Flow", "Bellaqueo", null));

    // N1 + N8 de punta a punta: lo que solo encuentra MusicBrainz sale con su confianza, para revisar,
    // y con el título en cirílico entero.
    [Fact]
    public async Task Kartashow_solo_en_MusicBrainz_sale_entero_y_para_revisar()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Kartashow - Золото (Rakurs & Ramirez Remix).mp3");
            Mp3Fixture.WriteMinMp3(p);
            var registro = Path.Combine(dir, "registro.log");
            var log = new Logger { LogFile = registro };
            var cat = new CatalogoFalso()
                .Responde(CatalogoFalso.BusquedaMusicBrainz(), CatalogoFalso.MusicBrainz(100, "Золото", "KARTASHOW"));

            var r = await cat.Procesador(dir, log).ProcessAsync(p, false, new ProcessOptions { Deezer = true, MusicBrainz = true });

            Assert.True(r.Found);
            Assert.Equal("MusicBrainz", r.Source);
            Assert.Equal("KARTASHOW - Золото (Rakurs & Ramirez Remix).mp3", r.New);
            // La puntuación de MusicBrainz llega hasta la fila: (100 - 80) / 2 = 10.
            Assert.Equal("10", r.Score);
            Assert.Contains("MusicBrainz", r.Revisar);
            Assert.StartsWith("revisar:", r.Why);
            // La línea DATA ya no dice SIN-RESULTADO de algo que sí se propone.
            var data = File.ReadAllLines(registro).Single(l => l.Contains("DATA|"));
            Assert.Contains("|OK-MB|MusicBrainz|", data);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
