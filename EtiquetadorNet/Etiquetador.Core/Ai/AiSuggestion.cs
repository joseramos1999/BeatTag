using System.Text;
using System.Text.RegularExpressions;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Core.Ai;

/// <summary>
/// Convierte la propuesta que la IA local no pudo verificar en una sugerencia que enseñarle al
/// usuario en "No encontradas".
///
/// El reparto de responsabilidades es deliberado: la IA propone y la persona decide. Estas
/// propuestas aciertan aproximadamente dos de cada tres veces, y el fallo típico -intercambiar
/// artista y título- se ve de un vistazo. Suficiente para ofrecerlas como atajo de un clic, muy
/// lejos de lo que hace falta para escribirlas solas en los archivos de alguien.
/// </summary>
public static class AiSuggestion
{
    /// <summary>Una palabra, contando las tildes que van sueltas detrás de la letra (\p{M}).</summary>
    private const string Palabra = @"[\p{L}\p{M}\p{N}']+";

    /// <summary>
    /// Compone el nombre de archivo (sin extensión) que resultaría de aceptar la propuesta.
    ///
    /// Con <paramref name="conservarAcentos"/> se respetan tildes y eñes. Lo usan las herramientas que
    /// arreglan nombres que YA existen en la biblioteca: ahí quitar los acentos no es normalizar, es
    /// estropear el nombre que el usuario tenía («Feliz Cumpleaños» → «Cumpleanos»). El pase a ASCII
    /// sigue siendo el de siempre cuando el nombre lo escribe el proceso normal de limpieza.
    /// </summary>
    public static string Compose(string? artist, string? title, string? version, bool conservarAcentos = false)
    {
        var a = Limpia(artist);
        var t = Limpia(title);
        if (t.Length == 0) return "";

        var v = Limpia(version);
        var name = a.Length > 0 ? $"{a} - {t}" : t;
        if (v.Length > 0) name = $"{name} ({v})";
        return conservarAcentos ? NombreCortado.Sanear(name) : TextUtils.Sanitize(TextUtils.ToAscii(name));
    }

    /// <summary>
    /// La sugerencia que corresponde a un resultado, o "" si no hay ninguna que ofrecer.
    ///
    /// Se calla cuando propondría lo mismo que la limpieza normal ya iba a dejar: enseñar una
    /// sugerencia que no cambia nada solo gasta la atención del usuario.
    /// </summary>
    public static string For(ProcessResult r)
    {
        var name = Compose(r.AiArtist, r.AiTitle, r.AiVersion);
        if (name.Length == 0) return "";

        // A veces el modelo devuelve el nombre sucio casi tal cual, con los BPM o la tonalidad
        // todavía pegados. Ahí la limpieza normal de la aplicación ya lo hace mejor, así que la
        // sugerencia sobra: sería ofrecerle al usuario un cambio a peor.
        if (Regex.IsMatch(name, @"\b\d{2,3}\s*(-\s*\d{2,3}\s*)?bpm\b", RegexOptions.IgnoreCase)) return "";

        var actual = TextUtils.Nk(Path.GetFileNameWithoutExtension(r.New));
        return TextUtils.Nk(name) == actual ? "" : name;
    }

    /// <summary>
    /// Devuelve a las palabras de <paramref name="propuesto"/> los acentos que tienen en
    /// <paramref name="original"/>.
    ///
    /// El modelo responde casi siempre sin tildes -«Titi Me Pregunto», «Feliz Cumpleanos»- y con eso
    /// un renombrado que venía a arreglar el nombre lo empeoraba. Solo se cambian palabras que ya
    /// estaban escritas en el original: no se inventan tildes nuevas.
    ///
    /// Las palabras que en español solo se distinguen por la tilde (sé/se, tú/tu, él/el, sí/si…) no
    /// se pueden restaurar por su forma sin tilde: visto en una biblioteca real, con
    /// «Ya_Sé_Que_Se_Fue (Extended)» la tilde de «Sé» acababa también en el otro «Se» («Ya Sé Que Sé
    /// Fue»). Cuando el original trae la palabra de las dos formas, se alinean por orden de aparición,
    /// y si la propuesta no la trae las mismas veces, se deja como la escribió la IA.
    /// </summary>
    public static string RestaurarAcentos(string propuesto, string original)
    {
        // Normalizados los dos: una «á» puede venir como un solo carácter o como «a» más la tilde
        // suelta detrás, y mezclar las dos formas dejaba nombres con la tilde repetida («Romá́»).
        propuesto = propuesto.Normalize(NormalizationForm.FormC);
        original = original.Normalize(NormalizationForm.FormC);

        static bool ConTilde(string palabra) => !string.Equals(TextUtils.ToAscii(palabra), palabra, StringComparison.Ordinal);

        // Cada palabra del original por su forma sin tildes, con TODAS las formas en que aparece, en orden.
        var formas = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(original, Palabra))
        {
            var sin = TextUtils.ToAscii(m.Value);
            if (!formas.TryGetValue(sin, out var lista)) formas[sin] = lista = new List<string>();
            lista.Add(m.Value);
        }
        if (!formas.Values.Any(l => l.Any(ConTilde))) return propuesto;

        var vecesEnPropuesta = Regex.Matches(propuesto, Palabra)
            .GroupBy(m => TextUtils.ToAscii(m.Value), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var vistas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        return Regex.Replace(propuesto, Palabra, m =>
        {
            var sin = TextUtils.ToAscii(m.Value);
            if (!formas.TryGetValue(sin, out var lista)) return m.Value;
            var orden = vistas.GetValueOrDefault(sin);
            vistas[sin] = orden + 1;

            var acentuadas = lista.Where(ConTilde).Select(f => f.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Count();
            if (acentuadas == 0) return m.Value;

            // Sin ambigüedad (solo aparece con una tilde): la recupera en todas partes, como siempre.
            if (acentuadas == 1 && lista.All(ConTilde)) return Mayusculiza(lista.First(ConTilde), m.Value);

            // Ambigua: solo se puede saber cuál es cuál si la propuesta la trae las mismas veces.
            if (vecesEnPropuesta.GetValueOrDefault(sin) != lista.Count) return m.Value;
            var suya = lista[orden];
            return ConTilde(suya) ? Mayusculiza(suya, m.Value) : m.Value;
        });
    }

    /// <summary>Copia en <paramref name="palabra"/> el uso de mayúsculas que traía <paramref name="como"/>.</summary>
    private static string Mayusculiza(string palabra, string como)
    {
        if (como.All(c => !char.IsLetter(c) || char.IsUpper(c))) return palabra.ToUpperInvariant();
        if (char.IsLower(como[0]) && palabra.Length > 0 && char.IsUpper(palabra[0]))
            return char.ToLowerInvariant(palabra[0]) + palabra[1..];
        if (char.IsUpper(como[0]) && palabra.Length > 0 && char.IsLower(palabra[0]))
            return char.ToUpperInvariant(palabra[0]) + palabra[1..];
        return palabra;
    }

    /// <summary>Quita comillas, guiones sueltos y espacios dobles de lo que devuelve el modelo.</summary>
    private static string Limpia(string? s)
    {
        var t = (s ?? "").Trim().Trim('"', '\'', '-', '_').Trim();
        return Regex.Replace(t, @"\s{2,}", " ");
    }
}
