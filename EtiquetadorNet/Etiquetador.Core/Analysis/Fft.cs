namespace Etiquetador.Core.Analysis;

/// <summary>
/// Transformada rápida de Fourier (radix-2, en el sitio). Convierte un trozo de onda en el reparto
/// de energía por frecuencias, que es lo que hace falta para saber qué notas suenan.
///
/// Se escribe aquí en lugar de tirar de una biblioteca para no meter dependencias de audio en la
/// capa pura: son cuarenta líneas y así <see cref="KeyDetector"/> se puede probar sin nada más.
/// </summary>
public static class Fft
{
    /// <summary>
    /// Transforma en el sitio. <paramref name="re"/> e <paramref name="im"/> deben medir lo mismo y
    /// ser una potencia de dos.
    /// </summary>
    public static void Forward(double[] re, double[] im)
    {
        var n = re.Length;
        if (n <= 1) return;
        if ((n & (n - 1)) != 0) throw new ArgumentException("El tamaño debe ser potencia de dos.", nameof(re));

        // Reordenación por inversión de bits: deja las muestras donde las espera el bucle siguiente.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }

        // Mariposas, de bloques de 2 hasta el tamaño completo.
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2.0 * Math.PI / len;
            var wRe = Math.Cos(ang);
            var wIm = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double curRe = 1.0, curIm = 0.0;
                for (var k = 0; k < len / 2; k++)
                {
                    var uRe = re[i + k];
                    var uIm = im[i + k];
                    var vRe = re[i + k + len / 2] * curRe - im[i + k + len / 2] * curIm;
                    var vIm = re[i + k + len / 2] * curIm + im[i + k + len / 2] * curRe;

                    re[i + k] = uRe + vRe;
                    im[i + k] = uIm + vIm;
                    re[i + k + len / 2] = uRe - vRe;
                    im[i + k + len / 2] = uIm - vIm;

                    var nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }

    /// <summary>Magnitud de cada frecuencia. Solo devuelve la mitad útil (el resto es su espejo).</summary>
    public static double[] Magnitudes(double[] re, double[] im)
    {
        Forward(re, im);
        var mitad = re.Length / 2;
        var mag = new double[mitad];
        for (var i = 0; i < mitad; i++) mag[i] = Math.Sqrt(re[i] * re[i] + im[i] * im[i]);
        return mag;
    }
}
