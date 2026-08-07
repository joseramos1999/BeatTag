using System;
using System.IO;

namespace Etiquetador.Core;

/// <summary>Una canción de la biblioteca: tags leídos + propiedades de audio.</summary>
public class Track
{
    public string FilePath { get; set; } = "";
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>Carpeta raíz (de las elegidas por el usuario) a la que pertenece; para agrupar la vista.</summary>
    public string Folder { get; set; } = "";

    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public uint Year { get; set; }
    public uint Bpm { get; set; }

    public int DurationSeconds { get; set; }
    public int Bitrate { get; set; }      // kbps
    public int SampleRate { get; set; }   // Hz
    public int Channels { get; set; }

    /// <summary>¿Le falta algún tag esencial? (para el apartado de "incompletas")</summary>
    public bool IsIncomplete =>
        string.IsNullOrWhiteSpace(Title) || string.IsNullOrWhiteSpace(Artist) ||
        string.IsNullOrWhiteSpace(Genre) || Year == 0;

    public string Duration => DurationSeconds > 0
        ? TimeSpan.FromSeconds(DurationSeconds).ToString(@"m\:ss")
        : "";

    public string Quality => Bitrate > 0 ? $"{Bitrate} kbps" : "";

    /// <summary>"Clean"/"Explícito"/"" deducido del nombre y título (para columna/filtros).</summary>
    public string Explicit => Analysis.ExplicitDetector.Label(this);
}
