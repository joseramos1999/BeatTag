using Etiquetador.Core.Pipeline;

namespace Etiquetador.Core.Dj;

/// <summary>
/// Lo que una herramienta de reglas quiere cambiar en las etiquetas de un archivo, junto con lo que
/// vio al proponerlo. Solo se escribe lo que SIGUE como se vio: si el usuario lo cambió después (en el
/// Editor, en rekordbox o en Mp3tag), se respeta lo suyo.
/// </summary>
public sealed record CambioEtiquetas(string Ruta)
{
    /// <summary>El álbum tal como estaba al proponer (null = no se toca el álbum).</summary>
    public string? AlbumVisto { get; init; }

    /// <summary>El álbum que queda ("" = vacío).</summary>
    public string AlbumNuevo { get; init; } = "";

    /// <summary>El artista tal como estaba al proponer (como lo lee la biblioteca: los valores unidos por «; »).</summary>
    public string? ArtistaVisto { get; init; }

    /// <summary>El artista que se escribe (null = no se toca el artista).</summary>
    public string? ArtistaNuevo { get; init; }

    /// <summary>El género que se pone, solo si el archivo sigue sin género (null = no se toca).</summary>
    public string? GeneroNuevo { get; init; }

    /// <summary>
    /// Lo que se quita del álbum y del artista se guarda en el comentario («[Pack: …]», «[Editor: …]»)
    /// en vez de borrarse.
    /// </summary>
    public bool GuardarEnComentario { get; init; }
}

/// <summary>Lo que hay que escribir en un archivo y cuántos campos se han dejado como estaban porque cambiaron después de proponerlos.</summary>
public sealed record CalculoEtiquetas(Dictionary<string, FieldChange> Cambios, int Respetados);

/// <summary>
/// El cálculo y la escritura de un <see cref="CambioEtiquetas"/>, separados para poder anotar el
/// cambio en el historial de deshacer ANTES de tocar el archivo: primero <see cref="Calcular"/> (solo
/// lee), luego se anota, y solo entonces <see cref="Poner"/>. Las claves de los cambios son las que
/// entiende UndoEngine («Album», «Artist», «Genre», «Comment»), así que «Deshacer última» los devuelve,
/// y solo si siguen como los dejó la aplicación.
/// </summary>
public static class EtiquetasPorReglas
{
    /// <summary>Qué hay que escribir en el archivo de <paramref name="tag"/> para aplicar <paramref name="c"/>. No modifica nada.</summary>
    public static CalculoEtiquetas Calcular(TagLib.Tag tag, CambioEtiquetas c)
    {
        var cambios = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
        var respetados = 0;
        var bloques = new List<string>();

        if (c.AlbumVisto != null)
        {
            var actual = tag.Album ?? "";
            var nuevo = c.AlbumNuevo.Trim();
            if (!string.Equals(actual, c.AlbumVisto, StringComparison.Ordinal)) respetados++;
            else if (!string.Equals(actual, nuevo, StringComparison.Ordinal))
            {
                cambios["Album"] = FieldChange.Str(actual, nuevo);
                if (c.GuardarEnComentario && actual.Trim().Length > 0) bloques.Add(LimpiezaPacks.BloquePack(actual));
            }
        }

        if (c.ArtistaNuevo is { } artista && artista.Trim().Length > 0)
        {
            var antes = tag.Performers ?? Array.Empty<string>();
            var actual = tag.JoinedPerformers ?? "";
            var nuevo = new[] { artista.Trim() };
            if (c.ArtistaVisto != null && !string.Equals(actual, c.ArtistaVisto, StringComparison.Ordinal)) respetados++;
            else if (!antes.SequenceEqual(nuevo, StringComparer.Ordinal))
            {
                // UN solo valor, como lo trae el nombre del archivo («Feid, Myke Towers»): así se lee
                // igual en BeatTag, rekordbox y Serato, y no se parten nombres con coma.
                cambios["Artist"] = FieldChange.Arr(antes, nuevo);
                if (c.GuardarEnComentario && actual.Trim().StartsWith('@')) bloques.Add(LimpiezaPacks.BloqueEditor(actual));
            }
        }

        if (c.GeneroNuevo is { } genero && genero.Trim().Length > 0)
        {
            var antes = tag.Genres ?? Array.Empty<string>();
            // Nunca se pisa un género: si ya tiene uno (puesto después de proponer), se respeta.
            if (antes.Any(g => !string.IsNullOrWhiteSpace(g))) respetados++;
            else cambios["Genre"] = FieldChange.Arr(antes, Ai.GenerosIa.ValoresParaEscribir(genero));
        }

        if (bloques.Count > 0)
        {
            var antes = tag.Comment ?? "";
            var despues = bloques.Aggregate(antes, LimpiezaPacks.ComentarioCon);
            if (!string.Equals(antes, despues, StringComparison.Ordinal)) cambios["Comment"] = FieldChange.Str(antes, despues);
        }
        return new CalculoEtiquetas(cambios, respetados);
    }

    /// <summary>Escribe en la etiqueta lo calculado (sin guardar el archivo).</summary>
    public static void Poner(TagLib.Tag tag, IReadOnlyDictionary<string, FieldChange> cambios)
    {
        foreach (var (campo, c) in cambios)
            switch (campo)
            {
                case "Album": tag.Album = string.IsNullOrEmpty(c.NewStr) ? null : c.NewStr; break;
                case "Comment": tag.Comment = string.IsNullOrEmpty(c.NewStr) ? null : c.NewStr; break;
                case "Artist": tag.Performers = (c.NewArr ?? new List<string>()).ToArray(); break;
                case "Genre": tag.Genres = (c.NewArr ?? new List<string>()).ToArray(); break;
            }
    }

    /// <summary>
    /// Hay que volver a leer el archivo para anotar lo que de verdad ha quedado escrito.
    ///
    /// MEDIDO con TagLib# 2.3 en un MP3 con ID3v2.3: al leer, separa los valores por «/», así que
    /// «AC/DC» escrito como artista vuelve como «AC» y «DC», y «Dance/Electrónica» como dos géneros.
    /// Deshacer compara lo que hay en el archivo con lo anotado, y con eso no coincidiría nunca: daría
    /// el cambio por editado a mano y no lo devolvería. Solo pasa con la barra, así que solo entonces
    /// se relee.
    /// </summary>
    public static bool HayQueReleer(IReadOnlyDictionary<string, FieldChange> cambios)
        => cambios.Values.Any(c => c.Kind == FieldKind.Arr && (c.NewArr ?? new List<string>()).Any(v => v.Contains('/')));

    /// <summary>
    /// Lo que se anota en el historial: los cambios, con cada lista como la VA A LEER el archivo
    /// después de escribirla. Lo que se escribe sigue siendo <paramref name="cambios"/>.
    ///
    /// POR QUÉ. Con un valor con «/» en ID3v2.3 («Hip-Hop/Rap», «R&amp;B/Soul», «AC/DC»), anotar lo que se
    /// pide y corregirlo después con un segundo registro dejaba DOS líneas por archivo: el historial
    /// contaba el doble de cambios, «Deshacer las marcadas» podía archivar la primera como deshecha
    /// (editada a mano) con el género aún puesto, y el resumen de deshacer daba el archivo por revertido
    /// y por cambiado a mano a la vez. Se calcula escribiendo una etiqueta ID3v2 de la misma versión
    /// en memoria y leyéndola, así que es exactamente lo que hará TagLib con el archivo, y queda una
    /// sola línea. Si aun así no coincidiera (otro formato, otra etiqueta delante), la relectura tras
    /// guardar lo corrige como antes (<see cref="Corregidos"/>).
    /// </summary>
    public static Dictionary<string, FieldChange> ComoSeLeera(TagLib.File archivo, IReadOnlyDictionary<string, FieldChange> cambios)
    {
        var res = new Dictionary<string, FieldChange>(cambios, StringComparer.Ordinal);
        if (!HayQueReleer(cambios) || archivo.GetTag(TagLib.TagTypes.Id3v2, false) is not TagLib.Id3v2.Tag id3) return res;
        foreach (var (campo, c) in cambios)
        {
            if (c.Kind != FieldKind.Arr || campo is not ("Artist" or "Genre")) continue;
            if (ListaReleidaId3v2(id3.Version, campo, c.NewArr ?? new List<string>()) is { } leida)
                res[campo] = FieldChange.Arr(c.OldArr ?? new List<string>(), leida);
        }
        return res;
    }

    /// <summary>
    /// Cómo se lee una lista de artistas («Artist») o de géneros («Genre») escrita en una etiqueta
    /// ID3v2 de esa versión. Null si TagLib no sabe escribir esa versión: entonces decide la relectura.
    /// </summary>
    public static string[]? ListaReleidaId3v2(byte version, string campo, IReadOnlyList<string> valores)
    {
        try
        {
            var tag = new TagLib.Id3v2.Tag { Version = version };
            if (campo == "Artist") tag.Performers = valores.ToArray();
            else tag.Genres = valores.ToArray();
            var leida = new TagLib.Id3v2.Tag(tag.Render());
            return campo == "Artist" ? leida.Performers : leida.Genres;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Las listas anotadas que no coinciden con lo que quedó escrito, ya corregidas (vacío si todas
    /// coinciden). Se anotan en un segundo registro, que deshacer procesa primero.
    /// </summary>
    public static Dictionary<string, FieldChange> Corregidos(TagLib.Tag releido, IReadOnlyDictionary<string, FieldChange> escritos)
    {
        var res = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
        foreach (var (campo, c) in escritos)
        {
            if (c.Kind != FieldKind.Arr) continue;
            var ahora = campo switch
            {
                "Artist" => releido.Performers,
                "Genre" => releido.Genres,
                _ => null,
            } ?? Array.Empty<string>();
            if (!ahora.SequenceEqual(c.NewArr ?? new List<string>(), StringComparer.Ordinal))
                res[campo] = FieldChange.Arr(c.OldArr ?? new List<string>(), ahora);
        }
        return res;
    }
}
