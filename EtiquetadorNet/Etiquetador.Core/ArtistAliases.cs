using System.Text;
using System.Text.Json;

namespace Etiquetador.Core;

/// <summary>
/// Nombres distintos de un MISMO artista (no confundir con <see cref="ArtistExceptions"/>, que solo
/// corrige la GRAFÍA de un nombre). Muchos artistas publican bajo un alias anterior, y el catálogo
/// conserva ese nombre: Cruz Cafuné figura en Deezer como "Cruzzi". Sin esta equivalencia la
/// coincidencia correcta se descarta por "artista≠" y la canción se queda sin identificar.
///
/// Cada grupo es una lista de nombres equivalentes cuyo PRIMER elemento es el nombre canónico:
/// es el que se escribirá en los tags cuando el catálogo devuelva uno de los alias.
///
/// La lista es ampliable por el usuario desde ArtistAliases.json, porque los alias dependen de
/// la biblioteca de cada cual y no hay forma de acertarlos todos por defecto.
/// </summary>
public sealed class ArtistAliases
{
    // Deliberadamente corta y solo con equivalencias comprobadas: un alias equivocado provoca
    // etiquetado incorrecto, que es peor que dejar la canción sin identificar.
    private static readonly string[][] Default =
    {
        new[] { "Cruz Cafuné", "Cruzzi" },
    };

    // clave = NK(ToAscii(alias)) -> índice de grupo
    private readonly Dictionary<string, int> _group = new();
    private readonly List<string[]> _groups = new();
    private readonly List<string[]> _groupKeys = new();

    public ArtistAliases(IEnumerable<string[]>? groups = null)
    {
        foreach (var g in groups ?? Default) Add(g);
    }

    private void Add(string[]? grupo)
    {
        if (grupo == null) return;
        var nombres = grupo.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToArray();
        if (nombres.Length < 2) return;                     // un grupo de uno no equivale a nada
        var claves = nombres.Select(n => TextUtils.Nk(TextUtils.ToAscii(n))).Where(k => k.Length > 0).ToArray();
        if (claves.Length < 2) return;

        var idx = _groups.Count;
        _groups.Add(nombres);
        _groupKeys.Add(claves);
        foreach (var k in claves) _group[k] = idx;
    }

    /// <summary>Nombre canónico (el primero del grupo) para un nombre ya normalizado con NK; null si no es un alias.</summary>
    public string? Canonical(string? nk)
        => nk != null && _group.TryGetValue(nk, out var i) ? _groups[i][0] : null;

    /// <summary>Todos los nombres tal cual se escriben, para alimentar la normalización de grafía.</summary>
    public IEnumerable<(string Alias, string Canonical)> Pairs()
    {
        foreach (var g in _groups)
            for (int i = 1; i < g.Length; i++) yield return (g[i], g[0]);
    }

    /// <summary>true si los dos nombres (ya normalizados con NK) son el mismo artista con distinto nombre.</summary>
    public bool SameArtist(string? nkA, string? nkB)
    {
        if (string.IsNullOrEmpty(nkA) || string.IsNullOrEmpty(nkB) || _groups.Count == 0) return false;

        var ga = _group.TryGetValue(nkA, out var ia) ? ia : -1;
        var gb = _group.TryGetValue(nkB, out var ib) ? ib : -1;
        if (ga >= 0 && ga == gb) return true;

        // Un lado puede traer varios intérpretes unidos ("cruzzihoke"): basta con que aparezca
        // dentro alguno de los alias del grupo del otro. El mínimo de 4 evita casar por casualidad.
        if (ga >= 0 && _groupKeys[ga].Any(k => k.Length >= 4 && nkB.Contains(k))) return true;
        if (gb >= 0 && _groupKeys[gb].Any(k => k.Length >= 4 && nkA.Contains(k))) return true;
        return false;
    }

    /// <summary>Lista activa. La fija el arranque de la app; por defecto, la de serie.</summary>
    public static ArtistAliases Current { get; set; } = new();

    /// <summary>
    /// Carga los alias del usuario desde un JSON (array de arrays; el primero de cada grupo es el
    /// nombre canónico). Si el archivo no existe, crea uno de arranque editable. Nunca lanza.
    /// </summary>
    public static ArtistAliases Load(string jsonPath)
    {
        try
        {
            if (File.Exists(jsonPath))
            {
                var arr = JsonSerializer.Deserialize<string[][]>(File.ReadAllText(jsonPath, Encoding.UTF8));
                if (arr != null) return new ArtistAliases(arr);
            }
            else
            {
                var dir = Path.GetDirectoryName(jsonPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(jsonPath,
                    JsonSerializer.Serialize(Default, new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8);
            }
        }
        catch { /* archivo corrupto/inaccesible -> solo la lista por defecto */ }
        return new ArtistAliases();
    }
}
