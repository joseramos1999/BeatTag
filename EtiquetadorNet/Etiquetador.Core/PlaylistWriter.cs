using System.Text;

namespace Etiquetador.Core;

/// <summary>Una entrada de lista de reproducción.</summary>
public sealed record PlaylistItem(string FilePath, string Artist, string Title, int DurationSeconds);

/// <summary>
/// Escribe listas en M3U8, que es lo que entienden rekordbox, Engine DJ, Serato, VLC y casi
/// cualquier reproductor. Sirve para llevarse fuera lo que se ha filtrado en una pestaña.
///
/// M3U8 es M3U en UTF-8. Se usa esa variante y no M3U a secas porque los nombres traen acentos,
/// eñes y símbolos que en la codificación antigua se rompen.
/// </summary>
public static class PlaylistWriter
{
    /// <summary>
    /// Genera el contenido de la lista. Las rutas van ABSOLUTAS: una lista de DJ se abre desde
    /// cualquier sitio, y las relativas dejarían de resolver en cuanto se moviera el archivo.
    /// </summary>
    public static string Build(IEnumerable<PlaylistItem> items)
    {
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        foreach (var it in items)
        {
            if (string.IsNullOrWhiteSpace(it.FilePath)) continue;

            // #EXTINF lleva la duración en segundos y el rótulo "artista - título". Si no hay
            // artista se pone solo el título, que es lo que esperan los reproductores.
            var rotulo = string.IsNullOrWhiteSpace(it.Artist)
                ? (it.Title ?? "")
                : $"{it.Artist} - {it.Title}";
            if (string.IsNullOrWhiteSpace(rotulo))
                rotulo = Path.GetFileNameWithoutExtension(it.FilePath);

            var dur = it.DurationSeconds > 0 ? it.DurationSeconds : -1;
            sb.Append("#EXTINF:").Append(dur).Append(',').Append(Limpiar(rotulo)).Append('\n');
            sb.Append(it.FilePath).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Escribe la lista. Devuelve "" si fue bien, o el motivo del fallo.</summary>
    public static string Write(string path, IEnumerable<PlaylistItem> items)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Sin BOM: algunos reproductores antiguos lo interpretan como parte de la primera línea.
            File.WriteAllText(path, Build(items), new UTF8Encoding(false));
            return "";
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>Un salto de línea dentro del rótulo partiría la entrada en dos y rompería la lista.</summary>
    private static string Limpiar(string s)
        => s.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
