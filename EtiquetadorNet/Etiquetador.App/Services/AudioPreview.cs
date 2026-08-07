using System;
using NAudio.Wave;

namespace Etiquetador.App.Services;

/// <summary>
/// Reproductor de vista previa: escucha una pista para verificar el match antes de aplicar.
/// Uno a la vez. Empieza ~25 % dentro del tema (la "chicha"). Windows/MediaFoundation
/// (mp3/m4a/aac/wav/wma). Los formatos sin soporte lanzan y se avisan en la UI.
/// </summary>
public sealed class AudioPreview : IDisposable
{
    private IWavePlayer? _out;
    private WaveStream? _reader;

    public string? CurrentPath { get; private set; }
    public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;

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

    public void Stop()
    {
        try { _out?.Stop(); } catch { }
        _out?.Dispose(); _out = null;
        _reader?.Dispose(); _reader = null;
        CurrentPath = null;
        StateChanged?.Invoke();
    }

    public void Dispose() => Stop();
}
