namespace Etiquetador.Core.Analysis;

/// <summary>
/// Dónde cae una canción dentro de su grupo por audio (hallazgo A14).
/// </summary>
/// <param name="Edicion">
/// Qué edición es. Las copias del MISMO archivo (mismo audio alineado y misma duración, salvo unos
/// segundos) comparten número; una Extended, una Hype Intro o un Radio Edit del mismo tema llevan
/// otro. Solo sirve para distinguirlas entre sí, el valor en sí no significa nada.
/// </param>
/// <param name="InicioSeg">
/// Segundo del archivo en el que empieza el audio común, medido respecto a la canción por la que se
/// empezó a recorrer el grupo. Solo tiene sentido comparado con el de las demás del mismo grupo: si
/// una vale 0 y otra 32, la segunda trae 32 s más de intro.
/// </param>
public sealed record AlineacionAudio(int Edicion, double InicioSeg);

/// <summary>Lo que devuelve agrupar por audio: los grupos y, de cada canción, su edición.</summary>
/// <param name="Grupos">Los grupos, primero los que tienen copias que sobran.</param>
/// <param name="Alineacion">Por ruta (sin distinguir mayúsculas): edición e inicio de cada canción agrupada.</param>
/// <param name="ParejasValidadas">
/// Parejas cuyo parecido se ha llegado a calcular. Sirve para medir: antes eran todas las que
/// compartían tres valores de huella, cada una en 161 desfases; ahora, solo las que votan un
/// desfase claro, y en ese desfase.
/// </param>
public sealed record ResultadoPorAudio(
    IReadOnlyList<DuplicateGroup> Grupos,
    IReadOnlyDictionary<string, AlineacionAudio> Alineacion,
    int ParejasValidadas)
{
    /// <summary>Grupos que reúnen más de una edición del mismo tema.</summary>
    public int GruposConVariasEdiciones => Grupos.Count(g => EdicionesPorAudio.Contar(g.Tracks, Alineacion) > 1);
}

/// <summary>
/// Cómo se le cuenta al usuario qué edición es cada canción de un grupo por audio (hallazgo A14).
///
/// Una Extended y su Original NO son duplicados para un DJ: son herramientas distintas, y la
/// Extended existe precisamente por su intro larga. Por eso Duplicados las enseña juntas, para
/// saber qué ediciones hay de cada tema, pero distinguidas, y cada edición conserva su mejor copia:
/// «Marcar sobrantes» solo marca copias de la MISMA edición.
///
/// Se compara cada edición con la de REFERENCIA del grupo, la que empieza antes (la de intro más
/// corta). Así la etiqueta dice lo que un DJ quiere saber para elegir: cuántos segundos más tarda
/// en entrar el tema.
/// </summary>
public static class EdicionesPorAudio
{
    /// <summary>Etiqueta de la edición con la que se comparan las demás.</summary>
    public const string Referencia = "De referencia";

    /// <summary>
    /// Por debajo de esta diferencia de inicio se considera que dos ediciones empiezan igual y lo
    /// que las distingue es la duración (un Radio Edit que corta antes, una Extended con más final).
    /// Tres segundos son menos de dos compases a cualquier tempo de baile: no es otra intro.
    /// </summary>
    public const int MismoInicioSeg = 3;

    /// <summary>Cuántas ediciones distintas hay entre estas canciones (1 si no hay datos de alineación).</summary>
    public static int Contar(IReadOnlyList<Track> tracks, IReadOnlyDictionary<string, AlineacionAudio>? alineacion)
    {
        if (alineacion == null || tracks.Count == 0) return 1;
        var ediciones = new HashSet<int>();
        foreach (var t in tracks)
            if (alineacion.TryGetValue(t.FilePath, out var a)) ediciones.Add(a.Edicion);
        return Math.Max(1, ediciones.Count);
    }

    /// <summary>
    /// La etiqueta de cada canción de un grupo, por ruta. Vacía para todas si el grupo tiene una sola
    /// edición (son copias y no hay nada que distinguir). Se calcula con las canciones que quedan, así
    /// que si se envía a la papelera la edición de referencia, la siguiente pasa a serlo.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Etiquetas(IReadOnlyList<Track> tracks,
                                                                IReadOnlyDictionary<string, AlineacionAudio>? alineacion)
    {
        var salida = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks) salida[t.FilePath] = "";
        if (Contar(tracks, alineacion) < 2) return salida;

        // De cada edición, la copia que empieza antes: su inicio y su duración representan a todas
        // (las copias de una misma edición solo se separan unos pocos segundos).
        var porEdicion = new Dictionary<int, (double Inicio, int Duracion)>();
        foreach (var t in tracks)
        {
            if (!alineacion!.TryGetValue(t.FilePath, out var a)) continue;
            if (!porEdicion.TryGetValue(a.Edicion, out var e) || a.InicioSeg < e.Inicio)
                porEdicion[a.Edicion] = (a.InicioSeg, t.DurationSeconds);
        }

        // La de referencia: la que empieza antes; a igualdad, la más corta, para que las demás se
        // describan como «más larga», que es lo habitual (Original frente a Extended).
        var referencia = porEdicion.OrderBy(kv => kv.Value.Inicio).ThenBy(kv => kv.Value.Duracion).First();

        foreach (var t in tracks)
        {
            if (!alineacion!.TryGetValue(t.FilePath, out var a)) continue;
            if (a.Edicion == referencia.Key) { salida[t.FilePath] = Referencia; continue; }
            var e = porEdicion[a.Edicion];
            salida[t.FilePath] = Texto(e.Inicio - referencia.Value.Inicio, e.Duracion - referencia.Value.Duracion);
        }
        return salida;
    }

    /// <summary>
    /// La etiqueta de una edición frente a la de referencia. Puro, para probarlo.
    ///
    /// Si empiezan igual y duran casi lo mismo, lo que las separa está en el contenido (otra intro
    /// sobre el mismo compás, una salida a capela): se dice solo «Otra edición». MEDIDO en una
    /// biblioteca real: «Nueva York (F3LY Melodic Intro)» frente a su «Live Mashup» salía como
    /// «mismo inicio, 1 s más larga», que da a entender que solo cambia la duración.
    /// </summary>
    /// <param name="despuesSeg">Cuánto después empieza el audio común (≥ 0: la referencia es la que empieza antes).</param>
    /// <param name="masLargaSeg">Cuánto más dura que la referencia (negativo si dura menos).</param>
    public static string Texto(double despuesSeg, int masLargaSeg)
    {
        var despues = (int)Math.Round(despuesSeg, MidpointRounding.AwayFromZero);
        if (despues >= MismoInicioSeg) return $"Otra edición: empieza {despues} s después";
        if (Math.Abs(masLargaSeg) > FingerprintDuplicates.ToleranciaDuracionSeg)
            return $"Otra edición: mismo inicio, {Duracion(Math.Abs(masLargaSeg))} más {(masLargaSeg > 0 ? "larga" : "corta")}";
        return "Otra edición";
    }

    /// <summary>«45 s» por debajo del minuto; «1:05» a partir de ahí, como la columna de duración.</summary>
    private static string Duracion(int segundos)
        => segundos < 60 ? $"{segundos} s" : $"{segundos / 60}:{segundos % 60:00}";

    // ---------- ¿El mismo tema, o una intro hecha con otro? ----------

    /// <summary>
    /// ¿Pueden dos canciones que comparten más de 30 s de audio ser ediciones del MISMO tema? Solo
    /// se duda cuando una de las dos es una edición de DJ por su nombre (<see cref="EdicionDj"/>):
    /// muchas «Hype Intro» y transiciones arrancan con un tramo largo de OTRA canción para entrar
    /// desde ella, y en los 120 s que cubre la huella ese tramo es casi todo lo que se ve.
    ///
    /// MEDIDO en una biblioteca real (8.219 huellas): sin esta guarda, 5 de 60 grupos con varias
    /// ediciones revisados a mano juntaban una Hype Intro con el tema que usa de entrada, no con el
    /// suyo («Plan B - Choca (Hype Intro)» con «Tocate Sola», «Sean Paul - Get Busy (Hype Intro)» con
    /// «Cuica»). En toda la biblioteca, la guarda quita 12 uniones: 11 de ese tipo (Hype Intros y
    /// ediciones en directo hechas con otro tema) y una buena, una intro de DJ con el título mal
    /// escrito («YONAGUINI 2023 INTRO» con «Yonaguni»). En otros 60 grupos al azar revisados después,
    /// ninguno junta dos temas sin relación. Se le escapa la que comparte una palabra por casualidad
    /// («Carbon (Hype Intro)» con «Carbon 15»).
    ///
    /// Por eso se exige que alguna palabra del título de una (la etiqueta o lo que va tras « - » en el
    /// nombre del archivo, sin descriptores ni corchetes) esté en el nombre completo de la otra. Si
    /// alguna de las dos no tiene título del que sacar palabras, no se puede juzgar y manda el audio.
    /// Entre canciones que no son ediciones de DJ no se mira el nombre: dos ediciones de catálogo
    /// (Original y Extended) con más de 30 s idénticos son el mismo tema aunque se titulen distinto.
    /// </summary>
    public static bool PuedenSerElMismoTema(Track a, Track b)
    {
        if (!EdicionDj.Es(a.FilePath) && !EdicionDj.Es(b.FilePath)) return true;
        var tituloA = PalabrasDelTitulo(a);
        var tituloB = PalabrasDelTitulo(b);
        if (tituloA.Count == 0 || tituloB.Count == 0) return true;
        return tituloA.Overlaps(PalabrasDelNombre(b)) || tituloB.Overlaps(PalabrasDelNombre(a));
    }

    /// <summary>
    /// Palabras que no distinguen un tema de otro: artículos, preposiciones y conjunciones comunes en
    /// español, inglés y portugués, y las marcas de colaboración. Con ellas, «Tenia Tanto Que Darte» y
    /// «Que Vas A Hacer Hoy» parecerían compartir título.
    /// </summary>
    private static readonly HashSet<string> SinSignificado = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "you", "your", "los", "las", "del", "con", "que", "por", "para",
        "una", "uno", "como", "mas", "sin", "pero", "todo", "esta", "este", "dos", "nao", "com",
        "feat", "featuring", "prod", "bpm",
    };

    /// <summary>Las palabras del título: la etiqueta y lo que va tras « - » en el nombre del archivo.</summary>
    internal static HashSet<string> PalabrasDelTitulo(Track t)
    {
        var palabras = Palabras(t.Title);
        var nombre = Path.GetFileNameWithoutExtension(t.FilePath);
        var guion = nombre.IndexOf(" - ", StringComparison.Ordinal);
        palabras.UnionWith(Palabras(guion >= 0 ? nombre[(guion + 3)..] : nombre));
        return palabras;
    }

    /// <summary>Las palabras de todo lo que nombra la canción: artista, título y nombre del archivo.</summary>
    private static HashSet<string> PalabrasDelNombre(Track t)
    {
        var palabras = Palabras(t.Artist);
        palabras.UnionWith(Palabras(t.Title));
        palabras.UnionWith(Palabras(Path.GetFileNameWithoutExtension(t.FilePath)));
        return palabras;
    }

    /// <summary>
    /// Palabras de tres o más letras, sin acentos ni mayúsculas, quitados los corchetes, los
    /// descriptores de versión (<see cref="Descriptors.CleanTitle"/>) y las de <see cref="SinSignificado"/>.
    /// </summary>
    private static HashSet<string> Palabras(string? texto)
    {
        var salida = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(texto)) return salida;
        foreach (var trozo in TextUtils.Rx(@"[^\p{L}\p{N}]+").Split(Descriptors.CleanTitle(texto)))
        {
            var p = TextUtils.NkClave(trozo);
            if (p.Length >= 3 && !SinSignificado.Contains(p)) salida.Add(p);
        }
        return salida;
    }
}
