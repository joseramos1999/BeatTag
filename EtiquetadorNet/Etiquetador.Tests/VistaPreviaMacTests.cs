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

    /// <summary>Un «reproductor» que deja su marca y termina en el acto.</summary>
    private static Func<string, ProcessStartInfo> ReproductorInstantaneo(string marca)
        => _ => OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { Arguments = $"/c echo listo> \"{marca}\"", UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", $"echo listo > '{marca}'" }, UseShellExecute = false };

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
    //
    // Se espera al aviso con el que la vista previa refresca los botones, no a un plazo. El
    // «reproductor» es un proceso de verdad (cmd.exe o /bin/sh) y tarda en arrancar y en salir lo
    // que el equipo le deje. MEDIDO en octubre de 2026, de lanzar la vista previa a soltarla: unos
    // 150 ms en reposo; 0,5-3,6 s con dos baterías y una compilación a la vez; 3,5-4,4 s en dos
    // núcleos saturados, de los que unos 3 s eran la vida del propio proceso según el sistema (de
    // StartTime a ExitTime). El plazo anterior de 10 s llegó a vencer con la batería completa en
    // Windows. El minuto es solo el tope para que una regresión falle en vez de colgar la batería.
    [Fact]
    public async Task Al_terminar_de_sonar_deja_de_contar_como_reproduciendo()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "Karol G - Provenza.mp3");
            Mp3Fixture.WriteMinMp3(mp3, frames: 100);
            var marca = Path.Combine(dir, "sono.txt");
            using var preview = new AudioPreview(caminoMac: true, reproductor: ReproductorInstantaneo(marca));

            preview.Toggle(mp3);

            // Suscrito y DESPUÉS comprobado, como hace la propia vista previa con el proceso: si ya
            // ha terminado entre una cosa y otra, no se espera a un aviso que ya pasó.
            var suelta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            preview.StateChanged += () => { if (preview.CurrentPath == null) suelta.TrySetResult(); };
            if (preview.CurrentPath == null) suelta.TrySetResult();
            await suelta.Task.WaitAsync(TimeSpan.FromMinutes(1));

            Assert.True(File.Exists(marca), "el reproductor no llegó a ejecutarse: se soltó por un error al prepararlo");
            Assert.False(preview.IsPlaying);
            Assert.Null(preview.CurrentPath);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
