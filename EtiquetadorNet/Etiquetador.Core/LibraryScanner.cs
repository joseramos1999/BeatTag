using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Etiquetador.Core;

/// <summary>Escanea una carpeta (recursivo) y lee tags + propiedades de audio de cada archivo.</summary>
public static class LibraryScanner
{
    public static readonly string[] AudioExtensions =
        { ".mp3", ".flac", ".m4a", ".aac", ".wav", ".ogg", ".wma", ".opus" };

    public static IEnumerable<Track> Scan(string folder, bool recursive = true)
    {
        foreach (var f in EnumerateFiles(folder, recursive))
            yield return ReadTrack(f);
    }

    /// <summary>Enumera solo las rutas de archivos de audio (sin leer tags). Nunca lanza.</summary>
    public static IEnumerable<string> EnumerateFiles(string folder, bool recursive = true)
    {
        var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        try
        {
            return Directory.EnumerateFiles(folder, "*.*", opt)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>Lee un archivo con TagLib; si falla, devuelve un Track solo con la ruta.</summary>
    public static Track ReadTrack(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return new Track
            {
                FilePath = path,
                Title = file.Tag.Title,
                Artist = file.Tag.JoinedPerformers,
                Album = file.Tag.Album,
                Genre = file.Tag.JoinedGenres,
                Year = file.Tag.Year,
                Bpm = file.Tag.BeatsPerMinute,
                DurationSeconds = (int)file.Properties.Duration.TotalSeconds,
                Bitrate = file.Properties.AudioBitrate,
                SampleRate = file.Properties.AudioSampleRate,
                Channels = file.Properties.AudioChannels,
            };
        }
        catch
        {
            return new Track { FilePath = path };
        }
    }
}
