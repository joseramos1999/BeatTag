using System.Numerics;

namespace Etiquetador.Core.Analysis;

/// <summary>
/// Comparación de huellas acústicas (Chromaprint en crudo, tal como las da «fpcalc -raw»).
///
/// Una huella es una secuencia de enteros de 32 bits, uno cada ~0,124 s de audio. Dos
/// codificaciones distintas del MISMO audio NO dan huellas idénticas —cambian bits sueltos— así
/// que no se pueden comparar por igualdad: hay que medir en qué porcentaje de bits coinciden.
///
/// Además, dos copias de la misma canción pueden empezar en momentos distintos (un silencio de
/// más, una intro recortada), de modo que hay que probar varios desplazamientos y quedarse con el
/// mejor. Es lo que distingue esto de comparar cadenas.
/// </summary>
public static class AudioFingerprint
{
    /// <summary>Bits por elemento de la huella.</summary>
    private const int BitsPorElemento = 32;

    /// <summary>
    /// Dos huellas sin relación rondan el 0,5 (los bits coinciden por azar la mitad de las veces),
    /// así que el umbral tiene que estar bastante por encima. 0,85 separa bien en la práctica.
    /// </summary>
    public const double UmbralIgual = 0.85;

    /// <summary>Desplazamientos que se prueban a cada lado, en elementos (~0,124 s cada uno).</summary>
    public const int DesplazamientoMaximo = 80;

    /// <summary>
    /// Parecido entre dos huellas, de 0 a 1: fracción de bits que coinciden en el mejor
    /// alineamiento. Devuelve 0 si alguna está vacía o no hay solape suficiente.
    /// </summary>
    public static double Similarity(int[]? a, int[]? b, int desplazamientoMaximo = DesplazamientoMaximo)
    {
        if (a is not { Length: > 0 } || b is not { Length: > 0 }) return 0.0;

        // Con menos solape que esto la medida no es fiable: unos pocos elementos coinciden por
        // casualidad con facilidad. Es un minimo ABSOLUTO (~2,5 s de audio): si una de las dos
        // huellas no llega, se rechaza en vez de rebajar el liston, que era el error de antes.
        const int minimoSolape = 20;
        if (a.Length < minimoSolape || b.Length < minimoSolape) return 0.0;

        var mejor = 0.0;
        for (var off = -desplazamientoMaximo; off <= desplazamientoMaximo; off++)
        {
            // off > 0 -> b empieza más tarde que a
            var iniA = off >= 0 ? off : 0;
            var iniB = off >= 0 ? 0 : -off;
            var n = Math.Min(a.Length - iniA, b.Length - iniB);
            if (n < minimoSolape) continue;

            var bitsIguales = 0L;
            for (var i = 0; i < n; i++)
                bitsIguales += BitsPorElemento - BitOperations.PopCount((uint)(a[iniA + i] ^ b[iniB + i]));

            var s = bitsIguales / (double)(n * BitsPorElemento);
            if (s > mejor) mejor = s;
        }
        return mejor;
    }

    /// <summary>true si las dos huellas corresponden a la misma grabación.</summary>
    public static bool SameRecording(int[]? a, int[]? b, double umbral = UmbralIgual)
        => Similarity(a, b) >= umbral;
}
