using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
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
    //
    // Comprobado con AIFF de prueba (M15): etiquetas, renombrado y deshacer como cualquier otro;
    // Volumen los mide y no los ajusta (Mp3Gain solo toca MP3); fpcalc 1.5.1 da su huella (MEDIDO);
    // la vista previa y «Comprobar audio» los abren con el lector de AIFF de NAudio, porque Media
    // Foundation no sabe (MEDIDO: 0xC00D36C4). Añadir extensiones no invalida la caché de escaneo,
    // que va por ruta, fecha y tamaño: lo que ya estaba sale de ella y solo se leen los AIFF nuevos.
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
    /// Ahora lo que no se puede leer se salta, y también las carpetas de sistema ($RECYCLE.BIN,
    /// «System Volume Information», .Trashes de macOS; ver <see cref="SaltarCarpeta"/>). Los «._*»
    /// que deja macOS en discos externos tampoco entran: llevan la extensión del audio pero no son
    /// audio.
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

            // AttributesToSkip = 0 a propósito (el valor por defecto de EnumerationOptions es
            // Hidden | System): con él se saltaba CUALQUIER carpeta que tuviera uno solo de los dos
            // atributos, y Windows pone System (o ReadOnly) a las carpetas personalizadas con
            // desktop.ini -icono o vista propios-. Una carpeta de música personalizada desaparecía
            // de la biblioteca sin aviso, y ScanCache y AnalysisCache podaban sus entradas porque
            // cuelga de una raíz recorrida. Qué se salta lo decide SaltarCarpeta, entrada a entrada.
            var opciones = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            };
            var enumerador = new FileSystemEnumerable<string>(folder,
                (ref FileSystemEntry entrada) => entrada.ToFullPath(), opciones)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entrada) =>
                    !entrada.IsDirectory && !SaltarArchivo(entrada.Attributes) && EsAudio(entrada.FileName.ToString()),
                ShouldRecursePredicate = (ref FileSystemEntry entrada) =>
                    !SaltarCarpeta(entrada.FileName.ToString(), entrada.Attributes),
            };
            var archivos = enumerador.ToList();
            return new CarpetaRecorrida(archivos, true, "");
        }
        catch (Exception e)
        {
            // Falló a mitad (el disco se desconectó, por ejemplo): lo leído no es la carpeta entera.
            return new CarpetaRecorrida(Array.Empty<string>(), false, e.Message);
        }
    }

    // Carpetas que el sistema crea en la raíz de un disco y que nunca son música del usuario. Se
    // saltan por NOMBRE porque sus atributos no son fiables: en macOS (y en un disco exFAT escrito
    // desde un Mac) no llevan System, y una «$RECYCLE.BIN» copiada de otro disco puede llegar sin
    // ninguno. Dentro de ellas hay audio de verdad (lo borrado), que no debe volver a la biblioteca.
    private static readonly HashSet<string> CarpetasDeSistema = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN", "RECYCLER", "System Volume Information",
        ".Trashes", ".Trash", ".Spotlight-V100", ".fseventsd", ".TemporaryItems", ".DocumentRevisions-V100",
        // Los de los NAS (Synology, QNAP, netatalk), donde muchos DJs guardan la música: papeleras
        // con audio borrado de verdad, miniaturas y las bifurcaciones de recursos de macOS, que se
        // llaman igual que la canción («.AppleDouble\x.mp3») pero no son audio.
        "#recycle", "@Recycle", "@eaDir", ".@__thumb", ".AppleDouble", ".AppleDB", ".AppleDesktop",
    };

    /// <summary>
    /// ¿Se salta esta subcarpeta al recorrer la biblioteca? Solo las de sistema por nombre, o las
    /// que tienen a la vez Hidden y System, que es como Windows marca las suyas.
    /// </summary>
    /// <remarks>
    /// Visto en una biblioteca real: con «Hidden o System» se perdía una carpeta personalizada con
    /// desktop.ini, porque Windows le pone System (o ReadOnly) para que lea el desktop.ini. Una carpeta
    /// que el usuario solo oculta tampoco se salta: sigue siendo suya y antes se escaneaba.
    /// </remarks>
    public static bool SaltarCarpeta(string nombre, FileAttributes atributos)
    {
        if (CarpetasDeSistema.Contains(nombre)) return true;
        const FileAttributes ocultaYDeSistema = FileAttributes.Hidden | FileAttributes.System;
        return (atributos & ocultaYDeSistema) == ocultaYDeSistema;
    }

    // Un archivo se salta con la misma regla de atributos (oculto Y de sistema); los «._*» de macOS
    // ya los quita EsAudio por nombre.
    private static bool SaltarArchivo(FileAttributes atributos)
    {
        const FileAttributes ocultoYDeSistema = FileAttributes.Hidden | FileAttributes.System;
        return (atributos & ocultoYDeSistema) == ocultoYDeSistema;
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
