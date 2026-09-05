using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Etiquetador.App.Services;

/// <summary>
/// Reproductor de vista previa: escucha una pista para verificar el match antes de aplicar.
/// Uno a la vez. Empieza ~25 % dentro del tema (la "chicha").
///
/// En Windows usa MediaFoundationReader + WaveOutEvent, EXACTAMENTE igual que siempre: esa parte no
/// se ha tocado, cero riesgo de regresión.
///
/// Fuera de Windows no hay salida de audio en NAudio -WaveOutEvent es WinMM, una API de Windows-,
/// así que se recurre a `afplay`, el reproductor de línea de comandos que trae todo macOS de serie.
/// Mismo patrón que ya usa esta aplicación para fpcalc: un proceso externo, nada que instalar ni
/// empaquetar. La única salvedad es que `afplay` no sabe EMPEZAR a mitad de un archivo (solo cuánto
/// reproducir, no desde dónde: comprobado, no es una limitación de esta aplicación), así que el
/// trozo desde el 25 % se decodifica aquí mismo con <see cref="AudioSamples"/> y se escribe a un WAV
/// temporal antes de lanzarlo. Ese temporal se borra al parar.
/// </summary>
public sealed class AudioPreview : IDisposable
{
    // --- Windows ---
    private IWavePlayer? _out;
    private WaveStream? _reader;

    // --- macOS ---
    private Process? _procMac;
    private string? _tmpMac;
    private CancellationTokenSource? _ctsMac;

    public string? CurrentPath { get; private set; }

    public bool IsPlaying => OperatingSystem.IsWindows()
        ? _out?.PlaybackState == PlaybackState.Playing
        : _procMac is { HasExited: false };

    /// <summary>Notifica cambios de estado (arranca/para) para refrescar los botones.</summary>
    public event Action? StateChanged;

    /// <summary>Alterna: si ya suena esta pista, para; si no, la reproduce.</summary>
    public void Toggle(string path)
    {
        if (IsPlaying && string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase)) { Stop(); return; }
        Play(path);
    }

    public void Play(string path)
    {
        Stop();
        if (OperatingSystem.IsWindows()) PlayWindows(path);
        else PlayMac(path);
    }

    private void PlayWindows(string path)
    {
        _reader = new MediaFoundationReader(path);
        try { _reader.CurrentTime = TimeSpan.FromSeconds(_reader.TotalTime.TotalSeconds * 0.25); } catch { }
        var player = new WaveOutEvent();
        _out = player;
        _out.Init(_reader);
        // Ignora el evento del reproductor ANTERIOR (podría llegar tras iniciar otro y borrar su estado).
        _out.PlaybackStopped += (s, _) => { if (ReferenceEquals(s, _out)) { CurrentPath = null; StateChanged?.Invoke(); } };
        _out.Play();
        CurrentPath = path;
        StateChanged?.Invoke();
    }

    private void PlayMac(string path)
    {
        // Feedback inmediato: decodificar el trozo tarda un instante y el botón no puede quedarse
        // mudo mientras tanto.
        CurrentPath = path;
        StateChanged?.Invoke();

        _ctsMac = new CancellationTokenSource();
        _ = PlayMacAsync(path, _ctsMac.Token);
    }

    /// <summary>Decodifica el recorte en un hilo aparte y lanza `afplay` sobre el WAV resultante.</summary>
    private async Task PlayMacAsync(string path, CancellationToken ct)
    {
        string tmp;
        try { tmp = await Task.Run(() => EscribirRecorte(path, ct), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }   // Stop() llegó durante la decodificación
        catch (Exception)
        {
            if (string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase)) { CurrentPath = null; StateChanged?.Invoke(); }
            return;
        }

        if (ct.IsCancellationRequested) { BorrarSiExiste(tmp); return; }

        var psi = new ProcessStartInfo("afplay") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(tmp);
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception) { p = null; }

        if (p == null)
        {
            BorrarSiExiste(tmp);
            if (string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase)) { CurrentPath = null; StateChanged?.Invoke(); }
            return;
        }

        // Publicar el proceso ANTES de volver a mirar el token, y mirarlo DESPUÉS de publicarlo.
        // Ese orden cierra la carrera: un Stop() que llegue mientras se decodificaba el WAV encuentra
        // _procMac todavía a null y no mata nada, pero deja el token cancelado; sin esta segunda
        // comprobación, afplay arrancaba igual y seguía sonando una pista que el usuario ya paró.
        _procMac = p; _tmpMac = tmp;

        if (ct.IsCancellationRequested)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            p.Dispose();
            if (ReferenceEquals(_procMac, p)) { _procMac = null; _tmpMac = null; }
            BorrarSiExiste(tmp);
            return;
        }

        p.EnableRaisingEvents = true;
        p.Exited += (s, _) =>
        {
            // Ignora el evento del proceso ANTERIOR, igual que el reproductor de Windows ignora el suyo.
            if (!ReferenceEquals(s, _procMac)) return;
            CurrentPath = null;
            BorrarSiExiste(_tmpMac);
            _procMac = null; _tmpMac = null;
            StateChanged?.Invoke();
        };
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Decodifica desde el 25 % del tema hasta el final y lo escribe a un WAV temporal. Se apoya en
    /// <see cref="AudioSamples.AbrirComoWaveStream"/>: los mismos formatos que ya sabe leer la
    /// pestaña Volumen, sin duplicar esa lógica.
    ///
    /// Interno y no privado para que las pruebas puedan comprobar el recorte en sí -que el WAV
    /// resultante empieza donde debe y con el audio que debe- sin depender de `afplay`, que no
    /// existe fuera de macOS.
    /// </summary>
    internal static string EscribirRecorte(string path, CancellationToken ct = default)
        => AudioSamples.EscribirRecorteWav(path, inicioFraccion: 0.25, segundos: null, ct).Ruta;

    private static void BorrarSiExiste(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); } catch { }
    }

    public void Stop()
    {
        _ctsMac?.Cancel();
        _ctsMac?.Dispose();
        _ctsMac = null;

        try { _out?.Stop(); } catch { }
        _out?.Dispose(); _out = null;
        _reader?.Dispose(); _reader = null;

        if (_procMac != null)
        {
            try { if (!_procMac.HasExited) _procMac.Kill(entireProcessTree: true); } catch { }
            _procMac.Dispose(); _procMac = null;
            BorrarSiExiste(_tmpMac); _tmpMac = null;
        }

        CurrentPath = null;
        StateChanged?.Invoke();
    }

    public void Dispose() => Stop();
}
