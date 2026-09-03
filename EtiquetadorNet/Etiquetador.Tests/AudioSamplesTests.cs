using Etiquetador.App.Services;

namespace Etiquetador.Tests;

/// <summary>
/// Apertura de audio multiplataforma (pestaña Volumen).
///
/// NAudio decodifica MP3 con codecs de Windows: compila en macOS y revienta al ejecutarse. La
/// salida es NLayer, un decodificador en C# puro, PERO solo fuera de Windows: alli el codec del
/// sistema es el que produjo las 6.629 medidas que el usuario ya tiene guardadas, y entre
/// decodificadores el pico puede moverse medio decibelio.
/// </summary>
public class AudioSamplesTests
{
    // Un MP3 minimo de verdad: lo que importa es que se ABRA con el camino de esta plataforma.
    [Fact]
    public void Un_mp3_se_abre_en_cualquier_sistema()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(mp3);

            var reader = AudioSamples.Abrir(mp3, out var recurso);
            using (recurso)
            {
                Assert.NotNull(reader.WaveFormat);
                Assert.True(reader.WaveFormat.SampleRate > 0);
                Assert.InRange(reader.WaveFormat.Channels, 1, 2);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Fuera de Windows, un formato que no sabemos decodificar tiene que decirlo claramente en vez de
    // fallar con un error del sistema que no explica nada.
    [Fact]
    public void Lo_que_no_se_puede_leer_se_dice_claro()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var flac = Path.Combine(dir, "a.flac");
            File.WriteAllBytes(flac, new byte[] { 0x66, 0x4C, 0x61, 0x43, 0, 0, 0, 0 });

            var ex = Record.Exception(() => AudioSamples.Abrir(flac, out _));

            // En Windows lo intenta el codec del sistema (falle o no); fuera, se avisa explicitamente.
            if (!OperatingSystem.IsWindows())
            {
                Assert.IsType<NotSupportedException>(ex);
                Assert.Contains("MP3", ex!.Message);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // En Windows manda el codec del sistema: es lo que garantiza que las medidas ya guardadas y las
    // nuevas signifiquen lo mismo. Si alguien cambia eso, esta prueba lo canta.
    [WindowsFact]
    public void En_windows_se_usa_el_codec_del_sistema()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(mp3);

            var reader = AudioSamples.Abrir(mp3, out var recurso);
            using (recurso)
                Assert.IsType<NAudio.Wave.AudioFileReader>(reader);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
