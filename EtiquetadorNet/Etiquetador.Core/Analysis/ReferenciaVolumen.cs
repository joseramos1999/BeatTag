namespace Etiquetador.Core.Analysis;

/// <summary>Una grabación tal como entra en el cálculo de la referencia automática.</summary>
/// <param name="Lufs">Nivel actual.</param>
/// <param name="PicoDb">Pico actual con el que se decide (el real si se conoce).</param>
/// <param name="Ajustable">Es un MP3 que se puede subir o bajar sin recodificar.</param>
/// <param name="Aplicada">Ganancia que BeatTag le ha aplicado desde que se midió (dB; 0 si ninguna).</param>
public readonly record struct FilaReferencia(double Lufs, double PicoDb, bool Ajustable, double Aplicada = 0);

/// <summary>Por qué la referencia automática ha quedado donde está (para explicarlo en la pestaña).</summary>
public enum MotivoReferencia
{
    /// <summary>No hay nada medido.</summary>
    SinMedidas,

    /// <summary>Casi todas alcanzan el nivel medio: la referencia es el nivel medio.</summary>
    Mediana,

    /// <summary>Más del 5 % no alcanza el nivel medio: la referencia baja hasta donde llega el 95 %.</summary>
    Alcance,

    /// <summary>Habría que bajar más de <see cref="ReferenciaVolumen.DescensoMaximo"/>: se queda en ese límite.</summary>
    Tope,

    /// <summary>Hay menos de <see cref="ReferenciaVolumen.MinimoGrabaciones"/>: no se baja del nivel medio.</summary>
    PocasGrabaciones,

    /// <summary>La mayoría no se puede ajustar (no son MP3): bajar no igualaría nada; se queda en el nivel medio.</summary>
    PocasAjustables,
}

/// <summary>Referencia automática, nivel medio y por qué. Los dos niveles en LUFS, redondeados a 0,1.</summary>
public readonly record struct ResultadoReferencia(double Referencia, double Mediana, MotivoReferencia Motivo)
{
    /// <summary>Para quien solo necesita los dos niveles.</summary>
    public void Deconstruct(out double referencia, out double mediana)
    {
        referencia = Referencia;
        mediana = Mediana;
    }
}

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
/// mediana y sin bajar más de <see cref="DescensoMaximo"/> por debajo de ella. Se calcula en cada
/// biblioteca, con un percentil convencional: nada está calibrado contra una colección concreta.
///
/// CON EL NIVEL ORIGINAL. Todo (mediana, alcance y tope) se calcula con el nivel y el pico que tenía
/// cada grabación ANTES de los ajustes de BeatTag (el actual menos la ganancia aplicada). Si se
/// calculara con el nivel actual, cada «Ajustar» bajaría la mediana, el tope de 3 dB bajaría con ella
/// y el siguiente «Ajustar» volvería a bajar las mismas canciones. MEDIDO con una biblioteca del 80 %
/// a −9 LUFS y el 20 % a −17 sin margen: la referencia pasaba de −12,2 a −15,3 y a −16,8 en tres
/// pasadas, y el grupo alto acababa 7,5 dB más bajo. Con el nivel original la referencia es la misma
/// en cada pasada, así que un segundo «Ajustar» no vuelve a tocar nada, y «nunca más de 3 dB por
/// debajo del nivel medio» se cumple de verdad, por muchas veces que se ajuste.
///
/// DOS SALVAGUARDAS, que dejan la referencia en la mediana:
///   · Menos de <see cref="MinimoGrabaciones"/> grabaciones: el 5 % no llega a una, y el «percentil 5»
///     sería la más baja. Una sola acapella o intro bajaría la referencia de toda la carpeta.
///   · Menos de la mitad son ajustables: bajar la referencia solo sirve si lo que queda por encima se
///     puede bajar. En una biblioteca de AIFF o FLAC no se puede, y bajarla solo marcaría como «Alto»
///     lo que está en su nivel.
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

    /// <summary>
    /// Grabaciones necesarias para que el percentil 5 deje fuera al menos a una: 1 / 0,05 = 20. Con
    /// menos, la referencia no baja del nivel medio.
    /// </summary>
    public static readonly int MinimoGrabaciones = (int)Math.Ceiling(1 / PercentilAlcance - 1e-9);

    /// <summary>Referencia cuando no hay nada medido (la de streaming, como hasta ahora).</summary>
    public const double SinMedidas = -14.0;

    /// <summary>
    /// Referencia automática, mediana (las dos en LUFS, redondeadas a 0,1) y motivo. La referencia nunca
    /// pasa de la mediana. Todo se calcula con el nivel original de cada grabación (ver la clase).
    /// </summary>
    /// <param name="filas">Nivel, pico, si es un MP3 ajustable y la ganancia ya aplicada por BeatTag.</param>
    /// <param name="margen">Margen bajo el tope que no se usa al subir (dB).</param>
    /// <param name="paso">Lo que mueve cada paso del ajuste (dB).</param>
    public static ResultadoReferencia Automatica(IReadOnlyList<FilaReferencia> filas,
        double margen = 0.5, double paso = Mp3Gain.DbPerStep)
    {
        var n = filas.Count;
        if (n == 0) return new ResultadoReferencia(SinMedidas, SinMedidas, MotivoReferencia.SinMedidas);

        var niveles = new double[n];
        var alcances = new double[n];
        var ajustables = 0;
        for (int i = 0; i < n; i++)
        {
            var f = filas[i];
            // El nivel y el pico se mueven juntos con la ganancia: restándola salen los de antes de
            // ajustar. Un pico recortado que se bajó se guarda en el tope (0 dBFS): restado, queda por
            // encima de 0 y su alcance es su nivel original, como antes de bajarlo.
            niveles[i] = f.Lufs - f.Aplicada;
            alcances[i] = Alcance(f.Lufs - f.Aplicada, f.PicoDb - f.Aplicada, f.Ajustable, margen, paso);
            if (f.Ajustable) ajustables++;
        }
        Array.Sort(niveles);
        Array.Sort(alcances);

        // La mediana, igual que se calculaba antes: el elemento central, redondeado a 0,1.
        var mediana = Math.Round(niveles[n / 2], 1);
        var p5 = alcances[(int)Math.Floor(PercentilAlcance * n)];
        var deseada = Math.Round(Math.Min(mediana, p5 + paso / 2), 1, MidpointRounding.AwayFromZero);
        if (deseada >= mediana) return new ResultadoReferencia(mediana, mediana, MotivoReferencia.Mediana);

        // Habría que bajar: solo si hay base para el percentil y algo que bajar.
        if (n < MinimoGrabaciones) return new ResultadoReferencia(mediana, mediana, MotivoReferencia.PocasGrabaciones);
        if (ajustables * 2 < n) return new ResultadoReferencia(mediana, mediana, MotivoReferencia.PocasAjustables);

        var tope = Math.Round(mediana - DescensoMaximo, 1);
        return deseada < tope
            ? new ResultadoReferencia(tope, mediana, MotivoReferencia.Tope)
            : new ResultadoReferencia(deseada, mediana, MotivoReferencia.Alcance);
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
