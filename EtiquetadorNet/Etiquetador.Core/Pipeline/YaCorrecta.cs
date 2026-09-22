using System.Text.RegularExpressions;
using Etiquetador.Core.Ai;

namespace Etiquetador.Core.Pipeline;

/// <summary>
/// Canciones que Enriquecer no necesita consultar: el nombre del archivo y sus tags ya dicen lo
/// mismo, y los datos que el usuario ha pedido escribir ya están.
///
/// MEDIDO en una biblioteca de 20.216 archivos: en 12.843 el artista y el título de los tags
/// coinciden con el nombre, y 11.945 de ellas tienen además género, año, álbum y portada. Buscarlas
/// en los catálogos solo gasta consultas y tiempo para acabar proponiendo lo que ya tienen.
///
/// Las 898 que coinciden pero les falta algo (628 sin género, 341 sin año) NO se saltan: rellenar
/// eso es precisamente para lo que sirve Enriquecer.
/// </summary>
public static class YaCorrecta
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Lo que separa artistas: «A, B», «A & B», «A; B», «A x B», «A feat. B», «A vs B», «A y B».</summary>
    private static readonly Regex SeparaArtistas =
        new(@"\s*(?:[,;&/+]|\s(?:x|y|and|feat\.?|ft\.?|featuring|vs\.?|with)\s)\s*", IC);

    /// <summary>
    /// El nombre del archivo y los tags dicen la misma canción.
    ///
    /// El título tiene que coincidir entero (sin contar acentos, mayúsculas ni signos). El artista
    /// basta con que los del nombre estén todos en los tags: es muy habitual que el nombre lleve
    /// solo el principal y los tags añadan a los invitados («Basstyler» frente a «Basstyler; Bad
    /// Legs»), y eso es la misma canción bien etiquetada.
    ///
    /// Un nombre sucio -con el record pool, BPM, tonalidad, guiones bajos, un paréntesis cortado-
    /// nunca cuenta: aunque los tags coincidan, a ese archivo le queda limpiar el nombre.
    /// </summary>
    public static bool Concuerda(string rutaArchivo, string? tagArtista, string? tagTitulo)
    {
        var ta = (tagArtista ?? "").Trim();
        var tt = (tagTitulo ?? "").Trim();
        if (ta.Length == 0 || tt.Length == 0) return false;

        var nombre = Path.GetFileNameWithoutExtension(rutaArchivo);
        var m = Regex.Match(nombre, @"^(.+?)\s+[-–]\s+(.+)$");
        if (!m.Success) return false;
        if (RenombradoIa.PareceSucio(rutaArchivo)) return false;

        var tituloNombre = TextUtils.Nk(m.Groups[2].Value);
        if (tituloNombre.Length == 0 || tituloNombre != TextUtils.Nk(tt)) return false;

        var artistaNombre = m.Groups[1].Value;
        if (TextUtils.Nk(artistaNombre) == TextUtils.Nk(ta)) return true;

        var delNombre = Artistas(artistaNombre);
        var deLosTags = Artistas(ta);
        return delNombre.Count > 0 && delNombre.IsSubsetOf(deLosTags);
    }

    /// <summary>
    /// A la canción le falta algo de lo que el usuario ha pedido escribir. Solo cuentan los campos
    /// marcados: si no escribe el álbum, que falte el álbum no es motivo para consultarla.
    /// </summary>
    public static bool LeFaltaAlgo(Track t, FieldFlags campos)
        => (campos.Genre && string.IsNullOrWhiteSpace(t.Genre))
        || (campos.Year && t.Year == 0)
        || (campos.Album && string.IsNullOrWhiteSpace(t.Album))
        || (campos.Bpm && t.Bpm == 0);

    /// <summary>Se puede saltar: nombre y tags coinciden y no le falta nada de lo que se escribe.</summary>
    public static bool SeSalta(Track t, FieldFlags campos)
        => Concuerda(t.FilePath, t.Artist, t.Title) && !LeFaltaAlgo(t, campos);

    private static HashSet<string> Artistas(string texto)
        => SeparaArtistas.Split(texto)
                         .Select(TextUtils.Nk)
                         .Where(a => a.Length > 0)
                         .ToHashSet(StringComparer.Ordinal);
}
