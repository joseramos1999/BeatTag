using Etiquetador.Core.Ai;

namespace Etiquetador.Core.Dj;

/// <summary>
/// Qué clase de género es uno: un nombre de mercado o de espectro («Latin», «Dance») que no dice
/// qué estilo se pincha, o un estilo concreto, y a qué familia de estilos pertenece.
///
/// Lo usa «Género por carpeta y artista» para decidir qué puede sustituir sin preguntar. MEDIDO en
/// una biblioteca real, lo que separa un arreglo de un estropicio al poner el género del artista no
/// es lo fuerte que sea el artista sino la clase de género que tiene la canción: si lleva un género
/// genérico, sustituirlo arregló 13 de 15 revisadas a mano sin estropear ninguna (y 155 de 178 sin
/// estropicios según las carpetas del usuario); si lleva otro estilo concreto, el 82 % eran arreglos,
/// pero no todos. Y dentro de una misma familia no se cambia nada: se perdería detalle (Trap bajo
/// Hip Hop, Electro House bajo EDM).
///
/// Las tablas salen de lo que significa cada género, no de una biblioteca: no hay ningún umbral
/// calibrado. Todo se compara con la misma clave que «Unificar géneros»: las grafías conocidas
/// unificadas (<see cref="GenreNormalizer.Canonical"/>), sin mayúsculas, acentos ni signos.
/// </summary>
public static class FamiliasGenero
{
    /// <summary>La clave con la que se compara: «Música Latina» y «musica latina» son la misma.</summary>
    public static string Clave(string? genero)
        => string.IsNullOrWhiteSpace(genero) ? "" : VocabularioBiblioteca.Clave(GenreNormalizer.Canonical(genero));

    private static HashSet<string> Claves(params string[] generos)
        => new(generos.Select(Clave).Where(k => k.Length > 0), StringComparer.Ordinal);

    /// <summary>
    /// Nombran un mercado o todo un espectro, no un estilo que se mezcle: «Latin» reúne reggaeton,
    /// bachata y salsa; «Dance» o «Electro», casi cualquier cosa con bombo. También los tipos de
    /// versión que algunos pools escriben en el campo Género.
    /// </summary>
    private static readonly HashSet<string> Genericos = Claves(
        "Latin", "Latino", "Latina", "Latin Music", "Música Latina", "Musica Latina", "Urbano", "Urbano Latino", "Latin Urban",
        "Música Urbana", "Urban", "World", "World Music", "Internacional", "International",
        "Dance", "Electro", "Electronic", "Electrónica", "Electronica",
        "Mashup", "Mash-up", "Mash Up", "Transition", "Transición", "Remix", "Edit", "Acapella", "Intro", "Extended");

    /// <summary>
    /// El género no dice qué estilo es: nombra un mercado o un espectro («Latin», «Dance»), es un tipo
    /// de versión usado como género («Mashup», «Extended») o no es un género («@usuario», un dominio,
    /// «Desconocido»). No son genéricos «EDM», «Pop», «Hip Hop» ni «Reggaeton»: con ellos un DJ ya
    /// sabe qué pincha. Un género vacío no es genérico: no tiene ninguno.
    /// </summary>
    public static bool EsGenerico(string? genero)
    {
        if (string.IsNullOrWhiteSpace(genero)) return false;
        if (GenerosIa.NoEsGenero(genero)) return true;
        return Genericos.Contains(Clave(genero));
    }

    public const string HipHop = "Hip Hop";
    public const string Tropical = "Tropical";
    public const string Electronica = "Electrónica";
    public const string PopRock = "Pop y rock";
    public const string Afro = "Afro";
    public const string Reggae = "Reggae";
    public const string Flamenco = "Flamenco";
    public const string Soul = "Soul";

    private static readonly (string Familia, HashSet<string> Claves)[] Familias =
    {
        (HipHop, Claves("Hip Hop", "Rap", "Trap", "Latin Trap", "Trap Latino", "Drill", "Grime", "Phonk")),
        (Tropical, Claves("Bachata", "Salsa", "Merengue", "Cumbia", "Vallenato", "Son", "Timba")),
        (Electronica, Claves("House", "Techno", "Trance", "Psytrance", "Drum & Bass", "Dubstep", "UK Garage", "Big Room", "EDM",
                             "Hardstyle", "Hardcore", "Breakbeat", "Electro House", "Electro Latino", "Latin Tech", "Makina",
                             "Nu Disco", "Jersey Club")),
        (PopRock, Claves("Pop", "Rock", "Indie", "Indie Pop", "Indie Rock", "Alternativo", "Alternative", "Pop Rock", "Synth-Pop",
                         "Dance-Pop", "Punk", "Metal", "Hard Rock", "Soft Rock")),
        (Afro, Claves("Afrobeats", "Afrobeat", "Afro", "Amapiano", "Afro Pop")),
        (Reggae, Claves("Reggae", "Dancehall", "Ragga")),
        (Flamenco, Claves("Flamenco", "Rumba", "Rumba Catalana", "Sevillanas", "Flamenco Pop", "Bulerías")),
        (Soul, Claves("R&B", "Soul", "Funk", "Disco")),
    };

    /// <summary>
    /// La familia del estilo, solo para saber si dos estilos son parientes: Trap y Hip Hop, Bachata y
    /// Salsa, Tech House y EDM. Cualquier «… House» o «… Techno» es electrónica (también Afro House).
    ///
    /// Null a propósito para los que un DJ distingue aunque suenen cerca, o que son ambiguos:
    /// Reggaeton, Dembow, Moombahton, Mambo, Guaracha, RKT. Un género sin familia cuenta como distinto
    /// de cualquier otro: «(Sandunga Moombahton Remix)» sobre una canción etiquetada Reggaeton sí es
    /// otro estilo. También null para lo genérico y lo que la tabla no conoce.
    /// </summary>
    public static string? Familia(string? genero)
    {
        var k = Clave(genero);
        if (k.Length == 0) return null;
        // Antes que «Afro»: Afro House se pincha con la electrónica. Sin exigir el espacio, porque
        // también se escribe junto («Afrohouse», «Techhouse»).
        if (k.EndsWith("house", StringComparison.Ordinal) || k.EndsWith("techno", StringComparison.Ordinal))
            return Electronica;
        foreach (var (familia, claves) in Familias)
            if (claves.Contains(k)) return familia;
        return null;
    }

    /// <summary>Los dos son estilos de la misma familia (ninguno sin familia).</summary>
    public static bool MismaFamilia(string? a, string? b)
        => Familia(a) is { } fa && string.Equals(fa, Familia(b), StringComparison.Ordinal);
}
