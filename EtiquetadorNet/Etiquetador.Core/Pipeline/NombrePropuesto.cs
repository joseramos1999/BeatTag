using System.Text.RegularExpressions;

namespace Etiquetador.Core.Pipeline;

/// <summary>
/// Comprobación final del nombre propuesto, mirando SOLO el resultado: devuelve los motivos por los
/// que conviene revisarlo antes de aplicarlo. No corrige nada ni decide nada; la fila sale
/// desmarcada y con el motivo a la vista, y el usuario decide.
///
/// Existe porque varios fallos de las reglas dejaban huellas que se ven sin entender la causa.
/// Vistos en una biblioteca real, y todos se proponían marcados para aplicar:
///   · «Gusty DJ, Lolo OG, DobleP - El Motorcito - (Remix)» — guion colgando.
///   · «KARTASHOW, - (Rakurs &amp; Ramirez Remix)» y «Damso - . Signaler» — el título se borraba al
///     pasarlo a ASCII.
///   · «Eric Prydz - Eric Prydz» — título igual que el artista.
///   · «… Ahora Me Llama (Adrukid Remix)» con BPM 135 cuando el nombre declaraba 155.
/// Ante una regla nueva que falle de forma parecida, la propuesta llega para revisar en vez de
/// aplicarse en bloque.
///
/// MEDIDO sobre las 6.878 propuestas de los registros (líneas DATA): 43 habrían salido para revisar,
/// 26 por el BPM, 9 por paréntesis descuadrados, 4 por guiones o comas colgando, 2 por un título que
/// es solo un descriptor, 1 por título igual al artista y 1 por artista repetido. Entre ellas está
/// «Daddy Yankee - REMIX», que es un tema de verdad: revisar no es descartar.
/// </summary>
public static class NombrePropuesto
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Descriptores que también son títulos de canción corrientes.
    private static readonly HashSet<string> TitulosCorrientes = new(StringComparer.Ordinal)
    {
        "clean", "dirty", "hype", "live", "dub", "loop", "vip", "flip", "blend", "starter", "segue",
    };

    /// <summary>
    /// Motivos para revisar la propuesta, en el registro con el que se enseñan en la tabla. Vacío si
    /// no hay nada raro.
    /// </summary>
    /// <param name="nombreNuevo">Nombre propuesto, sin extensión.</param>
    /// <param name="artista">Artista tal como se compuso el nombre (antes de pasar a ASCII).</param>
    /// <param name="titulo">Título tal como se compuso el nombre (sin las versiones).</param>
    /// <param name="nombreOriginal">Nombre del archivo, para comparar con lo que declara.</param>
    /// <param name="bpm">BPM propuesto, o "".</param>
    public static IReadOnlyList<string> Anomalias(string? nombreNuevo, string? artista, string? titulo,
        string? nombreOriginal = null, string? bpm = null)
    {
        var motivos = new List<string>();
        var nuevo = nombreNuevo ?? "";
        var a = TextUtils.AsciiSinPerderLetras(artista).Trim();
        var t = TextUtils.AsciiSinPerderLetras(titulo).Trim();

        // Título y artista sin letras ni números: lo que queda no identifica nada.
        if (!TieneLetras(t))
            motivos.Add((titulo ?? "").Trim().Length > 0
                ? "el título se queda sin letras al pasarlo a ASCII"
                : "no hay título");
        if ((artista ?? "").Trim().Length > 0 && !TieneLetras(a))
            motivos.Add("el artista se queda sin letras al pasarlo a ASCII");

        // «Eric Prydz - Eric Prydz»: la fuente devolvió el artista donde iba el título.
        var na = Clave(a);
        if (na.Length > 0 && na == Clave(t))
            motivos.Add("el título es igual que el artista");

        // El mismo intérprete dos veces en la lista.
        var vistos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parte in TextUtils.Rx(@"\s*,\s*").Split(a))
        {
            var k = Clave(parte);
            if (k.Length > 0 && !vistos.Add(k))
            {
                motivos.Add($"artista repetido: «{parte.Trim()}»");
                break;
            }
        }

        // Guiones y comas colgando: «Título - (Remix)», «Artista, - Título», «- Título». Un título que
        // EMPIEZA por paréntesis («(I've Had) The Time of My Life») no cuenta: ahí detrás sigue el título.
        if (TextUtils.Rx(@"\s[-–—]\s*(?:[\(\[][^\)\]]*[\)\]]\s*)+$|[-–—]\s*$|^\s*[-–—]|[-–—]\s+[-–—]|,\s*(?:[-–—(\[]|$)|\(\s*\)").IsMatch(nuevo))
            motivos.Add("guion o coma colgando en el nombre");

        if (!ParentesisCuadran(nuevo))
            motivos.Add("paréntesis sin cerrar o sin abrir");

        // Un título que solo es una palabra de versión («Remix», «Extended Mix») es que el título de
        // verdad se ha perdido por el camino. Las palabras que también son títulos corrientes («Dirty»,
        // «Live», «Loop», «VIP»…) no cuentan: «Salvatore Ganacci, Tokischa, Rosalia - Dirty» es un tema.
        var sinVersion = TextUtils.Rx(Descriptors.DescRe, IC).Replace(t, d => TitulosCorrientes.Contains(TextUtils.Nk(d.Value)) ? d.Value : " ");
        if (TieneLetras(t) && !TieneLetras(sinVersion))
            motivos.Add($"el título es solo un descriptor de versión («{t}»)");

        // BPM distinto del que declara el propio nombre del archivo.
        var declarado = FileProcessor.BpmDelNombre(Path.GetFileNameWithoutExtension(nombreOriginal ?? ""), out _);
        if (declarado > 0 && int.TryParse(bpm, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var propuesto) && propuesto > 0 && propuesto != declarado)
            motivos.Add($"el BPM propuesto ({propuesto}) no cuadra con el que declara el nombre ({declarado})");

        return motivos;
    }

    private static bool TieneLetras(string s)
    {
        foreach (var c in s) if (char.IsLetterOrDigit(c)) return true;
        return false;
    }

    /// <summary>
    /// Como TextUtils.Nk, pero sin tirar las letras no latinas: con Nk, un título en cirílico se
    /// quedaba en "" y parecía igual a cualquier otro título en cirílico.
    /// </summary>
    private static string Clave(string? s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in TextUtils.RemoveDiacritics(s).ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Paréntesis y corchetes abiertos y cerrados en orden.</summary>
    private static bool ParentesisCuadran(string s)
    {
        int p = 0, c = 0;
        foreach (var ch in s)
        {
            if (ch == '(') p++;
            else if (ch == ')' && --p < 0) return false;
            else if (ch == '[') c++;
            else if (ch == ']' && --c < 0) return false;
        }
        return p == 0 && c == 0;
    }
}
