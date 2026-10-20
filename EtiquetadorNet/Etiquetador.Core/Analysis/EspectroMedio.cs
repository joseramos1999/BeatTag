namespace Etiquetador.Core.Analysis;

/// <summary>
/// Espectro de potencia medio de un trozo de audio, calculado por bloques: recibe las muestras tal
/// como salen del decodificador (intercaladas por canal) y no necesita tener la canción entera en
/// memoria. Lo usa <see cref="CorteEspectral"/> para saber hasta qué frecuencia llega el audio.
///
/// Cada canal se analiza por separado y se suman las potencias, en vez de mezclar a mono antes: lo
/// que suena en contrafase entre los dos canales (agudos muy abiertos, reverberaciones) se anularía
/// al mezclar y haría parecer que el audio llega menos alto de lo que llega.
/// </summary>
public sealed class EspectroMedio
{
    /// <summary>
    /// Muestras por ventana. A 44,1 kHz son ~93 ms y 10,8 Hz por bin: de sobra para situar un corte
    /// con una precisión de unos 100 Hz, que es lo que separa los cortes típicos de cada tasa (16, 17,
    /// 18,6, 19,7 y 20,5 kHz).
    /// </summary>
    public const int Tamano = 4096;

    private readonly int _canales;
    private readonly int _usados;
    private readonly double[][] _pendiente;
    private int _lleno;
    private readonly double[] _potencia = new double[Tamano / 2];
    private readonly double[] _re = new double[Tamano];
    private readonly double[] _im = new double[Tamano];
    private static readonly double[] Hann = CrearHann();

    public EspectroMedio(int sampleRate, int canales)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (canales <= 0) throw new ArgumentOutOfRangeException(nameof(canales));
        SampleRate = sampleRate;
        _canales = canales;
        // Con más de dos canales (raro en música) bastan los dos primeros, que son los frontales.
        _usados = Math.Min(2, canales);
        _pendiente = new double[_usados][];
        for (var c = 0; c < _usados; c++) _pendiente[c] = new double[Tamano];
    }

    public int SampleRate { get; }

    /// <summary>Ventanas analizadas hasta ahora (por canal).</summary>
    public int Ventanas { get; private set; }

    /// <summary>
    /// Añade <paramref name="n"/> muestras intercaladas (n = tramas × canales). Las ventanas se solapan
    /// a la mitad, para que ningún trozo de audio quede con un peso casi nulo en el borde de la ventana.
    /// </summary>
    public void Add(float[] intercaladas, int n)
    {
        var tramas = Math.Min(n, intercaladas.Length) / _canales;
        for (var t = 0; t < tramas; t++)
        {
            var b = t * _canales;
            for (var c = 0; c < _usados; c++) _pendiente[c][_lleno] = intercaladas[b + c];
            if (++_lleno < Tamano) continue;

            for (var c = 0; c < _usados; c++)
            {
                var p = _pendiente[c];
                for (var i = 0; i < Tamano; i++) { _re[i] = p[i] * Hann[i]; _im[i] = 0.0; }
                Fft.Forward(_re, _im);
                for (var i = 0; i < _potencia.Length; i++) _potencia[i] += _re[i] * _re[i] + _im[i] * _im[i];
                // La segunda mitad pasa a ser la primera de la ventana siguiente.
                Array.Copy(p, Tamano / 2, p, 0, Tamano / 2);
            }
            Ventanas++;
            _lleno = Tamano / 2;
        }
    }

    /// <summary>
    /// Descarta la ventana a medio llenar. Se llama al saltar a otro punto de la canción: una ventana
    /// que juntara el final de un tramo con el principio del siguiente tendría un salto brusco, y un
    /// salto así suena en todas las frecuencias, también en las que el archivo no tiene.
    /// </summary>
    public void Cortar() => _lleno = 0;

    /// <summary>Potencia media por bin (de 0 a la mitad de la frecuencia de muestreo), o null sin ventanas.</summary>
    public double[]? Potencia()
    {
        if (Ventanas == 0) return null;
        var r = new double[_potencia.Length];
        for (var i = 0; i < r.Length; i++) r[i] = _potencia[i] / (Ventanas * (double)_usados);
        return r;
    }

    private static double[] CrearHann()
    {
        var w = new double[Tamano];
        for (var i = 0; i < Tamano; i++) w[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (Tamano - 1)));
        return w;
    }
}
