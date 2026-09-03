using Etiquetador.App.Services;
using NAudio.Wave;

namespace Etiquetador.Tests;

/// <summary>
/// El recorte que prepara la vista previa fuera de Windows: decodificar desde el 25 % del tema y
/// escribirlo a un WAV temporal para que `afplay` lo reproduzca.
///
/// `afplay` en sí no se puede probar aquí -no existe fuera de macOS-, pero el recorte que le sirve
/// de entrada es lógica normal y corriente (decodificar + seek + escribir), y NLayer es C# puro:
/// se puede ejercitar en cualquier sistema, Windows incluido.
/// </summary>
public class AudioPreviewTests
{
    [Fact]
    public void El_recorte_produce_un_wav_valido()
    {
        var dir = Mp3Fixture.NewTempDir();
        string? tmp = null;
        try
        {
            var mp3 = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(mp3, frames: 60);

            using var origen = AudioSamples.AbrirComoWaveStream(mp3);
            var duracionTotal = origen.TotalTime;

            tmp = AudioPreview.EscribirRecorte(mp3);

            Assert.True(File.Exists(tmp));
            using var salida = new WaveFileReader(tmp);
            Assert.Equal(origen.WaveFormat.SampleRate, salida.WaveFormat.SampleRate);
            Assert.Equal(origen.WaveFormat.Channels, salida.WaveFormat.Channels);

            // Debe faltarle aproximadamente el primer 25%: el seek de un MP3 redondea a tramas, asi
            // que se admite un margen en vez de exigir el segundo exacto.
            var esperado = duracionTotal.TotalSeconds * 0.75;
            Assert.InRange(salida.TotalTime.TotalSeconds, 0, esperado + 0.5);
            Assert.True(salida.TotalTime.TotalSeconds > 0, "el recorte no puede quedar vacio");
        }
        finally
        {
            if (tmp != null) { try { File.Delete(tmp); } catch { } }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // Cada llamada tiene que ir a su propio archivo: dos vistas previas seguidas no pueden pisarse.
    [Fact]
    public void Cada_recorte_va_a_un_archivo_distinto()
    {
        var dir = Mp3Fixture.NewTempDir();
        string? a = null, b = null;
        try
        {
            var mp3 = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(mp3, frames: 40);

            a = AudioPreview.EscribirRecorte(mp3);
            b = AudioPreview.EscribirRecorte(mp3);

            Assert.NotEqual(a, b);
            Assert.True(File.Exists(a));
            Assert.True(File.Exists(b));
        }
        finally
        {
            foreach (var f in new[] { a, b }) if (f != null) { try { File.Delete(f); } catch { } }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // Cancelar a mitad de la decodificacion tiene que abortar, no dejar un WAV a medias sonando raro.
    [Fact]
    public void Cancelar_durante_la_decodificacion_aborta()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "a.mp3");
            Mp3Fixture.WriteMinMp3(mp3, frames: 40);

            using var cts = new CancellationTokenSource();
            cts.Cancel();   // ya cancelado antes de empezar: el primer ThrowIfCancellationRequested debe saltar

            Assert.Throws<OperationCanceledException>(() => AudioPreview.EscribirRecorte(mp3, cts.Token));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Un formato que no se sabe leer fuera de Windows falla con un mensaje claro, no con lo que
    // sea que lance por dentro el intento de abrirlo.
    [Fact]
    public void Un_formato_no_soportado_falla_claro()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var flac = Path.Combine(dir, "a.flac");
            File.WriteAllBytes(flac, new byte[] { 0x66, 0x4C, 0x61, 0x43, 0, 0, 0, 0 });

            var ex = Assert.Throws<NotSupportedException>(() => AudioPreview.EscribirRecorte(flac));
            Assert.Contains("MP3", ex.Message);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
