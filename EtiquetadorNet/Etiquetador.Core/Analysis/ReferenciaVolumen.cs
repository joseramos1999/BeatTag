namespace Etiquetador.Core.Analysis;

/// <summary>
/// Nivel de referencia del modo automático de Volumen («Nivel común de la biblioteca»): el nivel al
/// que se igualan todas las grabaciones.
///
/// POR QUÉ NO ES LA MEDIANA. Con pasos de 1,5 dB y sin recodificar, BAJAR siempre se puede, pero
/// SUBIR depende del margen de pico, y las grabaciones bajas casi nunca lo tienen: su sonoridad es
/// baja por estar menos comprimidas, no por tener el pico lejos del tope. MEDIDO en una biblioteca
/// real de 10.173 canciones: el 100 % de los MP3 que estaban más de 2 dB por debajo de la mediana
/// tenía el pico por encima de −2 dBFS, y la correlación entre sonoridad y pico era de 0,11. Con la
/// referencia en la mediana esas canciones no llegan nunca; la única forma de igualarlas sin
/// distorsión es bajar las demás hasta ellas.
///
/// LA REGLA. Cada grabación tiene un «alcance»: el nivel más alto al que puede llegar sin pasar del
/// margen de pico (un MP3, subiendo los pasos que le caben; cualquier otro formato, su propio nivel,
/// porque no se ajusta). La referencia es el nivel más alto que alcanza el 95 % de la biblioteca
/// (percentil 5 del alcance, más medio paso, que es lo que el redondeo deja de error), sin pasar de la
/// mediana y sin bajar más de <see cref="DescensoMaximo"/> por debajo de ella.
///   · Se calcula en cada biblioteca, con un percentil convencional: nada está calibrado contra una
///     colección concreta. Si el 95 % ya alcanza la mediana, la referencia ES la mediana.
///   · Es estable: ajustar no cambia el alcance de nadie (nivel y pico se mueven juntos), así que un
///     segundo «Ajustar» no vuelve a tocar nada. MEDIDO: 0 % retocadas, también tras añadir un 20 %
///     de canciones nuevas; y en validación cruzada (50 particiones) la referencia de una mitad y la
///     de la otra difieren 0,024 dB.
/// </summary>
public static class ReferenciaVolumen
{
    /// <summary>Percentil del alcance que fija la referencia: el 95 % de las grabaciones llega a ella.</summary>
    public const double PercentilAlcance = 0.05;

    /// <summary>
    /// Lo más que puede quedar la referencia por debajo de la mediana: dos pasos. Más allá, bajar el
    /// nivel general es una decisión del DJ (un modo fijo, o excluir lo que debe quedar bajo), no algo
    /// que BeatTag haga por su cuenta. En 25 subbibliotecas reales el descenso máximo fue de 1,4 dB.
    /// </summary>
    public const double DescensoMaximo = 3.0;

    /// <summary>Referencia cuando no hay nada medido (la de streaming, como hasta ahora).</summary>
    public const double SinMedidas = -14.0;

    /// <summary>
    /// Referencia automática y mediana (las dos en LUFS, redondeadas a 0,1). La referencia nunca pasa
    /// de la mediana; la mediana se devuelve para poder explicar la diferencia.
    /// </summary>
    /// <param name="filas">Nivel, pico con el que se decide (el real si se conoce) y si es un MP3 ajustable.</param>
    /// <param name="margen">Margen bajo el tope que no se usa al subir (dB).</param>
    /// <param name="paso">Lo que mueve cada paso del ajuste (dB).</param>
    public static (double Referencia, double Mediana) Automatica(
        IReadOnlyList<(double Lufs, double PicoDb, bool Ajustable)> filas,
        double margen = 0.5, double paso = Mp3Gain.DbPerStep)
    {
        var n = filas.Count;
        if (n == 0) return (SinMedidas, SinMedidas);

        var niveles = new double[n];
        var alcances = new double[n];
        for (int i = 0; i < n; i++)
        {
            var f = filas[i];
            niveles[i] = f.Lufs;
            alcances[i] = Alcance(f.Lufs, f.PicoDb, f.Ajustable, margen, paso);
        }
        Array.Sort(niveles);
        Array.Sort(alcances);

        // La mediana, igual que se calculaba antes: el elemento central, redondeado a 0,1.
        var mediana = Math.Round(niveles[n / 2], 1);
        var p5 = alcances[(int)Math.Floor(PercentilAlcance * n)];
        var referencia = Math.Clamp(Math.Min(mediana, p5 + paso / 2), mediana - DescensoMaximo, mediana);
        return (Math.Round(referencia, 1, MidpointRounding.AwayFromZero), mediana);
    }

    /// <summary>
    /// El nivel más alto al que puede llegar una grabación sin pasar del margen de pico. Lo que no
    /// es un MP3 no se ajusta, así que se queda en su nivel.
    /// </summary>
    public static double Alcance(double lufs, double picoDb, bool ajustable, double margen = 0.5, double paso = Mp3Gain.DbPerStep)
    {
        if (!ajustable) return lufs;
        // Tope de 100 pasos (150 dB) para que un pico absurdo no desborde la conversión a entero.
        var cabe = Math.Min(100.0, (-margen - picoDb) / paso);
        var pasos = cabe > 0 ? (int)Math.Floor(cabe + 1e-9) : 0;
        return lufs + paso * pasos;
    }
}
