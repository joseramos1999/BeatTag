using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Etiquetador.Core.Analysis;

namespace Etiquetador.Core.Dj;

/// <summary>En qué parte de una sesión encaja una canción. Puede encajar en varias.</summary>
[Flags]
public enum MomentoSet
{
    Ninguno = 0,
    Apertura = 1,
    Subida = 2,
    Pico = 4,
    Cierre = 8,
    After = 16,
}

public enum TipoVoz { SinIndicar, Vocal, Instrumental }

public enum TipoLetra { SinIndicar, Limpia, Explicita }

/// <summary>
/// Lo que un DJ sabe de una canción y que no cabe en las etiquetas de siempre: cuánta energía
/// tiene, en qué momento de la sesión funciona, si lleva voz, si la letra es apta para cualquier
/// público, su ambiente.
///
/// Se guarda en la base local de BeatTag y NO en el archivo. Así no se toca nada de la música por
/// rellenar una ficha, y los datos sirven igual con rekordbox, Serato o Engine DJ. Pasarlos al
/// comentario del archivo es una acción aparte que el usuario decide (ver <see cref="Fichas.Comentario"/>).
/// </summary>
public sealed class FichaDj
{
    /// <summary>De 1 a 10. Cero significa sin indicar.</summary>
    public int Energia { get; set; }

    public MomentoSet Momentos { get; set; }
    public TipoVoz Voz { get; set; }
    public TipoLetra Letra { get; set; }
    public string Idioma { get; set; } = "";

    /// <summary>Ambiente: oscura, alegre, melancólica… Texto libre, porque cada estilo usa sus palabras.</summary>
    public List<string> Ambiente { get; set; } = new();

    /// <summary>Etiquetas libres: tipo de público, evento, lo que cada uno necesite para encontrarla.</summary>
    public List<string> Etiquetas { get; set; } = new();

    /// <summary>La que se guarda para cuando la pista necesita un empujón.</summary>
    public bool ArmaSecreta { get; set; }

    [JsonIgnore]
    public bool EstaVacia =>
        Energia == 0 && Momentos == MomentoSet.Ninguno && Voz == TipoVoz.SinIndicar &&
        Letra == TipoLetra.SinIndicar && Idioma.Trim().Length == 0 &&
        Ambiente.Count == 0 && Etiquetas.Count == 0 && !ArmaSecreta;

    public FichaDj Copia() => new()
    {
        Energia = Energia,
        Momentos = Momentos,
        Voz = Voz,
        Letra = Letra,
        Idioma = Idioma,
        Ambiente = new List<string>(Ambiente),
        Etiquetas = new List<string>(Etiquetas),
        ArmaSecreta = ArmaSecreta,
    };
}

/// <summary>Nombres para pantalla y conversiones de la ficha.</summary>
public static class Fichas
{
    public const int EnergiaMaxima = 10;

    /// <summary>Los momentos, en el orden en que transcurre una sesión.</summary>
    public static readonly MomentoSet[] TodosLosMomentos =
        { MomentoSet.Apertura, MomentoSet.Subida, MomentoSet.Pico, MomentoSet.Cierre, MomentoSet.After };

    public static string Nombre(MomentoSet m) => m switch
    {
        MomentoSet.Apertura => "Apertura",
        MomentoSet.Subida => "Subida",
        MomentoSet.Pico => "Pico",
        MomentoSet.Cierre => "Cierre",
        MomentoSet.After => "After",
        _ => "",
    };

    /// <summary>"Apertura, Pico", en orden de sesión.</summary>
    public static string NombreMomentos(MomentoSet m)
        => string.Join(", ", TodosLosMomentos.Where(x => (m & x) != 0).Select(Nombre));

    public static string Nombre(TipoVoz v) => v switch
    {
        TipoVoz.Vocal => "Vocal",
        TipoVoz.Instrumental => "Instrumental",
        _ => "",
    };

    public static string Nombre(TipoLetra l) => l switch
    {
        TipoLetra.Limpia => "Limpia",
        TipoLetra.Explicita => "Explícita",
        _ => "",
    };

    /// <summary>
    /// Si la letra es apta. Manda lo que diga la ficha; si no dice nada, lo que se deduce del nombre
    /// («Clean», «Dirty»…), que ya se muestra en el resto de la aplicación. Así una colección de
    /// «solo limpias» funciona desde el primer día sin tener que fichar la biblioteca entera.
    /// </summary>
    public static TipoLetra LetraEfectiva(FichaDj? ficha, Track t)
    {
        if (ficha != null && ficha.Letra != TipoLetra.SinIndicar) return ficha.Letra;
        return ExplicitDetector.Detect(t) switch
        {
            Explicitness.Clean => TipoLetra.Limpia,
            Explicitness.Explicit => TipoLetra.Explicita,
            _ => TipoLetra.SinIndicar,
        };
    }

    /// <summary>
    /// Convierte lo que se escribe en una casilla («oscura, hipnótica») en una lista. Quita vacíos y
    /// repetidos sin distinguir mayúsculas ni acentos, conservando cómo se escribió la primera vez.
    /// </summary>
    public static List<string> PartirLista(string? texto)
    {
        var res = new List<string>();
        if (string.IsNullOrWhiteSpace(texto)) return res;
        var vistos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parte in texto.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Regex.Replace(parte, @"\s+", " ").Trim();
            if (p.Length == 0) continue;
            var clave = Clave(p);
            if (clave.Length == 0 || !vistos.Add(clave)) continue;
            res.Add(p);
        }
        return res;
    }

    public static string UnirLista(IEnumerable<string>? lista) => lista == null ? "" : string.Join(", ", lista);

    /// <summary>Clave de comparación de una etiqueta: sin mayúsculas, acentos ni signos.</summary>
    public static string Clave(string? s) => TextUtils.Nk(s);

    // --- Comentario del archivo ---
    //
    // El comentario es el único campo que leen todos los programas de DJ, y por eso es donde tiene
    // sentido volcar la ficha. Pero muchos ya lo usan (Mixed In Key escribe ahí la tonalidad, hay
    // quien apunta sus notas), así que NUNCA se sustituye entero: la ficha va dentro de un bloque
    // propio «[DJ: …]» que se reemplaza en cada volcado y deja intacto el resto del texto.

    private static readonly Regex BloqueDj = new(@"\s*\[DJ:[^\]]*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>La ficha en una línea: «[DJ: E7 · Apertura, Pico · Vocal · Limpia · ES · oscura · #boda · Arma secreta]».</summary>
    public static string Comentario(FichaDj f)
    {
        var partes = new List<string>();
        if (f.Energia > 0) partes.Add("E" + f.Energia);
        if (f.Momentos != MomentoSet.Ninguno) partes.Add(NombreMomentos(f.Momentos));
        if (f.Voz != TipoVoz.SinIndicar) partes.Add(Nombre(f.Voz));
        if (f.Letra != TipoLetra.SinIndicar) partes.Add(Nombre(f.Letra));
        if (f.Idioma.Trim().Length > 0) partes.Add(f.Idioma.Trim());
        if (f.Ambiente.Count > 0) partes.Add(UnirLista(f.Ambiente));
        if (f.Etiquetas.Count > 0) partes.Add(string.Join(" ", f.Etiquetas.Select(e => "#" + e.Replace(' ', '-'))));
        if (f.ArmaSecreta) partes.Add("Arma secreta");
        // Un corchete de cierre dentro del texto partiría el bloque y el siguiente volcado lo duplicaría.
        var cuerpo = string.Join(" · ", partes).Replace(']', ')');
        return partes.Count == 0 ? "" : $"[DJ: {cuerpo}]";
    }

    /// <summary>
    /// El comentario que debe quedar en el archivo: lo que ya tenía, sin el bloque de una ficha
    /// anterior, y con el de la ficha actual al final. Con la ficha vacía solo retira el bloque.
    /// </summary>
    public static string ComentarioCombinado(string? actual, FichaDj f)
    {
        var sinBloque = BloqueDj.Replace(actual ?? "", "").Trim();
        var bloque = Comentario(f);
        if (bloque.Length == 0) return sinBloque;
        return sinBloque.Length == 0 ? bloque : sinBloque + " " + bloque;
    }
}
