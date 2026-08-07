using System.Net;
using System.Text.RegularExpressions;

namespace Etiquetador.Core.Pipeline;

/// <summary>Resultado de parsear un nombre de archivo sucio en sus partes.</summary>
public readonly record struct FileNameParseResult(string Base, string RawForOtros, string FnArtist, string FnTitle, string QTitle);

/// <summary>
/// Parsea el nombre de archivo (limpieza de pools/editores/URLs, normalización de separadores,
/// split artista - título). Port 1:1 del bloque inicial de Process-File. Puro y testeable.
/// </summary>
public static class FileNameParser
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static string Rep(string s, string pat, string rep) => Regex.Replace(s, pat, rep, IC);

    public static FileNameParseResult Parse(string fileName)
    {
        var b = Path.GetFileNameWithoutExtension(fileName ?? "");
        b = Rep(b, @"\s*DUPLICADA\s*$", "");
        try { b = WebUtility.HtmlDecode(b); } catch { /* deja b */ }
        b = Rep(b, @"^\s*\[?\s*(discogs|spotify|apple\s*music|applemusic|deezer|musicbrainz|tidal|beatport)\s*\]?\s*", "");
        b = Rep(b, @"\[[^\]]*\]", " ");
        b = Rep(b, @"\[[^\]]*$", " ");
        b = Rep(b, @"[\[\]]", " ");
        b = Descriptors.RemoveEditorTags(b);
        b = Rep(b, @"\s+", " ").Trim();
        b = Rep(b, @"https?://\S+", " ");
        b = Rep(b, @"\bwww\.\S+", " ");
        b = Rep(b, @"\b[\w-]+\.(?:com|net|org|io|kz|to|me|vip|club|info|biz)\b", " ");
        b = Rep(b, @"\bextended\s+latino\b", "Extended");
        b = Rep(b, Descriptors.PoolRe, " ");
        var rawForOtros = b;
        b = Rep(b, @"\s*\([^()]*$", " ");
        b = Rep(b, @"\s{2,}", " ").Trim();
        b = Rep(b, @"\s*-\s*\d{1,2}\s*$", "");
        b = Rep(b, @"\s+(?:vs\.?|x|&|feat\.?|ft\.?)\s*$", "");
        b = Descriptors.CompleteTruncated(b);
        b = Rep(b, @"\s*[–—]\s*", " - ");
        b = Rep(b, @"_+\s*-\s*_+", " - ");
        b = Rep(b, @"(?<=\S)-\s+", " - ");
        b = Rep(b, @"\s+-(?=\S)", " - ");
        b = Rep(b, @"\s{2,}", " ").Trim();
        b = Rep(b, @"^(.{3,}?) - \1 - ", "$1 - ");

        var parts = b.Split(new[] { " - " }, StringSplitOptions.None);
        string fnArtist, fnTitle;
        if (parts.Length >= 2) { fnArtist = parts[0].Trim(); fnTitle = parts[1].Trim(); }
        else { fnArtist = ""; fnTitle = b.Trim(); }

        var qTitle = Descriptors.CleanTitle(fnTitle);
        if (qTitle.Length == 0) qTitle = Regex.Replace(fnTitle, @"\([^)]*\)", "").Trim();
        if (qTitle.Length == 0) qTitle = fnTitle;

        return new FileNameParseResult(b, rawForOtros, fnArtist, fnTitle, qTitle);
    }
}
