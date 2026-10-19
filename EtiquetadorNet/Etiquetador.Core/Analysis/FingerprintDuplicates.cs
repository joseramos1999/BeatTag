namespace Etiquetador.Core.Analysis;

/// <summary>
/// Agrupa canciones cuyo AUDIO es el mismo, comparando huellas acústicas. Encuentra los duplicados
/// que se le escapan al nombre y a los tags: la misma canción guardada con títulos distintos, sin
/// etiquetar, o con el artista mal puesto.
///
/// EL PROBLEMA ES EL COSTE, y agrupar por duración NO basta. Medido sobre una biblioteca real de
/// 12.927 canciones: dentro de una tolerancia de 12 s quedan 10,7 MILLONES de parejas, porque las
/// canciones se apiñan alrededor de los tres o cuatro minutos (el mayor vecindario tenía 1406).
/// Comparar todas esas parejas son ~1,5 billones de operaciones: minutos de cálculo, no segundos.
///
/// La solución es un índice invertido. Dos grabaciones del mismo audio comparten muchos valores
/// EXACTOS de huella, mientras que dos canciones distintas casi no comparten ninguno: son enteros
/// de 32 bits, así que coincidir por azar es rarísimo. Indexando valor -> canciones, solo hay que
/// comparar a fondo las parejas que comparten unos cuantos, que son un puñado en vez de millones.
/// </summary>
public static class FingerprintDuplicates
{
    /// <summary>
    /// Diferencia de duración por debajo de la cual dos canciones pueden ser la misma. Sigue
    /// aplicándose como filtro final: es barato y descarta emparejamientos absurdos.
    /// </summary>
    public const int ToleranciaDuracionSeg = 12;

    /// <summary>
    /// De cada huella se indexa, de media, uno de cada 2^N valores (N = 3, uno de cada 8). Con ~900
    /// valores por canción quedan unos 110, suficientes para que dos copias compartan varios sin
    /// inflar el índice.
    /// </summary>
    private const int BitsDeMuestreo = 3;

    /// <summary>
    /// ¿Entra este valor en el índice? Lo decide el VALOR, nunca su posición en la huella.
    ///
    /// Antes se indexaban las posiciones 0, 8, 16… y bastaba con que una copia trajera 0,12 s más
    /// de silencio al principio (un elemento de huella) para que cada una indexara valores
    /// distintos y no compartieran ninguno: la pareja no llegaba a compararse aunque Similarity,
    /// que prueba ±80 desplazamientos, la diera por igual. Visto en una biblioteca real: de 13
    /// parejas con el mismo nombre que eran la misma grabación, el índice por posición perdía 3
    /// (desfases +1, −20 y +27 elementos, con parecido 0,986, 0,913 y 0,905). Eligiendo por
    /// contenido, si A[k] == B[k + d] las dos copias indexan ese valor o ninguna lo hace, sea cual
    /// sea el desfase d.
    ///
    /// El valor se mezcla con la constante de Knuth (2^32 / φ) antes de mirar sus bits altos: así
    /// la elección no depende de qué bits de la huella cambian más, y sale uno de cada 8 aunque
    /// los valores no estén repartidos por igual.
    /// </summary>
    internal static bool SeIndexa(int valor) => unchecked((uint)valor * 2654435761u) >> (32 - BitsDeMuestreo) == 0;

    /// <summary>Valores compartidos a partir de los cuales merece la pena comparar a fondo.</summary>
    internal const int MinimoValoresComunes = 3;

    /// <summary>
    /// Un valor que aparece en muchísimas canciones no distingue nada (silencios, tonos planos) y
    /// además dispararía el número de parejas. Se ignora, como una palabra vacía en un buscador.
    /// </summary>
    private const int MaximoCancionesPorValor = 40;

    /// <summary>
    /// Agrupa por audio. <paramref name="huellaDe"/> devuelve la huella de cada canción (null si no
    /// se ha calculado todavía, en cuyo caso esa canción se queda fuera).
    /// </summary>
    public static IReadOnlyList<DuplicateGroup> Find(
        IEnumerable<Track> tracks,
        Func<Track, int[]?> huellaDe,
        double umbral = AudioFingerprint.UmbralIgual,
        IProgress<(int Hechas, int Total)>? progreso = null,
        CancellationToken ct = default)
    {
        var conHuella = new List<(Track T, int[] F)>();
        foreach (var t in tracks)
        {
            if (t.DurationSeconds <= 0) continue;
            var f = huellaDe(t);
            if (f is { Length: > 0 }) conHuella.Add((t, f));
        }
        if (conHuella.Count < 2) return Array.Empty<DuplicateGroup>();

        // --- Índice invertido: valor de huella -> canciones que lo contienen ---
        // Va en un único vector de long (valor en la parte alta, canción en la baja) que se ordena:
        // las apariciones de un mismo valor quedan seguidas y con las canciones en orden. Antes era
        // un Dictionary<int, List<int>> con una lista pequeña por cada valor distinto. MEDIDO con
        // 13.800 huellas de 960 valores, 300 de ellas copias desplazadas de 1 a 30 elementos: el
        // índice por posición encontraba 30 parejas (solo las de desfase múltiplo de 8) y pedía
        // unos 200 MB de memoria en total (195 MB, medido de nuevo en la revisión; una primera cifra de
        // «2 GB» estaba inflada unas diez veces); este las encuentra las 300 en medio segundo con 15 MB.
        var total = 0;
        foreach (var (_, f) in conHuella)
            foreach (var v in f)
                if (SeIndexa(v)) total++;
        var entradas = new long[total];
        var e = 0;
        for (var i = 0; i < conHuella.Count; i++)
            foreach (var v in conHuella[i].F)
                if (SeIndexa(v)) entradas[e++] = ((long)v << 32) | (uint)i;
        Array.Sort(entradas);
        ct.ThrowIfCancellationRequested();

        // --- Parejas candidatas: las que comparten varios valores exactos ---
        var comunes = new Dictionary<long, int>();
        var delValor = new List<int>(MaximoCancionesPorValor + 1);
        for (var ini = 0; ini < entradas.Length;)
        {
            var valor = entradas[ini] >> 32;
            var fin = ini;
            delValor.Clear();
            for (; fin < entradas.Length && entradas[fin] >> 32 == valor; fin++)
            {
                var cancion = (int)(entradas[fin] & 0xFFFFFFFF);
                // Un mismo valor puede repetirse dentro de una canción; basta con anotarla una vez
                // (vienen ordenadas, así que las repeticiones quedan seguidas). Pasado el máximo ya
                // no hace falta seguir anotando: ese valor se va a ignorar.
                if (delValor.Count <= MaximoCancionesPorValor && (delValor.Count == 0 || delValor[^1] != cancion)) delValor.Add(cancion);
            }
            ini = fin;

            if (delValor.Count < 2 || delValor.Count > MaximoCancionesPorValor) continue;
            for (var a = 0; a < delValor.Count; a++)
                for (var b = a + 1; b < delValor.Count; b++)
                {
                    var clave = ((long)delValor[a] << 32) | (uint)delValor[b];
                    comunes[clave] = comunes.TryGetValue(clave, out var n) ? n + 1 : 1;
                }
        }
        ct.ThrowIfCancellationRequested();

        var candidatas = new List<(int A, int B)>();
        foreach (var kv in comunes)
        {
            if (kv.Value < MinimoValoresComunes) continue;
            var a = (int)(kv.Key >> 32);
            var b = (int)(kv.Key & 0xFFFFFFFF);
            // Filtro final por duración: barato y descarta emparejamientos sin sentido.
            if (Math.Abs(conHuella[a].T.DurationSeconds - conHuella[b].T.DurationSeconds) > ToleranciaDuracionSeg) continue;
            candidatas.Add((a, b));
        }

        // --- Solo ahora la comparación cara, sobre un puñado de parejas ---
        var padre = new int[conHuella.Count];
        for (var i = 0; i < padre.Length; i++) padre[i] = i;

        int Raiz(int x) { while (padre[x] != x) { padre[x] = padre[padre[x]]; x = padre[x]; } return x; }
        void Unir(int x, int y) { var a = Raiz(x); var b = Raiz(y); if (a != b) padre[b] = a; }

        var hechas = 0;
        foreach (var (a, b) in candidatas)
        {
            ct.ThrowIfCancellationRequested();
            if (++hechas % 500 == 0) progreso?.Report((hechas, candidatas.Count));
            if (Raiz(a) == Raiz(b)) continue;   // ya están juntas
            if (AudioFingerprint.Similarity(conHuella[a].F, conHuella[b].F) >= umbral) Unir(a, b);
        }
        progreso?.Report((candidatas.Count, candidatas.Count));

        var porGrupo = new Dictionary<int, List<Track>>();
        for (var i = 0; i < conHuella.Count; i++)
        {
            var r = Raiz(i);
            if (!porGrupo.TryGetValue(r, out var lista)) porGrupo[r] = lista = new List<Track>();
            lista.Add(conHuella[i].T);
        }

        var salida = new List<DuplicateGroup>();
        foreach (var g in porGrupo.Values)
        {
            if (g.Count < 2) continue;
            // Para el título del grupo se usa la copia que MÁS datos trae: si una está sin
            // etiquetar y otra no, conviene enseñar la que dice algo.
            var muestra = g.OrderByDescending(t => (t.Artist ?? "").Length + (t.Title ?? "").Length).First();
            var artista = muestra.Artist ?? "";
            var titulo = string.IsNullOrWhiteSpace(muestra.Title) ? muestra.FileName : muestra.Title!;
            salida.Add(new DuplicateGroup("fp:" + titulo, artista, titulo, g));
        }

        return salida.OrderByDescending(g => g.Tracks.Count).ThenBy(g => g.Artist).ToList();
    }
}
