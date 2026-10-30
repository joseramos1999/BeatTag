namespace Etiquetador.Core.Dj;

/// <summary>La regla de un artista: cómo se llama (como se ve) y su género ("" = excluido de la regla automática).</summary>
public sealed record EntradaGeneroArtista(string Nombre, string Genero);

/// <summary>
/// El mapa artista → género que rellena el usuario («Anuel AA» → Reggaeton, «David Guetta» → EDM),
/// guardado en su propio archivo (generos-por-artista.json, en la carpeta de datos). Es lo que pide
/// un DJ cuando la regla automática no llega: medido en una biblioteca real, Anuel AA lleva Reggaeton
/// en el 58 % de sus canciones con género (el resto es Trap de verdad), David Guetta reparte 14 en 8
/// géneros, y de muchos artistas solo hay una canción.
///
/// Como el mapa de carpetas (<see cref="MapaGenerosCarpeta"/>), es trabajo hecho a mano: se escribe
/// de forma atómica, un archivo ilegible se aparta en vez de pisarse y uno que no se pudo leer por un
/// bloqueo no se sobrescribe (<see cref="ArchivoJson"/>). Entra en la copia de seguridad con los demás
/// datos.
///
/// La clave es el artista PRINCIPAL (el primero del crédito), con <see cref="GeneroPorReglas.ClaveArtista"/>:
/// sin mayúsculas, acentos ni signos. MEDIDO: en las colaboraciones, el primer artista acierta el estilo
/// un 71,5 %, frente al 61,1 % del segundo y el 60,4 % de una votación entre todos; así «Besos Dorados
/// (Antony Z; Pablo Alborán)», que es merengue, no cuenta para Pablo Alborán.
///
/// Un artista con el género vacío ("") está EXCLUIDO: no recibe la regla automática del dominante.
/// </summary>
public sealed class MapaGenerosArtista
{
    /// <summary>El nombre del archivo dentro de la carpeta de datos.</summary>
    public const string NombreArchivo = "generos-por-artista.json";

    private readonly string _archivo;
    private readonly Dictionary<string, EntradaGeneroArtista> _reglas = new(StringComparer.Ordinal);

    public string AvisoCarga { get; }

    /// <summary>No se pudo leer ni apartar el archivo al abrirlo: <see cref="Guardar"/> no lo pisa.</summary>
    public bool SoloLectura { get; }

    public MapaGenerosArtista(string archivo)
    {
        _archivo = archivo;
        var leido = ArchivoJson.Leer(archivo, () => new Dictionary<string, EntradaGeneroArtista>(), out var aviso, out var soloLectura);
        foreach (var (clave, e) in leido)
        {
            // La clave se vuelve a calcular: así un archivo editado a mano («Anuel AA») sigue valiendo.
            var k = Clave(clave);
            if (k.Length == 0 || e == null) continue;
            _reglas[k] = new EntradaGeneroArtista((e.Nombre ?? "").Trim(), (e.Genero ?? "").Trim());
        }
        AvisoCarga = aviso;
        SoloLectura = soloLectura;
    }

    public static string RutaEn(AppPaths paths) => Path.Combine(paths.DataDir, NombreArchivo);

    /// <summary>
    /// La clave de un artista o de un crédito entero: la del artista principal. «Antonio Orozco; Pablo
    /// Alboran» es la de Antonio Orozco, y «ANUEL AA» la misma que «Anuel AA».
    /// </summary>
    public static string Clave(string? artista) => GeneroPorReglas.ClaveArtista(artista);

    /// <summary>Las reglas, por clave del artista.</summary>
    public IReadOnlyDictionary<string, EntradaGeneroArtista> Reglas => _reglas;

    /// <summary>Clave → género, como lo usa <see cref="GeneroPorReglas.Calcular"/> ("" = excluido).</summary>
    public Dictionary<string, string> Generos() => _reglas.ToDictionary(r => r.Key, r => r.Value.Genero, StringComparer.Ordinal);

    /// <summary>Cuántos artistas tienen un género asignado.</summary>
    public int ConGenero => _reglas.Values.Count(e => e.Genero.Length > 0);

    /// <summary>Cuántos artistas están excluidos de la regla automática.</summary>
    public int Excluidos => _reglas.Values.Count(e => e.Genero.Length == 0);

    /// <summary>La regla de ese artista (o de ese crédito), o null si no tiene.</summary>
    public EntradaGeneroArtista? De(string artista) => _reglas.TryGetValue(Clave(artista), out var e) ? e : null;

    /// <summary>Pone el género del artista. Con un género vacío, la quita. Devuelve si cambió algo. No guarda en disco.</summary>
    public bool Poner(string artista, string nombre, string? genero)
    {
        var g = (genero ?? "").Trim();
        if (g.Length == 0) return Quitar(artista);
        return Cambiar(artista, nombre, g);
    }

    /// <summary>Quita la regla del artista (también la exclusión): vuelve a valer la regla automática. Devuelve si cambió algo.</summary>
    public bool Quitar(string artista) => _reglas.Remove(Clave(artista));

    /// <summary>Excluye al artista de la regla automática del dominante. Devuelve si cambió algo. No guarda en disco.</summary>
    public bool Excluir(string artista, string nombre) => Cambiar(artista, nombre, "");

    private bool Cambiar(string artista, string nombre, string genero)
    {
        var k = Clave(artista);
        if (k.Length == 0) return false;
        if (_reglas.TryGetValue(k, out var antes) && string.Equals(antes.Genero, genero, StringComparison.Ordinal)
            && (antes.Nombre.Length > 0 || nombre.Trim().Length == 0)) return false;
        _reglas[k] = new EntradaGeneroArtista(nombre.Trim().Length > 0 ? nombre.Trim() : artista.Trim(), genero);
        return true;
    }

    /// <summary>Devuelve "" si fue bien, o el motivo del fallo. En solo lectura no escribe nada.</summary>
    public string Guardar() => SoloLectura ? ArchivoJson.AvisoSoloLectura(_archivo) : ArchivoJson.Escribir(_archivo, _reglas);
}
