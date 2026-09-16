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
public sealed record PasoMezcla(int Posicion, Track Track, FichaDj? Ficha, EncajeTono Tono, int DiferenciaBpm)
{
    /// <summary>«Armónica · +2 BPM». Vacío para la primera.</summary>
    public string Transicion => Posicion == 1 ? "" : $"{OrdenMezcla.Nombre(Tono)}{(DiferenciaBpm == 0 ? "" : $" · {DiferenciaBpm:+0;-0} BPM")}";
}

/// <summary>
/// Ordena una lista para pincharla seguida: cada canción, la que mejor entra después de la anterior.
///
/// No usa la IA, a propósito. Encadenar por tonalidad, tempo y energía son reglas conocidas y
/// exactas (la rueda Camelot), y un modelo de lenguaje no sabe de qué tono es una canción: solo lo
/// sabe la etiqueta. Con reglas sale igual cada vez, es instantáneo y se puede explicar.
///
/// El orden es una SUBIDA: empieza por la de menos energía y tempo, y a cada paso elige la
/// siguiente que menos cueste, penalizando sobre todo el choque de tonos, después los saltos de
/// tempo y por último bajar la energía. Es un algoritmo voraz: no garantiza el orden óptimo, pero
/// con listas de sesión (decenas o pocos cientos de temas) da un recorrido razonable al instante.
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

    private static double Coste(Track desde, FichaDj? fDesde, Track hasta, FichaDj? fHasta)
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
        // Entre dos igual de buenas, la de tempo más alto, para que la sesión suba.
        if (hasta.Bpm > 0 && desde.Bpm > 0 && hasta.Bpm < desde.Bpm) coste += 0.5;
        return coste;
    }

    public static IReadOnlyList<PasoMezcla> Ordenar(IReadOnlyList<(Track Track, FichaDj? Ficha)> canciones)
    {
        var res = new List<PasoMezcla>(canciones.Count);
        if (canciones.Count == 0) return res;

        var pendientes = canciones.ToList();

        // La de arranque: la más tranquila. Las que no tienen energía van detrás de las que sí.
        var actual = pendientes
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
            res.Add(new PasoMezcla(res.Count + 1, actual.Track, actual.Ficha,
                                   Encaje(previa.Track.KeyCamelot, actual.Track.KeyCamelot),
                                   DiferenciaBpm(previa.Track.Bpm, actual.Track.Bpm)));
        }
        return res;
    }
}
