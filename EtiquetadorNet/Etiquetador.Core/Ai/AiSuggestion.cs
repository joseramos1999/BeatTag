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
    /// Fue»). Esas palabras -las de <see cref="Diacriticas"/>, y cualquiera que el original traiga de
    /// las dos formas- se emparejan una a una con las del original: primero por las palabras que
    /// tienen al lado (así da igual que la IA las reordene: «Se Fue Ya Se» → «Se Fue Ya Sé») y luego
    /// por orden. Ninguna del original se usa dos veces: si la propuesta trae la palabra más veces
    /// que el original, las que sobran se quedan como las escribió la IA. Visto en revisión, al
    /// completar un nombre: «Él Me Mintió (Remix De» con la etiqueta «… (Remix De El Alfa)» daba
    /// «(Remix De Él Alfa)». Si la propuesta la trae MENOS veces, solo vale lo que case por contexto.
    /// </summary>
    public static string RestaurarAcentos(string propuesto, string original)
    {
        // Normalizados los dos: una «á» puede venir como un solo carácter o como «a» más la tilde
        // suelta detrás, y mezclar las dos formas dejaba nombres con la tilde repetida («Romá́»).
        propuesto = propuesto.Normalize(NormalizationForm.FormC);
        original = original.Normalize(NormalizationForm.FormC);

        static bool ConTilde(string palabra) => !string.Equals(TextUtils.ToAscii(palabra), palabra, StringComparison.Ordinal);

        var delOriginal = Ocurrencias(original);
        // Cada palabra del original por su forma sin tildes, con TODAS sus apariciones, en orden.
        var formas = delOriginal.GroupBy(o => o.Sin, StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        if (!delOriginal.Any(o => ConTilde(o.Forma))) return propuesto;

        var dePropuesta = Ocurrencias(propuesto);
        var decision = new string?[dePropuesta.Count];

        foreach (var grupo in Enumerable.Range(0, dePropuesta.Count).GroupBy(i => dePropuesta[i].Sin, StringComparer.OrdinalIgnoreCase))
        {
            if (!formas.TryGetValue(grupo.Key, out var lista)) continue;
            var acentuadas = lista.Where(o => ConTilde(o.Forma)).Select(o => o.Forma.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Count();
            if (acentuadas == 0) continue;

            // Sin ambigüedad (solo aparece con una tilde y no es de las que la tilde distingue): la
            // recupera en todas partes, como siempre («Despechá (DESPECHÁ Intro)»).
            var sensible = Diacriticas.Contains(grupo.Key) || acentuadas > 1 || lista.Any(o => !ConTilde(o.Forma));
            if (!sensible)
            {
                var forma = lista.First(o => ConTilde(o.Forma)).Forma;
                foreach (var i in grupo) decision[i] = Mayusculiza(forma, dePropuesta[i].Forma);
                continue;
            }

            foreach (var (i, o) in Emparejar(grupo.ToList(), dePropuesta, lista))
                if (ConTilde(lista[o].Forma)) decision[i] = Mayusculiza(lista[o].Forma, dePropuesta[i].Forma);
        }

        var k = 0;
        return Regex.Replace(propuesto, Palabra, m => decision[k++] ?? m.Value);
    }

    /// <summary>
    /// Palabras que en español cambian de significado con la tilde (tilde diacrítica) y alguna
    /// interrogativa: su forma sin tilde es otra palabra, así que no se puede dar por hecho que
    /// cualquier «el» del nombre propuesto sea el «Él» del original. En minúsculas y sin tilde.
    /// </summary>
    private static readonly HashSet<string> Diacriticas = new(StringComparer.OrdinalIgnoreCase)
    {
        "tu", "el", "mi", "se", "si", "te", "de", "mas", "que", "quien", "quienes", "cual", "cuales",
        "como", "cuando", "cuanto", "donde", "adonde", "aun", "solo", "o",
    };

    /// <summary>Una palabra con su forma sin tildes y las palabras que tiene al lado (sin tildes, en minúsculas).</summary>
    private readonly record struct Ocurrencia(string Forma, string Sin, string Antes, string Despues);

    private static List<Ocurrencia> Ocurrencias(string texto)
    {
        var palabras = Regex.Matches(texto, Palabra).Select(m => m.Value).ToList();
        var sin = palabras.Select(p => TextUtils.ToAscii(p).ToLowerInvariant()).ToList();
        return palabras.Select((p, i) => new Ocurrencia(p, sin[i], i > 0 ? sin[i - 1] : "", i + 1 < sin.Count ? sin[i + 1] : "")).ToList();
    }

    /// <summary>
    /// Empareja las apariciones de una palabra en la propuesta (<paramref name="enPropuesta"/>,
    /// índices de <paramref name="propuesta"/>) con las del original, sin repetir ninguna del
    /// original: primero las que comparten las dos palabras vecinas, luego una, y lo que quede, por
    /// orden si la propuesta la trae al menos tantas veces como el original.
    /// </summary>
    private static IEnumerable<(int Propuesta, int Original)> Emparejar(List<int> enPropuesta, List<Ocurrencia> propuesta, List<Ocurrencia> original)
    {
        var usada = new bool[original.Count];
        var asignada = new Dictionary<int, int>();
        for (var exigido = 2; exigido >= 1; exigido--)
        {
            foreach (var i in enPropuesta)
            {
                if (asignada.ContainsKey(i)) continue;
                var p = propuesta[i];
                for (var o = 0; o < original.Count; o++)
                {
                    if (usada[o]) continue;
                    var parecido = (p.Antes.Length > 0 && p.Antes == original[o].Antes ? 1 : 0)
                                 + (p.Despues.Length > 0 && p.Despues == original[o].Despues ? 1 : 0);
                    if (parecido < exigido) continue;
                    usada[o] = true;
                    asignada[i] = o;
                    break;
                }
            }
        }
        if (enPropuesta.Count >= original.Count)
        {
            var libres = Enumerable.Range(0, original.Count).Where(o => !usada[o]).GetEnumerator();
            foreach (var i in enPropuesta)
            {
                if (asignada.ContainsKey(i)) continue;
                if (!libres.MoveNext()) break;
                asignada[i] = libres.Current;
            }
        }
        return asignada.Select(kv => (kv.Key, kv.Value));
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
