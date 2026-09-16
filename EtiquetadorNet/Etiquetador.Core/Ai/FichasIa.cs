using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Providers;

namespace Etiquetador.Core.Ai;

/// <summary>Lo que la IA propone para la ficha de una canción. Campos vacíos = no propone nada para ese campo.</summary>
public sealed record PropuestaFicha(string Ruta, string Idioma, TipoVoz Voz, string Origen)
{
    public bool HayAlgo => Idioma.Length > 0 || Voz != TipoVoz.SinIndicar;

    /// <summary>Los cambios a aplicar si se acepta. Nunca incluye un campo que la propuesta no rellena.</summary>
    public CambiosFicha Cambios() => new()
    {
        Idioma = Idioma.Length > 0 ? Idioma : null,
        Voz = Voz != TipoVoz.SinIndicar ? Voz : null,
    };
}

/// <summary>
/// Propone idioma y voz para las fichas que no los tienen.
///
/// SOLO ESOS DOS, y es una decisión medida (llama3.2 3B, canciones conocidas):
///   · Idioma: 11 de 14. Los fallos siguen un patrón, deduce por la nacionalidad del artista
///     (Daft Punk → francés, Anitta → portugués aunque «Envolver» se canta en español).
///   · Voz: lo que dice de verdad es si hay letra o no, y eso ya sale del idioma.
///   · Energía: respondió 8 a las doce canciones, de Sandstorm a Camela. Proponerla sería inventar.
///   · «¿Estás seguro?»: respondió que sí siempre, también con las que falló. No sirve de filtro.
///
/// Por eso nada se guarda sin que el usuario lo acepte, y nunca se toca un campo ya rellenado: la
/// IA solo propone donde hay un hueco.
/// </summary>
public static class FichasIa
{
    public const string Sistema =
        "Dices en que idioma se canta una cancion. Responde SOLO JSON: {\"idioma\":\"XX\"} donde XX es el codigo de dos " +
        "letras del idioma principal de la letra (por ejemplo ES para español, EN para ingles, FR para frances, PT para " +
        "portugues). Si la cancion no tiene letra, responde {\"idioma\":\"--\"}.";

    private static readonly HashSet<string> IdiomasValidos = new(StringComparer.Ordinal)
    { "ES", "EN", "PT", "FR", "IT", "DE", "CA", "NL" };

    // Lo que el propio nombre deja claro no hace falta preguntarlo.
    private static readonly Regex NombreInstrumental = new(@"\binstrumental\b|\bbeat\s*tape\b", RegexOptions.IgnoreCase);

    /// <summary>Le falta idioma o voz. Las demás no se proponen.</summary>
    public static bool NecesitaPropuesta(FichaDj? f) => f == null || f.Idioma.Trim().Length == 0 || f.Voz == TipoVoz.SinIndicar;

    /// <summary>Lo que se le pregunta: artista y título, que es lo único con lo que el modelo puede deducir algo.</summary>
    public static string Peticion(Track t)
    {
        var artista = (t.Artist ?? "").Trim();
        var titulo = Descriptors.CleanTitle(t.Title);
        if (titulo.Length == 0) return Path.GetFileNameWithoutExtension(t.FilePath);
        return artista.Length > 0 ? $"{artista} - {titulo}" : titulo;
    }

    /// <summary>Clave de caché: la misma canción en dos archivos no se pregunta dos veces.</summary>
    public static string ClaveCache(Track t) => "idioma:" + VocabularioBiblioteca.Clave(Peticion(t));

    /// <summary>Sin preguntar a nadie, cuando el nombre lo dice. Null si hay que preguntar.</summary>
    public static PropuestaFicha? PorNombre(Track t, FichaDj? f)
    {
        if (!NombreInstrumental.IsMatch(t.FileName + " " + t.Title)) return null;
        return Recortar(new PropuestaFicha(t.FilePath, "", TipoVoz.Instrumental, "Nombre del archivo"), f);
    }

    /// <summary>La propuesta de la IA, validada. Lo que no sea un código de idioma reconocible se ignora.</summary>
    public static PropuestaFicha Interpretar(Track t, FichaDj? f, JsonNode? json)
    {
        var idioma = J.S(J.P(json, "idioma")).Trim().ToUpperInvariant();
        PropuestaFicha p;
        if (idioma == "--") p = new PropuestaFicha(t.FilePath, "", TipoVoz.Instrumental, "IA");
        else if (IdiomasValidos.Contains(idioma)) p = new PropuestaFicha(t.FilePath, idioma, TipoVoz.Vocal, "IA");
        else p = new PropuestaFicha(t.FilePath, "", TipoVoz.SinIndicar, "IA");
        return Recortar(p, f);
    }

    /// <summary>Quita de la propuesta lo que la ficha ya tiene: lo que puso el usuario no se discute.</summary>
    public static PropuestaFicha Recortar(PropuestaFicha p, FichaDj? f)
    {
        if (f == null) return p;
        return p with
        {
            Idioma = f.Idioma.Trim().Length > 0 ? "" : p.Idioma,
            Voz = f.Voz != TipoVoz.SinIndicar ? TipoVoz.SinIndicar : p.Voz,
        };
    }
}
