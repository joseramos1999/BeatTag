using System.Text.RegularExpressions;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Core.Ai;

/// <summary>Un nombre nuevo propuesto para un archivo, ya validado. <see cref="Avisos"/>: lo que conviene mirar antes de aceptar.</summary>
public sealed record PropuestaNombre(string Ruta, string Actual, string Propuesto, IReadOnlyList<string> Avisos);

/// <summary>
/// Renombrar con la IA los archivos con nombre sucio, sin pasar por el catálogo.
///
/// MEDIDO ANTES DE CONSTRUIR (nombres reales de una biblioteca de 15.000 canciones, llama3.2 y
/// llama3.1:8b, el mismo prompt que usa Enriquecer):
///   · El fallo que se temía, meter el descriptor dentro del título («Resentia (Hype Intro)» como
///     título), ya casi no ocurre: 3 de 60 con llama3.2 y 1 de 60 con llama3.1:8b.
///   · Los nombres que ya están limpios no ganan nada: la IA los devuelve igual. Por eso solo se
///     consultan los que <see cref="PareceSucio"/> reconoce como sucios.
///   · Los fallos que quedan son reconocibles, y cada uno tiene aquí su comprobación: el record pool
///     como artista («BRGS»), el título repetido como artista («Porfa - Porfa»), el título que es
///     uno de los artistas («Bizarrap, Daddy Yankee - Daddy Yankee»), basura en la versión («128
///     BPM», «[chemist-music.com]», «FREE DOWNLOAD!»), guiones bajos, títulos vacíos («_») y
///     artistas inventados («Spice Mash» → «Spice Girls»).
///   · Lo que NO se puede comprobar con reglas: intercambiar artista y título. Cuando la propuesta
///     los invierte respecto al nombre actual se AVISA, pero no se descarta, porque hay nombres que
///     vienen al revés («Titi Me Pregunto - Bad Bunny») y justo esos son los que hay que arreglar.
/// </summary>
public static class RenombradoIa
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex DescriptorRe = new(Descriptors.DescRe);
    private static readonly Regex PoolRe = new(Descriptors.PoolRe);
    private static readonly Regex Bpm = new(@"\b\d{2,3}\s*(?:-\s*\d{2,3}\s*)?bpm\b", IC);
    private static readonly Regex Camelot = new(@"\b(1[0-2]|[1-9])[AB]\b", IC);
    private static readonly Regex Dominio = new(@"\b[\w-]+\.(com|net|org|io|fm|es|info|vip|club|to|me)\b|https?://\S+|\bwww\.\S+", IC);
    private static readonly Regex Promo = new(@"\bfree\s+download\b|\bdescarga\s+gratis\b|\bout\s+now\b", IC);

    /// <summary>
    /// El nombre tiene algo que limpiar. Todo con reglas, sin preguntar a nadie: los nombres ya
    /// limpios («Feid - Lady Mi Amor (Extended)») la IA los devolvía igual, así que preguntarle por
    /// ellos es gastar tiempo en revisar propuestas que no cambian nada.
    /// </summary>
    public static bool PareceSucio(string archivo)
    {
        var nombre = Path.GetFileNameWithoutExtension(archivo);
        if (nombre.Trim().Length == 0) return false;

        // Un mashup no tiene «su» nombre correcto. Medido: de 120 nombres sucios reales, 63 eran
        // mashups y la IA los apartaba bien; con esto no se gasta una consulta en cada uno.
        if (Matching.IsMezclaDeVariosTemas(nombre)) return false;

        var p = FileNameParser.Parse(Path.GetFileName(archivo));
        if (p.FnArtist.Length == 0) return true;                             // sin «Artista - Título»
        if (TextUtils.Nk(p.Base) != TextUtils.Nk(nombre)) return true;      // el analizador quitó algo: pool, editor, web, paréntesis cortado…
        if (nombre.Contains('_')) return true;
        if (Regex.IsMatch(nombre, @"\w-\w+-\w")) return true;                // palabras-unidas-por-guiones
        if (Bpm.IsMatch(nombre) || Camelot.IsMatch(nombre)) return true;
        if (nombre.Count(c => c == '(') != nombre.Count(c => c == ')')) return true;

        // TODO EN MAYÚSCULAS: solo si hay varias palabras con letras, para no tocar «ABBA - SOS».
        var letras = nombre.Where(char.IsLetter).ToList();
        var palabras = Regex.Matches(nombre, @"\p{L}{2,}").Count;
        return letras.Count >= 8 && palabras >= 3 && letras.All(char.IsUpper);
    }

    /// <summary>
    /// Convierte lo que devolvió la IA en una propuesta de nombre, o explica por qué no hay propuesta.
    /// </summary>
    /// <returns>La propuesta, o null y el motivo.</returns>
    public static (PropuestaNombre? Propuesta, string Motivo) Evaluar(string ruta, string tagArtista, string tagTitulo, AiParse? ia)
    {
        var archivo = Path.GetFileName(ruta);
        var actual = Path.GetFileNameWithoutExtension(ruta);
        // «No Te Canses ✘ El Funeral»: otros símbolos que hacen de « x » entre dos canciones.
        var actualX = Regex.Replace(actual, @"\s*[✘✖×]\s*", " x ");

        if (ia == null) return (null, "La IA no respondió.");
        if (ia.IsMashup) return (null, "Parece un mashup: se deja como está.");
        if (ia.Title.Trim().Length == 0 || ia.Confidence < 0.5) return (null, "La IA no reconoce la canción.");

        var avisos = new List<string>();
        // La tonalidad pegada al título («(7A) GLOW UP», «10A LLEVA AL SOL») no es parte del título.
        // Y tampoco el BPM, suelto o entre corchetes: «Sexo Seguro [97BPM]».
        var titulo = Espacios(SinTono(Bpm.Replace(Regex.Replace(Limpiar(ia.Title), @"\[[^\]]*\d[^\]]*\]", " "), " ")));
        var version = Limpiar(ia.Version);

        // Un descriptor que se coló dentro del título entre paréntesis pasa a la versión.
        foreach (Match m in Regex.Matches(titulo, @"\(([^()]*)\)"))
        {
            if (!DescriptorRe.IsMatch(m.Groups[1].Value)) continue;
            titulo = titulo.Replace(m.Value, " ");
            // Visto en la app con una biblioteca real: versión «Hype Intro» y en el título «(Oscar
            // Alegre Hype Intro)». Sumarlas daba «Hype Intro Oscar Alegre Hype Intro»; si la del
            // título ya CONTIENE la otra, la sustituye.
            var movida = m.Groups[1].Value.Trim();
            if (TextUtils.Nk(movida).Contains(TextUtils.Nk(version), StringComparison.Ordinal))
                version = movida;
            else if (!TextUtils.Nk(version).Contains(TextUtils.Nk(movida), StringComparison.Ordinal))
                version = (version + " " + movida).Trim();
        }
        titulo = Espacios(titulo);
        if (!Regex.IsMatch(titulo, @"\p{L}")) return (null, "La propuesta no tiene un título válido.");
        if (titulo.Contains(" - ")) return (null, "La propuesta mezcla artista y título.");

        // Artistas: fuera el record pool, que distribuye pero no interpreta.
        var artistas = Regex.Split(Limpiar(ia.Artist), @"\s*,\s*")
                            .Select(Espacios)
                            .Where(a => a.Length > 0)
                            .ToList();
        var pools = artistas.Where(a => PoolRe.IsMatch(a)).ToList();
        if (pools.Count > 0)
        {
            artistas = artistas.Except(pools).ToList();
            avisos.Add($"Se quita «{string.Join(", ", pools)}» como artista: es un record pool.");
        }
        var artista = string.Join(", ", artistas);

        // Respuesta real: «40 mashups by Alex Gonzalez, 4BEATS». Sale de las etiquetas, que en los
        // record pools muchas veces llevan el nombre del pack en vez del intérprete.
        if (Regex.IsMatch(artista, @"\b(mashups?|pack|vol\.?\s*\d|by)\b", IC))
            return (null, "El artista propuesto es el nombre de un pack, no un intérprete.");

        // Respuesta real: «Pepas (David Guetta Remix)» → artista «David Guetta». Quien solo aparece
        // DENTRO del paréntesis suele ser quien hizo la versión, no el intérprete. Si las etiquetas
        // tampoco lo dicen, la propuesta no se acepta.
        if (artista.Length > 0)
        {
            var fueraDeParentesis = Regex.Replace(Regex.Replace(actual, @"[\(\[][^\)\]]*([\)\]]|$)", " "), @"\s+", " ");
            if (Matching.SoloReordena($"{archivo} {tagArtista}", artista, "")
                && !Matching.SoloReordena($"{fueraDeParentesis} {tagArtista}", artista, ""))
                return (null, "El artista propuesto solo aparece entre paréntesis: suele ser quien hizo la versión, no el intérprete.");
        }

        // Respuesta real: «La Nueva Escuela (Antonio Guevara Live Edit 125Bpm) - Dile (Dile)». Un
        // artista con paréntesis o con BPM es un trozo de versión mal colocado.
        if (artista.IndexOfAny(new[] { '(', ')', '[', ']' }) >= 0 || Bpm.IsMatch(artista))
            return (null, "La propuesta mete la versión dentro del artista.");

        var nkTitulo = TextUtils.Nk(titulo);

        // Respuesta real: «Rafa Pabon - Rafa Pabon DJ Turreo Sessions». El título no puede empezar
        // repitiendo el artista.
        if (artistas.Count > 0 && TextUtils.Nk(artistas[0]).Length >= 4 &&
            nkTitulo.StartsWith(TextUtils.Nk(artistas[0]), StringComparison.Ordinal) && nkTitulo != TextUtils.Nk(artistas[0]))
            return (null, "El título repite al artista.");

        // Respuesta real: «Dile a El x Normal» → «Dile a El (Normal)». Si el nombre juntaba dos
        // canciones con « x » y la propuesta ya no, ha deshecho un mashup en vez de limpiarlo.
        var partesOriginal = FileNameParser.Parse(archivo);
        if (Regex.IsMatch(partesOriginal.FnTitle.Length > 0 ? partesOriginal.FnTitle : partesOriginal.Base, @"\s[xX]\s")
            && !Regex.IsMatch(titulo, @"\s[xX]\s"))
            return (null, "El nombre parece juntar dos canciones y la propuesta pierde una.");

        // Lo mismo esté la « x » donde esté: «Mañana x Girl - Ozuna, Myke Towers» → «Ozuna, Myke
        // Towers - Mañana». Las palabras a los dos lados de cada « x » tienen que seguir en la
        // propuesta (como artista o como título); si alguna se pierde, se ha perdido una canción.
        var enPropuesta = new HashSet<string>(
            Regex.Split($"{ia.Artist} {titulo}", @"[^\p{L}\p{N}]+").Select(TextUtils.Nk).Where(w => w.Length > 0), StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(actualX, @"([\p{L}\p{N}']+)\s+[xX]\s+([\p{L}\p{N}']+)"))
            if (!enPropuesta.Contains(TextUtils.Nk(m.Groups[1].Value)) || !enPropuesta.Contains(TextUtils.Nk(m.Groups[2].Value)))
                return (null, "El nombre parece juntar dos canciones y la propuesta pierde una.");
        if (artistas.Any(a => TextUtils.Nk(a) == nkTitulo))
        {
            // «Porfa - Porfa»: el artista repetido no aporta nada. Si era el ÚNICO, se quita; si el
            // título coincide con uno de VARIOS artistas, el título está mal y no hay propuesta.
            if (artistas.Count > 1) return (null, "El título propuesto es uno de los artistas.");
            artista = "";
        }

        // La versión solo puede llevar lo que ya estaba en el nombre, y nunca BPM, tonos, webs o promos.
        // Los corchetes se quitan, pero no su contenido si es un descriptor: «[Remix]» es la versión.
        version = Regex.Replace(version, @"\[([^\]]*)\]", m => DescriptorRe.IsMatch(m.Groups[1].Value) ? " " + m.Groups[1].Value + " " : " ");
        version = Espacios(Promo.Replace(Dominio.Replace(Camelot.Replace(Bpm.Replace(PoolRe.Replace(version, " "), " "), " "), " "), " "));
        version = Espacios(Regex.Replace(version, @"(?<![\d.])\b\d{2,3}\b(?![\d.])", " "));   // «Extended Latino 160»: un BPM sin «bpm»
        version = version.Trim('(', ')', '[', ']', ' ', '-');

        // Respuesta real: «Soy Peor Veo Veo (F3L». Si el nombre venía cortado dentro del paréntesis,
        // copiar el trozo como versión deja «(F3L)», «(Jose)», «(HCTR G)». Si la IA lo completó
        // («Albert González Remi» → «Albert Gonzalez Remix») sí vale.
        if (Regex.Match(actual, @"\(([^()]*)$") is { Success: true } cortado &&
            version.Length > 0 && TextUtils.Nk(cortado.Groups[1].Value).StartsWith(TextUtils.Nk(version), StringComparison.Ordinal))
        {
            avisos.Add($"El nombre venía cortado en «({cortado.Groups[1].Value.Trim()}»: esa versión incompleta no se usa.");
            version = "";
        }
        if (version.Length > 0 && !TodasEstan(archivo, version))
        {
            // Respuesta real: «Rompe (F3LY Melodic Intro)» → versión «F3LY Intro Edit». Tirarla
            // entera dejaba la canción sin su descriptor; mejor el que ya traía el nombre.
            var delNombre = DescriptorDelNombre(actual);
            avisos.Add(delNombre.Length > 0
                ? $"La versión propuesta («{version}») no estaba en el nombre: se mantiene «{delNombre}»."
                : $"Se ignora la versión «{version}»: no estaba en el nombre.");
            version = delNombre;
        }
        // Respuesta real: «Try It - Poblado Remix X Culo (TRY IT RE-EDIT)». Si todo el artista está
        // dentro de la versión, es quien la firma, no quien la canta.
        if (artistas.Count == 1 && version.Length > 0 && TextUtils.Nk(artistas[0]).Length >= 3 &&
            TextUtils.Nk(version).Contains(TextUtils.Nk(artistas[0]), StringComparison.Ordinal))
            return (null, "El artista propuesto es quien firma la versión, no el intérprete.");

        // «Dile (Dile)», «Dandole (INTurrix) TG! (INTurrix)»: una versión que ya está en el título sobra.
        if (version.Length > 0 && nkTitulo.Contains(TextUtils.Nk(version), StringComparison.Ordinal)) version = "";

        // Lo que iba junto a una « x » en el nombre era otra canción o un artista, nunca una versión.
        // Si acaba en la versión, la propuesta ha convertido una canción en descriptor: «Dile a El x
        // Normal» → «Dile a El (Normal)», «MMC x Ven Conmigo» → «Ven Conmigo (MMC)».
        var palabrasVersion = Regex.Split(version, @"[^\p{L}\p{N}]+").Select(TextUtils.Nk).Where(w => w.Length > 0).ToHashSet();
        foreach (Match m in Regex.Matches(actual, @"([\p{L}\p{N}']+)\s+[xX]\s+([\p{L}\p{N}']+)"))
            if (palabrasVersion.Contains(TextUtils.Nk(m.Groups[1].Value)) || palabrasVersion.Contains(TextUtils.Nk(m.Groups[2].Value)))
                return (null, "El nombre parece juntar dos canciones y la propuesta convierte una en versión.");

        // Lo que no se puede aceptar nunca: nombres de artista o de canción que no estaban en ninguna parte.
        if (!Matching.SoloReordena($"{archivo} {tagArtista} {tagTitulo}", artista, titulo))
            return (null, "Añade palabras que no están ni en el nombre ni en las etiquetas.");

        var propuesto = AiSuggestion.Compose(artista, titulo, version);
        if (propuesto.Length == 0) return (null, "La propuesta queda vacía.");

        // Respuesta real: «Tranky Funky (Baila Baila Baila (E. Rodriguez Private Edit)».
        if (propuesto.Count(c => c == '(') != propuesto.Count(c => c == ')'))
            return (null, "La propuesta tiene los paréntesis descuadrados.");
        // Comparación LITERAL, no normalizada: normalizando, «Ese_Soyy_Yooo» y «Ese Soyy Yooo» son
        // iguales, y quitar los guiones bajos es justo uno de los arreglos que se buscan.
        if (string.Equals(propuesto, TextUtils.ToAscii(actual).Trim(), StringComparison.Ordinal))
            return (null, "Ya se llama así.");

        // Lo que las reglas no pueden decidir, se avisa.
        var partes = FileNameParser.Parse(archivo);
        if (artista.Length > 0 && partes.FnArtist.Length > 0 &&
            Contiene(partes.FnTitle, artistas[0]) && Contiene(partes.FnArtist, titulo))
            avisos.Add("Artista y título quedan al revés que en el nombre actual: comprueba cuál es cuál.");

        // Las etiquetas de los record pools a veces llevan al editor o al pack («Try It») en vez del
        // intérprete, y otras veces aciertan («Con Altura» → Rosalía & J Balvin). No se descarta,
        // pero se dice de dónde sale.
        if (artista.Length > 0 && !Matching.SoloReordena(archivo, artista, ""))
            avisos.Add("El artista no está en el nombre: sale de las etiquetas del archivo.");

        var perdidas = PalabrasPerdidas(partes.Base, propuesto);
        if (perdidas.Count > 0)
            avisos.Add($"Desaparece del nombre: {string.Join(", ", perdidas)}.");

        return (new PropuestaNombre(ruta, actual, propuesto, avisos), "");
    }

    /// <summary>
    /// Todas las palabras de <paramref name="texto"/> están en <paramref name="original"/> (o casi,
    /// para erratas: «EXTENED» vale por «Extended»). A diferencia de <see cref="Matching.SoloReordena"/>
    /// no se salta las palabras de relleno: allí «radio» y «edit» no cuentan, y así una versión
    /// «Radio Edit» inventada pasaba la comprobación.
    /// </summary>
    private static bool TodasEstan(string original, string texto)
    {
        var origen = Regex.Split(original, @"[^\p{L}\p{N}]+").Select(TextUtils.Nk).Where(w => w.Length > 0).ToList();
        return Regex.Split(texto, @"[^\p{L}\p{N}]+").Select(TextUtils.Nk).Where(w => w.Length >= 2)
                    .All(w => origen.Any(o => o == w || Matching.JaroWinkler(o, w) >= 0.85));
    }

    /// <summary>
    /// Renombra un archivo dentro de su carpeta, conservando la extensión. Nunca sobrescribe y nunca
    /// añade «(2)»: si el nombre ya existe no se renombra, porque dos archivos que acaban llamándose
    /// igual suelen ser la misma canción y eso hay que verlo, no esconderlo.
    /// </summary>
    public static Traslado RenombrarArchivo(string ruta, string nuevoNombre)
    {
        var nombre = Path.GetFileName(ruta);
        var dir = Path.GetDirectoryName(ruta) ?? "";
        var pedido = TextUtils.Sanitize(nuevoNombre) + Path.GetExtension(ruta);
        var destino = Path.Combine(dir, pedido);
        try
        {
            if (!File.Exists(ruta)) return new Traslado(ruta, destino, "el archivo ya no está.");
            if (!RenameSafety.TryResolveTarget(pedido, nombre, dir, out var resuelto, out var por))
                return new Traslado(ruta, destino, por);
            if (!string.Equals(resuelto, pedido, StringComparison.Ordinal))
                return new Traslado(ruta, destino, $"ya existe «{pedido}» en la carpeta; puede ser un duplicado.");
            if (string.Equals(resuelto, nombre, StringComparison.Ordinal))
                return new Traslado(ruta, destino, "ya se llama así.");

            File.Move(ruta, destino);
            return new Traslado(ruta, destino, "");
        }
        catch (Exception e) { return new Traslado(ruta, destino, e.Message); }
    }

    /// <summary>
    /// El descriptor que ya trae el nombre entre paréntesis, limpio de BPM, tonos y pools:
    /// «Rompe (F3LY Melodic Intro) [88 Bpm]» → «F3LY Melodic Intro». Vacío si no trae ninguno completo.
    /// </summary>
    private static string DescriptorDelNombre(string nombre)
    {
        foreach (Match m in Regex.Matches(nombre, @"\(([^()]*)\)"))
        {
            var limpio = Espacios(Camelot.Replace(Bpm.Replace(PoolRe.Replace(m.Groups[1].Value.Replace('_', ' '), " "), " "), " "));
            if (DescriptorRe.IsMatch(limpio)) return limpio;
        }
        return "";
    }

    /// <summary>Quita un código de tonalidad suelto o entre paréntesis: «(7A) GLOW UP» → «GLOW UP».</summary>
    private static string SinTono(string s) => Regex.Replace(s, @"\(\s*(1[0-2]|[1-9])[AB]\s*\)|\b(1[0-2]|[1-9])[AB]\b", " ", IC);

    /// <summary>Guiones bajos a espacios, comillas fuera, espacios simples.</summary>
    private static string Limpiar(string? s)
        => Espacios((s ?? "").Replace('_', ' ').Trim().Trim('"', '\'', '*'));

    private static string Espacios(string s) => Regex.Replace(s, @"\s{2,}", " ").Trim();

    private static bool Contiene(string texto, string parte)
    {
        var p = TextUtils.Nk(parte);
        return p.Length >= 3 && TextUtils.Nk(texto).Contains(p, StringComparison.Ordinal);
    }

    /// <summary>
    /// Palabras con contenido del nombre actual que no llegan al propuesto. No cuenta lo que se quita
    /// a propósito (BPM, tonos, pools, descriptores, números sueltos): solo lo que podría ser un
    /// artista o parte del título perdido, que es lo que hay que mirar.
    /// </summary>
    private static List<string> PalabrasPerdidas(string actual, string propuesto)
    {
        var limpio = Promo.Replace(Dominio.Replace(Camelot.Replace(Bpm.Replace(PoolRe.Replace(actual, " "), " "), " "), " "), " ");
        var destino = new HashSet<string>(Regex.Split(propuesto, @"[^\p{L}\p{N}]+").Select(TextUtils.Nk), StringComparer.Ordinal);
        return Regex.Split(limpio, @"[^\p{L}\p{N}]+")
                    // Los descriptores SÍ cuentan: «Si Tu Te Vas [Remix]» perdía el «Remix» sin avisar.
                    .Where(w => w.Length >= 3 && Regex.IsMatch(w, @"\p{L}"))
                    .Where(w => !Regex.IsMatch(w, @"^(feat|ft|vs|the|los|las|del|con|and)$", IC))
                    .Where(w => !destino.Contains(TextUtils.Nk(w)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(4)
                    .ToList();
    }
}
