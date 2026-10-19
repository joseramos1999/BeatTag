using System.Diagnostics;
using Etiquetador.App.Services;
using NAudio.Wave;

namespace Etiquetador.Tests;

/// <summary>
/// Hallazgo A13: la vista previa fuera de Windows.
///
/// «Escuchar» decodificaba desde el 25 % del tema hasta el FINAL antes de sonar (un tema de 6
/// minutos son unos 48 MB de WAV con NLayer, y varios segundos de silencio). Y mientras se
/// decodificaba no contaba como «sonando», así que el segundo clic para parar cancelaba y volvía a
/// empezar en vez de parar.
///
/// El camino de macOS se ejercita aquí en cualquier sistema cambiando `afplay` por otro programa.
/// </summary>
public class VistaPreviaMacTests
{
    /// <summary>Un «reproductor» que tarda unos 2 s y deja una marca si nadie lo para antes.</summary>
    private static Func<string, ProcessStartInfo> ReproductorLento(string marca)
        => _ => OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { Arguments = $"/c ping -n 3 127.0.0.1 >nul & echo listo> \"{marca}\"", UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", $"sleep 2; echo listo > '{marca}'" }, UseShellExecute = false };

    private static Func<string, ProcessStartInfo> ReproductorInstantaneo()
        => _ => OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { Arguments = "/c exit 0", UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "exit 0" }, UseShellExecute = false };

    // Solo se prepara lo que va a sonar: un minuto desde el 25 %, no el resto del tema.
    [Fact]
    public void El_recorte_de_la_vista_previa_es_de_un_minuto_como_mucho()
    {
        var dir = Mp3Fixture.NewTempDir();
        string? tmp = null;
        try
        {
            var mp3 = Path.Combine(dir, "Tema largo.mp3");
            Mp3Fixture.WriteMinMp3(mp3, frames: 3500);   // unos 91 s: desde el 25 % quedan unos 68
            double total;
            using (var entero = AudioSamples.AbrirComoWaveStream(mp3)) total = entero.TotalTime.TotalSeconds;
            Assert.True(total * 0.75 > AudioPreview.SegundosVistaPrevia + 5, "el tema de prueba tiene que dar para más de un minuto");

            tmp = AudioPreview.EscribirRecorte(mp3);

            using var wav = new WaveFileReader(tmp);
            Assert.InRange(wav.TotalTime.TotalSeconds, AudioPreview.SegundosVistaPrevia - 1, AudioPreview.SegundosVistaPrevia + 0.05);
        }
        finally
        {
            if (tmp != null) { try { File.Delete(tmp); } catch { } }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // El segundo clic sobre la misma canción, mientras aún se prepara, PARA: no vuelve a empezar y
    // no llega a sonar nada.
    [Fact]
    public async Task Un_segundo_clic_mientras_se_prepara_para_la_vista_previa()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "Quevedo - Columbia.mp3");
            Mp3Fixture.WriteMinMp3(mp3, frames: 400);
            var marca = Path.Combine(dir, "sono.txt");
            using var preview = new AudioPreview(caminoMac: true, reproductor: ReproductorLento(marca));

            preview.Toggle(mp3);
            Assert.True(preview.IsPlaying, "mientras se prepara cuenta como reproduciendo");
            Assert.Equal(mp3, preview.CurrentPath);

            preview.Toggle(mp3);
            Assert.False(preview.IsPlaying);
            Assert.Null(preview.CurrentPath);

            await Task.Delay(3500);
            Assert.False(File.Exists(marca), "la vista previa llegó a sonar después de pararla");
            Assert.False(preview.IsPlaying);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Cuando el reproductor termina solo, deja de contar como reproduciendo: el siguiente clic
    // vuelve a reproducir en vez de «parar» algo que ya no suena.
    [Fact]
    public async Task Al_terminar_de_sonar_deja_de_contar_como_reproduciendo()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "Karol G - Provenza.mp3");
            Mp3Fixture.WriteMinMp3(mp3, frames: 100);
            using var preview = new AudioPreview(caminoMac: true, reproductor: ReproductorInstantaneo());

            preview.Toggle(mp3);
            var reloj = Stopwatch.StartNew();
            while (preview.IsPlaying && reloj.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);

            Assert.False(preview.IsPlaying);
            Assert.Null(preview.CurrentPath);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
