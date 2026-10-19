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
    /// <summary>
    /// Segundos que se preparan para escuchar fuera de Windows. Para comprobar que una canción es
    /// la que dice ser sobra con un minuto.
    ///
    /// Antes se decodificaba desde el 25 % hasta el FINAL antes de que sonara nada: con NLayer, en
    /// C#, un tema de 6 minutos son unos 48 MB de WAV escritos y varios segundos de silencio, y una
    /// sesión revisando canciones acumulaba cientos de megas de temporales.
    /// </summary>
    internal const double SegundosVistaPrevia = 60;

    // --- Windows ---
    private IWavePlayer? _out;
    private WaveStream? _reader;

    // --- macOS ---
    private Process? _procMac;
    private string? _tmpMac;
    private CancellationTokenSource? _ctsMac;

    /// <summary>Usar el camino de macOS (recorte + reproductor externo). Fuera de Windows, siempre.</summary>
    private readonly bool _caminoMac;

    /// <summary>Cómo se lanza el reproductor sobre el WAV recortado. En macOS, `afplay`.</summary>
    private readonly Func<string, ProcessStartInfo> _reproductor;

    public AudioPreview() : this(!OperatingSystem.IsWindows(), null) { }

    /// <summary>
    /// Para las pruebas: permite ejercitar el camino de macOS en cualquier sistema cambiando
    /// `afplay` por otro programa.
    /// </summary>
    internal AudioPreview(bool caminoMac, Func<string, ProcessStartInfo>? reproductor)
    {
        _caminoMac = caminoMac;
        _reproductor = reproductor ?? Afplay;
    }

    private static ProcessStartInfo Afplay(string wav)
    {
        var psi = new ProcessStartInfo("afplay") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(wav);
        return psi;
    }

    public string? CurrentPath { get; private set; }

    /// <summary>
    /// Suena algo, o se está preparando para sonar.
    ///
    /// Fuera de Windows cuenta también el rato en que se decodifica el recorte: antes solo contaba
    /// con `afplay` ya en marcha, así que un segundo clic para parar mientras se preparaba no
    /// paraba nada, sino que cancelaba y volvía a empezar la decodificación.
    /// </summary>
    public bool IsPlaying => _caminoMac
        ? SuenaOPreparaMac()
        : _out?.PlaybackState == PlaybackState.Playing;

    private bool SuenaOPreparaMac()
    {
        if (CurrentPath == null) return false;
        var p = _procMac;
        try { return p == null || !p.HasExited; }   // sin proceso todavía: se está preparando
        catch { return false; }
    }

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
        if (_caminoMac) PlayMac(path);
        else PlayWindows(path);
    }

    private void PlayWindows(string path)
    {
        // AIFF (M15): Media Foundation no lo abre en Windows; el lector de NAudio sí (ver AudioSamples.EsAiff),
        // y los AIFF-C de macOS, el propio. Uno de 24 bits se pasa a 16 para WaveOut (AudioSamples.ParaReproducir).
        var aiff = AudioSamples.EsAiff(path);
        _reader = aiff ? AudioSamples.AbrirAiff(path) : new MediaFoundationReader(path);
        try { _reader.CurrentTime = TimeSpan.FromSeconds(_reader.TotalTime.TotalSeconds * 0.25); } catch { }
        var player = new WaveOutEvent();
        _out = player;
        _out.Init(aiff ? AudioSamples.ParaReproducir(_reader) : _reader);
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

        Process? p;
        try { p = Process.Start(_reproductor(tmp)); }
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

        // Suscrito ANTES de activar el aviso, y comprobado después: un reproductor que termina en el
        // acto podía acabar entre una cosa y otra, y como ahora «sonando» incluye el rato de
        // preparación, la canción se quedaría marcada como sonando para siempre.
        try
        {
            p.Exited += (s, _) => AlTerminar(s as Process);
            p.EnableRaisingEvents = true;
            if (p.HasExited) AlTerminar(p);
        }
        catch (InvalidOperationException) { return; }   // un Stop() lo acaba de soltar: ya está limpio
        StateChanged?.Invoke();
    }

    /// <summary>El reproductor de macOS ha terminado solo: se limpia, una única vez.</summary>
    private void AlTerminar(Process? p)
    {
        // Ignora el evento del proceso ANTERIOR, igual que el reproductor de Windows ignora el suyo,
        // y gana una sola de las dos vías (el evento y la comprobación de arriba).
        if (p == null || !ReferenceEquals(Interlocked.CompareExchange(ref _procMac, null, p), p)) return;
        CurrentPath = null;
        BorrarSiExiste(Interlocked.Exchange(ref _tmpMac, null));
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Decodifica desde el 25 % del tema el minuto que va a sonar (<see cref="SegundosVistaPrevia"/>)
    /// y lo escribe a un WAV temporal. Se apoya en <see cref="AudioSamples.AbrirComoWaveStream"/>:
    /// los mismos formatos que ya sabe leer la pestaña Volumen, sin duplicar esa lógica.
    ///
    /// Interno y no privado para que las pruebas puedan comprobar el recorte en sí -que el WAV
    /// resultante empieza donde debe y con el audio que debe- sin depender de `afplay`, que no
    /// existe fuera de macOS.
    /// </summary>
    internal static string EscribirRecorte(string path, CancellationToken ct = default)
        => AudioSamples.EscribirRecorteWav(path, inicioFraccion: 0.25, segundos: SegundosVistaPrevia, ct).Ruta;

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

        // Se toma con un intercambio: el aviso de «terminado» llega por otro hilo y también lo suelta.
        var proc = Interlocked.Exchange(ref _procMac, null);
        if (proc != null)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            proc.Dispose();
            BorrarSiExiste(Interlocked.Exchange(ref _tmpMac, null));
        }

        CurrentPath = null;
        StateChanged?.Invoke();
    }

    public void Dispose() => Stop();
}
