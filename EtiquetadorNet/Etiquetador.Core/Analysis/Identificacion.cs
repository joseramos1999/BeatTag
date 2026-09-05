using System.Text.RegularExpressions;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Core.Analysis;

/// <summary>En qué queda la comprobación de una canción.</summary>
public enum VeredictoId
{
    /// <summary>Mezcla o edición de DJ: no se comprueba, porque no existe como grabación publicada.</summary>
    Excluida,

    /// <summary>El servicio no reconoció el audio. No dice nada sobre si el nombre está bien o mal.</summary>
    SinIdentificar,

    /// <summary>El audio es lo que el archivo dice que es.</summary>
    Coincide,

    /// <summary>El audio NO es lo que el archivo dice que es. Es lo único que hay que revisar.</summary>
    Difiere,
}

/// <summary>Lo que el AUDIO resultó ser, según el servicio de identificación.</summary>
public sealed record Identificado(string Artist, string Title, string Album = "", string Fuente = "");

/// <summary>Veredicto de una canción, con la explicación en lenguaje llano.</summary>
public readonly record struct Veredicto(VeredictoId Estado, string Motivo);

/// <summary>
/// Compara lo que DICE un archivo (su nombre y sus etiquetas) con lo que su audio resultó SER.
///
/// Es la pieza que decide qué se pinta en rojo, y por eso está aquí, separada de la red y de la
/// interfaz: se puede probar entera sin llamar a ningún servicio.
///
/// La comparación se hace sobre el NÚCLEO del título, no sobre el texto literal. Una biblioteca de
/// DJ está llena de «(Hype Intro)», «(Extended Mix)» y «128 BPM», y el servicio devuelve el título
/// comercial pelado: comparar a pelo marcaría en rojo la biblioteca entera. Quitar esos adornos
/// -con la MISMA limpieza que ya usa el resto de la aplicación- es lo que hace que el rojo
/// signifique algo.
/// </summary>
public static class Identificacion
{
    /// <summary>
    /// A partir de aquí dos títulos se consideran el mismo. Tolera erratas y acentos perdidos
    /// («Resentia» / «Resentía»), que son cosa de quien nombró el archivo, no del audio.
    /// </summary>
    public const double UmbralTitulo = 0.88;

    /// <summary>
    /// A partir de aquí dos nombres de artista se consideran el mismo. Más exigente que el de los
    /// títulos: los nombres de artista son cortos y se parecen entre sí con facilidad.
    /// </summary>
    public const double UmbralArtista = 0.92;

    /// <summary>
    /// Longitud mínima para aceptar que un título esté CONTENIDO en otro. Sin este mínimo, un
    /// título corto como «Si» casaría con casi cualquier cosa.
    /// </summary>
    private const int MinimoParaContener = 5;

    /// <summary>
    /// ¿Esta canción se queda fuera de la comprobación? Mismas reglas que usa Enriquecer, y a
    /// propósito: una mezcla no existe como lanzamiento, así que el servicio devolverá el tema que
    /// más suene en ella y la marcaría en rojo siendo el archivo correcto. Serían falsos avisos, y
    /// unos pocos bastan para que el usuario deje de fiarse de la columna entera.
    /// </summary>
    public static bool SeExcluye(string filePath, out string motivo)
    {
        if (Matching.IsMashupFolder(Path.GetDirectoryName(filePath)))
        {
            motivo = "está en una carpeta de mashups";
            return true;
        }

        if (EsAcapella(filePath))
        {
            motivo = "es una acapella, no una grabación publicada";
            return true;
        }

        var p = FileNameParser.Parse(Path.GetFileName(filePath));
        if (Matching.IsSkipMix(p.Base, p.FnTitle, p.FnArtist))
        {
            motivo = "es una mezcla o una edición de DJ";
            return true;
        }

        motivo = "";
        return false;
    }

    /// <summary>
    /// Una acapella o percapella: solo la voz, sin instrumental. Se reconoce por el nombre o por
    /// vivir en una carpeta dedicada.
    ///
    /// Se excluye por la misma razón que un mashup: NO es una grabación publicada, es una
    /// herramienta de DJ. Medido sobre la biblioteca del autor, los servicios no las reconocen (664
    /// de 887 sin identificar) y cuando dicen algo suele ser una recopilación de DJ que las
    /// contiene: «China (Acapella)» devolvió «DJ Nicky Sensation – Latin Heat Live Mix 4» y
    /// «Feliz Navidad 3 (Acapella)» devolvió una versión de Peppa Pig. Eran diez de las cincuenta y
    /// nueve discrepancias, todas falsas.
    ///
    /// Cuesta perder las que sí se reconocían bien, pero un aviso correcto sobre una acapella no
    /// pedía ninguna acción, y un aviso falso sí gasta el tiempo de quien lo revisa.
    /// </summary>
    public static bool EsAcapella(string filePath)
    {
        var texto = Path.GetFileNameWithoutExtension(filePath) + " " + (Path.GetDirectoryName(filePath) ?? "");
        // Se escribe de muchas maneras y todas aparecen en una biblioteca real: acapella, accapella,
        // acappella, acapela, «a capella», percapella. La expresión admite las dobles consonantes
        // sueltas en vez de listar cada grafía, que siempre se queda corta.
        return Regex.IsMatch(texto, @"\b(?:a\s*c{1,2}a+|perc?a)p{1,2}e+l{1,2}a+s?\b|\baca\s*(?:in|out)\b",
                             RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Veredicto de una canción. <paramref name="id"/> a null significa que el servicio no la
    /// reconoció.
    /// </summary>
    public static Veredicto Comparar(string fileName, string? tagArtist, string? tagTitle, Identificado? id)
    {
        if (id is null || (id.Title.Length == 0 && id.Artist.Length == 0))
            return new Veredicto(VeredictoId.SinIdentificar, "el servicio no reconoció este audio");

        var p = FileNameParser.Parse(fileName);

        // El título del archivo puede venir del nombre o de la etiqueta. Se aceptan LOS DOS: hay
        // archivos bien etiquetados con el nombre hecho un desastre, y al revés. Basta con que uno
        // de los dos coincida para dar la canción por correcta.
        //
        // Se añade además el NOMBRE ENTERO del archivo como candidato, y eso resuelve dos cosas de
        // golpe, ambas vistas en una biblioteca real:
        //   · nombres al revés («Hombres Y Mujeres - Feid ft. Gordo»), donde el título está en el
        //     hueco del artista y ninguna regla de separación lo va a colocar bien;
        //   · títulos alternativos entre paréntesis («I Know You Want Me (Calle Ocho)»), que la
        //     limpieza tira porque para BUSCAR estorban, aunque para RECONOCER son justo la clave.
        var artistasArchivo = Artistas(p.FnArtist).Concat(Artistas(tagArtist)).Distinct().ToList();
        var titulosAudio = Candidatos(id.Title).Where(s => s.Length > 0).ToList();
        var artistasAudio = Artistas(id.Artist);

        var comoSuena = Comillas(id);

        // Lo que el archivo AFIRMA ser: solo el núcleo del título, y solo si dice algo. Esta lista
        // decide una única cosa —si hay algo que contrastar— y por eso se mantiene estrecha.
        var afirma = new[] { p.FnTitle, tagTitle }.Select(Nucleo).Where(Afirma).ToList();

        // Sin título por ningún lado no hay nada que contrastar: el archivo no afirma nada que
        // pueda estar mal. Se informa de lo que es, que además es justo lo que hacía falta.
        if (afirma.Count == 0)
            return new Veredicto(VeredictoId.SinIdentificar,
                $"el archivo no dice qué canción es; el audio es {comoSuena}");

        // Con qué se compara: más ancho que lo anterior, porque aquí solo se puede ABSOLVER. Todo
        // lo que se añada aquí puede salvar una canción, nunca condenarla.
        //
        // La distinción entre las dos listas es la lección de esta pasada: al meter las formas
        // largas en la primera, un archivo llamado «120 (Acapella)» pasó de «el nombre no dice qué
        // canción es» -que era correcto- a «no coincide», acusándolo por un dato que nunca afirmó.
        var comparables = afirma.Concat(Candidatos(p.FnTitle)).Concat(Candidatos(tagTitle))
                                .Where(s => s.Length > 0).Distinct().ToList();

        var tituloOk = titulosAudio.Any(a => comparables.Any(b => MismoTitulo(a, b)))
                    || SuenaEnElNombre(titulosAudio, p.Base);

        if (!tituloOk)
            return new Veredicto(VeredictoId.Difiere, $"el audio es {comoSuena}");

        // El título casa. Queda el artista, que solo se puede juzgar si el archivo lo trae: no
        // tener artista en el nombre es de lo más corriente y no es un error.
        //
        // Se comparan las LISTAS enteras, todos contra todos. El servicio y el archivo casi nunca
        // nombran a los mismos ni en el mismo orden -«Bad Bunny, Jowell & Randy, Nengo Flow» frente
        // a «Randy; Nengo Flow; Bad Bunny»-, y quedarse con el primero de cada lado convertía en
        // discrepancia lo que era la misma canción con los créditos barajados.
        // Igual que con el título, el nombre completo puede absolver: en «Somebody (Extended Mix) -
        // Gotye, FISHER, Chris Lake - bpm - DJTOOLSVIP» el hueco del artista lo ocupa el título, así
        // que los artistas de verdad quedan fuera de donde se los busca. Están en el nombre; basta
        // con mirar ahí antes de acusar.
        if (artistasArchivo.Count > 0 && artistasAudio.Count > 0
            && !artistasAudio.Any(a => artistasArchivo.Any(b => MismoArtista(a, b)))
            && !SuenaEnElNombre(artistasAudio, p.Base))
            return new Veredicto(VeredictoId.Difiere, $"el título coincide, pero el audio es de {id.Artist}");

        return new Veredicto(VeredictoId.Coincide, $"el audio es {comoSuena}");
    }

    /// <summary>
    /// Dos artistas son el mismo si lo dice <see cref="Matching.ArtistMatch"/> o si difieren en una
    /// errata. Los títulos ya toleraban erratas y los artistas no, y no había razón para la
    /// diferencia: «Dione Farris» por «Dionne Farris» o «Annuel Aa» por «Anuel AA» son faltas de
    /// quien tecleó el catálogo, no dos personas distintas.
    /// </summary>
    private static bool MismoArtista(string a, string b)
        => Matching.ArtistMatch(a, b)
        || (a.Length >= 5 && b.Length >= 5 && Matching.JaroWinkler(a, b) >= UmbralArtista);

    /// <summary>
    /// Palabras que ocupan el sitio de un título sin ser uno. Salen solas en cualquier biblioteca:
    /// descargas sin etiquetar, rips y grabaciones sueltas.
    /// </summary>
    private static readonly HashSet<string> Marcadores = new(StringComparer.Ordinal)
    {
        "track", "pista", "tema", "cancion", "song", "audio", "sonido",
        "untitled", "sintitulo", "unknown", "desconocido",
        "file", "archivo", "new", "nuevo", "rec", "recording", "grabacion", "mp",
    };

    /// <summary>
    /// ¿Este título dice de verdad QUÉ canción es?
    ///
    /// «01», «track 07» o «pista 3» no afirman nada: son un número de orden. Tomarlos por un título
    /// y contrastarlos con lo que dijo el audio los marcaría TODOS en rojo, cuando el archivo no se
    /// ha equivocado en nada; simplemente no dice de qué va. Esos casos se informan, no se acusan.
    ///
    /// El precio de la regla es que un título que de verdad sea una de esas palabras («Song 2»)
    /// tampoco se marcará. Es el lado correcto por el que fallar: dejar de avisar de un archivo
    /// cuesta una revisión perdida; avisar en falso de cientos hace que no se mire ninguno.
    /// </summary>
    private static bool Afirma(string nucleo)
    {
        var sinNumeros = Regex.Replace(nucleo, @"\d+", "");
        return sinNumeros.Length >= 2 && !Marcadores.Contains(sinNumeros);
    }

    /// <summary>
    /// Último recurso: ¿el título que dice el audio aparece en el NOMBRE COMPLETO del archivo,
    /// esté donde esté?
    ///
    /// Sirve para los nombres que están al revés -«Hombres Y Mujeres - Feid ft. Gordo», donde el
    /// título ocupa el hueco del artista- y para los que llevan el título de verdad metido entre
    /// paréntesis. Ninguna regla de separación va a colocar bien esos, pero el dato está ahí.
    ///
    /// Solo puede ABSOLVER, nunca acusar: se consulta cuando la comparación normal ya ha fallado, y
    /// no forma parte de lo que el archivo «afirma». Es una distinción que importa, porque hacerlo
    /// al revés convertía en discrepancia cosas de las que el archivo no decía nada -un tema
    /// llamado «1999» frente a un audio llamado «1999»- en vez de dejarlas en paz.
    /// </summary>
    private static bool SuenaEnElNombre(List<string> titulosAudio, string? baseName)
    {
        var nombre = TextUtils.Nk(baseName);
        if (nombre.Length == 0) return false;
        return titulosAudio.Any(t => t.Length >= MinimoParaContener && nombre.Contains(t));
    }

    /// <summary>Dos títulos son el mismo si son iguales, si uno contiene al otro, o si difieren en erratas.</summary>
    private static bool MismoTitulo(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;

        // Contención: el archivo suele traer de más («Titulo Dirty», el nombre del pool), nunca de
        // menos. Se exige un mínimo de longitud para que un título corto no case con todo.
        var corto = a.Length <= b.Length ? a : b;
        var largo = a.Length <= b.Length ? b : a;
        if (corto.Length >= MinimoParaContener && largo.Contains(corto)) return true;

        return Matching.JaroWinkler(a, b) >= UmbralTitulo;
    }

    /// <summary>
    /// Núcleo de un título: sin adornos de edición, sin BPM, sin tonalidad y normalizado. Es lo que
    /// queda de «Tití Me Preguntó (IVAN RF Hype Intro) 106 BPM 8A» y de «Titi Me Pregunto» por
    /// igual, que es exactamente de lo que va esto.
    /// </summary>
    private static string Nucleo(string? s)
        => TextUtils.Nk(Descriptors.CleanTitle(Descriptors.CleanKeywords(s)));

    /// <summary>
    /// Las dos formas de un mismo título que merece la pena comparar: el núcleo sin adornos y el
    /// texto completo tal cual.
    ///
    /// Hace falta el completo porque la limpieza tira los paréntesis, y ahí es donde vive muchas
    /// veces el título por el que se conoce el tema: «Got That Good (My Bubble Gum)» se quedaba en
    /// «Got That Good» y dejaba de casar con un archivo llamado «My Bubble Gum», siendo la misma
    /// canción. Lo que para BUSCAR en un catálogo es ruido, para RECONOCER es la clave.
    /// </summary>
    private static IEnumerable<string> Candidatos(string? s)
    {
        var nucleo = Nucleo(s);
        if (nucleo.Length > 0) yield return nucleo;

        var completo = TextUtils.Nk(s);
        if (completo.Length > 0 && completo != nucleo) yield return completo;
    }

    /// <summary>
    /// TODOS los artistas que nombra un texto, cada uno por su cuenta.
    ///
    /// Antes se cogía solo el primero, y eso convertía en discrepancia media colaboración: el
    /// catálogo devuelve «Randy; Nengo Flow; Bad Bunny» donde el archivo dice «Bad Bunny, Jowell &
    /// Randy, Nengo Flow». Son los mismos, en otro orden. Comparando las listas enteras basta con
    /// que coincida uno, que es lo que de verdad significa «es de este artista».
    /// </summary>
    private static List<string> Artistas(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new List<string>();

        // Se separa ANTES de limpiar, y este orden es el que importa: CleanKeywords convierte las
        // comas en espacios -para ella son ruido de una consulta-, así que limpiar primero dejaba
        // «Farruko, Rvssian, J Balvin» hecho un solo pegote que no casaba con «J.Balvin, Rvssian,
        // Farruko». Los mismos tres artistas, y salía discrepancia.
        return Regex.Split(s, @"\s*(?:,|;|&|/|\+|\bfeat\.?\b|\bft\.?\b|\bvs\.?\b|\bwith\b|\by\b)\s*",
                           RegexOptions.IgnoreCase)
                    .Select(parte => TextUtils.Nk(Descriptors.CleanKeywords(parte)))
                    .Where(a => a.Length >= 2)
                    .Distinct()
                    .ToList();
    }

    /// <summary>Cómo se nombra en pantalla lo que dice el audio.</summary>
    private static string Comillas(Identificado id)
        => id.Artist.Length > 0 && id.Title.Length > 0 ? $"«{id.Artist} – {id.Title}»"
         : id.Title.Length > 0 ? $"«{id.Title}»"
         : $"de {id.Artist}";
}
