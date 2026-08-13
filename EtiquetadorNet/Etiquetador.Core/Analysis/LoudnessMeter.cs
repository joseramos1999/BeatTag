namespace Etiquetador.Core.Analysis;

/// <summary>Resultado de medir una canción.</summary>
/// <param name="Lufs">Sonoridad integrada en LUFS (negativo; -14 es lo típico de streaming).</param>
/// <param name="PeakDb">Pico de muestra en dBFS (0 = tope; por encima satura).</param>
/// <param name="Seconds">Duración analizada.</param>
public readonly record struct LoudnessResult(double Lufs, double PeakDb, double Seconds)
{
    public bool Ok => !double.IsNaN(Lufs) && !double.IsNegativeInfinity(Lufs);
}

/// <summary>
/// Medidor de sonoridad EBU R128 / ITU-R BS.1770: filtro K, bloques de 400 ms solapados y doble
/// puerta (absoluta a -70 LUFS y relativa a -10 LU). Es el estándar que usan streaming y radio,
/// y mide el volumen PERCIBIDO, no el pico.
///
/// Es puro: se le van dando muestras y al final se le pide el resultado. Así se puede probar sin
/// tocar archivos (ver los tests de conformidad con tonos de referencia).
/// </summary>
public sealed class LoudnessMeter
{
    private const double AbsoluteGate = -70.0;   // LUFS
    private const double RelativeGate = -10.0;   // LU por debajo de la media con puerta absoluta
    private const double Offset = -0.691;        // constante de BS.1770

    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly Biquad[] _shelf;            // etapa 1 del filtro K (realce de agudos)
    private readonly Biquad[] _hp;               // etapa 2 (paso alto RLB)
    private readonly double[] _weights;          // peso por canal

    private readonly int _blockSize;             // muestras de 400 ms
    private readonly int _hopSize;               // 100 ms -> 75 % de solape
    private readonly double[][] _acc;            // suma de cuadrados por canal, en curso
    private int _accCount;
    private readonly List<double[]> _pending = new();   // bloques parciales para el solape

    private readonly List<double> _blockLoudness = new();
    private readonly List<double[]> _blockPower = new();

    private double _peak;
    private long _samples;

    public LoudnessMeter(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = Math.Max(1, channels);
        _shelf = new Biquad[_channels];
        _hp = new Biquad[_channels];
        for (int c = 0; c < _channels; c++)
        {
            _shelf[c] = Biquad.KWeightingShelf(sampleRate);
            _hp[c] = Biquad.KWeightingHighPass(sampleRate);
        }

        // Pesos de BS.1770: los canales normales pesan 1; los surround, 1,41. Aquí mono/estéreo.
        _weights = new double[_channels];
        for (int c = 0; c < _channels; c++) _weights[c] = 1.0;

        _blockSize = (int)(sampleRate * 0.400);
        _hopSize = (int)(sampleRate * 0.100);
        _acc = new double[_channels][];
        for (int c = 0; c < _channels; c++) _acc[c] = new double[_blockSize];
    }

    /// <summary>Añade muestras INTERCALADAS (L,R,L,R…) en punto flotante -1..1.</summary>
    public void Add(float[] buffer, int count)
    {
        for (int i = 0; i + _channels <= count; i += _channels)
        {
            for (int c = 0; c < _channels; c++)
            {
                double x = buffer[i + c];
                var a = Math.Abs(x);
                if (a > _peak) _peak = a;

                // Filtro K: realce de agudos + paso alto (aproxima cómo oye el oído).
                var y = _hp[c].Process(_shelf[c].Process(x));
                _acc[c][_accCount] = y * y;
            }
            _accCount++;
            _samples++;

            if (_accCount == _blockSize) CerrarBloque();
        }
    }

    private void CerrarBloque()
    {
        var potencia = new double[_channels];
        for (int c = 0; c < _channels; c++)
        {
            double suma = 0;
            var v = _acc[c];
            for (int i = 0; i < _blockSize; i++) suma += v[i];
            potencia[c] = suma / _blockSize;
        }

        double mezcla = 0;
        for (int c = 0; c < _channels; c++) mezcla += _weights[c] * potencia[c];
        var l = mezcla > 0 ? Offset + 10.0 * Math.Log10(mezcla) : double.NegativeInfinity;

        _blockPower.Add(potencia);
        _blockLoudness.Add(l);

        // Solape del 75 %: se conserva la cola del bloque para el siguiente.
        var resto = _blockSize - _hopSize;
        for (int c = 0; c < _channels; c++)
            Array.Copy(_acc[c], _hopSize, _acc[c], 0, resto);
        _accCount = resto;
    }

    /// <summary>Sonoridad integrada y pico, aplicando la doble puerta del estándar.</summary>
    public LoudnessResult GetResult()
    {
        var segundos = _samples / (double)_sampleRate;
        if (_blockLoudness.Count == 0)
            return new LoudnessResult(double.NegativeInfinity, PicoDb(), segundos);

        // Puerta absoluta: fuera lo que esté por debajo de -70 LUFS (silencios).
        var idx = new List<int>();
        for (int i = 0; i < _blockLoudness.Count; i++)
            if (_blockLoudness[i] > AbsoluteGate) idx.Add(i);
        if (idx.Count == 0) return new LoudnessResult(double.NegativeInfinity, PicoDb(), segundos);

        // Puerta relativa: 10 LU por debajo de la media de lo anterior (fuera los pasajes flojos).
        var umbral = Offset + 10.0 * Math.Log10(MediaPonderada(idx)) + RelativeGate;
        var idx2 = new List<int>();
        foreach (var i in idx)
            if (_blockLoudness[i] > umbral) idx2.Add(i);
        if (idx2.Count == 0) idx2 = idx;

        var lufs = Offset + 10.0 * Math.Log10(MediaPonderada(idx2));
        return new LoudnessResult(lufs, PicoDb(), segundos);
    }

    private double MediaPonderada(List<int> indices)
    {
        double total = 0;
        for (int c = 0; c < _channels; c++)
        {
            double suma = 0;
            foreach (var i in indices) suma += _blockPower[i][c];
            total += _weights[c] * (suma / indices.Count);
        }
        return total;
    }

    private double PicoDb() => _peak > 0 ? 20.0 * Math.Log10(_peak) : double.NegativeInfinity;

    /// <summary>Filtro biquad de segundo orden (forma directa II transpuesta).</summary>
    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _z1, _z2;

        private Biquad(double b0, double b1, double b2, double a1, double a2)
            => (_b0, _b1, _b2, _a1, _a2) = (b0, b1, b2, a1, a2);

        public double Process(double x)
        {
            var y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }

        /// <summary>
        /// Etapa 1 del filtro K: realce de agudos (+4 dB). Los coeficientes de BS.1770 están dados
        /// para 48 kHz; aquí se recalculan para la frecuencia real del archivo, que es lo correcto
        /// cuando hay material a 44,1 kHz (la mayoría de los MP3).
        /// </summary>
        public static Biquad KWeightingShelf(int fs)
        {
            const double f0 = 1681.974450955533;
            const double G = 3.999843853973347;
            const double Q = 0.7071752369554196;

            var K = Math.Tan(Math.PI * f0 / fs);
            var Vh = Math.Pow(10.0, G / 20.0);
            var Vb = Math.Pow(Vh, 0.4996667741545416);
            var a0 = 1.0 + K / Q + K * K;

            return new Biquad(
                (Vh + Vb * K / Q + K * K) / a0,
                2.0 * (K * K - Vh) / a0,
                (Vh - Vb * K / Q + K * K) / a0,
                2.0 * (K * K - 1.0) / a0,
                (1.0 - K / Q + K * K) / a0);
        }

        /// <summary>Etapa 2 del filtro K: paso alto RLB (quita los graves que no aportan sonoridad).</summary>
        public static Biquad KWeightingHighPass(int fs)
        {
            const double f0 = 38.13547087602444;
            const double Q = 0.5003270373238773;

            var K = Math.Tan(Math.PI * f0 / fs);
            var den = 1.0 + K / Q + K * K;

            return new Biquad(1.0, -2.0, 1.0,
                2.0 * (K * K - 1.0) / den,
                (1.0 - K / Q + K * K) / den);
        }
    }
}
