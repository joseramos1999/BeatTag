using System.Text.RegularExpressions;

namespace Etiquetador.Core;

/// <summary>
/// Filtro de texto de las tablas: decide si una fila coincide con lo que el usuario está tecleando.
///
/// Las reglas salen de cómo se busca de verdad en una biblioteca de DJ:
///
/// - Por PALABRAS sueltas y en cualquier orden. Quien busca "bunny titi" quiere encontrar
///   "Bad Bunny - Tití Me Preguntó" sin acordarse del orden ni escribirlo entero.
/// - Sin acentos y sin distinguir mayúsculas: "titi" encuentra "Tití", "rosalia" encuentra
///   "ROSALÍA". Escribir acentos para buscar es un impuesto absurdo.
/// - Ignorando los separadores del nombre de archivo: "bad bunny" encuentra
///   "Bad_Bunny-Titi.mp3", porque el guion bajo y el guion no son parte de lo que uno busca.
/// - TODAS las palabras tienen que aparecer: escribir más palabras acota, nunca amplía.
/// </summary>
public static class BusquedaTexto
{
    /// <summary>
    /// ¿Casan los campos de una fila con la consulta? Con la consulta vacía siempre sí, de modo que
    /// borrar el cuadro de búsqueda devuelve la lista entera.
    /// </summary>
    public static bool Coincide(string? consulta, params string?[] campos)
    {
        var palabras = Palabras(consulta);
        if (palabras.Length == 0) return true;

        var heno = Normalizar(string.Join(" ", campos.Where(c => !string.IsNullOrEmpty(c))));
        return palabras.All(p => heno.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>Palabras de la consulta, ya normalizadas. Vacío si no hay nada que buscar.</summary>
    public static string[] Palabras(string? consulta)
        => Normalizar(consulta).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Minúsculas, sin acentos y con todo lo que no sea letra o número convertido en espacio. Eso
    /// último es lo que hace que un nombre de archivo con guiones bajos se busque como una frase.
    /// </summary>
    private static string Normalizar(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = TextUtils.RemoveDiacritics(s).ToLowerInvariant();
        return Regex.Replace(Regex.Replace(d, @"[^\p{L}\p{N}]+", " "), @"\s{2,}", " ").Trim();
    }
}
