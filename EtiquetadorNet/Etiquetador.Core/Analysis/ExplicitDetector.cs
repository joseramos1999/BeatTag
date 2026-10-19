using System.Text.RegularExpressions;

namespace Etiquetador.Core.Analysis;

public enum Explicitness { Unknown, Clean, Explicit }

/// <summary>
/// Deduce si una pista es "clean" o explícita/"dirty" a partir del nombre y el título.
///
/// Los marcadores se buscan SOLO donde va la versión: entre paréntesis o corchetes, en lo que
/// sigue al último paréntesis («(Manu Garcia Edit) Clean») y en los trozos separados por « - »
/// que van detrás del título («Artista - Tema - Clean»). Nunca en el artista ni en el título en
/// sí. Antes se buscaban en el nombre entero y «Clean Bandit - Rockabye.mp3» salía como Limpia y
/// entraba en una colección «solo limpias» para una boda, que es justo el fallo que más daño hace;
/// al revés, «Dirty Vegas - Days Go By» salía como Explícita, y «Talk Dirty (Clean Version)»
/// también, porque se miraba «dirty» antes que «clean».
/// </summary>
public static class ExplicitDetector
{
    private static readonly Regex ExplicitRe = new(
        @"\b(explicit|explicita|explícita|dirty|uncensored|sucia)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // «Radio Edit» ya no cuenta como limpia: dice cuánto dura la versión, no cómo es la letra. En
    // una biblioteca real hay 18 «Radio Edit» y muchos conservan la letra explícita.
    private static readonly Regex CleanRe = new(
        @"\b(clean|censored|censurada|limpia)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Lo que va entre paréntesis o corchetes.</summary>
    private static readonly Regex Parentesis = new(@"[\(\[]([^\(\)\[\]]*)[\)\]]", RegexOptions.Compiled);

    /// <summary>Separadores entre descriptores dentro de una misma zona: «(Remix, Clean, Radio Edit)».</summary>
    private static readonly Regex Separadores = new(@"\s+[-–]\s+|[,;/|+]", RegexOptions.Compiled);

    private static readonly Regex Palabras = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>
    /// Palabras que pueden ir DETRÁS del marcador sin que deje de serlo: «Clean Extended»,
    /// «Dirty Intro», «Clean Version». Si detrás viene otra cosa, el marcador es parte de un
    /// nombre: «(Dirty Sou Mashup)» es un DJ (Dirty Sou firma 13 mashups en la
    /// biblioteca real) y «(Clean Bandit Remix)», un grupo.
    /// </summary>
    private static readonly HashSet<string> Descriptores = new(StringComparer.OrdinalIgnoreCase)
    {
        "version", "versión", "ver", "edit", "edits", "extended", "intro", "outro", "introoutro",
        "mix", "short", "radio", "club", "original", "quick", "hit", "hype", "transition", "clap",
        "in", "out", "acapella", "acappella", "instrumental", "redrum", "remix", "vocal", "main",
        "album", "single", "cut", "lyrics", "bar", "bars", "dj", "bpm", "letra", "and", "y",
        "mashup",   // «(Clean Mashup)»: el marcador sigue siendo el de la letra
    };

    /// <summary>
    /// Palabras que presentan a quien firma la versión: «Clean By Dj», «(Dirty Intro by DJ X)».
    /// Lo que va detrás es un nombre y no anula el marcador. Visto en el propio explicit_eval.txt
    /// del equipo: «Diabla en Bellakera (Extended) - Clean By Dj.mp3» pasaba de Limpia a sin indicar
    /// porque «By» no era un descriptor. Iba en la dirección segura, pero era un marcador verdadero.
    /// </summary>
    private static readonly HashSet<string> Autoria = new(StringComparer.OrdinalIgnoreCase) { "by", "por", "de" };

    /// <summary>
    /// Lo que se sabe de la letra: lo que dice el nombre y, si no dice nada, lo que dice el catálogo
    /// (ver <see cref="Combinar"/> y <see cref="PonerCatalogo"/>). Es lo que usan la columna
    /// «Explícito», las estadísticas y la letra de la ficha cuando el DJ no la ha indicado.
    /// </summary>
    public static Explicitness Detect(Track t) => Combinar(DelNombre(t), DelCatalogo(t.FilePath));

    /// <summary>Lo mismo con el dato del catálogo a mano (el de un análisis concreto).</summary>
    public static Explicitness Detect(Track t, Explicitness catalogo) => Combinar(DelNombre(t), catalogo);

    /// <summary>
    /// Solo lo que dice el NOMBRE (y el título del tag). Es lo que hay que comparar entre dos archivos
    /// para saber si son la misma versión, y lo que se le dice al catálogo para elegir entre la clean y
    /// la explícita (C14): el dato del propio catálogo ahí no pinta nada.
    /// </summary>
    public static Explicitness DelNombre(Track t)
    {
        var limpia = false;
        foreach (var zona in Zonas(Path.GetFileNameWithoutExtension(t.FileName), esNombreDeArchivo: true)
                     .Concat(Zonas(t.Title ?? "", esNombreDeArchivo: false)))
        {
            foreach (var trozo in Separadores.Split(zona))
            {
                // Explícita manda: si una zona lo dice, no se arriesga a llamarla limpia.
                if (EsMarcador(trozo, ExplicitRe)) return Explicitness.Explicit;
                if (EsMarcador(trozo, CleanRe)) limpia = true;
            }
        }
        return limpia ? Explicitness.Clean : Explicitness.Unknown;
    }

    /// <summary>
    /// Las partes del texto donde se describe la versión. En el nombre de archivo, los trozos
    /// separados por « - » a partir del tercero (el primero es el artista y el segundo el título);
    /// en el título, a partir del segundo.
    /// </summary>
    private static IEnumerable<string> Zonas(string texto, bool esNombreDeArchivo)
    {
        if (string.IsNullOrWhiteSpace(texto)) yield break;

        foreach (Match m in Parentesis.Matches(texto)) yield return m.Groups[1].Value;

        var cierre = texto.LastIndexOfAny(new[] { ')', ']' });
        if (cierre >= 0 && cierre + 1 < texto.Length) yield return texto[(cierre + 1)..];

        var segmentos = texto.Split(" - ");
        for (var k = esNombreDeArchivo ? 2 : 1; k < segmentos.Length; k++) yield return segmentos[k];
    }

    /// <summary>
    /// ¿Es este trozo un marcador de letra? Contiene la palabra y lo que va detrás de ella son solo
    /// descriptores de versión o cifras («Clean Extended», «Dirty 95 Bpm»). Lo de delante da igual:
    /// «Intro Clean», «IntroOutro Dirty».
    /// </summary>
    private static bool EsMarcador(string trozo, Regex marcador)
    {
        foreach (Match m in marcador.Matches(trozo))
        {
            var detras = trozo[(m.Index + m.Length)..];
            var esMarcador = true;
            foreach (Match p in Palabras.Matches(detras))
            {
                if (Descriptores.Contains(p.Value) || EsCifra(p.Value)) continue;
                // La primera palabra que no describe la versión decide: si presenta al autor
                // («by», «por», «de»), lo que sigue es su nombre; si no, el marcador era parte de un
                // nombre («Clean Bandit Remix», «Dirty Sou Mashup»).
                esMarcador = Autoria.Contains(p.Value);
                break;
            }
            if (esMarcador) return true;
        }
        return false;
    }

    /// <summary>
    /// «95», «2024», «128bpm» y también la tonalidad Camelot («8A», «12B»), que los pools de DJ
    /// ponen junto a la versión. Visto en una biblioteca real: «(Deville 126-90 Toneplay Trans)
    /// Clean 8A 126 - Bad Bunny, Enur - bpm - DJTOOLSVIP».
    /// </summary>
    private static bool EsCifra(string palabra)
    {
        var i = 0;
        while (i < palabra.Length && char.IsAsciiDigit(palabra[i])) i++;
        if (i == 0) return false;
        var resto = palabra[i..];
        return resto.Length == 0
               || resto.Equals("bpm", StringComparison.OrdinalIgnoreCase)
               || (i <= 2 && resto is "A" or "B" or "a" or "b");
    }

    public static string Label(Explicitness e) => e switch
    {
        Explicitness.Explicit => "Explícito",
        Explicitness.Clean => "Clean",
        _ => "",
    };

    public static string Label(Track t) => Label(Detect(t));

    // ---- El dato del catálogo (C14) ----
    //
    // Hasta ahora solo se miraba el nombre, y la mayoría de canciones salían sin indicar: solo los
    // pools ponen «Clean» o «Dirty». Deezer da la marca de cada pista en las mismas respuestas que ya
    // se piden al analizar. MEDIDO en las respuestas de Deezer guardadas en la caché de una biblioteca
    // real: de 78.975 pistas, 16.187 explícitas (20 %), 358 «editadas» (la versión clean de un tema
    // explícito), 53.178 «no explícitas» y el resto sin dato.

    /// <summary>
    /// Junta lo que dice el nombre con lo que dice el catálogo.
    ///
    /// Manda el nombre: es la versión de ESTE archivo. «Tusa (Clean)» es limpia aunque el catálogo dé
    /// el tema por explícito, porque es la edición de radio que publica el pool.
    ///
    /// Del catálogo solo se acepta «explícita». «No explícita» es la marca por defecto de muchas
    /// distribuidoras, no una garantía, y su «editada» puede ser la versión clean de un tema del que
    /// el archivo es la explícita: si el nombre no lo dice, el catálogo pudo elegir cualquiera de las
    /// dos. Dar por limpia una canción que no lo es la metería en una colección «solo limpias» para
    /// una boda, que es el fallo que más daño hace; equivocarse hacia «explícita» solo aparta una de más.
    /// </summary>
    public static Explicitness Combinar(Explicitness delNombre, Explicitness delCatalogo)
    {
        if (delNombre != Explicitness.Unknown) return delNombre;
        return delCatalogo == Explicitness.Explicit ? Explicitness.Explicit : Explicitness.Unknown;
    }

    // Concurrente: el análisis anota canción a canción fuera del hilo de la interfaz mientras la tabla
    // de la biblioteca lo consulta por cada fila visible. Consultarlo no toca el disco.
    private static System.Collections.Concurrent.ConcurrentDictionary<string, Explicitness> _catalogo = Nuevo();

    private static System.Collections.Concurrent.ConcurrentDictionary<string, Explicitness> Nuevo()
        => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fija lo que dice el catálogo de cada archivo, por ruta (null lo vacía). Lo pone el motor al
    /// cargar la caché de análisis, que guarda la marca de cada canción que identificó Deezer (ver
    /// <c>AnalysisCache.LetraDelCatalogo</c>). Se construye aparte y se sustituye de una vez.
    /// </summary>
    public static void PonerCatalogo(IEnumerable<KeyValuePair<string, Explicitness>>? porRuta)
    {
        var copia = Nuevo();
        if (porRuta != null)
            foreach (var (ruta, letra) in porRuta)
                if (letra != Explicitness.Unknown && !string.IsNullOrEmpty(ruta)) copia[ruta] = letra;
        Volatile.Write(ref _catalogo, copia);
    }

    /// <summary>
    /// Anota lo que acaba de decir el catálogo de un archivo, tras analizarlo. Sin dato
    /// (<see cref="Explicitness.Unknown"/>) se quita lo que hubiera: un análisis nuevo manda sobre uno
    /// anterior. Con <paramref name="sustituir"/> a false solo se anota si no había nada, que es lo
    /// que hace falta para la ruta que tendrá el archivo al aplicar el nombre propuesto (ver
    /// <c>AnalysisCache.RutasDeLaLetra</c>): no puede pisar lo que dijo el análisis de ese otro archivo.
    /// </summary>
    public static void AnotarCatalogo(string? ruta, Explicitness letra, bool sustituir = true)
    {
        if (string.IsNullOrEmpty(ruta)) return;
        var mapa = Volatile.Read(ref _catalogo);
        if (letra == Explicitness.Unknown) { if (sustituir) mapa.TryRemove(ruta, out _); }
        else if (sustituir) mapa[ruta] = letra;
        else mapa.TryAdd(ruta, letra);
    }

    /// <summary>Lo que dice el catálogo de ese archivo, o <see cref="Explicitness.Unknown"/>.</summary>
    public static Explicitness DelCatalogo(string? ruta)
    {
        var mapa = Volatile.Read(ref _catalogo);
        return !string.IsNullOrEmpty(ruta) && mapa.TryGetValue(ruta, out var e) ? e : Explicitness.Unknown;
    }
}
