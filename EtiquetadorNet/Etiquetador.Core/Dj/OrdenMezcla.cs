using System.Text.RegularExpressions;

namespace Etiquetador.Core.Dj;

/// <summary>Qué tal encaja una canción con la anterior, en tono.</summary>
public enum EncajeTono
{
    /// <summary>Mismo código Camelot.</summary>
    Igual,
    /// <summary>Un paso en la rueda (8A→9A), o relativa mayor/menor (8A→8B). Mezcla armónica.</summary>
    Compatible,
    /// <summary>Dos pasos (8A→10A): subida de energía que muchos DJs usan a propósito.</summary>
    Salto,
    /// <summary>Alguna de las dos no tiene tonalidad en las etiquetas.</summary>
    Desconocido,
    /// <summary>No encajan armónicamente.</summary>
    Choque,
}

/// <summary>Una canción en el orden propuesto, con cómo entra desde la anterior.</summary>
/// <param name="PasosAtras">
/// Cuántos momentos de la sesión retrocede respecto a la anterior (Pico → Subida es uno), según las
/// fichas. Cero si no retrocede o si alguna de las dos no tiene momento.
/// </param>
/// <param name="MomentosSaltados">Cuántos momentos se salta hacia delante (Subida → Cierre se salta el Pico).</param>
public sealed record PasoMezcla(int Posicion, Track Track, FichaDj? Ficha, EncajeTono Tono, int DiferenciaBpm,
                                int PasosAtras = 0, int MomentosSaltados = 0)
{
    /// <summary>«Armónica · +2 BPM». Vacío para la primera.</summary>
    public string Transicion => Posicion == 1 ? "" :
        $"{OrdenMezcla.Nombre(Tono)}{(DiferenciaBpm == 0 ? "" : $" · {DiferenciaBpm:+0;-0} BPM")}"
        + (Ficha == null ? "" : PasosAtras > 0 ? $" · vuelve a {Fichas.NombreMomentos(Ficha.Momentos)}"
                              : MomentosSaltados > 0 ? $" · salta a {Fichas.NombreMomentos(Ficha.Momentos)}" : "");
}

/// <summary>Las cifras de un orden ya calculado, para la línea de resumen.</summary>
public sealed record ResumenMezcla(int Canciones, int Transiciones, int Armonicas, int Choques, int SinTonalidad,
                                   int VueltasAtras, int Saltos)
{
    /// <summary>
    /// Más de la mitad de las transiciones sin tonalidad: el orden sale casi solo por tempo, y hay que
    /// decir de dónde sacarla.
    /// </summary>
    public bool FaltaTonalidad => Transiciones > 0 && SinTonalidad > Transiciones / 2;

    // Singular y plural (revisión de la tanda 3, interfaz-11): con una sola salía «1 choques de tono».
    public string Texto =>
        $"{Canciones} {(Canciones == 1 ? "canción" : "canciones")} · {Armonicas} de {Transiciones} "
        + $"{(Transiciones == 1 ? "transición armónica" : "transiciones armónicas")} · "
        + $"{Choques} {(Choques == 1 ? "choque" : "choques")} de tono · {SinTonalidad} sin tonalidad"
        + (VueltasAtras > 0 ? $" · {VueltasAtras} {(VueltasAtras == 1 ? "vuelve" : "vuelven")} a un momento anterior" : "")
        + (Saltos > 0 ? $" · {Saltos} {(Saltos == 1 ? "se salta" : "se saltan")} un momento" : "");
}

/// <summary>
/// Ordena una lista para pincharla seguida: cada canción, la que mejor entra después de la anterior.
///
/// No usa la IA, a propósito. Encadenar por tonalidad, tempo y energía son reglas conocidas y
/// exactas (la rueda Camelot), y un modelo de lenguaje no sabe de qué tono es una canción: solo lo
/// sabe la etiqueta. Con reglas sale igual cada vez, es instantáneo y se puede explicar.
///
/// El orden es una SUBIDA: empieza por la de menos energía y tempo (o por la que elija el usuario),
/// y a cada paso elige la siguiente que menos cueste, penalizando sobre todo el choque de tonos,
/// después los saltos de tempo, salirse del orden de los momentos de la sesión y bajar la energía.
/// Es un algoritmo voraz: no garantiza el orden óptimo, pero con listas de sesión (decenas o pocos
/// cientos de temas) da un recorrido razonable al instante.
/// </summary>
public static class OrdenMezcla
{
    private static readonly Regex CamelotRe = new(@"^\s*(1[0-2]|[1-9])\s*([AB])\s*$", RegexOptions.IgnoreCase);

    public static (int Numero, char Letra)? Camelot(string? codigo)
    {
        var m = CamelotRe.Match(codigo ?? "");
        return m.Success ? (int.Parse(m.Groups[1].Value), char.ToUpperInvariant(m.Groups[2].Value[0])) : null;
    }

    public static EncajeTono Encaje(string? a, string? b)
    {
        if (Camelot(a) is not { } ca || Camelot(b) is not { } cb) return EncajeTono.Desconocido;
        var (na, la) = ca;
        var (nb, lb) = cb;
        var pasos = Math.Min((na - nb + 12) % 12, (nb - na + 12) % 12);
        if (la == lb)
            return pasos switch { 0 => EncajeTono.Igual, 1 => EncajeTono.Compatible, 2 => EncajeTono.Salto, _ => EncajeTono.Choque };
        return pasos == 0 ? EncajeTono.Compatible : EncajeTono.Choque;
    }

    public static string Nombre(EncajeTono e) => e switch
    {
        EncajeTono.Igual => "Mismo tono",
        EncajeTono.Compatible => "Armónica",
        EncajeTono.Salto => "Salto de tono",
        EncajeTono.Desconocido => "Tono desconocido",
        _ => "Choque de tono",
    };

    /// <summary>
    /// Diferencia de tempo teniendo en cuenta el doble y la mitad: un tema a 170 entra sobre uno a 85
    /// sin tocar el pitch. Cero si alguno no tiene BPM.
    /// </summary>
    public static int DiferenciaBpm(uint desde, uint hasta)
    {
        if (desde == 0 || hasta == 0) return 0;
        int a = (int)desde, b = (int)hasta;
        var opciones = new[] { b - a, b - 2 * a, 2 * b - a };
        return opciones.OrderBy(Math.Abs).First();
    }

    /// <summary>
    /// Lo que cuesta pasar de una canción a otra: cuanto menos, mejor entra. Lo usan este orden y la
    /// búsqueda de compatibles (<see cref="Compatibles"/>), para que las dos digan lo mismo.
    /// </summary>
    public static double Coste(Track desde, FichaDj? fDesde, Track hasta, FichaDj? fHasta)
    {
        double coste = Encaje(desde.KeyCamelot, hasta.KeyCamelot) switch
        {
            EncajeTono.Igual => 0,
            EncajeTono.Compatible => 1,
            EncajeTono.Salto => 3,
            EncajeTono.Desconocido => 4,
            _ => 8,
        };
        // Sin BPM no se sabe cuánto cuesta el cambio: se cuenta como un salto moderado, no gratis.
        coste += desde.Bpm == 0 || hasta.Bpm == 0 ? 6 : Math.Abs(DiferenciaBpm(desde.Bpm, hasta.Bpm));
        // Bajar energía rompe la subida; subirla, no.
        if ((fDesde?.Energia ?? 0) > 0 && (fHasta?.Energia ?? 0) > 0)
            coste += 2 * Math.Max(0, fDesde!.Energia - fHasta!.Energia);
        // Salirse del orden de los momentos de la sesión rompe el recorrido (ver CostePorMomento).
        var mDesde = fDesde?.Momentos ?? MomentoSet.Ninguno;
        var mHasta = fHasta?.Momentos ?? MomentoSet.Ninguno;
        coste += CostePorMomento * (PasosAtras(mDesde, mHasta) + MomentosSaltados(mDesde, mHasta));
        // Entre dos igual de buenas, la de tempo más alto, para que la sesión suba.
        if (hasta.Bpm > 0 && desde.Bpm > 0 && hasta.Bpm < desde.Bpm) coste += 0.5;
        return coste;
    }

    /// <summary>
    /// Lo que cuesta cada momento de la sesión que se retrocede (Pico → Subida) o que se salta hacia
    /// delante (Subida → Cierre, sin pasar por el Pico).
    ///
    /// Tres, en la misma escala que el resto del coste: lo mismo que un salto de dos pasos en la
    /// rueda Camelot o que tres BPM de diferencia. Así salirse del orden solo se acepta si las demás
    /// opciones entran todavía peor de tono o de tempo; dos momentos (6) ya pesan como no saber el
    /// BPM, y tres (9) más que un choque de tono (8). El motivo: un choque de tono se disimula con un
    /// corte o un efecto, pero meter un tema de cierre en mitad de la subida rompe el recorrido de la
    /// noche y no tiene arreglo.
    ///
    /// Saltar hacia delante cuesta lo mismo que volver atrás, y no por simetría: el orden se elige paso
    /// a paso, y si solo costara retroceder, el tema de cierre que encaja mejor de tono entraría gratis
    /// en mitad de la subida y la penalización la pagarían DESPUÉS los temas de pico que quedaran, que
    /// es justo el caso que se quería evitar. Si en la lista no hay ningún tema de un momento, saltarlo
    /// cuesta lo mismo a todas las candidatas y no cambia nada.
    ///
    /// Solo cuenta cuando LAS DOS canciones tienen el momento en su ficha: sin ficha no se sabe nada y
    /// no se penaliza.
    /// </summary>
    public const double CostePorMomento = 3;

    /// <summary>
    /// Cuántos momentos retrocede la sesión al pasar de una canción a otra. Una canción puede valer
    /// para varios momentos («Apertura, Subida»): solo se cuenta como retroceso si NINGUNA de sus
    /// lecturas evita volver atrás. Cero si alguna de las dos no tiene momento.
    /// </summary>
    public static int PasosAtras(MomentoSet desde, MomentoSet hasta)
    {
        if (Tramo(desde) is not { } a || Tramo(hasta) is not { } b) return 0;
        return Math.Max(0, a.Primero - b.Ultimo);
    }

    /// <summary>
    /// Cuántos momentos se saltan hacia delante: de Subida a Cierre, uno (el Pico). Pasar al momento
    /// siguiente no se salta nada. Con varios momentos marcados, se mira la lectura más cercana.
    /// </summary>
    public static int MomentosSaltados(MomentoSet desde, MomentoSet hasta)
    {
        if (Tramo(desde) is not { } a || Tramo(hasta) is not { } b) return 0;
        return Math.Max(0, b.Primero - a.Ultimo - 1);
    }

    /// <summary>El primer y el último momento marcados, por su posición en la sesión.</summary>
    private static (int Primero, int Ultimo)? Tramo(MomentoSet m)
    {
        int primero = -1, ultimo = -1;
        for (var i = 0; i < Fichas.TodosLosMomentos.Length; i++)
        {
            if ((m & Fichas.TodosLosMomentos[i]) == 0) continue;
            if (primero < 0) primero = i;
            ultimo = i;
        }
        return primero < 0 ? null : (primero, ultimo);
    }

    /// <param name="canciones">Las canciones a ordenar, con su ficha si la tienen.</param>
    /// <param name="primera">
    /// La canción con la que se quiere abrir (se busca por ruta). Sin ella, o si no está en la lista,
    /// se abre con la más tranquila, que es el criterio de siempre.
    /// </param>
    public static IReadOnlyList<PasoMezcla> Ordenar(IReadOnlyList<(Track Track, FichaDj? Ficha)> canciones, Track? primera = null)
    {
        var res = new List<PasoMezcla>(canciones.Count);
        if (canciones.Count == 0) return res;

        var pendientes = canciones.ToList();

        // La de arranque: la elegida o, si no hay, la más tranquila. Las que no tienen energía van
        // detrás de las que sí.
        var elegida = primera == null ? -1
            : pendientes.FindIndex(c => string.Equals(c.Track.FilePath, primera.FilePath, StringComparison.OrdinalIgnoreCase));
        var actual = elegida >= 0 ? pendientes[elegida] : pendientes
            .OrderBy(c => c.Ficha?.Energia is > 0 ? c.Ficha.Energia : 99)
            .ThenBy(c => c.Track.Bpm == 0 ? uint.MaxValue : c.Track.Bpm)
            .ThenBy(c => c.Track.FilePath, StringComparer.OrdinalIgnoreCase)
            .First();
        pendientes.Remove(actual);
        res.Add(new PasoMezcla(1, actual.Track, actual.Ficha, EncajeTono.Igual, 0));

        while (pendientes.Count > 0)
        {
            var previa = actual;
            actual = pendientes
                .OrderBy(c => Coste(previa.Track, previa.Ficha, c.Track, c.Ficha))
                .ThenBy(c => c.Track.FilePath, StringComparer.OrdinalIgnoreCase)   // mismo resultado cada vez
                .First();
            pendientes.Remove(actual);
            var mPrevia = previa.Ficha?.Momentos ?? MomentoSet.Ninguno;
            var mActual = actual.Ficha?.Momentos ?? MomentoSet.Ninguno;
            res.Add(new PasoMezcla(res.Count + 1, actual.Track, actual.Ficha,
                                   Encaje(previa.Track.KeyCamelot, actual.Track.KeyCamelot),
                                   DiferenciaBpm(previa.Track.Bpm, actual.Track.Bpm),
                                   PasosAtras(mPrevia, mActual), MomentosSaltados(mPrevia, mActual)));
        }
        return res;
    }

    /// <summary>Las cifras del resumen de un orden: transiciones armónicas, choques, sin tonalidad.</summary>
    public static ResumenMezcla Resumir(IReadOnlyList<PasoMezcla> orden)
    {
        var transiciones = orden.Skip(1).ToList();
        int N(EncajeTono e) => transiciones.Count(x => x.Tono == e);
        return new ResumenMezcla(orden.Count, transiciones.Count, N(EncajeTono.Igual) + N(EncajeTono.Compatible),
                                 N(EncajeTono.Choque), N(EncajeTono.Desconocido),
                                 transiciones.Count(x => x.PasosAtras > 0), transiciones.Count(x => x.MomentosSaltados > 0));
    }

    /// <summary>El orden como entradas de una lista M3U8, en ese mismo orden.</summary>
    public static IEnumerable<PlaylistItem> ParaLista(IEnumerable<PasoMezcla> orden)
        => orden.Select(p => new PlaylistItem(p.Track.FilePath, p.Track.Artist ?? "", p.Track.Title ?? p.Track.FileName,
                                              p.Track.DurationSeconds));
}
