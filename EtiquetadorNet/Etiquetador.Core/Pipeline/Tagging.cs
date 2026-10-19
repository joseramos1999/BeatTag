using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Etiquetador.Core.Pipeline;

/// <summary>Escritura de tags con captura del valor ANTERIOR y el ESCRITO por campo (para deshacer con seguridad).</summary>
public static class Tagging
{
    /// <summary>
    /// Escribe los tags de <paramref name="info"/> según <paramref name="fields"/> y overwrite.
    /// Devuelve, por cada campo tocado, su valor anterior y el valor escrito.
    ///
    /// La carátula anterior se guarda como archivo en <paramref name="carpetaPortadas"/> (la carpeta
    /// «portadas» del directorio de deshacer) y su ruta queda anotada: ver <see cref="SustituirCaratula"/>.
    /// </summary>
    public static Dictionary<string, FieldChange> ApplyTags(ProcessResult info, bool overwrite, FieldFlags fields, TagLib.Picture? coverPic,
        string? carpetaPortadas = null)
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
        if (fields.Year && TextUtils.Rx(@"^\d{4}$").IsMatch(info.Year) && (overwrite || tag.Year == 0))
        {
            var ny = uint.Parse(info.Year);
            log["Year"] = FieldChange.Num(tag.Year, ny);
            tag.Year = ny;
        }
        if (fields.Artist && info.Artist.Length > 0 && (overwrite || string.IsNullOrEmpty(tag.JoinedPerformers)))
        {
            var old = tag.Performers.ToList();
            var neu = TextUtils.Rx(@",\s*").Split(info.Artist).ToList();
            log["Artist"] = FieldChange.Arr(old, neu);
            tag.Performers = neu.ToArray();
        }
        if (fields.Title && info.Title.Length > 0 && (overwrite || string.IsNullOrEmpty(tag.Title)))
        {
            log["Title"] = FieldChange.Str(tag.Title ?? "", info.Title);
            tag.Title = info.Title;

            // Quién firma la versión, en su propio campo (TPE4 en ID3, REMIXER en Vorbis y MP4):
            // rekordbox y Serato lo enseñan en su columna y dejan filtrar y ordenar por él. Hasta
            // ahora solo acababa como texto dentro del título, y para reunir todas las ediciones de
            // «Miguel Roma» o «Alex Cortes» había que buscar dentro de los títulos. Va con el título
            // porque es la misma información: si el título no se escribe, esto tampoco.
            // Sale de RemixerTag y no de Remixer: lo que guardó en la caché una versión anterior no
            // lo trae, y su Remixer en bruto no debe acabar en el tag (ver ProcessResult.RemixerTag).
            var remixer = info.RemixerTag ?? "";
            if (remixer.Length > 0 && (overwrite || string.IsNullOrEmpty(tag.RemixedBy)))
            {
                var antes = tag.RemixedBy ?? "";
                tag.RemixedBy = remixer;
                // Hay formatos sin ese campo: solo se anota lo que de verdad ha quedado escrito.
                if ((tag.RemixedBy ?? "") == remixer) log["Remixer"] = FieldChange.Str(antes, remixer);
            }
        }
        if (fields.Bpm && info.Bpm.Length > 0 && TextUtils.Rx(@"^\d+$").IsMatch(info.Bpm) && EscribeBpm(info, tag.BeatsPerMinute, overwrite))
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
            var cambio = SustituirCaratula(tag, coverPic, overwrite, carpetaPortadas);
            if (cambio != null) log["Cover"] = cambio;
        }

        f.Save();
        f.Dispose();
        return log;
    }

    /// <summary>Valor que se anota como escrito en el campo «Cover» del registro de deshacer.</summary>
    public const string CaratulaSustituida = "sustituida";

    /// <summary>
    /// Pone <paramref name="nueva"/> como carátula frontal. Devuelve el cambio para el registro de
    /// deshacer, o null si no se ha tocado nada.
    ///
    /// Antes se sustituían TODAS las imágenes del archivo aunque «Sobrescribir» estuviera desactivado,
    /// y deshacer no la devolvía: un DJ con portadas propias que aplicaba 2.000 canciones con
    /// carátula del catálogo las perdía todas. Ahora:
    ///   · solo se pone si «Sobrescribir» está activado o el archivo no tenía carátula;
    ///   · solo se cambia la frontal (la que lleva ese tipo o, si ninguna lo lleva, la primera de
    ///     tipo «Otro», que es como la guardan muchos programas); contraportada, artista y demás se
    ///     quedan;
    ///   · la anterior se guarda ANTES como archivo, con el SHA-1 de sus bytes por nombre, y el
    ///     cambio se anota con su ruta absoluta (o "" si no había carátula) para que deshacer la
    ///     restaure. Si no se puede guardar, la carátula no se toca: sin copia no hay marcha atrás.
    /// </summary>
    public static FieldChange? SustituirCaratula(TagLib.Tag tag, TagLib.IPicture nueva, bool overwrite, string? carpetaPortadas)
    {
        var imagenes = (tag.Pictures ?? Array.Empty<TagLib.IPicture>()).ToList();
        var idx = IndiceCaratula(imagenes);
        if (idx >= 0 && !overwrite) return null;

        var anterior = "";
        if (idx >= 0)
        {
            anterior = GuardarCaratula(imagenes[idx], carpetaPortadas);
            if (anterior.Length == 0) return null;
        }

        // Copia propia: la imagen descargada se comparte entre todos los archivos de la tirada.
        var pic = new TagLib.Picture(nueva) { Type = TagLib.PictureType.FrontCover };
        if (idx >= 0) imagenes[idx] = pic;
        else imagenes.Insert(0, pic);
        tag.Pictures = imagenes.ToArray();
        return FieldChange.Str(anterior, CaratulaSustituida);
    }

    /// <summary>Posición de la carátula frontal: la de tipo «FrontCover» o, si no hay, la primera de tipo «Otro». -1 si no hay.</summary>
    public static int IndiceCaratula(IReadOnlyList<TagLib.IPicture> imagenes)
    {
        for (int i = 0; i < imagenes.Count; i++)
            if (imagenes[i].Type == TagLib.PictureType.FrontCover) return i;
        for (int i = 0; i < imagenes.Count; i++)
            if (imagenes[i].Type == TagLib.PictureType.Other) return i;
        return -1;
    }

    /// <summary>
    /// Guarda la imagen en la carpeta dada con un nombre único (SHA-1 de los bytes + extensión según
    /// su contenido o su tipo MIME) y devuelve la ruta absoluta, o "" si no se ha podido. Si ya existe
    /// un archivo con esos bytes se reutiliza: la misma portada en cien canciones se guarda una vez.
    /// </summary>
    public static string GuardarCaratula(TagLib.IPicture imagen, string? carpeta)
    {
        if (string.IsNullOrEmpty(carpeta)) return "";
        try
        {
            var datos = imagen.Data?.Data ?? Array.Empty<byte>();
            if (datos.Length == 0) return "";
            var nombre = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(datos)).ToLowerInvariant()
                       + ExtensionImagen(datos, imagen.MimeType);
            Directory.CreateDirectory(carpeta);
            var ruta = Path.GetFullPath(Path.Combine(carpeta, nombre));
            if (!File.Exists(ruta)) File.WriteAllBytes(ruta, datos);
            return ruta;
        }
        catch { return ""; }
    }

    /// <summary>Extensión por el contenido (los primeros bytes mandan: el MIME declarado a veces miente) y si no, por el MIME.</summary>
    private static string ExtensionImagen(byte[] d, string? mime)
    {
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return ".jpg";
        if (d.Length >= 4 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return ".png";
        if (d.Length >= 3 && d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46) return ".gif";
        if (d.Length >= 2 && d[0] == 0x42 && d[1] == 0x4D) return ".bmp";
        if (d.Length >= 12 && d[0] == 0x52 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x46
            && d[8] == 0x57 && d[9] == 0x45 && d[10] == 0x42 && d[11] == 0x50) return ".webp";
        return (mime ?? "").ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/webp" => ".webp",
            _ => ".jpg",
        };
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

        if (fields.Year && TextUtils.Rx(@"^\d{4}$").IsMatch(info.Year) && (overwrite || actual.Year == 0)
            && uint.Parse(info.Year) != actual.Year) return true;

        if (fields.Bpm && TextUtils.Rx(@"^\d+$").IsMatch(info.Bpm) && EscribeBpm(info, actual.Bpm, overwrite)
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
        if (fields.Year && TextUtils.Rx(@"^\d{4}$").IsMatch(info.Year) && (overwrite || actual.Year == 0)
            && uint.Parse(info.Year) != actual.Year)
            p += actual.Year == 0 ? 2.0 : 1.5;

        if (fields.Bpm && TextUtils.Rx(@"^\d+$").IsMatch(info.Bpm) && EscribeBpm(info, actual.Bpm, overwrite)
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
        => TextUtils.Rx(@"[^a-z0-9]+").Split(TextUtils.ToAscii(s ?? "").ToLowerInvariant())
                .Where(w => w.Length > 1)
                .ToHashSet();

    /// <summary>
    /// Si el BPM se llegaría a escribir. Un BPM que ya tiene el archivo no se sustituye por el del
    /// catálogo ni con «Sobrescribir» activado: el del archivo suele salir de analizar el audio
    /// (rekordbox, Serato), y el del catálogo es el de la pista que se encontró, que en una edición de
    /// DJ ni siquiera es la misma. Sí se sustituye por el que declara el nombre o por el que el usuario
    /// haya escrito a mano en la tabla (entonces ya no coincide con el del catálogo).
    /// </summary>
    private static bool EscribeBpm(ProcessResult info, uint actual, bool overwrite)
    {
        if (actual == 0) return true;
        if (!overwrite) return false;
        return info.BpmCatalogo.Length == 0 || info.Bpm != info.BpmCatalogo;
    }

    /// <summary>Si ese campo se llegaría a escribir, con las mismas reglas que ApplyTags.</summary>
    private static bool Escribe(bool activado, string propuesto, string? actual, bool overwrite)
        => activado && propuesto.Length > 0 && (overwrite || string.IsNullOrEmpty(actual));
}
