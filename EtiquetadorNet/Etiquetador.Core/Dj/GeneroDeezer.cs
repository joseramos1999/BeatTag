using Etiquetador.Core.Ai;
using Etiquetador.Core.Providers;

namespace Etiquetador.Core.Dj;

/// <summary>
/// Deezer para las canciones sin género que las reglas no cubren: se busca la canción y se mira el
/// género de su álbum. Solo para ese hueco, y solo con los subgéneros concretos.
///
/// MEDIDO en una biblioteca real: Deezer encuentra el 93 % de las canciones que no son mashups, pero
/// solo 1 de cada 30 mashups (y el 79 % de las canciones sin género lo son), así que los mashups ni se
/// buscan. El género del álbum es grueso: coincide exactamente con el de la canción un 35 % y por
/// familia un 69 %; «Rap/Hip Hop» era Reggaeton en 9 canciones y Hip Hop en 8. Solo el subgénero
/// «Reggaeton» (id 122) acierta lo bastante: un 68 % exacto y un 82 % dentro del urbano latino, en 22
/// casos. Por eso solo se traducen los subgéneros concretos, y la propuesta sale siempre desmarcada.
///
/// Se traduce por el IDENTIFICADOR de Deezer y no por el nombre, porque el nombre llega en el idioma de
/// la IP («Latino» o «Latin Music»). Los identificadores se comprobaron en las respuestas de la medición.
///
/// Enriquecer usa también estas tablas, a través de <see cref="EnIngles"/> y <see cref="EsGrueso"/>, para
/// escribir el género del álbum en inglés (ver <see cref="DeezerProvider.GeneroDelAlbum"/>).
/// </summary>
public static class GeneroDeezer
{
    /// <summary>Subgéneros concretos de Deezer, por su identificador.</summary>
    private static readonly Dictionary<int, string> PorId = new()
    {
        [122] = "Reggaeton",
        [71] = "Cumbia",
        [36] = "Flamenco",
    };

    /// <summary>
    /// Los gruesos, que nunca se proponen aunque su nombre se parezca a un estilo: Rap/Hip Hop (116),
    /// Latino (197), Pop latino (138), Pop (132), Dance (113), Electro (106), Alternativo (85), Rock
    /// (152), Techno/House (111).
    /// </summary>
    private static readonly HashSet<int> Gruesos = new() { 116, 197, 138, 132, 113, 106, 85, 152, 111 };

    /// <summary>
    /// El nombre en inglés de cada género de Deezer, por su identificador. Comprobados en
    /// api.deezer.com/genre pidiéndolos en inglés y en español (octubre de 2026): los 24 géneros
    /// principales y los tres subgéneros de la medición que no lo son (Pop latino 138, Techno/House 111,
    /// Flamenco 36). Dos no se escriben como los nombra Deezer: «Latin Music» queda en «Latin», que es
    /// como se etiqueta, y «Films/Games» en «Soundtrack», que no se parte por la barra al normalizarlo.
    /// </summary>
    private static readonly Dictionary<int, string> EnInglesPorId = new()
    {
        [132] = "Pop", [116] = "Rap/Hip Hop", [122] = "Reggaeton", [152] = "Rock", [113] = "Dance", [165] = "R&B",
        [85] = "Alternative", [106] = "Electro", [466] = "Folk", [144] = "Reggae", [129] = "Jazz", [67] = "Salsa",
        [98] = "Classical", [464] = "Metal", [173] = "Soundtrack", [169] = "Soul & Funk", [153] = "Blues", [71] = "Cumbia",
        [197] = "Latin", [75] = "Brazilian Music", [2] = "African Music", [16] = "Asian Music", [81] = "Indian Music",
        [95] = "Kids", [138] = "Latin Pop", [111] = "Techno/House", [36] = "Flamenco",
    };

    /// <summary>
    /// Los nombres que Deezer da en español distintos del inglés, para un identificador que no está en
    /// la tabla. La clave no distingue mayúsculas ni acentos («Música Latina», «musica latina»).
    /// </summary>
    private static readonly Dictionary<string, string> EnInglesPorNombre = new (string Es, string En)[]
    {
        ("Latino", "Latin"), ("Música Latina", "Latin"), ("Latin Music", "Latin"), ("Pop latino", "Latin Pop"),
        ("Alternativo", "Alternative"), ("Clásica", "Classical"), ("Películas/Juegos", "Soundtrack"), ("Films/Games", "Soundtrack"),
        ("Música Brasileña", "Brazilian Music"), ("Música africana", "African Music"), ("Música asiática", "Asian Music"),
        ("Música india", "Indian Music"), ("Niños", "Kids"),
    }.ToDictionary(p => VocabularioBiblioteca.Clave(p.Es), p => p.En, StringComparer.Ordinal);

    /// <summary>Los gruesos con el nombre que les da <see cref="EnIngles"/>.</summary>
    private static readonly HashSet<string> GruesosEnIngles =
        Gruesos.Select(id => EnInglesPorId[id]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// El nombre en inglés de un género de Deezer: por su identificador si está comprobado y, si no, por
    /// su nombre en español («Latino» → «Latin»). Lo que no se reconoce se deja como llega.
    /// </summary>
    public static string EnIngles(int id, string nombre)
    {
        if (EnInglesPorId.TryGetValue(id, out var ingles)) return ingles;
        var limpio = (nombre ?? "").Trim();
        return EnInglesPorNombre.TryGetValue(VocabularioBiblioteca.Clave(limpio), out ingles) ? ingles : limpio;
    }

    /// <summary>
    /// Es uno de los géneros gruesos de Deezer (Rap/Hip Hop, Latin, Pop, Dance, Electro…), con el nombre
    /// en inglés que da <see cref="EnIngles"/>: dicen el mercado o el espectro del álbum, no su estilo.
    /// </summary>
    public static bool EsGrueso(string nombreEnIngles) => GruesosEnIngles.Contains(nombreEnIngles);

    /// <summary>
    /// Subgéneros concretos por su nombre, para los que no tienen un identificador comprobado. Se
    /// escriben igual en español y en inglés; la clave no distingue acentos («Norteño», «Norteno»).
    /// </summary>
    private static readonly Dictionary<string, string> PorNombre =
        new[] { "Salsa", "Bachata", "Merengue", "Reggae", "Dancehall", "Ranchera", "Norteño" }
            .ToDictionary(VocabularioBiblioteca.Clave, x => x, StringComparer.Ordinal);

    /// <summary>
    /// El estilo que dicen los géneros de un álbum, o null si solo trae géneros gruesos. Un subgénero
    /// concreto manda sobre su padre: [Reggaeton, Latino] → Reggaeton.
    /// </summary>
    public static string? Traducir(IEnumerable<(int Id, string Nombre)> generos)
    {
        var lista = generos.ToList();
        foreach (var (id, _) in lista)
            if (PorId.TryGetValue(id, out var estilo)) return estilo;
        foreach (var (id, nombre) in lista)
        {
            if (Gruesos.Contains(id)) continue;
            if (PorNombre.TryGetValue(VocabularioBiblioteca.Clave(nombre), out var estilo)) return estilo;
        }
        return null;
    }

    /// <summary>
    /// Las canciones que se buscan: sin género, que no son mashups ni transiciones y que no tienen
    /// ninguna propuesta de las reglas.
    /// </summary>
    public static List<Track> Candidatas(IEnumerable<Track> canciones, IEnumerable<string> conPropuesta)
    {
        var ya = new HashSet<string>(conPropuesta, StringComparer.Ordinal);
        return canciones.Where(t => GeneroPorReglas.SinGenero(t) && !ya.Contains(t.FilePath) && !GeneroPorReglas.EsMezcla(t)).ToList();
    }

    /// <summary>
    /// Busca en Deezer cada candidata y propone el subgénero concreto de su álbum, siempre desmarcado.
    /// Cada álbum se pide una sola vez. El ritmo lo pone el limitador de ApiClient (unas 9 peticiones
    /// por segundo como mucho, con caché de 30 días). Unas 2 peticiones por canción: la búsqueda y, si
    /// es nuevo, el álbum; la ficha de la pista (BPM, ISRC, contribuidores) no hace falta y no se pide.
    ///
    /// Se puede cancelar entre una canción y la siguiente, o a mitad de una petición: entonces se
    /// devuelve lo calculado hasta ahí, con <see cref="BusquedaGeneroDeezer.Cancelada"/>. Cancelar al
    /// 90 % no tira lo encontrado.
    /// </summary>
    public static async Task<BusquedaGeneroDeezer> BuscarAsync(IReadOnlyList<Track> candidatas, DeezerProvider deezer,
        IProgress<(int Hechas, int Total)>? progreso, CancellationToken ct)
    {
        var res = new List<PropuestaGeneroRegla>();
        var albumes = new Dictionary<string, (string? Estilo, string Nombres)>(StringComparer.Ordinal);
        int buscadas = 0, encontradas = 0;
        for (var i = 0; i < candidatas.Count; i++)
        {
            if (ct.IsCancellationRequested) return new BusquedaGeneroDeezer(res, buscadas, encontradas, true);
            var t = candidatas[i];
            // Por si llega alguna sin pasar por Candidatas: un mashup no se encuentra y gastaría peticiones.
            if (!GeneroPorReglas.SinGenero(t) || GeneroPorReglas.EsMezcla(t)) { progreso?.Report((i + 1, candidatas.Count)); continue; }

            var titulo = string.IsNullOrWhiteSpace(t.Title) ? Path.GetFileNameWithoutExtension(t.FilePath) : t.Title!;
            var version = RemixParser.Parse(titulo);
            // Los dos contadores se suman juntos, al terminar la canción: si se cancela mientras se pide
            // su álbum, no cuenta ni como buscada ni como encontrada (Encontradas <= Buscadas siempre).
            var encontrada = false;
            try
            {
                var r = await deezer.SearchAsync(t.Artist ?? "", titulo, wantRemix: GeneroPorReglas.RemixFirmado(t), wantLive: false,
                                                 localDur: t.DurationSeconds, isEdit: version.IsVersion, ct: ct, conFicha: false).ConfigureAwait(false);
                if (r is { AlbumId.Length: > 0 })
                {
                    encontrada = true;
                    if (!albumes.TryGetValue(r.AlbumId, out var album))
                    {
                        var generos = await deezer.AlbumGenresAsync(r.AlbumId, ct).ConfigureAwait(false);
                        albumes[r.AlbumId] = album = (Traducir(generos), string.Join(", ", generos.Select(g => g.Nombre).Where(n => n.Length > 0)));
                    }
                    if (album.Estilo is { } estilo)
                        res.Add(new PropuestaGeneroRegla(t.FilePath, estilo, ReglaGenero.Deezer,
                            $"Deezer: álbum «{r.Album}» ({album.Nombres}) → {estilo} · el género de Deezer es del álbum entero: revísalo",
                            false, t.Genre ?? "", CambioGenero.Rellenar));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancelada a mitad de esta canción (en la búsqueda o en su álbum): no cuenta.
                return new BusquedaGeneroDeezer(res, buscadas, encontradas, true);
            }
            buscadas++;
            if (encontrada) encontradas++;
            progreso?.Report((i + 1, candidatas.Count));
        }
        return new BusquedaGeneroDeezer(res, buscadas, encontradas, false);
    }
}

/// <summary>Lo que ha dado buscar en Deezer las canciones sin propuesta.</summary>
/// <param name="Propuestas">Las canciones con un subgénero concreto en su álbum, desmarcadas.</param>
/// <param name="Buscadas">Canciones que se han llegado a buscar.</param>
/// <param name="Encontradas">De ellas, cuántas ha encontrado Deezer (con su álbum).</param>
/// <param name="Cancelada">Se canceló antes de acabar: las propuestas son las de las canciones buscadas hasta ese momento.</param>
public sealed record BusquedaGeneroDeezer(List<PropuestaGeneroRegla> Propuestas, int Buscadas, int Encontradas, bool Cancelada);
