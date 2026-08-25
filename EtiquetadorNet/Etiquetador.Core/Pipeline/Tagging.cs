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

    /// <summary>
    /// Cuánto cambia el archivo si se aplica la propuesta, de 0 a 10. Sirve para apartar de la
    /// lista los retoques cosméticos y dejar a la vista lo que de verdad merece una revisión.
    ///
    /// Cómo se reparte:
    ///   · Nombre del archivo — hasta 6 puntos, según lo distinto que sea, de modo que un renombrado
    ///     completo supera el umbral por sí solo. Corregir un acento o un
    ///     espacio apenas suma; pasar de "pista01.mp3" a "Artista - Título.mp3" suma casi todo.
    ///   · Rellenar un tag vacío — 2 puntos. Es información nueva, lo que más aporta.
    ///   · Corregir un tag que ya tenía valor — hasta 1,5, según lo distinto que sea el valor.
    ///
    /// Solo cuentan los campos que se llegarían a escribir, con las mismas condiciones que
    /// <see cref="ApplyTags"/>: un campo apagado en las opciones, o uno que ya tiene valor con
    /// "Sobrescribir" desactivado, no suman porque no se van a tocar.
    /// </summary>
    public static double ChangeIndex(ProcessResult info, Track actual, bool overwrite, FieldFlags fields)
    {
        double p = 0;

        // El nombre es lo más visible y lo que más molesta si está mal, así que pesa lo que más.
        // Se compara en crudo, no normalizado: así un acento o una mayúscula suman algo, poco.
        var nombreActual = Path.GetFileName(actual.FilePath);
        if (info.New.Length > 0 && !string.Equals(info.New.Trim(), nombreActual, StringComparison.Ordinal))
            p += 6.0 * Distancia(nombreActual, info.New.Trim());

        p += PuntosTag(fields.Title, info.Title, actual.Title, overwrite);
        p += PuntosTag(fields.Artist, info.Artist, actual.Artist, overwrite);
        p += PuntosTag(fields.Album, info.Album, actual.Album, overwrite);
        p += PuntosTag(fields.Genre, info.Genre, actual.Genre, overwrite);

        // Año y BPM son un número: o está o no está, no hay medias tintas.
        if (fields.Year && Regex.IsMatch(info.Year, @"^\d{4}$") && (overwrite || actual.Year == 0)
            && uint.Parse(info.Year) != actual.Year)
            p += actual.Year == 0 ? 2.0 : 1.5;

        if (fields.Bpm && Regex.IsMatch(info.Bpm, @"^\d+$") && (overwrite || actual.Bpm == 0)
            && uint.Parse(info.Bpm) != actual.Bpm)
            p += actual.Bpm == 0 ? 2.0 : 1.5;

        return Math.Round(Math.Min(10.0, p), 1);
    }

    /// <summary>Puntos de un tag: 2 si se rellena uno vacío, hasta 1,5 si se corrige uno existente.</summary>
    private static double PuntosTag(bool activado, string propuesto, string? actual, bool overwrite)
    {
        if (!Escribe(activado, propuesto, actual, overwrite)) return 0;
        if (string.IsNullOrEmpty(actual)) return 2.0;                     // información nueva
        if (string.Equals(propuesto, actual, StringComparison.Ordinal)) return 0;
        return 1.5 * Distancia(actual, propuesto);
    }

    /// <summary>
    /// Lo distinto que son dos textos, de 0 (iguales) a 1 (nada que ver).
    ///
    /// Se miran DOS cosas y se toma la mayor, porque cada una ve lo que a la otra se le escapa:
    ///
    ///   · Caracteres (Jaro-Winkler). Capta las erratas y los retoques finos, pero no se entera de
    ///     que falte una palabra: si las letras son casi las mismas, le da igual.
    ///   · Palabras. Capta lo estructural: añadir "(Extended)" o el artista invitado cambia poco el
    ///     texto y mucho el significado. Medido sobre renombrados reales, sin esto un
    ///     "Limbo" -> "Limbo (Extended)" se quedaba en 0,2 y se filtraba.
    ///
    /// La similitud de Jaro-Winkler no llega a 0 con textos reales: dos nombres sin ninguna
    /// relación rondan 0,3, no 0. Por eso se reescala: a partir de 0,45 de diferencia se considera
    /// "otro nombre" y puntúa el máximo, y por debajo de 0,05 -un acento, un espacio- no puntúa.
    /// </summary>
    private static double Distancia(string a, string b)
    {
        var porCaracter = 1.0 - Matching.JaroWinkler(a ?? "", b ?? "");
        var d = Math.Max(porCaracter, PorPalabras(a, b));

        const double minimo = 0.05, tope = 0.45;
        return Math.Clamp((d - minimo) / (tope - minimo), 0.0, 1.0);
    }

    /// <summary>Proporción de palabras que no comparten los dos textos.</summary>
    private static double PorPalabras(string? a, string? b)
    {
        var pa = Palabras(a);
        var pb = Palabras(b);
        if (pa.Count == 0 && pb.Count == 0) return 0;

        var comunes = pa.Intersect(pb).Count();
        var union = pa.Union(pb).Count();
        return union == 0 ? 0 : 1.0 - (double)comunes / union;
    }

    private static HashSet<string> Palabras(string? s)
        => Regex.Split(TextUtils.ToAscii(s ?? "").ToLowerInvariant(), @"[^a-z0-9]+")
                .Where(w => w.Length > 1)
                .ToHashSet();

    /// <summary>Si ese campo se llegaría a escribir, con las mismas reglas que ApplyTags.</summary>
    private static bool Escribe(bool activado, string propuesto, string? actual, bool overwrite)
        => activado && propuesto.Length > 0 && (overwrite || string.IsNullOrEmpty(actual));
}
