using System.Text.RegularExpressions;

namespace Etiquetador.Core.Dj;

/// <summary>Dónde se ha encontrado el estilo de la versión.</summary>
public enum FuenteEstilo { Titulo, Archivo, Artista, Album }

/// <summary>
/// El estilo que nombra la propia versión de una canción: «Dividimos (Bachata Version)» es bachata
/// aunque el tema original sea reggaeton.
/// </summary>
/// <param name="Estilo">El nombre del estilo, ya unificado («Drum &amp; Bass» para «Dnb»).</param>
/// <param name="Texto">Lo que lo dice, tal como está escrito («Dnb Bootleg»).</param>
/// <param name="Ambiguo">«Afro» y «Electro» pueden nombrar varios estilos: hay que revisarlo.</param>
public sealed record EstiloVersion(string Estilo, string Texto, FuenteEstilo Fuente, bool Ambiguo);

/// <summary>
/// Lee en el título el estilo que indica la versión: «(Sandunga Moombahton Remix)», «(Versión
/// Rumba)», «- Reggaeton Mix», «(Salsa)». Es lo que pide un DJ: si el remix o la versión dice su
/// estilo, ese manda sobre el del tema original.
///
/// MEDIDO en una biblioteca real de 13.700 canciones: aparece en 114 (0,8 %), y revisadas a mano 112
/// son inequívocas y ninguna es un falso positivo; la etiqueta de género solo coincidía con él en el
/// 26 %, porque guarda el género del tema original. Lo que lo hace fiable es exigir el CONTEXTO de
/// versión: buscar el estilo en cualquier parte del título daba 353 falsos positivos («La Disco
/// Resplandece», «Mambo Kingz», «Trap Queen»), y por eso cuenta solo:
///   (a) justo delante de una palabra de versión («Moombahton Remix», «Techno VIP»), dentro de un
///       paréntesis o un corchete, tras un guion o detrás del nombre del tema («COCHINAE GUARACHA
///       REMIX DJ PROF EDIT»); al principio del título solo si es todo lo que dice;
///   (b) detrás de una palabra de versión y al final del fragmento, dentro de un paréntesis o tras un
///       guion («Versión Rumba», «Mashup Techno»);
///   (c) un paréntesis que, quitando el BPM, la clave Camelot, Extended, Clean y Dirty, es solo el
///       estilo («(Dembow)», «(Sevillanas)»).
/// Nunca cuentan «Latin», «Latino» ni «Latina» sueltos («Extended Latino» es el nombre de un pool:
/// 204 falsos positivos más), ni un fragmento de transición («Trap To Mambo Transition», «Transition
/// House To Reggaeton»), que junta dos estilos, ni el estilo detrás de un artículo («Llegamos A La
/// Disco Remix»: la palabra es parte del nombre de la canción) o de un «DJ» o un «MC» que abren el
/// nombre de quien hizo la versión («(DJ Mambo Remix)», «(Extended DJ Mambo Remix)», «(DJ Pepe x DJ
/// Salsa Remix)», «Gasolina DJ Mambo Remix»: son remixers, no el estilo; ver <see cref="NoEsElEstilo"/>).
/// Detrás de otro nombre, «DJ» lo cierra y el estilo sí cuenta: «(Alex Gramage DJ Mambo Remix)».
/// «Disco» solo cuenta en el contexto (c) («(Disco)») o como «Nu Disco»: «(Disco Mix)», «(Disco
/// Version)» o «Versión Disco» son el nombre clásico de la versión de 12" (la extendida) de cualquier
/// tema de los 80, no el estilo.
///
/// No se leen el comentario, el remixer (TPE4), el subtítulo (TIT3) ni la agrupación: medido, no
/// aportan ningún caso en 2.441 archivos, y leerlos costaría unos 8 minutos en una biblioteca así.
/// </summary>
public static class EstiloDeVersion
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Cada forma de escribir un estilo, con su nombre. Las compuestas van delante: en la misma
    /// posición gana la primera alternativa, y así «Afro House Remix» es Afro House y no Afro.
    /// </summary>
    private static readonly (string Patron, string Estilo)[] Vocabulario =
    {
        (@"afro[\s-]*house", "Afro House"),
        (@"tech[\s-]*house", "Tech House"),
        (@"deep[\s-]*house", "Deep House"),
        (@"bass[\s-]*house", "Bass House"),
        (@"progressive[\s-]*house", "Progressive House"),
        (@"electro[\s-]*house", "Electro House"),
        (@"future[\s-]*house", "Future House"),
        (@"slap[\s-]*house", "Slap House"),
        (@"tribal[\s-]*house", "Tribal House"),
        (@"latin[\s-]*house", "Latin House"),
        (@"funky[\s-]*house", "Funky House"),
        (@"melodic[\s-]*techno", "Melodic Techno"),
        (@"hard[\s-]*techno", "Hard Techno"),
        (@"latin[\s-]*tech", "Latin Tech"),
        (@"electro[\s-]*latino", "Electro Latino"),
        (@"jersey[\s-]*club", "Jersey Club"),
        (@"baile[\s-]*funk|funk[\s-]*carioca|brazilian[\s-]*funk", "Baile Funk"),
        (@"drum[\s-]*(?:&|and|n)[\s-]*bass|dnb|d&b", "Drum & Bass"),
        (@"uk[\s-]*garage", "UK Garage"),
        (@"big[\s-]*room", "Big Room"),
        (@"nu[\s-]*disco", "Nu Disco"),
        (@"psy[\s-]*trance", "Psytrance"),
        (@"hard[\s-]*style", "Hardstyle"),
        (@"moombahton|moombathon|moombah", "Moombahton"),
        (@"regg?a?e?t[oó]n|reguet[oó]n|reggeaton", "Reggaeton"),
        (@"dembow", "Dembow"),
        (@"guaracha|aleteo", "Guaracha"),
        (@"mambo", "Mambo"),
        (@"cumbiat[oó]n|cumbia", "Cumbia"),
        (@"bachata", "Bachata"),
        (@"salsa", "Salsa"),
        (@"merengue", "Merengue"),
        (@"rumbat[oó]n|rumbita|rumba", "Rumba"),
        (@"sevillanas?", "Sevillanas"),
        (@"flamenquito|flamenco", "Flamenco"),
        (@"amapiano", "Amapiano"),
        (@"afro[\s-]*beats?", "Afrobeats"),
        (@"afro", "Afro"),
        (@"dancehall", "Dancehall"),
        (@"reggae", "Reggae"),
        (@"kizomba", "Kizomba"),
        (@"rkt|turreo|cachengue", "RKT"),
        (@"tribal", "Tribal"),
        (@"techno", "Techno"),
        (@"trance", "Trance"),
        (@"dubstep", "Dubstep"),
        (@"trap", "Trap"),
        (@"drill", "Drill"),
        (@"phonk", "Phonk"),
        (@"breakbeat|breaks", "Breakbeat"),
        (@"disco", "Disco"),
        (@"house", "House"),
        (@"edm", "EDM"),
        (@"electro", "Electro"),
        (@"makina", "Makina"),
        (@"hardcore", "Hardcore"),
    };

    /// <summary>Pueden nombrar varios estilos: «Afro» (Afro House, Afrobeats…) y «Electro» (Electro House, electro latino…).</summary>
    private static readonly HashSet<string> Ambiguos = new(StringComparer.Ordinal) { "Afro", "Electro" };

    /// <summary>
    /// Junto a una palabra de versión nombran un tipo de versión, no el estilo: «(Disco Mix)» y
    /// «(Disco Version)» son la versión de 12" de cualquier tema de los 80 (Synth-Pop, Hi-NRG, Italo
    /// Disco…). Solo cuentan cuando el paréntesis no dice nada más («(Disco)»). «Nu Disco» es otra
    /// entrada del vocabulario y no se ve afectado.
    /// </summary>
    private static readonly HashSet<string> SoloSueltos = new(StringComparer.Ordinal) { "Disco" };

    /// <summary>El estilo leído junto a una palabra de versión (contextos a y b) cuenta como estilo.</summary>
    private static bool ValeJuntoAVersion(string escrito) => !SoloSueltos.Contains(Nombre(escrito));

    // El estilo como palabra completa: «Mambo» no está en «Mambos», ni «Trap» en «Trapero».
    private static readonly string Estilo = @"\b(?:" + string.Join("|", Vocabulario.Select(v => v.Patron)) + @")\b";

    // Lo que dice que el fragmento es una versión de la canción.
    private const string Version =
        @"\b(?:club[\s-]*edit|re[\s-]*edit|remix|rmx|edit|mix|versi[oó]n|bootleg|flip|rework|refix|vip|mash[\s-]*up|blend|cover|intro|extended|remake|tribute)\b";

    /// <summary>Filtro rápido: casi ningún título nombra un estilo, y esos no pasan de aquí.</summary>
    private static readonly Regex AlgunEstilo = new(Estilo, IC | RegexOptions.Compiled);

    // (a) El estilo delante de la palabra de versión.
    private static readonly Regex EstiloYVersion = new($@"(?<estilo>{Estilo})\s+{Version}", IC | RegexOptions.Compiled);

    // (b) La palabra de versión delante del estilo, al final del fragmento.
    private static readonly Regex VersionYEstilo = new($@"{Version}\s+(?<estilo>{Estilo})\s*$", IC | RegexOptions.Compiled);

    // (c) El fragmento entero es el estilo.
    private static readonly Regex SoloEstilo = new($@"^\s*(?<estilo>{Estilo})\s*$", IC | RegexOptions.Compiled);

    // Lo que acompaña a la versión sin describirla: el BPM, la clave Camelot, un número suelto, la
    // duración de la intro, Extended, Clean, Dirty.
    private static readonly Regex Morralla = new(
        @"\b\d{2,3}(?:[.,]\d{1,2})?\s*bpm\b|\b\d{1,2}[AB]\b|\b\d+\b|\b(?:extended|clean|dirty|explicit)\b", IC | RegexOptions.Compiled);

    // Un fragmento de transición junta dos estilos: ninguno es el de la canción.
    private static readonly Regex Transicion = new($@"\btransici[oó]n\b|\btransitions?\b|{Estilo}\s+to\b|\bto\s+{Estilo}", IC | RegexOptions.Compiled);

    private static readonly Regex Parentesis = new(@"[\(\[]([^\)\]]*)[\)\]]", RegexOptions.Compiled);

    // Un guion que separa partes del nombre («Ven Bailalo - Reggaeton Mix»), no el de «Re-Edit» ni el de «95-128».
    private static readonly Regex Guion = new(@"\s+[-–—]+\s*|\s*[-–—]+\s+", RegexOptions.Compiled);

    private static readonly (Regex Patron, string Estilo)[] Nombres =
        Vocabulario.Select(v => (new Regex($@"^(?:{v.Patron})$", IC | RegexOptions.Compiled), v.Estilo)).ToArray();

    /// <summary>
    /// El estilo que indica la versión, buscado por este orden: el título, el nombre del archivo (sin
    /// la extensión), el artista y el álbum. Gana el primero que lo diga. Lo que solo dice el álbum
    /// sale con <see cref="FuenteEstilo.Album"/>: suele describir el pack entero, no la canción.
    /// </summary>
    public static EstiloVersion? De(string? titulo, string? nombreArchivoSinExtension, string? artista, string? album)
        => Buscar(titulo, FuenteEstilo.Titulo) ?? Buscar(nombreArchivoSinExtension, FuenteEstilo.Archivo)
           ?? Buscar(artista, FuenteEstilo.Artista) ?? Buscar(album, FuenteEstilo.Album);

    private static EstiloVersion? Buscar(string? texto, FuenteEstilo fuente)
    {
        if (string.IsNullOrWhiteSpace(texto) || !AlgunEstilo.IsMatch(texto)) return null;

        // Primero lo que va entre paréntesis o corchetes, que es donde los pools escriben la versión.
        foreach (Match m in Parentesis.Matches(texto))
            if (EnParentesis(m.Groups[1].Value) is { } r) return Resultado(r, fuente);

        // Después el resto, partido por los guiones: lo de delante del primero es el nombre del tema.
        // Los paréntesis se cambian por una barra para que no junten lo que tienen a cada lado.
        var partes = Guion.Split(Parentesis.Replace(texto, " | "));
        // Las partes que forman el nombre del tema: la primera o, en el nombre del archivo, las dos
        // primeras («Artista - Título»). Un número de pista, la clave o el BPM sueltos delante
        // («01 - », «8A - 95 - ») no cuentan como parte.
        var nombre = fuente == FuenteEstilo.Archivo ? 2 : 1;
        for (var k = 0; k < partes.Length && SoloAcompana(partes[k]); k++) nombre++;
        for (var i = 0; i < partes.Length; i++)
            foreach (var trozo in partes[i].Split('|'))
                if (FueraDeParentesis(trozo, trasGuion: i > 0, enNombreDelTema: i < nombre) is { } r) return Resultado(r, fuente);
        return null;
    }

    // Un artículo o una preposición delante dice que la palabra es parte del nombre de la canción, no
    // su estilo: «Llegamos A La Disco Remix», «El Mambo Remix». Visto al validar en una biblioteca real.
    private static readonly HashSet<string> Articulos = new(StringComparer.OrdinalIgnoreCase)
    {
        "la", "el", "los", "las", "lo", "un", "una", "de", "del", "al", "a", "en", "the", "mi", "tu", "su", "my",
    };

    // «DJ» y «MC» delante del estilo; también escritos «D.J.», «Dj.» o «DJ-»: solo se miran las letras.
    private static readonly HashSet<string> Prefijos = new(StringComparer.OrdinalIgnoreCase) { "dj", "mc" };

    // Lo que separa a dos autores en un crédito: «DJ Mambo & DJ Trap», «DJ Pepe x DJ Salsa», «feat. MC Trap».
    private static readonly HashSet<string> Separadores = new(StringComparer.OrdinalIgnoreCase)
    {
        "x", "feat", "featuring", "ft", "prod", "vs", "by", "and", "y", "with", "con", "b2b",
    };
    private const string SignosSeparadores = "&+,/×";

    // Lo que acompaña a la versión sin ser un nombre: las palabras de versión («Intro», «Extended»…) y
    // las de los pools que la describen.
    private static readonly Regex AcompanaVersion = new(
        $@"{Version}|\b(?:outro|short|radio|quick[\s-]*hit|acc?app?ella|a[\s-]+capp?ella|instrumental)\b", IC | RegexOptions.Compiled);

    /// <summary>
    /// No queda ningún nombre: solo el BPM, la clave, números, Extended, Clean, Dirty, palabras de
    /// versión o signos. «Extended Intro», «95 BPM», «01», «8A».
    /// </summary>
    private static bool SoloAcompana(string s) => !AcompanaVersion.Replace(Limpio(s), " ").Any(char.IsLetter);

    /// <summary>Termina en un separador de créditos: «DJ Pepe x», «DJ Mambo &amp;», «DJ Pepe feat.».</summary>
    private static bool TrasSeparador(string s)
    {
        s = s.TrimEnd();
        if (s.Length == 0) return false;
        if (SignosSeparadores.Contains(s[^1])) return true;
        return Separadores.Contains(Letras(UltimaPalabra(s)));
    }

    private static string UltimaPalabra(string s)
    {
        var j = s.Length;
        while (j > 0 && !char.IsWhiteSpace(s[j - 1])) j--;
        return s[j..];
    }

    private static string Letras(string s) => new(s.Where(char.IsLetter).ToArray());

    /// <summary>
    /// Lo que va justo delante dice que la palabra no es el estilo de la versión:
    ///  · un artículo o una preposición: es parte del nombre de la canción;
    ///  · «DJ» o «MC» que abren el nombre artístico de quien hizo la versión («(DJ Mambo Remix)»). Lo
    ///    abren cuando delante solo hay lo que acompaña a la versión («(Extended DJ Mambo Remix)»,
    ///    «(Intro DJ Mambo Remix)», «(95 BPM DJ Mambo Remix)»), cuando hay un separador de créditos
    ///    («(DJ Mambo &amp; DJ Trap Remix)», «(DJ Pepe x DJ Salsa Remix)») o cuando, fuera de paréntesis,
    ///    lo de delante es el nombre del tema («Gasolina DJ Mambo Remix», «Artista - Gasolina DJ Mambo
    ///    Remix» en el nombre del archivo). Detrás de otro nombre, en cambio, lo cierran, y el estilo sí
    ///    lo es: «(Alex Gramage DJ Mambo Remix)», «- Alex Gramage DJ Mambo Remix», visto al validar en
    ///    una biblioteca real.
    /// </summary>
    private static bool NoEsElEstilo(string texto, int posicion, bool enNombreDelTema)
    {
        var antes = texto[..posicion].TrimEnd();
        var i = antes.Length;
        while (i > 0 && char.IsLetter(antes[i - 1])) i--;
        // Solo una palabra entera (revisión 3): la «A» de una clave Camelot («8A Reggaeton Remix») no es
        // la preposición.
        if (i < antes.Length && (i == 0 || !char.IsLetterOrDigit(antes[i - 1])) && Articulos.Contains(antes[i..])) return true;

        var palabra = UltimaPalabra(antes);
        if (!Prefijos.Contains(Letras(palabra))) return false;
        var delante = antes[..^palabra.Length];
        return enNombreDelTema || SoloAcompana(delante) || TrasSeparador(delante);
    }

    /// <summary>Dentro de un paréntesis o un corchete valen los tres contextos.</summary>
    private static (string Estilo, string Texto)? EnParentesis(string fragmento)
    {
        if (!AlgunEstilo.IsMatch(fragmento) || Transicion.IsMatch(fragmento)) return null;
        foreach (Match a in EstiloYVersion.Matches(fragmento))
            if (!NoEsElEstilo(fragmento, a.Index, enNombreDelTema: false) && ValeJuntoAVersion(a.Groups["estilo"].Value))
                return (a.Groups["estilo"].Value, a.Value.Trim());
        var limpio = Limpio(fragmento);
        if (VersionYEstilo.Match(limpio) is { Success: true } b && ValeJuntoAVersion(b.Groups["estilo"].Value))
            return (b.Groups["estilo"].Value, b.Value.Trim());
        if (SoloEstilo.Match(limpio) is { Success: true } c) return (c.Groups["estilo"].Value, "(" + fragmento.Trim() + ")");
        return null;
    }

    /// <summary>
    /// Fuera de los paréntesis: (a) si va detrás de algo (el nombre del tema), tras un guion o al final;
    /// (b) solo tras un guion. <paramref name="enNombreDelTema"/>: el trozo es el nombre del tema (o
    /// «Artista - Título» en el nombre del archivo), y un «DJ» dentro abre el nombre del remixer.
    /// </summary>
    private static (string Estilo, string Texto)? FueraDeParentesis(string trozo, bool trasGuion, bool enNombreDelTema)
    {
        if (!AlgunEstilo.IsMatch(trozo) || Transicion.IsMatch(trozo)) return null;
        foreach (Match a in EstiloYVersion.Matches(trozo))
        {
            if (NoEsElEstilo(trozo, a.Index, enNombreDelTema) || !ValeJuntoAVersion(a.Groups["estilo"].Value)) continue;
            var delante = trozo[..a.Index].Trim().Length > 0;
            var alFinal = Limpio(trozo[(a.Index + a.Length)..]).Length == 0;
            if (trasGuion || delante || alFinal) return (a.Groups["estilo"].Value, a.Value.Trim());
        }
        if (trasGuion && VersionYEstilo.Match(Limpio(trozo)) is { Success: true } b && ValeJuntoAVersion(b.Groups["estilo"].Value))
            return (b.Groups["estilo"].Value, b.Value.Trim());
        return null;
    }

    private static string Limpio(string s) => Regex.Replace(Morralla.Replace(s, " ").Replace(',', ' '), @"\s+", " ").Trim();

    private static EstiloVersion Resultado((string Estilo, string Texto) r, FuenteEstilo fuente)
    {
        var nombre = Nombre(r.Estilo);
        return new EstiloVersion(nombre, r.Texto, fuente, Ambiguos.Contains(nombre));
    }

    /// <summary>El nombre unificado de lo escrito: «Dnb» → Drum &amp; Bass, «Rumbaton» → Rumba, «Flamenquito» → Flamenco.</summary>
    private static string Nombre(string escrito)
    {
        var e = escrito.Trim();
        foreach (var (patron, estilo) in Nombres)
            if (patron.IsMatch(e)) return estilo;
        return TextUtils.TitleCase(e);
    }
}
