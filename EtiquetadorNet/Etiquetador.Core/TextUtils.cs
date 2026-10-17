using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Etiquetador.Core;

/// <summary>
/// Utilidades de texto puras portadas 1:1 desde Enriquecer-App.ps1.
/// Nada de I/O ni de red: todo determinista y testeable (contrato = pruebas Pester).
/// </summary>
public static class TextUtils
{
    // Mapa de caracteres latinos no descomponibles por FormD (equivalente al $map de To-Ascii).
    // La «ı» turca sin punto tampoco se descompone: sin ella, un nombre turco perdía letras al pasar
    // a ASCII y acababa conservándose entero en Unicode (ver AsciiSinPerderLetras).
    private static readonly (char From, string To)[] AsciiMap =
    {
        ('ø', "o"), ('Ø', "O"), ('ł', "l"), ('Ł', "L"),
        ('đ', "d"), ('Đ', "D"), ('æ', "ae"), ('Æ', "AE"),
        ('œ', "oe"), ('Œ', "OE"), ('ß', "ss"), ('þ', "th"),
        ('ð', "d"), ('ı', "i"),
    };

    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex> RxCache = new();

    /// <summary>
    /// La expresión regular de un patrón FIJO, construida una sola vez y guardada para siempre.
    ///
    /// MEDIDO sobre 13.545 nombres reales de una biblioteca: FileNameParser.Parse tardaba 5,2 s en
    /// total y con las expresiones guardadas aquí tarda 0,5 s; la cadena completa de la limpieza
    /// (lectura del nombre, consulta, versión, descriptores y título del catálogo) pasa de 11,3 s a
    /// 2,8 s. Los métodos estáticos de Regex guardan solo los 15 últimos patrones (Regex.CacheSize),
    /// y una sola lectura de un nombre recorre unos 30 distintos: la caché fallaba siempre y cada
    /// llamada volvía a analizar y construir el patrón, PoolRe y DescRe incluidos, que son
    /// alternancias largas. Eso pasa una vez por canción al cruzar la biblioteca en Tendencias y
    /// Playlists, en el hilo de la interfaz.
    ///
    /// Solo para patrones constantes: uno que dependa de los datos (un nombre de artista) llenaría
    /// esta caché sin límite.
    /// </summary>
    public static Regex Rx(string pattern, RegexOptions options = RegexOptions.None)
        => RxCache.GetOrAdd((pattern, options), k => new Regex(k.Pattern, k.Options));

    /// <summary>Descompone (FormD) y elimina las marcas diacríticas, conservando la base.</summary>
    public static string RemoveDiacritics(string? t)
    {
        if (string.IsNullOrEmpty(t)) return "";
        var n = t.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(n.Length);
        foreach (var c in n)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Convierte a ASCII puro: mapea latinas especiales, quita acentos y descarta lo no imprimible ASCII.</summary>
    public static string ToAscii(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = SinMarcas(s);
        return Rx(@"[^\x20-\x7E]").Replace(s, "");
    }

    /// <summary>Lo que hace ToAscii antes de descartar: latinas especiales mapeadas y sin acentos.</summary>
    private static string SinMarcas(string s)
    {
        foreach (var (from, to) in AsciiMap) s = s.Replace(from.ToString(), to);
        return RemoveDiacritics(s);
    }

    /// <summary>
    /// ¿Pasar este texto a ASCII le borra letras? Las latinas con acento no cuentan (se quedan sin
    /// el acento, pero se quedan); sí cuentan las de escrituras sin equivalente: cirílico, griego,
    /// hebreo, árabe, CJK…
    /// </summary>
    public static bool PierdeLetrasEnAscii(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in SinMarcas(s))
            if (c > '\x7E' && char.IsLetter(c)) return true;
        return false;
    }

    /// <summary>
    /// Un campo del nombre de archivo (artista, título, versión) en ASCII, como siempre, salvo que
    /// el paso a ASCII le borre letras: entonces se conserva tal cual, en Unicode, que NTFS y APFS
    /// admiten sin problema.
    ///
    /// Visto en una biblioteca real: «Kartashow - Золото (Rakurs &amp; Ramirez Remix)» se proponía
    /// como «KARTASHOW, - (Rakurs &amp; Ramirez Remix)» y «Damso - Ε. Signaler» como
    /// «Damso - . Signaler». Un tema entero en cirílico o en japonés quedaba en «-». Se decide campo a
    /// campo para que el resto del nombre siga normalizado como antes.
    /// </summary>
    public static string AsciiSinPerderLetras(string? campo)
    {
        if (string.IsNullOrEmpty(campo)) return "";
        return PierdeLetrasEnAscii(campo) ? campo.Normalize(NormalizationForm.FormC) : ToAscii(campo);
    }

    /// <summary>NK: minúsculas + sin acentos + solo [a-z0-9]. Base del matching por igualdad/inclusión.</summary>
    public static string Nk(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = RemoveDiacritics(s).ToLowerInvariant();
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Limpia un texto para usarlo como nombre de archivo (quita ilegales, colapsa espacios).</summary>
    public static string Sanitize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Rx("[\"<>|?*]").Replace(s, "");
        s = Rx(@"[:/\\]").Replace(s, " ");
        s = Rx("_+").Replace(s, " ");
        s = Rx(@"\s+").Replace(s, " ").Trim();
        return s.TrimEnd('.', ' ').Trim();
    }

    public static string UrlEnc(string? s) => Uri.EscapeDataString(s ?? "");

    /// <summary>Formato Título respetando el estilo (se pre-minusculiza como en la app PS).</summary>
    public static string TitleCase(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
    }

    /// <summary>
    /// Des-grita un texto TODO EN MAYÚSCULAS a Formato Título, pero respeta:
    /// estilos con minúsculas (TiK ToK), palabras sueltas/siglas (RVFV) y textos sin mayúsculas.
    /// </summary>
    public static string UnScream(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        if (Rx(@"\p{Ll}").IsMatch(s)) return s;      // ya hay minúsculas -> respeta el estilo
        if (!Rx(@"\s").IsMatch(s)) return s;         // una sola palabra -> respeta siglas
        if (!Rx(@"\p{Lu}").IsMatch(s)) return s;     // no hay mayúsculas -> nada que hacer
        return TitleCase(s);
    }

    /// <summary>
    /// Un campo listo para un CSV de los informes: entrecomillado, con las comillas internas
    /// dobladas y sin saltos de línea.
    ///
    /// Va entrecomillado SIEMPRE, y no solo cuando hace falta, porque el separador de estos
    /// informes es «;» -que es lo que espera Excel en español- y un punto y coma dentro del nombre
    /// de un archivo partiría la fila en dos columnas sin avisar de nada.
    /// </summary>
    public static string CsvField(string? s)
        => "\"" + (s ?? "").Replace("\"", "\"\"").Replace('\n', ' ').Replace('\r', ' ') + "\"";

    /// <summary>ETA legible: "2m 5s", "1h 3m", "45s".</summary>
    public static string FormatEta(double sec)
    {
        if (sec <= 0 || double.IsInfinity(sec)) return "--";
        var ts = TimeSpan.FromSeconds(Math.Round(sec));
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }
}
