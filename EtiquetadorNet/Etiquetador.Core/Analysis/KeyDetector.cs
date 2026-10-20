namespace Etiquetador.Core.Analysis;

/// <summary>Tonalidad detectada, con su notación musical y su código Camelot.</summary>
/// <param name="PitchClass">0 = Do, 1 = Do#, … 11 = Si.</param>
/// <param name="IsMinor">true si es menor.</param>
/// <param name="Confidence">
/// Cuánto destaca sobre la segunda candidata, de 0 a 1 (1 para la leída de una etiqueta). Por debajo
/// de <see cref="KeyDetector.MargenConcluyente"/> la elección es dudosa: suele pasar con percusión
/// sola o con temas que cambian de tono.
/// </param>
public sealed record MusicalKey(int PitchClass, bool IsMinor, double Confidence)
{
    private static readonly string[] Nombres = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    /// <summary>Notación estándar: "Am", "F#", "C".</summary>
    public string Name => Nombres[PitchClass] + (IsMinor ? "m" : "");

    /// <summary>
    /// Código Camelot ("8A", "5B"), que es el que se usa para mezclar en armónico: dos temas
    /// encajan si comparten número, o si están a un paso en la rueda.
    /// </summary>
    public string Camelot
    {
        get
        {
            // La rueda avanza por quintas. El relativo mayor y menor comparten número (Do mayor 8B
            // y La menor 8A), que es justo lo que la hace útil.
            var numero = IsMinor
                ? MenorACamelot[PitchClass]
                : MayorACamelot[PitchClass];
            return numero + (IsMinor ? "A" : "B");
        }
    }

    // Índice = clase de altura (0 = Do). Valores comprobados contra la rueda Camelot estándar.
    private static readonly int[] MayorACamelot = { 8, 3, 10, 5, 12, 7, 2, 9, 4, 11, 6, 1 };
    private static readonly int[] MenorACamelot = { 5, 12, 7, 2, 9, 4, 11, 6, 1, 8, 3, 10 };


    private static readonly Dictionary<string, int> Notas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["C"] = 0, ["B#"] = 0,
        ["C#"] = 1, ["Db"] = 1,
        ["D"] = 2,
        ["D#"] = 3, ["Eb"] = 3,
        ["E"] = 4, ["Fb"] = 4,
        ["F"] = 5, ["E#"] = 5,
        ["F#"] = 6, ["Gb"] = 6,
        ["G"] = 7,
        ["G#"] = 8, ["Ab"] = 8,
        ["A"] = 9,
        ["A#"] = 10, ["Bb"] = 10,
        ["B"] = 11, ["Cb"] = 11,
    };

    /// <summary>
    /// Interpreta la tonalidad escrita en un tag. Acepta lo que ponen los programas de DJ: notación
    /// musical ("Am", "F#m", "Dbm", "A min"), y también el propio código Camelot ("8A", "12B"), que
    /// algunos escriben directamente.
    ///
    /// Devuelve null si el texto no se reconoce, en vez de inventarse una tonalidad: dar por buena
    /// una clave equivocada es peor que no dar ninguna, porque se mezcla en armónico fiándose de ella.
    /// </summary>
    public static MusicalKey? Parse(string? texto)
    {
        var s = (texto ?? "").Trim();
        if (s.Length == 0) return null;

        // Camelot directo: número del 1 al 12 seguido de A (menor) o B (mayor).
        var cam = System.Text.RegularExpressions.Regex.Match(s, @"^\s*(\d{1,2})\s*([AB])\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (cam.Success)
        {
            var num = int.Parse(cam.Groups[1].Value);
            if (num is < 1 or > 12) return null;
            var menorCam = cam.Groups[2].Value.Equals("A", StringComparison.OrdinalIgnoreCase);
            var tabla = menorCam ? MenorACamelot : MayorACamelot;
            var pc = Array.IndexOf(tabla, num);
            return pc < 0 ? null : new MusicalKey(pc, menorCam, 1.0);
        }

        // Notación musical. El modo puede venir como "m", "min", "minor" o "-"; si no hay nada, mayor.
        var m = System.Text.RegularExpressions.Regex.Match(s,
            @"^\s*([A-Ga-g])\s*([#b♯♭]?)\s*(m|min|minor|-)?\s*(maj|major)?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        var nota = m.Groups[1].Value.ToUpperInvariant()
                 + m.Groups[2].Value.Replace('♯', '#').Replace('♭', 'b');
        if (!Notas.TryGetValue(nota, out var clase)) return null;

        var menor = m.Groups[3].Success;
        return new MusicalKey(clase, menor, 1.0);
    }
    public override string ToString() => $"{Name} ({Camelot})";
}

/// <summary>
/// Deduce la tonalidad de una grabación a partir del audio. NO SE OFRECE EN LA INTERFAZ: acierta
/// demasiado poco (cifras abajo), y una tonalidad equivocada es peor que ninguna, porque se mezcla en
/// armónico fiándose de ella.
///
/// El método: el audio se pasa a mono y se baja a unos 11 kHz (por encima de 4 kHz no hay nada que
/// sirva para la tonalidad), se reparte la energía de cada trozo en tercios de semitono, se separa
/// la parte ARMÓNICA de la percusiva (lo que se mantiene en el tiempo frente a lo que se reparte por
/// todas las frecuencias, como un bombo o una caja) y se suma, con el mismo peso para cada trozo, en
/// las doce notas de Do2 a Si6 (un «cromagrama»). Ese reparto se compara con el perfil de cada una de
/// las 24 tonalidades; la que más se parece, gana. Los perfiles son los de Sha'ath (KeyFinder), que
/// salieron de música actual y no de la clásica como los de Krumhansl.
///
/// Trabaja sobre muestras ya decodificadas, sin saber de archivos ni de códecs, para poder probarse
/// con tonos generados de tonalidad conocida; y por bloques (<see cref="AcumuladorTonalidad"/>), sin
/// tener la canción entera en memoria.
///
/// MEDIDO (A15, octubre de 2026) contra las tonalidades que ya traen etiquetadas las canciones de una
/// biblioteca real (las ponen Mixed In Key, rekordbox y similares; notaciones normalizadas con
/// <see cref="MusicalKey.Parse"/>), en tres muestras al azar DISJUNTAS: «desarrollo» (400 canciones)
/// para elegir el método y el umbral, y «prueba» (400) y «validación» (240) para medirlo. Tres
/// medidas: exacta; exacta o relativa (mismo número Camelot con la otra letra, 8A/8B); exacta o vecina
/// en la rueda (relativa o un paso con la misma letra).
///   · El método anterior (solo los picos del espectro, reparto a los subarmónicos y perfiles de
///     Krumhansl): 26,5 % exacta en desarrollo y 30,7 % en prueba.
///   · Este, en prueba y validación (640): 57,3 % exacta · 68,0 % exacta o relativa · 82,2 % exacta
///     o vecina. En desarrollo, 58,5 %.
///   · Esta implementación, por bloques: da lo mismo que el prototipo con el que se midió (40 de 40
///     canciones con la misma tonalidad y el mismo margen) y, sobre una cuarta muestra nueva y
///     disjunta de las otras tres (300 canciones), 60,7 % exacta · 70,0 % exacta o relativa · 83,3 %
///     exacta o vecina.
///   · Probado sin mejora (en desarrollo, frente a 58,5 %): perfiles de Krumhansl, Temperley (dos
///     versiones), Aarden-Essen y Bellman-Budge; potencia, logaritmo o solo picos en vez de la
///     magnitud; otros tramos de octavas; dar más peso a los graves (baja hasta el 36-50 %); corregir la
///     afinación de cada canción; quedarse con el tramo central. Ni siquiera con perfiles sacados de la
///     propia biblioteca, que no valdrían para otra, se pasa del 62 % en prueba.
///   · Si solo se diera cuando la ganadora destaca (<see cref="MargenConcluyente"/>), se respondería en
///     el 29 % de las canciones con un 81 % exacta (82,6 % en prueba y validación, 184 de 640; 78,2 %
///     en la muestra nueva, 87 de 300; en prueba y validación, 75 % en las de tonalidad mayor y 84 % en
///     las de menor, que son el 83 % de la biblioteca medida). Queda anotado para el día que
///     se quiera ofrecer como propuesta para las canciones sin tonalidad: en la biblioteca medida solo
///     39 de 13.590 no la traen, porque los programas de DJ la ponen al analizar, y con un detector
///     mejor que este.
///   · Coste: 1,0 s por canción (mediana; 1,6 s el percentil 90), decodificándola entera, con 2 hilos.
/// Como referencia, por debajo de un 70-75 % exacto una tonalidad propuesta no aporta frente a la que
/// ya ponen esos programas: con un 58-61 % exacto en conjunto, no se ofrece.
/// </summary>
public static class KeyDetector
{
    /// <summary>
    /// Margen sobre la segunda tonalidad a partir del cual la elección es fiable. Elegido en la muestra
    /// de desarrollo (75 % exacta con él, en el 31 % de las canciones) y comprobado en las otras
    /// (82,6 % y 78,2 %, ver arriba). Es una diferencia de correlaciones, así que no depende del
    /// volumen ni de la duración.
    /// </summary>
    public const double MargenConcluyente = 0.15;

    /// <summary>Notas que entran en el cromagrama: de Do2 (MIDI 36, 65 Hz) a Si6 (MIDI 95, 1.976 Hz).</summary>
    internal const int MidiDesde = 36, MidiHasta = 96;

    // Perfiles de Sha'ath (KeyFinder, 2011): peso de cada grado dentro de la tonalidad, de la tónica en adelante.
    private static readonly double[] PerfilMayor =
    {
        7.23900502618145, 3.50351166725158, 3.58445177536649, 2.84511816478676, 5.81898892118605, 4.55865057415321,
        2.44778850545506, 6.99473192910742, 3.39106613588571, 4.55614256655736, 4.07392666663523, 4.45932757378887,
    };
    private static readonly double[] PerfilMenor =
    {
        7.00255045060285, 3.14360019416386, 4.35904129136004, 5.40418900513990, 3.67234372652245, 4.08971531294114,
        3.90791896890401, 6.20018962082221, 3.63229253045848, 2.87241513248305, 5.35610707193532, 3.83397687456968,
    };

    /// <summary>
    /// Reparto de energía por nota (12 valores, 0 = Do) de unas muestras en mono, normalizado para que
    /// sume 1. Devuelve null si no hay señal suficiente.
    /// </summary>
    public static double[]? Chromagram(float[] muestras, int sampleRate)
    {
        if (sampleRate <= 0 || muestras.Length == 0) return null;
        var a = new AcumuladorTonalidad(sampleRate, 1);
        a.Add(muestras, muestras.Length);
        return a.Croma();
    }

    /// <summary>Deduce la tonalidad de unas muestras en mono. Devuelve null si el audio no da para decidir.</summary>
    public static MusicalKey? Detect(float[] muestras, int sampleRate)
    {
        var croma = Chromagram(muestras, sampleRate);
        return croma == null ? null : FromChromagram(croma);
    }

    /// <summary>
    /// Elige la tonalidad que mejor encaja con un cromagrama ya calculado. <see cref="MusicalKey.Confidence"/>
    /// es el margen sobre la segunda (ver <see cref="MargenConcluyente"/>).
    /// </summary>
    public static MusicalKey? FromChromagram(double[] croma)
    {
        if (croma.Length != 12 || croma.Sum() <= 0) return null;

        double mejor = double.NegativeInfinity, segunda = double.NegativeInfinity;
        var mejorClase = 0;
        var mejorMenor = false;

        for (var tonica = 0; tonica < 12; tonica++)
        {
            foreach (var menor in new[] { false, true })
            {
                var perfil = menor ? PerfilMenor : PerfilMayor;
                var r = Correlacion(croma, perfil, tonica);
                if (r > mejor) { segunda = mejor; mejor = r; mejorClase = tonica; mejorMenor = menor; }
                else if (r > segunda) segunda = r;
            }
        }

        if (double.IsNegativeInfinity(mejor)) return null;

        // Confianza = cuánto le saca a la siguiente. Dos tonalidades que puntúan casi igual
        // significan que el tema no tiene un centro tonal claro, y conviene que se note.
        var conf = double.IsNegativeInfinity(segunda) ? 1.0 : Math.Clamp(mejor - segunda, 0.0, 1.0);
        return new MusicalKey(mejorClase, mejorMenor, Math.Round(conf, 3));
    }

    /// <summary>¿La tonalidad destaca lo bastante sobre la segunda como para fiarse? (<see cref="MargenConcluyente"/>).</summary>
    public static bool EsConcluyente(MusicalKey k) => k.Confidence >= MargenConcluyente;

    /// <summary>Correlación de Pearson entre el cromagrama y el perfil girado a esa tónica.</summary>
    private static double Correlacion(double[] croma, double[] perfil, int tonica)
    {
        double mediaC = 0, mediaP = 0;
        for (var i = 0; i < 12; i++) { mediaC += croma[i]; mediaP += perfil[i]; }
        mediaC /= 12; mediaP /= 12;

        double num = 0, denC = 0, denP = 0;
        for (var i = 0; i < 12; i++)
        {
            var c = croma[i] - mediaC;
            var p = perfil[(i - tonica + 12) % 12] - mediaP;
            num += c * p;
            denC += c * c;
            denP += p * p;
        }
        var den = Math.Sqrt(denC * denP);
        return den <= 0 ? 0 : num / den;
    }
}

/// <summary>
/// El cálculo de <see cref="KeyDetector"/> por bloques: recibe las muestras tal como salen del
/// decodificador (intercaladas por canal) y no necesita la canción entera en memoria. Lo que guarda
/// es el espectro de cada trozo en tercios de semitono (252 valores cada 0,19 s; 2 MB para 6 minutos).
/// </summary>
public sealed class AcumuladorTonalidad
{
    /// <summary>A esto se baja el audio: por encima de 4 kHz no queda nada que diga la tonalidad.</summary>
    public const double FrecuenciaAnalisis = 11025.0;

    /// <summary>Muestras por ventana (ya diezmadas): a 11 kHz, 0,74 s y 1,35 Hz por bin, de sobra para separar semitonos en los graves.</summary>
    public const int TamanoVentana = 8192;

    /// <summary>Salto entre ventanas: un cuarto de ventana, 0,19 s.</summary>
    public const int Salto = 2048;

    /// <summary>
    /// Como mucho tantas ventanas (unos 37 minutos a 11 kHz): una sesión de dos horas no necesita más
    /// para saber su tonalidad, y así la memoria tiene techo.
    /// </summary>
    public const int MaxTramas = 12000;

    internal const int Subdivisiones = 3;      // tercios de semitono
    internal const int MidiMinimo = 24;        // Do1, 32,7 Hz
    internal const int Bandas = 84 * Subdivisiones;   // siete octavas, hasta Si7
    private const double FrecuenciaMinima = 30.0, FrecuenciaMaxima = 4000.0;
    private const int Coeficientes = 127;      // del filtro de paso bajo antes de diezmar
    private const int Mitad = Coeficientes / 2;

    // Separación armónico/percusivo: lo armónico es la mediana en el tiempo (9 ventanas, 1,7 s) y lo
    // percusivo, la mediana en frecuencia (15 tercios, 5 semitonos). Se queda lo que es más lo primero.
    private const int RadioTiempo = 4, RadioFrecuencia = 7;

    private readonly int _canales;
    private readonly int _q;                   // factor de diezmado
    private readonly double[] _fir;
    private readonly int[] _mapa;              // bin de la FFT -> tercio de semitono (-1 fuera de rango)
    private static readonly double[] Hann = CrearHann(TamanoVentana);

    // Mono sin diezmar pendiente de filtrar: _mono[0] es la muestra número _inicio.
    private float[] _mono = new float[1 << 15];
    private int _lenMono;
    private long _inicio;
    private long _total;                       // muestras mono recibidas
    private long _siguiente;                   // próxima muestra diezmada por calcular

    private readonly float[] _ventana = new float[TamanoVentana];
    private int _llenos;
    private readonly double[] _re = new double[TamanoVentana];
    private readonly double[] _im = new double[TamanoVentana];
    private readonly List<float[]> _tramas = new();
    private bool _cerrado;

    public AcumuladorTonalidad(int sampleRate, int canales)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (canales <= 0) throw new ArgumentOutOfRangeException(nameof(canales));
        _canales = canales;
        _q = Math.Max(1, (int)Math.Round(sampleRate / FrecuenciaAnalisis));
        _fir = CrearFiltro(_q);
        var srDiezmada = sampleRate / _q;
        _mapa = new int[TamanoVentana / 2];
        for (var k = 0; k < _mapa.Length; k++)
        {
            var f = k * (double)srDiezmada / TamanoVentana;
            _mapa[k] = -1;
            if (f < FrecuenciaMinima || f > FrecuenciaMaxima) continue;
            var midi = 69 + 12 * Math.Log2(f / 440.0);
            var idx = (int)Math.Round((midi - MidiMinimo) * Subdivisiones);
            if (idx >= 0 && idx < Bandas) _mapa[k] = idx;
        }
    }

    /// <summary>Ventanas analizadas hasta ahora.</summary>
    public int Tramas => _tramas.Count;

    /// <summary>Añade <paramref name="n"/> muestras intercaladas (n = tramas × canales).</summary>
    public void Add(float[] intercaladas, int n)
    {
        if (_cerrado) throw new InvalidOperationException("El análisis ya está cerrado.");
        var tramas = Math.Min(n, intercaladas.Length) / _canales;
        for (var t = 0; t < tramas && _tramas.Count < MaxTramas; t++)
        {
            var b = t * _canales;
            float s = 0;
            for (var c = 0; c < _canales; c++) s += intercaladas[b + c];
            AnadirMono(s / _canales);
        }
    }

    private void AnadirMono(float x)
    {
        if (_q == 1) { AnadirDiezmada(x); _total++; return; }
        if (_lenMono == _mono.Length) Compactar();
        _mono[_lenMono++] = x;
        _total++;
        // Cada muestra diezmada necesita las 63 siguientes a su centro.
        while (_siguiente * _q + Mitad < _total) Filtrar();
    }

    /// <summary>Calcula la siguiente muestra diezmada. Lo que cae fuera de lo recibido cuenta como silencio.</summary>
    private void Filtrar()
    {
        var c = _siguiente * _q;
        double acc = 0;
        for (var i = 0; i < Coeficientes; i++)
        {
            var g = c + i - Mitad;
            if (g < 0 || g >= _total) continue;
            acc += _fir[i] * _mono[g - _inicio];
        }
        _siguiente++;
        AnadirDiezmada((float)acc);
    }

    /// <summary>Descarta lo que ya no hace falta para filtrar (lo anterior a la próxima ventana del filtro).</summary>
    private void Compactar()
    {
        var desde = Math.Max(_inicio, _siguiente * _q - Mitad);
        var quitar = (int)(desde - _inicio);
        if (quitar <= 0)
        {
            Array.Resize(ref _mono, _mono.Length * 2);
            return;
        }
        Array.Copy(_mono, quitar, _mono, 0, _lenMono - quitar);
        _lenMono -= quitar;
        _inicio = desde;
    }

    private void AnadirDiezmada(float y)
    {
        if (_tramas.Count >= MaxTramas) return;
        _ventana[_llenos++] = y;
        if (_llenos < TamanoVentana) return;
        AnalizarVentana();
        Array.Copy(_ventana, Salto, _ventana, 0, TamanoVentana - Salto);
        _llenos = TamanoVentana - Salto;
    }

    private void AnalizarVentana()
    {
        for (var i = 0; i < TamanoVentana; i++) { _re[i] = _ventana[i] * Hann[i]; _im[i] = 0.0; }
        Fft.Forward(_re, _im);
        var fila = new float[Bandas];
        for (var k = 1; k < TamanoVentana / 2 - 1; k++)
        {
            var b = _mapa[k];
            if (b < 0) continue;
            fila[b] += (float)Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
        }
        _tramas.Add(fila);
    }

    /// <summary>
    /// El cromagrama (12 valores, 0 = Do, suman 1) de todo lo recibido, o null si no hay señal
    /// suficiente. Cierra el análisis: después ya no se puede añadir más.
    /// </summary>
    public double[]? Croma()
    {
        if (!_cerrado)
        {
            _cerrado = true;
            // Las últimas muestras diezmadas, con silencio después del final.
            if (_q > 1)
                while (_siguiente < _total / _q && _tramas.Count < MaxTramas) Filtrar();
        }
        if (_tramas.Count == 0) return null;

        var armonico = ParteArmonica(_tramas);
        var croma = new double[12];
        for (var b = 0; b < Bandas; b++)
        {
            var midi = (int)Math.Round(MidiMinimo + b / (double)Subdivisiones);
            if (midi < KeyDetector.MidiDesde || midi >= KeyDetector.MidiHasta) continue;
            croma[midi % 12] += armonico[b];
        }
        var suma = croma.Sum();
        if (suma <= 0) return null;
        for (var i = 0; i < 12; i++) croma[i] /= suma;
        return croma;
    }

    /// <summary>La tonalidad de todo lo recibido (<see cref="KeyDetector.FromChromagram"/>), o null.</summary>
    public MusicalKey? Tonalidad() => Croma() is { } c ? KeyDetector.FromChromagram(c) : null;

    /// <summary>
    /// Suma, ventana a ventana, lo que es más armónico que percusivo, con el mismo peso para cada
    /// ventana: una intro de percusión fuerte no tapa la armonía del resto.
    /// </summary>
    internal static double[] ParteArmonica(IReadOnlyList<float[]> tramas)
    {
        var res = new double[Bandas];
        var h = new double[Bandas];
        var col = new float[2 * RadioTiempo + 1];
        var fil = new float[2 * RadioFrecuencia + 1];
        for (var t = 0; t < tramas.Count; t++)
        {
            var fr = tramas[t];
            double tot = 0;
            Array.Clear(h);
            for (var b = 0; b < Bandas; b++)
            {
                var c = 0;
                for (var u = t - RadioTiempo; u <= t + RadioTiempo; u++) col[c++] = tramas[Math.Clamp(u, 0, tramas.Count - 1)][b];
                Array.Sort(col);
                var armonico = col[RadioTiempo];
                c = 0;
                for (var v = b - RadioFrecuencia; v <= b + RadioFrecuencia; v++) fil[c++] = fr[Math.Clamp(v, 0, Bandas - 1)];
                Array.Sort(fil);
                var percusivo = fil[RadioFrecuencia];
                if (armonico >= percusivo) { h[b] = fr[b]; tot += fr[b]; }
            }
            if (tot > 1e-6) for (var b = 0; b < Bandas; b++) res[b] += h[b] / tot;
        }
        return res;
    }

    /// <summary>Paso bajo (sinc con ventana de Blackman) para diezmar por <paramref name="q"/> sin que los agudos se cuelen en los graves.</summary>
    private static double[] CrearFiltro(int q)
    {
        var h = new double[Coeficientes];
        if (q == 1) { h[Mitad] = 1; return h; }
        var fc = 0.45 / q;
        double suma = 0;
        for (var i = 0; i < Coeficientes; i++)
        {
            var m = i - Mitad;
            var sinc = m == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * m) / (Math.PI * m);
            var w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (Coeficientes - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (Coeficientes - 1));
            h[i] = sinc * w;
            suma += h[i];
        }
        for (var i = 0; i < Coeficientes; i++) h[i] /= suma;
        return h;
    }

    private static double[] CrearHann(int n)
    {
        var w = new double[n];
        for (var i = 0; i < n; i++) w[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
        return w;
    }
}
