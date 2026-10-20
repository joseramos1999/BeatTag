namespace Etiquetador.Core.Analysis;

/// <summary>
/// Agrupa canciones cuyo AUDIO es el mismo, comparando huellas acústicas. Encuentra los duplicados
/// que se le escapan al nombre y a los tags: la misma canción guardada con títulos distintos, sin
/// etiquetar, o con el artista mal puesto. Y, a petición, las OTRAS EDICIONES del mismo tema (una
/// Extended o una Hype Intro frente a su Original), distinguidas de las copias.
///
/// EL PROBLEMA ES EL COSTE, y agrupar por duración NO basta. Medido sobre una biblioteca real de
/// 12.927 canciones: dentro de una tolerancia de 12 s quedan 10,7 MILLONES de parejas, porque las
/// canciones se apiñan alrededor de los tres o cuatro minutos (el mayor vecindario tenía 1406).
/// Comparar todas esas parejas son ~1,5 billones de operaciones: minutos de cálculo, no segundos.
///
/// La solución es un índice invertido. Dos grabaciones del mismo audio comparten muchos valores
/// EXACTOS de huella, mientras que dos canciones distintas casi no comparten ninguno: son enteros
/// de 32 bits, así que coincidir por azar es rarísimo. Indexando valor -> (canción, posición), solo
/// hay que comparar a fondo las parejas que comparten unos cuantos, que son un puñado en vez de
/// millones.
///
/// Y como el índice guarda la POSICIÓN de cada valor (hallazgo A14), cada valor compartido es
/// además un VOTO por un desfase: si A tiene el valor en la posición 10 y B en la 250, B va 240
/// elementos (~30 s) por detrás. Las coincidencias de la misma grabación votan todas el mismo
/// desfase; las casuales se reparten. Así se sabe DÓNDE comparar, y basta con mirar ese desfase en
/// vez de probar los 161 de ±10 s: sirve para desfases de hasta lo que dure la huella, que es lo
/// que separa una Extended de su Original.
/// </summary>
public static class FingerprintDuplicates
{
    /// <summary>
    /// Diferencia de duración por debajo de la cual dos canciones pueden ser COPIAS del mismo
    /// archivo. Por encima, si el audio coincide, son otra edición (un Radio Edit que corta antes,
    /// una Extended con más final), nunca una copia sobrante.
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

    /// <summary>
    /// Votos (valores compartidos EN EL MISMO DESFASE, ±1 elemento) a partir de los cuales una pareja
    /// se compara a fondo. Antes hacían falta tres valores compartidos en cualquier sitio. Que
    /// coincidan en el desfase ya descarta casi todas las coincidencias casuales, así que basta con
    /// dos, y eso recupera copias muy recodificadas: con parecido 0,87 solo coinciden exactos un 2-3 %
    /// de los valores, y de esos se indexa uno de cada ocho.
    ///
    /// MEDIDO con las 15.241 huellas de una biblioteca real (incluidas las de archivos ya movidos):
    /// con tres votos se partía 1 de los 611 grupos de antes («MALA»: su Extended casaba con el
    /// original a 0,870 con solo 2 votos; el índice viejo lo encontraba de rebote, por 3 valores
    /// sueltos); con dos no se parte ninguno, aparecen 3 grupos de copias que antes se escapaban
    /// (todos la misma canción) y se tarda 387 ms frente a 458 ms del índice anterior.
    /// </summary>
    internal const int MinimoValoresComunes = 2;

    /// <summary>
    /// Un valor que aparece en muchísimas canciones no distingue nada (silencios, tonos planos) y
    /// además dispararía el número de parejas. Se ignora, como una palabra vacía en un buscador.
    /// </summary>
    private const int MaximoCancionesPorValor = 40;

    /// <summary>
    /// Desfases distintos que se comprueban por pareja, de más a menos votado. Normalmente gana el
    /// primero, pero un tema con partes repetidas (un estribillo que vuelve) puede votar más un
    /// desfase falso. MEDIDO en una biblioteca real (8.219 huellas vigentes): con solo el más votado
    /// se partía 1 de los 246 grupos de antes (dos ediciones en vivo de «Muevelo»: 19 votos en el
    /// desfase 0, que no casa, y 17 en el 66, que sí, con parecido 0,905); con los tres primeros no
    /// se pierde ninguno. Cada desfase se mira con sus dos vecinos: como mucho 9 comparaciones por
    /// pareja, frente a las 161 de <see cref="AudioFingerprint.Similarity"/>.
    /// </summary>
    private const int PicosPorPareja = 3;

    /// <summary>
    /// Solape mínimo, en segundos, para dar dos canciones por otra edición del mismo tema. Que
    /// compartan unos pocos segundos puede ser un sample, una transición o un mashup que los cita;
    /// 30 s son 16 compases a 128 BPM, una sección entera. Con huellas de 120 s, se reconocen
    /// intros hasta unos 90 s más largas.
    ///
    /// MEDIDO en una biblioteca real: de 497 parejas que casaban como otra edición, solo 3
    /// compartían menos de 30 s: un mashup que citaba otro tema (12 s), un remix frente a su
    /// original (26 s) y una Extended de verdad (29 s). El límite quita el primero a costa de casos
    /// rarísimos como el último.
    /// </summary>
    public const int MinimoSolapeEdicionSeg = 30;

    /// <summary>Bits para la canción y para la posición en cada entrada del índice.</summary>
    private const int BitsPosicion = 12;    // 4096 elementos: 8 minutos y medio de huella
    private const int BitsCancion = 20;     // 1.048.576 canciones
    private const int MascaraPosicion = (1 << BitsPosicion) - 1;
    private const int MascaraCancion = (1 << BitsCancion) - 1;

    /// <summary>
    /// Agrupa por audio solo las COPIAS: el mismo audio alineado a ±10 s y con la misma duración a
    /// ±12 s. <paramref name="huellaDe"/> devuelve la huella de cada canción (null si no se ha
    /// calculado todavía, en cuyo caso esa canción se queda fuera).
    /// </summary>
    public static IReadOnlyList<DuplicateGroup> Find(
        IEnumerable<Track> tracks,
        Func<Track, int[]?> huellaDe,
        double umbral = AudioFingerprint.UmbralIgual,
        IProgress<(int Hechas, int Total)>? progreso = null,
        CancellationToken ct = default)
        => Agrupar(tracks, huellaDe, incluirEdiciones: false, umbral, progreso, ct).Grupos;

    /// <summary>
    /// Agrupa por audio. Con <paramref name="incluirEdiciones"/>, además de las copias junta en el
    /// mismo grupo las otras ediciones del mismo tema (una intro más larga, otra duración), y dice de
    /// cada canción a qué edición pertenece (<see cref="ResultadoPorAudio.Alineacion"/>).
    ///
    /// MEDIDO en una biblioteca real (8.219 huellas vigentes, un hilo): 459 grupos frente a los 246
    /// de solo copias, 293 de ellos con varias ediciones y 229 que no existían; los 246 de antes
    /// siguen igual y ninguna pareja de copias pasa a ser «otra edición». Comparar cuesta mucho
    /// menos: antes se comparaban 881 parejas en 161 desfases cada una (141.841 comparaciones);
    /// ahora 5.121 para las copias y 17.337 con las ediciones, porque cada pareja se mira solo en los
    /// desfases que vota el índice. El tiempo total no baja (160-180 ms antes y después): lo que pesa
    /// es construir y recorrer el índice, no comparar. Revisados a mano 60 grupos al azar con varias
    /// ediciones: 52 son solo el mismo tema y 8 suman además un mashup que lo contiene; ninguno junta
    /// dos temas sin relación (antes del filtro de títulos de EdicionesPorAudio.PuedenSerElMismoTema,
    /// 5 de cada 60 juntaban una Hype Intro con el tema que usa de entrada). Con «Excluir ediciones de
    /// DJ» quedan 123 grupos con varias ediciones. Cada edición conserva siempre su copia (Duplicados
    /// nunca la marca como sobrante), así que un grupo así no pone nada en riesgo.
    /// </summary>
    /// <param name="progreso">Canciones recorridas en la votación, que es lo que más tarda.</param>
    public static ResultadoPorAudio Agrupar(
        IEnumerable<Track> tracks,
        Func<Track, int[]?> huellaDe,
        bool incluirEdiciones,
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
        var vacio = new ResultadoPorAudio(Array.Empty<DuplicateGroup>(),
                                          new Dictionary<string, AlineacionAudio>(StringComparer.OrdinalIgnoreCase), 0);
        if (conHuella.Count < 2) return vacio;
        if (conHuella.Count > MascaraCancion)
            throw new NotSupportedException($"Comparar el audio admite hasta {MascaraCancion} canciones a la vez.");

        var aristas = Votar(conHuella, incluirEdiciones, umbral, progreso, ct, out var validadas);
        return Construir(conHuella, aristas, validadas);
    }

    /// <summary>Una pareja ya validada: el audio de A en la posición i está en B en i + Desfase.</summary>
    private readonly record struct Arista(int A, int B, int Desfase, bool EsCopia);

    /// <summary>
    /// El índice, la votación del desfase y la validación de cada pareja en el desfase votado.
    /// </summary>
    private static List<Arista> Votar(List<(Track T, int[] F)> conHuella, bool incluirEdiciones, double umbral,
                                      IProgress<(int Hechas, int Total)>? progreso, CancellationToken ct,
                                      out int validadas)
    {
        // --- Índice invertido: valor de huella -> (canción, posición) ---
        // Va en un único vector de long (valor en la parte alta; canción y posición en la baja) que se
        // ordena: las apariciones de un mismo valor quedan seguidas. Antes era un Dictionary<int,
        // List<int>> con una lista pequeña por cada valor distinto. MEDIDO con 13.800 huellas de 960
        // valores, 300 de ellas copias desplazadas de 1 a 30 elementos: el índice por posición
        // encontraba 30 parejas (solo las de desfase múltiplo de 8) y pedía unos 200 MB de memoria;
        // el vector ordenado las encuentra las 300 en medio segundo con 15 MB. Las posiciones más
        // allá de 4096 elementos (8,5 min) no se indexan: no hacen falta para votar.
        var total = 0;
        foreach (var (_, f) in conHuella)
            for (var k = 0; k < f.Length && k <= MascaraPosicion; k++)
                if (SeIndexa(f[k])) total++;
        var entradas = new long[total];
        var e = 0;
        for (var i = 0; i < conHuella.Count; i++)
        {
            var f = conHuella[i].F;
            for (var k = 0; k < f.Length && k <= MascaraPosicion; k++)
                if (SeIndexa(f[k])) entradas[e++] = ((long)f[k] << 32) | ((long)i << BitsPosicion) | (uint)k;
        }
        Array.Sort(entradas);
        ct.ThrowIfCancellationRequested();

        // --- Tramos útiles: valores que comparten entre 2 y 40 canciones ---
        // Los que solo tiene una canción (la inmensa mayoría) no votan nada y ni se guardan.
        var valores = new List<int>();
        var tramos = new List<(int Ini, int Fin)>();
        for (var ini = 0; ini < entradas.Length;)
        {
            var valor = entradas[ini] >> 32;
            var fin = ini;
            int canciones = 0, ultima = -1;
            for (; fin < entradas.Length && entradas[fin] >> 32 == valor; fin++)
            {
                var c = Cancion(entradas[fin]);
                if (c != ultima) { canciones++; ultima = c; }   // vienen ordenadas por canción
            }
            if (canciones >= 2 && canciones <= MaximoCancionesPorValor) { valores.Add((int)valor); tramos.Add((ini, fin)); }
            ini = fin;
        }
        var valoresUtiles = valores.ToArray();
        ct.ThrowIfCancellationRequested();

        // --- Votación, canción a canción ---
        // Para cada canción A, cada uno de sus valores indexados vota, con cada canción B posterior
        // que lo tenga, el desfase posB − posA. La memoria es la de UNA canción a la vez: un mapa
        // pequeño que se vacía al pasar a la siguiente.
        var aristas = new List<Arista>();
        var votos = new Dictionary<long, int>();
        var claves = new List<long>();
        var picos = new List<(int Votos, int Desfase)>(8);
        validadas = 0;
        const int CeroDesfase = 1 << BitsPosicion;   // los desfases van de −4095 a +4095
        const int BitsDesfase = BitsPosicion + 1;
        const long MascaraDesfase = (1L << BitsDesfase) - 1;

        for (var a = 0; a < conHuella.Count; a++)
        {
            if ((a & 255) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progreso?.Report((a, conHuella.Count));
            }
            votos.Clear();
            var fa = conHuella[a].F;
            for (var k = 0; k < fa.Length && k <= MascaraPosicion; k++)
            {
                if (!SeIndexa(fa[k])) continue;
                var r = Array.BinarySearch(valoresUtiles, fa[k]);
                if (r < 0) continue;
                var (ini, fin) = tramos[r];
                for (var x = ini; x < fin; x++)
                {
                    var b = Cancion(entradas[x]);
                    if (b <= a) continue;   // cada pareja se vota una sola vez, desde la primera
                    var clave = ((long)b << BitsDesfase) | (uint)(Posicion(entradas[x]) - k + CeroDesfase);
                    votos[clave] = votos.TryGetValue(clave, out var n) ? n + 1 : 1;
                }
            }
            if (votos.Count == 0) continue;

            // Las claves ordenadas dejan seguidos los desfases de cada B, y de menor a mayor.
            claves.Clear();
            claves.AddRange(votos.Keys);
            claves.Sort();
            for (var i = 0; i < claves.Count;)
            {
                var b = (int)(claves[i] >> BitsDesfase);
                var j = i;
                while (j < claves.Count && (int)(claves[j] >> BitsDesfase) == b) j++;

                // Cada desfase suma los votos de sus vecinos (±1): el desfase real casi nunca es un
                // número entero de elementos, y sus votos se reparten entre los dos más cercanos.
                picos.Clear();
                for (var x = i; x < j; x++)
                {
                    var d = (int)(claves[x] & MascaraDesfase);
                    var v = votos[claves[x]];
                    if (x > i && (int)(claves[x - 1] & MascaraDesfase) == d - 1) v += votos[claves[x - 1]];
                    if (x + 1 < j && (int)(claves[x + 1] & MascaraDesfase) == d + 1) v += votos[claves[x + 1]];
                    if (v >= MinimoValoresComunes) picos.Add((v, d - CeroDesfase));
                }
                i = j;
                if (picos.Count == 0) continue;

                // Sin ediciones, lo que dura muy distinto no puede ser una copia: ni se compara.
                if (!incluirEdiciones
                    && Math.Abs(conHuella[a].T.DurationSeconds - conHuella[b].T.DurationSeconds) > ToleranciaDuracionSeg) continue;

                validadas++;
                if (Validar(conHuella[a], conHuella[b], picos, incluirEdiciones, umbral) is not { } arista) continue;
                // Una intro de DJ hecha con OTRO tema comparte de verdad más de 30 s de audio con
                // él, pero no es otra edición suya (ver EdicionesPorAudio.PuedenSerElMismoTema).
                if (!arista.EsCopia && !EdicionesPorAudio.PuedenSerElMismoTema(conHuella[a].T, conHuella[b].T)) continue;
                aristas.Add(arista with { A = a, B = b });
            }
        }
        progreso?.Report((conHuella.Count, conHuella.Count));
        return aristas;
    }

    private static int Cancion(long entrada) => (int)(entrada >> BitsPosicion) & MascaraCancion;
    private static int Posicion(long entrada) => (int)entrada & MascaraPosicion;

    /// <summary>
    /// Compara una pareja en los desfases más votados (y sus vecinos) y decide si es una copia, otra
    /// edición del mismo tema o nada. Null si no pasa.
    /// </summary>
    private static Arista? Validar((Track T, int[] F) a, (Track T, int[] F) b, List<(int Votos, int Desfase)> picos,
                                   bool incluirEdiciones, double umbral)
    {
        // De más a menos votado; a igualdad, el desfase más pequeño.
        picos.Sort((x, y) => x.Votos != y.Votos ? y.Votos.CompareTo(x.Votos) : Math.Abs(x.Desfase).CompareTo(Math.Abs(y.Desfase)));

        var mismaDuracion = Math.Abs(a.T.DurationSeconds - b.T.DurationSeconds) <= ToleranciaDuracionSeg;
        var minimoSolape = (int)Math.Ceiling(MinimoSolapeEdicionSeg / AudioFingerprint.SegundosPorElemento);
        int? copia = null, edicion = null;
        double parecidoCopia = 0, parecidoEdicion = 0;
        var probados = 0;
        Span<int> anteriores = stackalloc int[PicosPorPareja];
        foreach (var (_, d) in picos)
        {
            // Los vecinos de un pico ya probado no son otro pico: son el mismo, visto de lado.
            var repetido = false;
            for (var p = 0; p < probados; p++) repetido |= Math.Abs(anteriores[p] - d) <= 2;
            if (repetido) continue;
            anteriores[probados++] = d;

            for (var dd = d - 1; dd <= d + 1; dd++)
            {
                var (parecido, solape) = AudioFingerprint.SimilitudEnDesfase(a.F, b.F, dd);
                if (parecido < umbral) continue;
                if (mismaDuracion && Math.Abs(dd) <= AudioFingerprint.DesplazamientoMaximo)
                {
                    if (parecido > parecidoCopia) { parecidoCopia = parecido; copia = dd; }
                }
                // Una edición necesita un tramo común largo; una copia, no (sus huellas se solapan enteras).
                else if (incluirEdiciones && solape >= minimoSolape && parecido > parecidoEdicion)
                {
                    parecidoEdicion = parecido;
                    edicion = dd;
                }
            }
            if (probados == PicosPorPareja) break;
        }

        // Si casan como copia, son copias aunque también casen en otro desfase (un tema con partes
        // repetidas casa consigo mismo desplazado un estribillo): la copia es lo que importa.
        if (copia is { } c) return new Arista(0, 0, c, EsCopia: true);
        if (edicion is { } e) return new Arista(0, 0, e, EsCopia: false);
        return null;
    }

    /// <summary>Une las parejas en grupos, separa las ediciones y sitúa cada canción en su grupo.</summary>
    private static ResultadoPorAudio Construir(List<(Track T, int[] F)> conHuella, List<Arista> aristas, int validadas)
    {
        var n = conHuella.Count;

        // Ediciones: lo que unen las copias. Grupos: lo que unen copias y ediciones.
        var edicion = new UnionFind(n);
        var grupo = new UnionFind(n);
        var vecinos = new Dictionary<int, List<(int Otra, int Desfase)>>();
        foreach (var ar in aristas)
        {
            if (ar.EsCopia) edicion.Unir(ar.A, ar.B);
            grupo.Unir(ar.A, ar.B);
            if (!vecinos.TryGetValue(ar.A, out var la)) vecinos[ar.A] = la = new();
            if (!vecinos.TryGetValue(ar.B, out var lb)) vecinos[ar.B] = lb = new();
            la.Add((ar.B, ar.Desfase));
            lb.Add((ar.A, -ar.Desfase));
        }

        var porGrupo = new Dictionary<int, List<int>>();
        for (var i = 0; i < n; i++)
        {
            var r = grupo.Raiz(i);
            if (!porGrupo.TryGetValue(r, out var lista)) porGrupo[r] = lista = new List<int>();
            lista.Add(i);
        }

        var alineacion = new Dictionary<string, AlineacionAudio>(StringComparer.OrdinalIgnoreCase);
        var salida = new List<(DuplicateGroup G, bool ConCopias)>();
        foreach (var miembros in porGrupo.Values)
        {
            if (miembros.Count < 2) continue;

            // Dónde empieza el audio común en cada canción, recorriendo las parejas desde la primera:
            // si A→B es +240 y B→C es −40, C empieza 200 elementos después que A.
            var inicio = new Dictionary<int, int> { [miembros[0]] = 0 };
            var cola = new Queue<int>();
            cola.Enqueue(miembros[0]);
            while (cola.Count > 0)
            {
                var x = cola.Dequeue();
                if (!vecinos.TryGetValue(x, out var lx)) continue;
                foreach (var (otra, d) in lx)
                    if (inicio.TryAdd(otra, inicio[x] + d)) cola.Enqueue(otra);
            }

            var tracks = new List<Track>(miembros.Count);
            foreach (var i in miembros)
            {
                tracks.Add(conHuella[i].T);
                alineacion[conHuella[i].T.FilePath] = new AlineacionAudio(
                    edicion.Raiz(i), inicio.GetValueOrDefault(i) * AudioFingerprint.SegundosPorElemento);
            }

            var muestra = MuestraParaElTitulo(tracks);
            var artista = muestra.Artist ?? "";
            var titulo = string.IsNullOrWhiteSpace(muestra.Title) ? muestra.FileName : muestra.Title!;
            var conCopias = miembros.GroupBy(edicion.Raiz).Any(g => g.Count() > 1);
            salida.Add((new DuplicateGroup("fp:" + titulo, artista, titulo, tracks), conCopias));
        }

        // Primero los grupos que tienen copias que sobran, que es a lo que se viene a Duplicados; los
        // que solo reúnen ediciones distintas, al final.
        var grupos = salida.OrderByDescending(x => x.ConCopias)
                           .ThenByDescending(x => x.G.Tracks.Count)
                           .ThenBy(x => x.G.Artist)
                           .Select(x => x.G)
                           .ToList();
        return new ResultadoPorAudio(grupos, alineacion, validadas);
    }

    /// <summary>
    /// La canción que da nombre al grupo. Primero, una que no sea una edición de DJ: un grupo por
    /// audio reúne el tema con sus intros, acapellas y mashups, y titularlo «MUCHACHA x El Farsante
    /// (Borja Solla Mashup)» o «Bailando por Ahi (F3LY Melodic Intro) [130BPM] [INTRO LIMPIA]», como
    /// pasaba en una biblioteca real, no dice de qué tema es. Después, una con el título etiquetado:
    /// si una copia está sin etiquetar y otra no, conviene enseñar la que dice algo, y eso incluye el
    /// artista: «TUSA» sin artista ganaba por título corto a «Tusa (feat. Nicki Minaj)» de Karol G, y el
    /// grupo salía como «¿? - TUSA». Entre esas, la de título más corto, que es el menos adornado
    /// («Columbia» antes que «Columbia x High Hopes»), y a igualdad la que más artistas acredita.
    /// </summary>
    internal static Track MuestraParaElTitulo(IReadOnlyList<Track> tracks)
        => tracks.OrderBy(t => EdicionDj.Es(t.FilePath))
                 .ThenBy(t => string.IsNullOrWhiteSpace(t.Title))
                 .ThenBy(t => string.IsNullOrWhiteSpace(t.Artist))
                 .ThenBy(t => (t.Title ?? "").Trim().Length)
                 .ThenByDescending(t => (t.Artist ?? "").Length)
                 .First();

    private sealed class UnionFind
    {
        private readonly int[] _padre;
        public UnionFind(int n) { _padre = new int[n]; for (var i = 0; i < n; i++) _padre[i] = i; }
        public int Raiz(int x) { while (_padre[x] != x) { _padre[x] = _padre[_padre[x]]; x = _padre[x]; } return x; }
        public void Unir(int x, int y) { var a = Raiz(x); var b = Raiz(y); if (a != b) _padre[b] = a; }
    }
}
