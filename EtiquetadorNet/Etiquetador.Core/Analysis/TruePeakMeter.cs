namespace Etiquetador.Core.Analysis;

/// <summary>
/// Pico real (true peak, dBTP) según ITU-R BS.1770-4, anexo 2: sobremuestreo ×4 con el filtro FIR
/// polifásico de 48 coeficientes (4 fases de 12) que da la propia norma, y el valor absoluto más
/// alto del resultado. Se trabaja en coma flotante, así que no hace falta la atenuación de 12,04 dB
/// que la norma prevé para las implementaciones con enteros.
///
/// POR QUÉ. El pico de MUESTRA se queda corto: al reproducir, el convertidor reconstruye la onda
/// entre muestra y muestra, y ahí se forman picos más altos. MEDIDO en una biblioteca real (300
/// MP3 al azar): mediana 0,23 dB por encima del pico de muestra, percentil 99 de 1,98 dB y máximo de
/// 2,0 dB. Con el pico de muestra, el 13-16 % de las subidas al máximo acababan por encima de
/// 0 dBTP, es decir, con distorsión al reproducir.
///
/// El resultado nunca es menor que el pico de muestra: la fase 0 del filtro pesa 0,972 en la muestra
/// central, y una muestra aislada saldría algo por debajo de sí misma (igual que hace libebur128).
///
/// VARIANTE RÁPIDA Y EXACTA. Solo se interpola alrededor de una muestra si alguna de las 12 que
/// entran en el filtro está a menos de 7 dB del máximo visto hasta ese momento. La suma de los
/// valores absolutos de los coeficientes de cualquier fase es como mucho 2,023 (+6,12 dB), así que
/// si las 12 muestras están por debajo de máximo·10^(−7/20), la salida no pasa de 0,904·máximo y no
/// puede batirlo. El resultado es idéntico, bit a bit, al del cálculo completo (lo comprueban las
/// pruebas), y cuesta una fracción.
/// </summary>
public sealed class TruePeakMeter
{
    // ITU-R BS.1770-4, anexo 2, tabla 1: fases 0 a 3, coeficientes en orden de retardo (k = 0..11).
    private static readonly double[][] Fases =
    {
        new[] {  0.0017089843750,  0.0109863281250, -0.0196533203125,  0.0332031250000, -0.0594482421875,  0.1373291015625,
                 0.9721679687500, -0.1022949218750,  0.0476074218750, -0.0266113281250,  0.0148925781250, -0.0083007812500 },
        new[] { -0.0291748046875,  0.0292968750000, -0.0517578125000,  0.0891113281250, -0.1665039062500,  0.4650878906250,
                 0.7797851562500, -0.2003173828125,  0.1015625000000, -0.0582275390625,  0.0330810546875, -0.0189208984375 },
        new[] { -0.0189208984375,  0.0330810546875, -0.0582275390625,  0.1015625000000, -0.2003173828125,  0.7797851562500,
                 0.4650878906250, -0.1665039062500,  0.0891113281250, -0.0517578125000,  0.0292968750000, -0.0291748046875 },
        new[] { -0.0083007812500,  0.0148925781250, -0.0266113281250,  0.0476074218750, -0.1022949218750,  0.9721679687500,
                 0.1373291015625, -0.0594482421875,  0.0332031250000, -0.0196533203125,  0.0109863281250,  0.0017089843750 },
    };

    /// <summary>Muestras que entran en cada fase del filtro.</summary>
    private const int Taps = 12;

    /// <summary>
    /// 10^(−7/20): por debajo de esta fracción del máximo, una ventana entera no puede batirlo
    /// (2,023 · 0,4467 = 0,904 &lt; 1). Ver el comentario de la clase.
    /// </summary>
    internal static readonly double FraccionVentana = Math.Pow(10.0, -7.0 / 20.0);

    private readonly int _ch;
    private readonly bool _completo;
    private readonly double[][] _hist;     // por canal, duplicado (2 × Taps) para leer sin módulo
    private readonly long[] _ultimaAlta;   // por canal: índice de la última muestra cerca del máximo
    private int _pos;
    private long _n;                       // muestras por canal vistas
    private double _max;                   // pico real hasta ahora (lineal)
    private double _umbral;                // _max · FraccionVentana

    /// <summary>Muestras (por canal) en las que se ha interpolado. Solo informativo.</summary>
    public long Interpoladas { get; private set; }

    /// <param name="channels">Canales intercalados en los búferes que se van a pasar.</param>
    /// <param name="completo">Interpola en todas las muestras, sin el atajo exacto. Solo para pruebas.</param>
    public TruePeakMeter(int channels, bool completo = false)
    {
        _ch = Math.Max(1, channels);
        _completo = completo;
        _hist = new double[_ch][];
        _ultimaAlta = new long[_ch];
        for (int c = 0; c < _ch; c++)
        {
            _hist[c] = new double[Taps * 2];
            _ultimaAlta[c] = long.MinValue / 2;
        }
    }

    /// <summary>Canal de la siguiente muestra: una trama puede llegar partida entre dos búferes.</summary>
    private int _canal;

    /// <summary>
    /// Añade muestras INTERCALADAS (L,R,L,R…) en coma flotante, -1..1. El búfer no tiene por qué
    /// acabar en una trama completa: lo que falte de la última llega con el siguiente. Antes se
    /// descartaba la muestra sobrante, esa muestra no contaba para el pico y el historial del filtro
    /// mezclaba los dos canales en cada frontera (ver <see cref="LoudnessMeter.Add"/>).
    /// </summary>
    public void Add(float[] buffer, int count)
    {
        for (int i = 0; i < count; i++)
        {
            // La primera muestra de cada trama avanza el historial de todos los canales.
            if (_canal == 0) _pos = _pos + 1 == Taps ? 0 : _pos + 1;
            Muestra(_canal, buffer[i]);
            if (++_canal < _ch) continue;
            _canal = 0;
            _n++;
        }
    }

    /// <summary>Una muestra del canal <paramref name="c"/> de la trama en curso.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void Muestra(int c, double x)
    {
        var b = _pos + Taps;   // h[b - k] = x[n - k]
        var h = _hist[c];
        h[_pos] = x;
        h[b] = x;

        var a = Math.Abs(x);
        if (a > _max) { _max = a; _umbral = a * FraccionVentana; }
        if (a > 0 && a >= _umbral) _ultimaAlta[c] = _n;

        // Ninguna de las 12 muestras de la ventana está cerca del máximo: la salida no lo bate.
        if (!_completo && _n - _ultimaAlta[c] >= Taps) return;

        Interpoladas++;
        for (int f = 0; f < 4; f++)
        {
            var co = Fases[f];
            double y = 0;
            for (int k = 0; k < Taps; k++) y += co[k] * h[b - k];
            var ay = Math.Abs(y);
            if (ay > _max) { _max = ay; _umbral = ay * FraccionVentana; }
        }
    }

    /// <summary>
    /// Olvida el máximo visto hasta ahora, sin vaciar el filtro. Lo usan las pruebas de conformidad
    /// para no contar el transitorio del arranque brusco de las señales de prueba.
    /// </summary>
    internal void ReiniciarMaximo()
    {
        _max = 0;
        _umbral = 0;
        // Con el umbral a cero, las muestras que ya están en el filtro pueden volver a contar: se
        // interpola en las 12 siguientes para no saltarse ninguna ventana que las contenga.
        for (int c = 0; c < _ch; c++) _ultimaAlta[c] = _n;
    }

    /// <summary>Pico real en dBTP (−∞ si todo fue silencio).</summary>
    public double Db => _max > 0 ? 20.0 * Math.Log10(_max) : double.NegativeInfinity;
}
