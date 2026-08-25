using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Etiquetador.Core.Pipeline;

/// <summary>Escritura de tags con captura del valor ANTERIOR y el ESCRITO por campo (para deshacer con seguridad).</summary>
public static class Tagging
{
    /// <summary>
    /// Escribe los tags de <paramref name="info"/> según <paramref name="fields"/> y overwrite.
    /// Devuelve, por cada campo tocado, su valor anterior y el valor escrito. La carátula solo se marca.
    /// </summary>
    public static Dictionary<string, FieldChange> ApplyTags(ProcessResult info, bool overwrite, FieldFlags fields, TagLib.Picture? coverPic)
    {
        var f = TagLib.File.Create(info.FilePath);
        var tag = f.Tag;
        var log = new Dictionary<string, FieldChange>();

        if (fields.Genre && info.Genre.Length > 0 && (overwrite || string.IsNullOrEmpty(tag.JoinedGenres)))
        {
            var old = tag.Genres.ToList();
            var neu = new[] { info.Genre };
            log["Genre"] = FieldChange.Arr(old, neu);
            tag.Genres = neu;
        }
        if (fields.Album && info.Album.Length > 0 && (overwrite || string.IsNullOrEmpty(tag.Album)))
        {
            log["Album"] = FieldChange.Str(tag.Album ?? "", info.Album);
            tag.Album = info.Album;
        }
        if (fields.Year && Regex.IsMatch(info.Year, @"^\d{4}$") && (overwrite || tag.Year == 0))
        {
            var ny = uint.Parse(info.Year);
            log["Year"] = FieldChange.Num(tag.Year, ny);
            tag.Year = ny;
        }
        if (fields.Artist && info.Artist.Length > 0 && (overwrite || string.IsNullOrEmpty(tag.JoinedPerformers)))
        {
            var old = tag.Performers.ToList();
            var neu = Regex.Split(info.Artist, @",\s*").ToList();
            log["Artist"] = FieldChange.Arr(old, neu);
            tag.Performers = neu.ToArray();
        }
        if (fields.Title && info.Title.Length > 0 && (overwrite || string.IsNullOrEmpty(tag.Title)))
        {
            log["Title"] = FieldChange.Str(tag.Title ?? "", info.Title);
            tag.Title = info.Title;
        }
        if (fields.Bpm && info.Bpm.Length > 0 && Regex.IsMatch(info.Bpm, @"^\d+$") && (overwrite || tag.BeatsPerMinute == 0))
        {
            try
            {
                var nb = uint.Parse(info.Bpm);
                log["Bpm"] = FieldChange.Num(tag.BeatsPerMinute, nb);
                tag.BeatsPerMinute = nb;
            }
            catch { /* algunos formatos no admiten BPM */ }
        }
        if (coverPic != null)
        {
            log["Cover"] = FieldChange.Str(null, "changed");   // la carátula no se restaura al deshacer
            tag.Pictures = new TagLib.IPicture[] { coverPic };
        }

        f.Save();
        f.Dispose();
        return log;
    }

    /// <summary>
    /// ¿Aplicar esta propuesta cambiaría algo de verdad? Si no, no merece la pena enseñarla: en una
    /// biblioteca ya ordenada la lista se llena de filas que dejan el archivo exactamente igual.
    ///
    /// Replica las mismas condiciones que <see cref="ApplyTags"/>: un campo solo cuenta si se iba a
    /// escribir. Con "Sobrescribir" desactivado, una diferencia en un campo que YA tiene valor no se
    /// llega a aplicar, así que tampoco es un cambio.
    ///
    /// Va aquí, junto a ApplyTags, para que las dos reglas se lean de un vistazo y no se separen.
    /// </summary>
    public static bool WouldChange(ProcessResult info, Track actual, bool overwrite, FieldFlags fields)
    {
        // Renombrado: lo que se compara es el nombre, no la ruta.
        var nombreActual = Path.GetFileName(actual.FilePath);
        if (info.New.Length > 0 && !string.Equals(info.New.Trim(), nombreActual, StringComparison.Ordinal))
            return true;

        if (Escribe(fields.Title, info.Title, actual.Title, overwrite)
            && !string.Equals(info.Title, actual.Title ?? "", StringComparison.Ordinal)) return true;

        if (Escribe(fields.Artist, info.Artist, actual.Artist, overwrite)
            && !string.Equals(info.Artist, actual.Artist ?? "", StringComparison.Ordinal)) return true;

        if (Escribe(fields.Album, info.Album, actual.Album, overwrite)
            && !string.Equals(info.Album, actual.Album ?? "", StringComparison.Ordinal)) return true;

        if (Escribe(fields.Genre, info.Genre, actual.Genre, overwrite)
            && !string.Equals(info.Genre, actual.Genre ?? "", StringComparison.Ordinal)) return true;

        if (fields.Year && Regex.IsMatch(info.Year, @"^\d{4}$") && (overwrite || actual.Year == 0)
            && uint.Parse(info.Year) != actual.Year) return true;

        if (fields.Bpm && Regex.IsMatch(info.Bpm, @"^\d+$") && (overwrite || actual.Bpm == 0)
            && uint.Parse(info.Bpm) != actual.Bpm) return true;

        return false;
    }

    /// <summary>Si ese campo se llegaría a escribir, con las mismas reglas que ApplyTags.</summary>
    private static bool Escribe(bool activado, string propuesto, string? actual, bool overwrite)
        => activado && propuesto.Length > 0 && (overwrite || string.IsNullOrEmpty(actual));
}
