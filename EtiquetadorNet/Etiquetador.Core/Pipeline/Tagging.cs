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
}
