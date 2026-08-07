using System.Text.RegularExpressions;

namespace Etiquetador.Core;

/// <summary>Valores de tag de un archivo (para el editor manual).</summary>
public sealed record TagValues(string Title, string Artist, string Album, string Genre, uint Year, uint Bpm, string Comment);

/// <summary>Lectura/escritura manual de tags de un archivo (editor). Escribe exactamente lo indicado.</summary>
public static class TagEditor
{
    public static TagValues Read(string path)
    {
        try
        {
            using var f = TagLib.File.Create(path);
            return new TagValues(
                f.Tag.Title ?? "",
                string.Join(", ", f.Tag.Performers ?? System.Array.Empty<string>()),   // coma como separador (coherente con la app)
                f.Tag.Album ?? "",
                f.Tag.JoinedGenres ?? "",
                f.Tag.Year,
                f.Tag.BeatsPerMinute,
                f.Tag.Comment ?? "");
        }
        catch { return new TagValues("", "", "", "", 0, 0, ""); }
    }

    /// <summary>Escribe los tags tal cual (siempre sobrescribe). Vacíos = se limpian.</summary>
    public static void Write(string path, string? title, string? artist, string? album,
        string? genre, uint year, uint bpm, string? comment)
    {
        using var f = TagLib.File.Create(path);
        f.Tag.Title = string.IsNullOrEmpty(title) ? null : title;
        f.Tag.Performers = SplitArtists(artist);
        f.Tag.Album = string.IsNullOrEmpty(album) ? null : album;
        f.Tag.Genres = string.IsNullOrWhiteSpace(genre) ? System.Array.Empty<string>() : new[] { genre };
        f.Tag.Year = year;
        f.Tag.BeatsPerMinute = bpm;
        f.Tag.Comment = string.IsNullOrEmpty(comment) ? null : comment;
        f.Save();
    }

    private static string[] SplitArtists(string? a)
        => string.IsNullOrWhiteSpace(a) ? System.Array.Empty<string>() : Regex.Split(a, @",\s*");

    /// <summary>Escribe solo BPM y clave musical (para importar de rekordbox). Devuelve true si tocó algo.</summary>
    public static bool WriteBpmKey(string path, uint bpm, string? key, bool overwrite)
        => ImportBpmKey(path, bpm, key, overwrite).Count > 0;

    /// <summary>
    /// Escribe BPM y clave (InitialKey) devolviendo los cambios por campo (valor anterior + escrito),
    /// para poder anotarlos en el manifiesto reversible. Vacío si no tocó nada.
    /// </summary>
    public static Dictionary<string, Pipeline.FieldChange> ImportBpmKey(string path, uint bpm, string? key, bool overwrite)
    {
        using var f = TagLib.File.Create(path);
        var log = new Dictionary<string, Pipeline.FieldChange>();
        if (bpm > 0 && (overwrite || f.Tag.BeatsPerMinute == 0))
        {
            log["Bpm"] = Pipeline.FieldChange.Num(f.Tag.BeatsPerMinute, bpm);
            f.Tag.BeatsPerMinute = bpm;
        }
        if (!string.IsNullOrWhiteSpace(key) && (overwrite || string.IsNullOrWhiteSpace(f.Tag.InitialKey)))
        {
            log["Key"] = Pipeline.FieldChange.Str(f.Tag.InitialKey ?? "", key);
            f.Tag.InitialKey = key;
        }
        if (log.Count > 0) f.Save();
        return log;
    }
}
