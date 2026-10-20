namespace Etiquetador.Core.Analysis;

/// <summary>Qué se pudo medir del espectro de un archivo.</summary>
public enum EstadoCorte
{
    /// <summary>No hay audio suficiente (silencio, archivo cortísimo).</summary>
    SinDatos,

    /// <summary>El audio llega hasta arriba sin un corte brusco.</summary>
    SinCorte,

    /// <summary>Hay un corte brusco y sostenido: por encima de él no queda casi nada.</summary>
    Corte,

    /// <summary>
    /// Hay un escalón, pero lo que suena justo debajo es tan débil que cualquier codificador lo
    /// habría quitado a cualquier tasa: no permite deducir nada.
    /// </summary>
    NoConcluyente,
}

/// <summary>
/// Resultado de medir el espectro. <see cref="CorteHz"/> solo tiene sentido con
/// <see cref="EstadoCorte.Corte"/>; <see cref="TechoHz"/> es hasta dónde se miró (lo limita la
/// frecuencia de muestreo: un archivo a 32 kHz no puede tener nada por encima de 16 kHz).
/// </summary>
public readonly record struct MedidaEspectro(EstadoCorte Estado, double CorteHz, double TechoHz)
{
    /// <summary>
    /// Hasta dónde llega el audio de verdad: el corte si lo hay, y si no, el techo de lo medido.
    /// Null si no se pudo medir o no fue concluyente.
    /// </summary>
    public double? AnchoReal => Estado switch
    {
        EstadoCorte.Corte => CorteHz,
        EstadoCorte.SinCorte => TechoHz,
        _ => null,
    };
}

/// <summary>Lo que el corte medido dice del archivo, comparado con lo que declara.</summary>
public enum ValoracionCalidad
{
    /// <summary>No hay medida, o no fue concluyente.</summary>
    SinValorar,

    /// <summary>El corte cuadra con la tasa o el formato que declara el archivo.</summary>
    Coherente,

    /// <summary>
    /// Zona de duda: el corte es algo bajo para lo que declara, pero hay codificadores legítimos
    /// que cortan ahí. Se enseña, pero no cuenta como sospechoso.
    /// </summary>
    Dudosa,

    /// <summary>El archivo declara mucha más calidad de la que tiene su audio.</summary>
    Sospechosa,
}

/// <summary>
/// Calidad real de un archivo, deducida de su espectro: hasta qué frecuencia llega el audio de
/// verdad. Cada codificador con pérdida tira los agudos por encima de un corte que depende de la
/// tasa. Un «320» hecho a partir de un 128 (convertidores de vídeo, resubidas) conserva el corte del
/// 128, y en un equipo grande suena apagado. Es lo que buscan herramientas para DJ del tipo de
/// Fakin' The Funk.
///
/// Solo da SOSPECHAS: el corte es un hecho medido, pero su causa no. Un máster puede llevar un
/// filtro propio y una grabación antigua puede no tener agudos. Por eso la interfaz enseña siempre
/// la frecuencia medida junto a la estimación, y nada de esto borra ni cambia archivos.
///
/// MEDIDO (octubre de 2026, 1.795 archivos de una biblioteca real y 180 codificaciones controladas;
/// las cifras completas están junto a cada umbral):
///   · Dónde cae el corte medido con este método: los MP3 que declaran 128 kbps, entre 15,6 y 16,6 kHz
///     (LAME corta a 17 kHz nominales y el codificador de Windows a 16); los de 192, hacia 18,6; los
///     de 320, entre 19,6 y 20,3 (LAME: 20,5 nominales). El método sitúa el corte donde la caída ya
///     es clara, unos 0,4 kHz por debajo del valor nominal del filtro del codificador.
///   · 36 WAV sin corte recodificados a 128 con el codificador de Windows y luego a 320 (un «320
///     falso» de origen conocido): 35 de 36 detectados (el que falta tiene unos agudos tan débiles
///     que el resultado es «no concluyente», no un error).
///   · Sin pérdida (45 WAV): ningún falso positivo achacable al método. 3 salen sospechosos, y los 3
///     tienen un escalón de más de 20 dB en 300 Hz a 15,6-16,6 kHz, que no da ningún máster: son
///     acapellas y un remix hechos con material con pérdida. 6 salen dudosos (corte hacia 20 kHz,
///     el de un MP3 de 320). Los 36 restantes, sin corte.
/// </summary>
public static class CorteEspectral
{
    /// <summary>Ancho de las bandas en las que se resume el espectro.</summary>
    public const double AnchoBanda = 100.0;

    /// <summary>
    /// Por encima de esto no se mira. Ningún codificador corta más arriba, y a 48 kHz lo que hay
    /// entre 22 y 24 kHz depende del filtro del conversor, no de la tasa.
    /// </summary>
    public const double TechoAnalisis = 22050.0;

    /// <summary>Duración de cada tramo analizado.</summary>
    public const double SegundosTramo = 10.0;

    /// <summary>
    /// Caída mínima, en dB, entre lo que hay justo debajo del corte y lo MÁS alto que queda por encima,
    /// hasta el final. MEDIDO: con 20 dB, de 45 WAV salen 3 escalones (los tres de origen con pérdida);
    /// con 15, saldrían 6 y uno de ellos (un remix con un sintetizador filtrado a 19 kHz y agudos
    /// a −30 dB por encima) ya sería un falso positivo. Con 25 se perderían el 15 % de los 128 kbps.
    /// </summary>
    public const double CaidaMinimaDb = 20.0;

    /// <summary>
    /// Lo que hay justo debajo del corte no puede estar más de esto por debajo del nivel del medio
    /// (1-8 kHz), o el resultado es «no concluyente». MEDIDO: un WAV con agudos a −45 dB codificado
    /// a 320 por el codificador de Windows sale con un corte a 16 kHz, porque el codificador quita lo
    /// que no se oiría. En los MP3 de 128 reales, lo que hay debajo del corte está casi siempre entre
    /// −35 y −5 dB (287 de 292).
    /// </summary>
    public const double FuerzaMinimaDb = 35.0;

    /// <summary>Por debajo de esto no se buscan cortes: ningún archivo con un mínimo de calidad corta ahí.</summary>
    public const double CorteMinimoHz = 8000.0;

    // Umbrales de la estimación. MEDIDO sobre 1.150 MP3 (reparto del corte medido, en kHz):
    //   declaran 128: 15,6-16,6 el 92 %;   declaran 160-319: picos en 15,6-16,4 (falsos), 18,6 y 19,2;
    //   declaran 320: 19,6-20,3 el 85 %, 18,6-19,4 el 2 %, por debajo de 17,8 el 5 %.
    // Los límites se ponen en los valles entre picos, y valen para cualquier biblioteca porque los
    // fija el codificador, no la música.

    /// <summary>Por debajo, el origen es de 160 kbps o menos (LAME 160: 17,5 kHz nominales).</summary>
    public const double LimiteOrigenBajoHz = 17800.0;

    /// <summary>Entre <see cref="LimiteOrigenBajoHz"/> y esto, un origen de unos 192 kbps.</summary>
    public const double Limite192Hz = 19000.0;

    /// <summary>Entre <see cref="Limite192Hz"/> y esto, de unos 256 kbps; por encima, de 320.</summary>
    public const double Limite320Hz = 19500.0;

    /// <summary>
    /// Un archivo sin pérdida con un corte por debajo de esto probablemente salió de un MP3 de 320. Por
    /// encima, el corte puede ser el filtro del conversor o de un cambio de frecuencia de muestreo.
    /// </summary>
    public const double LimiteSinPerdidaHz = 20800.0;

    /// <summary>
    /// Qué tramos de la canción se analizan: tres de <see cref="SegundosTramo"/> al 25 %, 50 % y 75 %.
    /// Repartidos y no uno solo en el centro porque un tramo puede ser un puente filtrado (un filtro
    /// de paso bajo durante 30 s es un recurso habitual en música de baile) y parecería un corte; al
    /// promediar tres sitios distintos, basta con que uno tenga agudos para que el corte no aparezca.
    /// Una canción corta se analiza entera.
    /// </summary>
    public static IReadOnlyList<(double Inicio, double Segundos)> Tramos(double duracion)
    {
        if (duracion <= 0) return new[] { (0.0, 3600.0) };
        if (duracion < SegundosTramo * 4) return new[] { (0.0, duracion) };
        return new[] { 0.25, 0.50, 0.75 }
            .Select(f => (Math.Max(0, duracion * f - SegundosTramo / 2), SegundosTramo))
            .ToArray();
    }

    /// <summary>
    /// Resume el espectro en bandas de <see cref="AnchoBanda"/> Hz, en decibelios, de 0 Hz a la mitad
    /// de la frecuencia de muestreo (o <see cref="TechoAnalisis"/>, lo que sea menor). La escala de
    /// los bins depende de la frecuencia de muestreo (a 48 kHz cada bin mide 11,7 Hz y a 44,1 kHz
    /// 10,8): se pasa a hercios aquí, una vez, y el resto trabaja ya en hercios.
    /// </summary>
    public static double[] Bandas(double[] potencia, int sampleRate)
    {
        var anchoBin = sampleRate / 2.0 / potencia.Length;
        var techo = Math.Min(sampleRate / 2.0, TechoAnalisis);
        var n = (int)Math.Floor(techo / AnchoBanda);
        var r = new double[n];
        for (var j = 0; j < n; j++)
        {
            var i0 = (int)Math.Ceiling(j * AnchoBanda / anchoBin);
            var i1 = Math.Min(potencia.Length - 1, (int)Math.Floor((j + 1) * AnchoBanda / anchoBin - 1e-9));
            double s = 0; var c = 0;
            for (var i = i0; i <= i1; i++) { s += potencia[i]; c++; }
            r[j] = 10 * Math.Log10(s / Math.Max(1, c) + 1e-20);
        }
        return r;
    }

    /// <summary>Mide el corte a partir del espectro acumulado.</summary>
    public static MedidaEspectro Medir(EspectroMedio espectro)
    {
        // Menos de ~1 s de audio no da para un promedio fiable.
        var pot = espectro.Ventanas >= 20 ? espectro.Potencia() : null;
        return pot == null
            ? new MedidaEspectro(EstadoCorte.SinDatos, 0, 0)
            : Medir(pot, espectro.SampleRate);
    }

    /// <summary>
    /// Busca el corte sostenido: la frecuencia más BAJA a partir de la cual todo lo que queda por
    /// encima, hasta el final, está al menos <see cref="CaidaMinimaDb"/> por debajo de lo que suena
    /// justo debajo.
    ///
    /// Por qué «todo lo que queda por encima» y no solo la banda de al lado: un descenso natural de
    /// los agudos (un máster suave, una grabación antigua) baja poco a poco y nunca cumple eso, porque
    /// la banda siguiente siempre está cerca. El filtro de un codificador, en cambio, es un muro.
    ///
    /// Por qué el más bajo: un «320» hecho de un 128 tiene DOS muros, el del 128 (16-17 kHz) y el del
    /// 320 al recodificar (20,5 kHz), y entre medias un resto de ruido del segundo codificador a
    /// −40 dB. El que dice de dónde viene el audio es el primero.
    /// </summary>
    public static MedidaEspectro Medir(double[] potencia, int sampleRate) => MedirBandas(Bandas(potencia, sampleRate));

    /// <summary>
    /// Lo mismo, a partir del espectro ya resumido en bandas de <see cref="AnchoBanda"/> Hz desde 0 Hz
    /// (<see cref="Bandas"/>). El número de bandas marca hasta dónde se midió.
    /// </summary>
    public static MedidaEspectro MedirBandas(double[] b)
    {
        var n = b.Length;
        var arriba = n - 3;   // las últimas bandas rozan la frecuencia de Nyquist o el techo
        var techo = (arriba + 1) * AnchoBanda;
        if (n < 90) return new MedidaEspectro(EstadoCorte.SinDatos, 0, 0);

        // Nivel del medio (1-8 kHz), la referencia para saber si hay algo que medir.
        var medio = Mediana(b, 10, 80);
        // Por debajo de esto es silencio digital o casi: ~−100 dBFS en esas bandas.
        if (medio < -50) return new MedidaEspectro(EstadoCorte.SinDatos, 0, 0);

        // Mediana de 3 bandas: un pico aislado (un pitido de 19 kHz en un efecto) no es «contenido».
        var s = new double[n];
        for (var j = 0; j < n; j++)
        {
            double x = b[Math.Max(0, j - 1)], y = b[j], z = b[Math.Min(n - 1, j + 1)];
            s[j] = Math.Max(Math.Min(x, y), Math.Min(Math.Max(x, y), z));
        }

        // Máximo de lo que queda por encima de cada banda, de arriba abajo (para no recorrerlo cada vez).
        var maxDesde = new double[n + 1];
        maxDesde[arriba + 1] = double.NegativeInfinity;
        for (var j = arriba; j >= 0; j--) maxDesde[j] = Math.Max(s[j], maxDesde[j + 1]);

        const int debajo = 8;   // 800 Hz por debajo del candidato
        const int hueco = 2;    // y se mira desde 200 Hz por encima: la caída del filtro no es vertical
        var desde = Math.Max(debajo, (int)(CorteMinimoHz / AnchoBanda));
        for (var j = desde; j <= arriba - hueco; j++)
        {
            var nivel = Mediana(s, j - debajo, j);
            if (nivel - maxDesde[j + hueco] < CaidaMinimaDb) continue;

            // El corte, donde el nivel cae de verdad: la última banda que sigue a menos de 6 dB del
            // nivel de debajo, más una.
            var k = j + hueco - 1;
            while (k > j - debajo && s[k] < nivel - 6) k--;
            var corte = (k + 1) * AnchoBanda;

            // Con agudos débiles, un corte bajo no dice nada: el codificador los habría quitado a
            // cualquier tasa. Uno alto sí: al menos llegó hasta ahí, que es lo que se quiere saber.
            // MEDIDO: exigirlo también a los cortes altos dejaba «no concluyentes» el 10 % de los 320
            // (agudos a −35/−50 dB justo antes de los 20 kHz), todos ellos coherentes.
            if (corte < Limite320Hz && nivel < medio - FuerzaMinimaDb)
                return new MedidaEspectro(EstadoCorte.NoConcluyente, 0, techo);
            return new MedidaEspectro(EstadoCorte.Corte, corte, techo);
        }
        return new MedidaEspectro(EstadoCorte.SinCorte, 0, techo);
    }

    /// <summary>
    /// Compara el corte con lo que el archivo declara: su formato (sin pérdida o no) y su tasa en kbps.
    ///
    ///   Sospechosa  declara 224 kbps o más (o sin pérdida) y el audio acaba antes de 17,8 kHz: viene
    ///               de 160 kbps o menos.
    ///   Dudosa      declara 300 kbps o más y el corte está entre 17,8 y 19,5 kHz. MEDIDO: es lo que
    ///               hace a veces el propio codificador de Windows a 320 (4 de 36 codificaciones, entre
    ///               18,1 y 18,9 kHz), así que no basta para sospechar.
    ///               Declara sin pérdida y el corte está por debajo de 20,8 kHz (probable origen MP3).
    ///               Declara 160-223 kbps y el audio acaba antes de 17,8 kHz (posible origen de 128).
    ///   Coherente   el resto.
    /// </summary>
    public static ValoracionCalidad Valorar(MedidaEspectro m, bool sinPerdida, int kbps)
    {
        if (m.AnchoReal is not double ancho) return ValoracionCalidad.SinValorar;
        if (sinPerdida)
            return ancho < LimiteOrigenBajoHz ? ValoracionCalidad.Sospechosa
                 : ancho < LimiteSinPerdidaHz && m.Estado == EstadoCorte.Corte ? ValoracionCalidad.Dudosa
                 : ValoracionCalidad.Coherente;
        if (kbps >= 224 && ancho < LimiteOrigenBajoHz) return ValoracionCalidad.Sospechosa;
        if (kbps >= 300 && ancho < Limite320Hz) return ValoracionCalidad.Dudosa;
        if (kbps >= 160 && ancho < LimiteOrigenBajoHz) return ValoracionCalidad.Dudosa;
        return ValoracionCalidad.Coherente;
    }

    /// <summary>La frecuencia medida, para la columna «Corte medido».</summary>
    public static string TextoCorte(MedidaEspectro m) => m.Estado switch
    {
        EstadoCorte.Corte => Khz(m.CorteHz),
        EstadoCorte.SinCorte => "Sin corte",
        EstadoCorte.NoConcluyente => "No concluyente",
        _ => "Sin datos",
    };

    /// <summary>
    /// La calidad que corresponde a ese corte, para la columna «Calidad real (estimada)». Es una
    /// estimación: nombra la tasa a la que suele cortar un codificador ahí, no la que tuvo el archivo.
    /// </summary>
    public static string TextoCalidadReal(MedidaEspectro m, bool sinPerdida)
    {
        if (m.AnchoReal is not double ancho) return "";
        if (ancho < LimiteOrigenBajoHz) return "≈ 128 kbps o menos";
        if (ancho < Limite192Hz) return "≈ 192 kbps";
        if (ancho < Limite320Hz) return "≈ 256 kbps";
        if (m.Estado == EstadoCorte.SinCorte || ancho >= LimiteSinPerdidaHz)
            return sinPerdida ? "Sin pérdida" : "≈ 320 kbps o más";
        return "≈ 320 kbps";
    }

    /// <summary>Texto corto de la valoración, para acompañar a la estimación.</summary>
    public static string TextoValoracion(ValoracionCalidad v) => v switch
    {
        ValoracionCalidad.Sospechosa => "Sospechoso",
        ValoracionCalidad.Dudosa => "Dudoso",
        ValoracionCalidad.Coherente => "Coherente",
        _ => "",
    };

    private static string Khz(double hz)
        => (hz / 1000.0).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("es-ES")) + " kHz";

    private static double Mediana(double[] v, int desde, int hasta)
    {
        var n = hasta - desde;
        if (n <= 0) return double.NaN;
        var t = new double[n];
        Array.Copy(v, desde, t, 0, n);
        Array.Sort(t);
        return t[n / 2];
    }
}
