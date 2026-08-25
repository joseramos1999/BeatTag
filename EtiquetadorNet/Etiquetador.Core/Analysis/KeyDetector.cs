namespace Etiquetador.Core.Analysis;

/// <summary>Tonalidad detectada, con su notación musical y su código Camelot.</summary>
/// <param name="PitchClass">0 = Do, 1 = Do#, … 11 = Si.</param>
/// <param name="IsMinor">true si es menor.</param>
/// <param name="Confidence">
/// Cuánto destaca sobre la segunda candidata, de 0 a 1. Por debajo de 0,1 la elección es dudosa:
/// suele pasar con percusión sola o con temas que cambian de tono.
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
/// Deduce la tonalidad de una grabación a partir del audio.
///
/// El método es el clásico: se reparte la energía de cada trozo en las doce notas de la escala
/// (un "cromagrama", que ignora la octava y se queda con la nota), se promedia todo el tema y se
/// compara ese perfil con los de Krumhansl-Schmuckler, obtenidos midiendo cuánto pesa cada nota en
/// una tonalidad. La que más se parece, gana.
///
/// Trabaja sobre muestras ya descodificadas, sin saber de archivos ni de códecs, para poder
/// probarse con tonos generados de tonalidad conocida.
/// </summary>
public static class KeyDetector
{
    /// <summary>Muestras por ventana de análisis. A 44,1 kHz son unos 0,19 s y ~5,4 Hz de resolución.</summary>
    public const int TamanoVentana = 8192;

    /// <summary>Rango que se mira, en hercios: de Do2 a Si6.</summary>
    private const double FrecuenciaMinima = 65.0;
    private const double FrecuenciaMaxima = 2100.0;

    // Perfiles de Krumhansl-Schmuckler: peso de cada grado dentro de la tonalidad.
    private static readonly double[] PerfilMayor =
        { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
    private static readonly double[] PerfilMenor =
        { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };

    /// <summary>
    /// Reparto de energía por nota (12 valores, 0 = Do), normalizado para que sume 1. Devuelve null
    /// si no hay señal suficiente.
    /// </summary>
    public static double[]? Chromagram(float[] muestras, int sampleRate)
    {
        if (muestras.Length < TamanoVentana || sampleRate <= 0) return null;

        var croma = new double[12];
        var ventana = Hann(TamanoVentana);
        var salto = TamanoVentana / 2;          // 50 % de solape: no se pierde nada entre ventanas
        var re = new double[TamanoVentana];
        var im = new double[TamanoVentana];
        var hubo = false;

        // La intro y la salida de un tema de DJ suelen ser percusión sola, que no dice nada de la
        // tonalidad y sí ensucia el promedio. Se analiza el tramo central.
        var desde = muestras.Length / 8;
        var hasta = muestras.Length - muestras.Length / 8;
        if (hasta - desde < TamanoVentana * 4) { desde = 0; hasta = muestras.Length; }

        for (var inicio = desde; inicio + TamanoVentana <= hasta; inicio += salto)
        {
            for (var i = 0; i < TamanoVentana; i++) { re[i] = muestras[inicio + i] * ventana[i]; im[i] = 0.0; }
            var mag = Fft.Magnitudes(re, im);

            for (var bin = 2; bin < mag.Length - 1; bin++)
            {
                // Solo PICOS del espectro. Un bombo o una caja reparten energía por todas las
                // frecuencias, y sumando bin a bin esa banda ancha se lleva por delante el análisis;
                // las notas, en cambio, aparecen como máximos locales bien definidos.
                if (mag[bin] <= mag[bin - 1] || mag[bin] <= mag[bin + 1]) continue;

                var f = bin * (double)sampleRate / TamanoVentana;
                if (f < FrecuenciaMinima || f > FrecuenciaMaxima) continue;

                // Compresión logarítmica: sin ella un bajo potente pesa más que toda la armonía.
                var peso = Math.Log(1.0 + mag[bin]);
                if (peso <= 0) continue;

                // Un pico puede ser el armónico de una nota más grave. Se reparte también a los
                // fundamentales de los que podría venir (f/2, f/3, f/4), con peso decreciente: eso
                // refuerza la nota real en lugar de sus armónicos.
                for (var h = 1; h <= 4; h++)
                {
                    var fh = f / h;
                    if (fh < FrecuenciaMinima) break;
                    var midi = 69.0 + 12.0 * Math.Log2(fh / 440.0);
                    var clase = ((int)Math.Round(midi) % 12 + 12) % 12;
                    croma[clase] += peso / h;
                    hubo = true;
                }
            }
        }
        if (!hubo) return null;

        var suma = croma.Sum();
        if (suma <= 0) return null;
        for (var i = 0; i < 12; i++) croma[i] /= suma;
        return croma;
    }

    /// <summary>Deduce la tonalidad. Devuelve null si el audio no da para decidir.</summary>
    public static MusicalKey? Detect(float[] muestras, int sampleRate)
    {
        var croma = Chromagram(muestras, sampleRate);
        return croma == null ? null : FromChromagram(croma);
    }

    /// <summary>Elige la tonalidad que mejor encaja con un cromagrama ya calculado.</summary>
    public static MusicalKey? FromChromagram(double[] croma)
    {
        if (croma.Length != 12) return null;

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

    private static double[] Hann(int n)
    {
        var w = new double[n];
        for (var i = 0; i < n; i++) w[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
        return w;
    }
}
