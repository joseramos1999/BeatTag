using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Etiquetador.Core.Ai;

namespace Etiquetador.Core.Dj;

/// <summary>
/// Una lista que se rellena sola a partir de unas condiciones: «house vocal, energía 6-7, para
/// apertura». No guarda canciones, guarda el criterio; por eso sigue al día cuando entra música
/// nueva o se completa una ficha.
///
/// Todas las condiciones se tienen que cumplir a la vez. Dentro de los momentos basta con uno de
/// los marcados (una canción de «Subida» sirve en una colección de «Subida o Pico»); en ambiente y
/// etiquetas, en cambio, hacen falta todos, porque cada uno se añade para afinar.
/// </summary>
public sealed class ColeccionInteligente
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Nombre { get; set; } = "Nueva colección";

    /// <summary>Palabras que deben aparecer en artista, título o nombre del archivo.</summary>
    public string Texto { get; set; } = "";

    /// <summary>
    /// Palabras que debe contener el género («house» encuentra también «Deep House», pero «rap» no
    /// encuentra «Trap»). Varios separados por comas valen como «cualquiera de ellos»: «cumbia, merengue».
    /// </summary>
    public string Genero { get; set; } = "";

    /// <summary>Límites de año de publicación. Cero significa sin límite.</summary>
    public int AnioMin { get; set; }
    public int AnioMax { get; set; }

    /// <summary>Límites de tempo. Cero significa sin límite.</summary>
    public int BpmMin { get; set; }
    public int BpmMax { get; set; }

    /// <summary>Límites de energía, de 1 a 10. Cero significa sin límite.</summary>
    public int EnergiaMin { get; set; }
    public int EnergiaMax { get; set; }

    public MomentoSet Momentos { get; set; }
    public TipoVoz Voz { get; set; }
    public TipoLetra Letra { get; set; }
    public string Idioma { get; set; } = "";
    public List<string> Ambiente { get; set; } = new();
    public List<string> Etiquetas { get; set; } = new();
    public bool SoloArmasSecretas { get; set; }

    /// <summary>Otra colección con las mismas condiciones y un Id nuevo.</summary>
    public ColeccionInteligente Copia(string nombre) => new()
    {
        Nombre = nombre,
        Texto = Texto, Genero = Genero, BpmMin = BpmMin, BpmMax = BpmMax, AnioMin = AnioMin, AnioMax = AnioMax,
        EnergiaMin = EnergiaMin, EnergiaMax = EnergiaMax, Momentos = Momentos, Voz = Voz, Letra = Letra,
        Idioma = Idioma, Ambiente = new List<string>(Ambiente), Etiquetas = new List<string>(Etiquetas),
        SoloArmasSecretas = SoloArmasSecretas,
    };
}

public static class FiltroColeccion
{
    /// <summary>
    /// Tiene alguna condición. Una colección sin ninguna abarcaría la biblioteca entera, que no es
    /// una colección sino la biblioteca, y exportarla como lista sería un error fácil de cometer.
    /// </summary>
    public static bool TieneCriterios(ColeccionInteligente c) =>
        c.Texto.Trim().Length > 0 || c.Genero.Trim().Length > 0 ||
        c.BpmMin > 0 || c.BpmMax > 0 || c.EnergiaMin > 0 || c.EnergiaMax > 0 || c.AnioMin > 0 || c.AnioMax > 0 ||
        c.Momentos != MomentoSet.Ninguno || c.Voz != TipoVoz.SinIndicar || c.Letra != TipoLetra.SinIndicar ||
        c.Idioma.Trim().Length > 0 || c.Ambiente.Count > 0 || c.Etiquetas.Count > 0 || c.SoloArmasSecretas;

    /// <summary>
    /// La canción entra en la colección. Si una condición pide un dato de la ficha y la canción no
    /// lo tiene, NO entra: una colección de «energía 8 o más» no puede llenarse de temas que nadie ha
    /// valorado todavía.
    /// </summary>
    public static bool Cumple(ColeccionInteligente c, Track t, FichaDj? f)
    {
        if (!TieneCriterios(c)) return false;

        if (c.Texto.Trim().Length > 0 && !BusquedaTexto.Coincide(c.Texto, t.Artist, t.Title, t.FileName)) return false;

        if (c.Genero.Trim().Length > 0)
        {
            var buscados = ClavesPedidas(c.Genero);
            if (buscados.Length > 0)
            {
                var delTema = ClavesDelTema(t.Genre);
                if (!buscados.Any(b => delTema.Any(g => ContieneGenero(g, b)))) return false;
            }
        }

        // Sin año conocido no se puede afirmar que sea de esa época.
        if (c.AnioMin > 0 && (t.Year == 0 || t.Year < c.AnioMin)) return false;
        if (c.AnioMax > 0 && (t.Year == 0 || t.Year > c.AnioMax)) return false;

        // Sin BPM conocido no se puede decir que esté dentro del margen.
        if (c.BpmMin > 0 && (t.Bpm == 0 || t.Bpm < c.BpmMin)) return false;
        if (c.BpmMax > 0 && (t.Bpm == 0 || t.Bpm > c.BpmMax)) return false;

        if (c.EnergiaMin > 0 || c.EnergiaMax > 0)
        {
            var e = f?.Energia ?? 0;
            if (e == 0) return false;
            if (c.EnergiaMin > 0 && e < c.EnergiaMin) return false;
            if (c.EnergiaMax > 0 && e > c.EnergiaMax) return false;
        }

        if (c.Momentos != MomentoSet.Ninguno && (f == null || (f.Momentos & c.Momentos) == 0)) return false;
        if (c.Voz != TipoVoz.SinIndicar && f?.Voz != c.Voz) return false;
        if (c.Letra != TipoLetra.SinIndicar && Fichas.LetraEfectiva(f, t) != c.Letra) return false;

        if (c.Idioma.Trim().Length > 0 && (f == null || Fichas.Clave(f.Idioma) != Fichas.Clave(c.Idioma))) return false;

        if (!ContieneTodas(f?.Ambiente, c.Ambiente)) return false;
        if (!ContieneTodas(f?.Etiquetas, c.Etiquetas)) return false;

        if (c.SoloArmasSecretas && f?.ArmaSecreta != true) return false;
        return true;
    }

    // --- Género ---
    //
    // Se compara por PALABRAS. Antes se buscaba un trozo de texto sin espacios ni signos, y MEDIDO en
    // una biblioteca real (237 géneros distintos): «rap» metía además 432 canciones de «Trap», y
    // «dance» 82 de «Dancehall» y 2 de «Eurodance». Por palabras, «house» sigue encontrando «Deep
    // House», que es lo que se quiere: un estilo dentro de otro.
    //
    // Las claves se calculan una vez por texto y se reutilizan: una colección se evalúa contra
    // quince mil canciones, pero en una biblioteca hay unos cientos de géneros distintos.

    private static readonly ConcurrentDictionary<string, string[]> _clavesPedidas = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string[]> _clavesDelTema = new(StringComparer.Ordinal);

    /// <summary>
    /// Los géneros pedidos, cada uno tal cual y con su sinónimo conocido: «rap» también es «Hip Hop»
    /// para <see cref="GenreNormalizer"/>, y así lo cuenta el Asistente al proponer colecciones.
    /// </summary>
    private static string[] ClavesPedidas(string genero)
        => Recordar(_clavesPedidas, genero, g => Fichas.PartirLista(g)
            .SelectMany(x => new[] { VocabularioBiblioteca.Clave(x), VocabularioBiblioteca.Clave(GenreNormalizer.Canonical(x)) })
            .Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToArray());

    /// <summary>
    /// El género de la canción entero y cada uno de los que lista («Rap; Hip Hop; Latino»), también
    /// con su sinónimo conocido: una canción etiquetada «Rap» entra en una colección de «Hip Hop».
    /// </summary>
    private static string[] ClavesDelTema(string? genero)
    {
        if (string.IsNullOrWhiteSpace(genero)) return Array.Empty<string>();
        return Recordar(_clavesDelTema, genero, g => new[] { VocabularioBiblioteca.Clave(g) }
            .Concat(Regex.Split(g, @"\s*[/;,|]\s*").Select(s => VocabularioBiblioteca.Clave(GenreNormalizer.Canonical(s))))
            .SelectMany(k => EstiloQueContiene.TryGetValue(k, out var padre) ? new[] { k, padre } : new[] { k })
            .Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Géneros de UNA palabra que son un estilo dentro de otro, como «Deep House» lo es de «House».
    /// Comparando por palabras, «pop» dejó de encontrar «Electropop» (un canónico de
    /// GenreNormalizer) y «house» «Housemusic», que sí son ese estilo. Va en una lista cerrada a
    /// propósito: «Eurodance» no entra en «dance», ni «Electronica» en «electro», que es otro
    /// género, igual que «Trap» no entra en «rap».
    /// </summary>
    private static readonly Dictionary<string, string> EstiloQueContiene = new(StringComparer.Ordinal)
    {
        ["electropop"] = "pop",
        ["housemusic"] = "house",
    };

    private static string[] Recordar(ConcurrentDictionary<string, string[]> cache, string clave, Func<string, string[]> calcular)
    {
        if (cache.Count > 4096) cache.Clear();   // nunca debería pasar: son géneros, no canciones
        return cache.GetOrAdd(clave, calcular);
    }

    /// <summary>Terminaciones de género y número que no cambian el estilo: «Latin» / «Latino» / «Latina».</summary>
    private static readonly string[] Terminaciones = { "s", "es", "o", "a", "os", "as" };

    /// <summary>
    /// <paramref name="claveGenero"/> contiene las palabras de <paramref name="claveBuscada"/>, seguidas
    /// y enteras. Se admite la misma palabra con otra terminación de género o número, porque comparando
    /// por palabras «latin» dejaba de encontrar las 27 canciones de «Latino» que antes sí encontraba.
    /// </summary>
    private static bool ContieneGenero(string claveGenero, string claveBuscada)
    {
        var tema = claveGenero.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pedido = claveBuscada.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (pedido.Length == 0) return false;
        for (var i = 0; i + pedido.Length <= tema.Length; i++)
        {
            var todas = true;
            for (var k = 0; k < pedido.Length && todas; k++) todas = MismaPalabra(tema[i + k], pedido[k]);
            if (todas) return true;
        }
        return false;
    }

    private static bool MismaPalabra(string a, string b)
        => string.Equals(a, b, StringComparison.Ordinal) || Flexion(a, b) || Flexion(b, a);

    /// <summary>
    /// <paramref name="larga"/> es <paramref name="corta"/> con una terminación de género o número.
    /// Solo con palabras de cuatro letras o más: con menos, «pop» + «s» o «rap» + «a» ya no son el estilo.
    /// </summary>
    private static bool Flexion(string larga, string corta)
        => corta.Length >= 4 && larga.Length > corta.Length && larga.StartsWith(corta, StringComparison.Ordinal)
           && Terminaciones.Contains(larga[corta.Length..], StringComparer.Ordinal);

    private static bool ContieneTodas(List<string>? tiene, List<string> pedidas)
    {
        if (pedidas.Count == 0) return true;
        if (tiene == null || tiene.Count == 0) return false;
        var claves = tiene.Select(Fichas.Clave).ToHashSet(StringComparer.Ordinal);
        return pedidas.All(p => claves.Contains(Fichas.Clave(p)));
    }
}

/// <summary>Las colecciones guardadas, en el orden en que el usuario las creó.</summary>
public sealed class AlmacenColecciones
{
    private readonly string _archivo;

    public List<ColeccionInteligente> Todas { get; }
    public string AvisoCarga { get; }

    /// <summary>No se pudo leer ni apartar el archivo al abrirlo: <see cref="Guardar"/> no lo pisa.</summary>
    public bool SoloLectura { get; }

    public AlmacenColecciones(string archivo)
    {
        _archivo = archivo;
        Todas = ArchivoJson.Leer(archivo, () => new List<ColeccionInteligente>(), out var aviso, out var soloLectura);
        AvisoCarga = aviso;
        SoloLectura = soloLectura;
    }

    /// <summary>Devuelve "" si fue bien, o el motivo del fallo. En solo lectura no escribe nada.</summary>
    public string Guardar() => SoloLectura ? ArchivoJson.AvisoSoloLectura(_archivo) : ArchivoJson.Escribir(_archivo, Todas);
}
