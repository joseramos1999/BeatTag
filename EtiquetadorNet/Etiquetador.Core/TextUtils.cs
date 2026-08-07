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
    private static readonly (char From, string To)[] AsciiMap =
    {
        ('ø', "o"), ('Ø', "O"), ('ł', "l"), ('Ł', "L"),
        ('đ', "d"), ('Đ', "D"), ('æ', "ae"), ('Æ', "AE"),
        ('œ', "oe"), ('Œ', "OE"), ('ß', "ss"), ('þ', "th"),
        ('ð', "d"),
    };

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
        foreach (var (from, to) in AsciiMap) s = s.Replace(from.ToString(), to);
        s = RemoveDiacritics(s);
        return Regex.Replace(s, @"[^\x20-\x7E]", "");
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
        s = Regex.Replace(s, "[\"<>|?*]", "");
        s = Regex.Replace(s, @"[:/\\]", " ");
        s = Regex.Replace(s, "_+", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
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
        if (Regex.IsMatch(s, @"\p{Ll}")) return s;      // ya hay minúsculas -> respeta el estilo
        if (!Regex.IsMatch(s, @"\s")) return s;         // una sola palabra -> respeta siglas
        if (!Regex.IsMatch(s, @"\p{Lu}")) return s;     // no hay mayúsculas -> nada que hacer
        return TitleCase(s);
    }

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
