namespace Etiquetador.Core.Analysis;

/// <summary>
/// Agrupa canciones cuyo AUDIO es el mismo, comparando huellas acústicas. Encuentra los duplicados
/// que se le escapan al nombre y a los tags: la misma canción guardada con títulos distintos, sin
/// etiquetar, o con el artista mal puesto.
///
/// El problema es el coste. Comparar todas contra todas en una biblioteca de 15.000 canciones son
/// 112 millones de parejas, inviable. Por eso se agrupa antes por DURACIÓN: dos copias de la misma
/// grabación duran casi lo mismo, así que solo hace falta comparar dentro de cada tramo de
/// duraciones parecidas. Eso reduce el trabajo en varios órdenes de magnitud.
/// </summary>
public static class FingerprintDuplicates
{
    /// <summary>
    /// Diferencia de duración por debajo de la cual dos canciones se comparan entre sí. Generosa a
    /// propósito: una copia puede llevar un silencio de más al principio o al final.
    /// </summary>
    public const int ToleranciaDuracionSeg = 12;

    /// <summary>
    /// Agrupa por audio. <paramref name="huellaDe"/> devuelve la huella de cada canción (null si no
    /// se ha calculado todavía, en cuyo caso esa canción se queda fuera).
    /// </summary>
    public static IReadOnlyList<DuplicateGroup> Find(
        IEnumerable<Track> tracks,
        Func<Track, int[]?> huellaDe,
        double umbral = AudioFingerprint.UmbralIgual)
    {
        // Solo las que tienen huella y duración: sin una de las dos no hay nada que comparar.
        var conHuella = new List<(Track T, int[] F)>();
        foreach (var t in tracks)
        {
            if (t.DurationSeconds <= 0) continue;
            var f = huellaDe(t);
            if (f is { Length: > 0 }) conHuella.Add((t, f));
        }
        if (conHuella.Count < 2) return Array.Empty<DuplicateGroup>();

        // Ordenar por duración permite, para cada canción, mirar solo hacia adelante hasta salirse
        // de la tolerancia, en vez de recorrer la lista entera.
        conHuella.Sort((x, y) => x.T.DurationSeconds.CompareTo(y.T.DurationSeconds));

        // Conjuntos disjuntos: si A suena igual que B y B igual que C, los tres son el mismo grupo
        // aunque A y C no se hayan comparado directamente.
        var padre = new int[conHuella.Count];
        for (var i = 0; i < padre.Length; i++) padre[i] = i;

        int Raiz(int x) { while (padre[x] != x) { padre[x] = padre[padre[x]]; x = padre[x]; } return x; }
        void Unir(int x, int y) { var a = Raiz(x); var b = Raiz(y); if (a != b) padre[b] = a; }

        for (var i = 0; i < conHuella.Count; i++)
        {
            for (var j = i + 1; j < conHuella.Count; j++)
            {
                // La lista está ordenada por duración: en cuanto una se pasa de la tolerancia,
                // todas las siguientes también, así que se corta.
                if (conHuella[j].T.DurationSeconds - conHuella[i].T.DurationSeconds > ToleranciaDuracionSeg) break;
                if (Raiz(i) == Raiz(j)) continue;   // ya están juntas, no hace falta comparar
                if (AudioFingerprint.Similarity(conHuella[i].F, conHuella[j].F) >= umbral) Unir(i, j);
            }
        }

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
