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

    /// <summary>
    /// Tonalidad tal como viene en el tag ("Am", "Dbm", "8A"…). La escriben rekordbox y programas
    /// similares; BeatTag la lee y la muestra, pero no la deduce del audio.
    /// </summary>
    public string? Key { get; set; }

    // La conversión a Camelot se guarda calculada: la rejilla la pide una vez por fila visible y
    // al ordenar por esa columna, por todas, y no merece la pena repetir el análisis del texto.
    private string? _keyLeida;
    private string _keyNombre = "";
    private string _keyCamelot = "";

    private void AsegurarKey()
    {
        if (_keyLeida == Key) return;
        _keyLeida = Key;
        var k = Analysis.MusicalKey.Parse(Key);
        _keyNombre = k?.Name ?? "";
        _keyCamelot = k?.Camelot ?? "";
    }

    /// <summary>Tonalidad normalizada ("Am", "C#m"), o vacío si el tag no trae nada legible.</summary>
    public string KeyName { get { AsegurarKey(); return _keyNombre; } }

    /// <summary>
    /// Código Camelot ("8A"), que es con el que se mezcla en armónico: encajan los temas del mismo
    /// número y los de un paso a cada lado.
    /// </summary>
    public string KeyCamelot { get { AsegurarKey(); return _keyCamelot; } }

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
