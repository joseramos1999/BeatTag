using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Etiquetador.Core;

/// <summary>
/// Lo que dio recorrer una carpeta de la biblioteca.
/// </summary>
/// <param name="Archivos">Los archivos de audio encontrados.</param>
/// <param name="Disponible">
/// false si la carpeta NO se pudo recorrer: no existe, el disco no está conectado, o falló a mitad.
/// En ese caso <paramref name="Archivos"/> no dice nada de lo que hay de verdad, y quien llama NO debe
/// tratar sus canciones como borradas: ni podar su caché ni dar la carpeta por escaneada.
/// </param>
/// <param name="Motivo">Por qué no estaba disponible, en claro, para el registro ("" si lo estaba).</param>
public sealed record CarpetaRecorrida(IReadOnlyList<string> Archivos, bool Disponible, string Motivo);

/// <summary>Escanea una carpeta (recursivo) y lee tags + propiedades de audio de cada archivo.</summary>
public static class LibraryScanner
{
    // AIFF: es habitual entre DJs (lo venden las tiendas y rekordbox lo usa), TagLib lo lee y
    // escribe, y el resto del código ya lo trataba como sin pérdida (AudioQuality). Sin él aquí,
    // esos archivos no aparecían en ninguna pestaña y nada lo avisaba.
    public static readonly string[] AudioExtensions =
        { ".mp3", ".flac", ".m4a", ".aac", ".wav", ".ogg", ".wma", ".opus", ".aiff", ".aif" };

    public static IEnumerable<Track> Scan(string folder, bool recursive = true)
    {
        foreach (var f in EnumerateFiles(folder, recursive))
            yield return ReadTrack(f);
    }

    /// <summary>Enumera solo las rutas de archivos de audio (sin leer tags). Nunca lanza.</summary>
    /// <remarks>
    /// No distingue una carpeta vacía de una que no está disponible; para eso está
    /// <see cref="Recorrer"/>, que es lo que debe usar quien después pode cachés.
    /// </remarks>
    public static IEnumerable<string> EnumerateFiles(string folder, bool recursive = true)
        => Recorrer(folder, recursive).Archivos;

    /// <summary>
    /// Recorre una carpeta y dice, además de los archivos de audio, si de verdad se pudo recorrer.
    ///
    /// Antes se usaba la sobrecarga con SearchOption, que no salta nada que no se pueda leer: una
    /// sola subcarpeta sin permiso -«System Volume Information» si se añade la raíz de una unidad,
    /// como «E:\»- lanzaba a mitad del recorrido y se perdía la carpeta ENTERA, sin una línea en el
    /// registro. Con un disco externo desconectado pasaba lo mismo: lista vacía, la caché de esa
    /// carpeta podada y la biblioteca dada por escaneada. MEDIDO: volver a leer sin caché costó
    /// 340.680 ms para 13.464 canciones, frente a 7.774 ms con ella.
    ///
    /// Ahora lo que no se puede leer se salta, y también lo oculto y de sistema ($RECYCLE.BIN,
    /// «System Volume Information», .Trashes de macOS). Los «._*» que deja macOS en discos
    /// externos tampoco entran: llevan la extensión del audio pero no son audio.
    /// </summary>
    public static CarpetaRecorrida Recorrer(string folder, bool recursive = true)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return new CarpetaRecorrida(Array.Empty<string>(), false, "carpeta sin indicar");
        try
        {
            if (!Directory.Exists(folder))
                return new CarpetaRecorrida(Array.Empty<string>(), false, "no existe o el disco no está conectado");

            // Que la propia carpeta se pueda abrir. Con IgnoreInaccessible, una raíz sin permiso
            // daría una lista vacía sin más, indistinguible de una carpeta vacía.
            using (var e = Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false }).GetEnumerator())
                e.MoveNext();

            var opciones = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };
            var archivos = Directory.EnumerateFiles(folder, "*", opciones)
                .Where(EsAudio)
                .ToList();
            return new CarpetaRecorrida(archivos, true, "");
        }
        catch (Exception e)
        {
            // Falló a mitad (el disco se desconectó, por ejemplo): lo leído no es la carpeta entera.
            return new CarpetaRecorrida(Array.Empty<string>(), false, e.Message);
        }
    }

    /// <summary>¿Es un archivo de audio de la biblioteca? Por extensión, y sin los «._*» de macOS.</summary>
    public static bool EsAudio(string ruta)
    {
        var nombre = Path.GetFileName(ruta);
        if (nombre.StartsWith("._", StringComparison.Ordinal)) return false;
        return AudioExtensions.Contains(Path.GetExtension(nombre).ToLowerInvariant());
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
                Key = file.Tag.InitialKey,
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
