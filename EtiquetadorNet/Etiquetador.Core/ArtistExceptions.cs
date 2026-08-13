using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Etiquetador.Core;

/// <summary>
/// Excepciones de grafía: artistas cuya escritura NO debe alterar UnScream
/// (evita "DJ SNAKE" -> "Dj Snake", restaura acentos/eñes). Lista por defecto del .ps1;
/// ampliable con nombres extra (p. ej. desde ArtistsExceptions.json del usuario).
/// </summary>
public sealed class ArtistExceptions
{
    private static readonly string[] Default =
    {
        "AC/DC", "P!nk", "deadmau5", "will.i.am", "MØ", "Björk", "A$AP Rocky", "Ke$ha", "Se7en",
        "JAY-Z", "DJ Snake", "DJ Khaled", "Tiësto", "Wu-Tang Clan", "3OH!3", "blink-182",
        "Panic! At The Disco", "Rosalía", "C. Tangana", "Beyoncé", "KAROL G", "RVFV",
        // Ampliación según biblioteca real (siglas, acentos, mayúsculas oficiales)
        "Anuel AA", "Arcángel", "Tego Calderón", "Ñengo Flow", "DELLAFUENTE", "Cruz Cafuné",
        "Miguel Bosé", "Los Marismeños", "JC Reyes", "Cris MJ", "DJ Luian",
    };

    // clave = NK(ToAscii(nombre)) -> grafía canónica
    private readonly Dictionary<string, string> _map = new();

    public ArtistExceptions(IEnumerable<string>? extra = null, ArtistAliases? aliases = null)
    {
        foreach (var x in Default) Add(x);
        if (extra != null) foreach (var x in extra) Add(x);

        // Los alias también entran aquí: cuando el catálogo devuelve el nombre antiguo, se escribe
        // el canónico. Van los últimos para que tengan prioridad sobre la grafía por defecto.
        foreach (var (alias, canon) in (aliases ?? ArtistAliases.Current).Pairs())
        {
            var k = TextUtils.Nk(TextUtils.ToAscii(alias));
            if (k.Length > 0) _map[k] = canon;
        }
    }

    private void Add(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var k = TextUtils.Nk(TextUtils.ToAscii(name));
        if (k.Length > 0) _map[k] = name;
    }

    /// <summary>
    /// Carga las excepciones del usuario desde un JSON (array de nombres). Si el archivo no existe,
    /// crea uno de arranque con la lista por defecto (editable). Nunca lanza.
    /// </summary>
    public static ArtistExceptions Load(string jsonPath)
    {
        var extra = new List<string>();
        try
        {
            if (File.Exists(jsonPath))
            {
                var arr = JsonSerializer.Deserialize<string[]>(File.ReadAllText(jsonPath, Encoding.UTF8));
                if (arr != null)
                    foreach (var x in arr) if (!string.IsNullOrWhiteSpace(x)) extra.Add(x);
            }
            else
            {
                var dir = Path.GetDirectoryName(jsonPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(Default, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            }
        }
        catch { /* archivo corrupto/inaccesible -> solo la lista por defecto */ }
        return new ArtistExceptions(extra);
    }

    /// <summary>
    /// Normaliza una lista de intérpretes separada por comas: grafía canónica si es excepción,
    /// si no UnScream por componente. Se aplica al tag y al nombre de archivo.
    /// </summary>
    public string NormalizeArtists(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        var outList = new List<string>();
        foreach (var part in Regex.Split(s, @",\s*"))
        {
            var p = part.Trim();
            if (p.Length == 0) continue;
            var k = TextUtils.Nk(TextUtils.ToAscii(p));
            outList.Add(k.Length > 0 && _map.TryGetValue(k, out var canon) ? canon : TextUtils.UnScream(p));
        }
        return string.Join(", ", outList);
    }
}
