using Etiquetador.Core.Analysis;
using Xunit;

namespace Etiquetador.Tests;

/// <summary>
/// Pico real (ITU-R BS.1770-4, anexo 2). Las subidas de volumen se limitan con él: si midiera de
/// menos, el ajuste dejaría canciones por encima de 0 dBTP, con distorsión al reproducirlas.
/// </summary>
public class TruePeakMeterTests
{
    /// <summary>
    /// Seno a una fracción de la frecuencia de muestreo, con la fase y la amplitud dadas, durante
    /// 5 s. El primer medio segundo no cuenta: el arranque brusco de la señal produce un pico de
    /// transitorio que no forma parte de la prueba (las señales de la norma tampoco empiezan así).
    /// </summary>
    private static double PicoDeSeno(int fs, double fraccion, double faseGrados, double amp, int canales = 2)
    {
        var m = new TruePeakMeter(canales);
        var total = 5 * fs;
        var buf = new float[canales * 1000];
        for (int n0 = 0; n0 < total; n0 += 1000)
        {
            for (int i = 0; i < 1000; i++)
            {
                var v = (float)(amp * Math.Sin(2 * Math.PI * fraccion * (n0 + i) + faseGrados * Math.PI / 180));
                for (int c = 0; c < canales; c++) buf[i * canales + c] = v;
            }
            m.Add(buf, buf.Length);
            if (n0 + 1000 <= fs / 2) m.ReiniciarMaximo();
        }
        return m.Db;
    }

    // EBU Tech 3341, casos 15 a 19: el pico real cae entre muestras. Tolerancia de la norma +0,2/−0,4 dB.
    [Theory]
    [InlineData(48000, 1 / 4.0, 0.0, 0.5, -6.0)]     // 15: fs/4, el pico coincide con una muestra
    [InlineData(48000, 1 / 4.0, 45.0, 0.5, -6.0)]    // 16: fs/4 a 45°: las muestras se quedan en −9 dB
    [InlineData(48000, 1 / 6.0, 60.0, 0.5, -6.0)]    // 17
    [InlineData(48000, 1 / 8.0, 67.5, 0.5, -6.0)]    // 18
    [InlineData(48000, 1 / 4.0, 45.0, 1.41, 3.0)]    // 19: muestras a 0 dBFS y pico real de +3 dBTP
    [InlineData(44100, 1 / 4.0, 0.0, 0.5, -6.0)]
    [InlineData(44100, 1 / 4.0, 45.0, 0.5, -6.0)]
    [InlineData(44100, 1 / 6.0, 60.0, 0.5, -6.0)]
    [InlineData(44100, 1 / 8.0, 67.5, 0.5, -6.0)]
    [InlineData(44100, 1 / 4.0, 45.0, 1.41, 3.0)]
    public void Cumple_EBU_Tech_3341_casos_15_a_19(int fs, double fraccion, double fase, double amp, double esperado)
    {
        var db = PicoDeSeno(fs, fraccion, fase, amp);
        Assert.True(db <= esperado + 0.2 && db >= esperado - 0.4, $"esperaba {esperado} dBTP (+0,2/−0,4) y midió {db:0.000}");
    }

    // El caso que motiva todo: muestras a −9 dBFS y onda a −6. El pico de muestra se queda 3 dB corto.
    [Fact]
    public void El_pico_real_ve_lo_que_el_pico_de_muestra_no_ve()
    {
        var real = PicoDeSeno(48000, 1 / 4.0, 45, 0.5);
        var muestra = 20 * Math.Log10(0.5 * Math.Sin(Math.PI / 4));
        Assert.True(real - muestra > 2.5, $"pico real {real:0.00}, de muestra {muestra:0.00}");
    }

    // Nunca por debajo del pico de muestra (una muestra aislada saldría al 97,2 % por la fase 0).
    [Fact]
    public void Nunca_da_menos_que_el_pico_de_muestra()
    {
        var m = new TruePeakMeter(1);
        var buf = new float[100];
        buf[50] = 0.8f;
        m.Add(buf, buf.Length);
        Assert.True(m.Db >= 20 * Math.Log10(0.8) - 1e-9, $"midió {m.Db:0.000}");
    }

    [Fact]
    public void El_silencio_no_tiene_pico()
    {
        var m = new TruePeakMeter(2);
        m.Add(new float[2000], 2000);
        Assert.True(double.IsNegativeInfinity(m.Db));
    }

    // ---------- La variante rápida da EXACTAMENTE lo mismo que interpolar en todas las muestras ----------

    /// <summary>50 señales de prueba con semilla fija: lo que más le cuesta al atajo.</summary>
    public static TheoryData<int> Semillas()
    {
        var d = new TheoryData<int>();
        for (int s = 0; s < 50; s++) d.Add(s);
        return d;
    }

    private static float[] Senal(int semilla, int canales)
    {
        var rnd = new Random(semilla);
        var n = 20000 + rnd.Next(20000);
        var x = new float[n * canales];
        switch (semilla % 5)
        {
            case 0:   // ruido blanco a un nivel al azar
            {
                var amp = 0.05 + rnd.NextDouble();
                for (int i = 0; i < x.Length; i++) x[i] = (float)(amp * (2 * rnd.NextDouble() - 1));
                break;
            }
            case 1:   // seno cerca de fs/4 con fase al azar: los picos caen entre muestras
            {
                var f = 0.25 + (rnd.NextDouble() - 0.5) * 0.02;
                var fase = rnd.NextDouble() * 2 * Math.PI;
                var amp = 0.3 + rnd.NextDouble() * 0.9;
                for (int i = 0; i < n; i++)
                    for (int c = 0; c < canales; c++) x[i * canales + c] = (float)(amp * Math.Sin(2 * Math.PI * f * i + fase + c));
                break;
            }
            case 2:   // alternancias ±1 en tramos: el peor caso de sobreoscilación del filtro
            {
                for (int i = 0; i < n; i++)
                    for (int c = 0; c < canales; c++)
                        x[i * canales + c] = (i / 37 + c) % 3 == 0 ? (i % 2 == 0 ? 1f : -1f) : (float)(0.1 * (2 * rnd.NextDouble() - 1));
                break;
            }
            case 3:   // ráfagas tras silencio, cada vez más fuertes: el máximo sube a saltos
            {
                for (int r = 0; r < 8; r++)
                {
                    var ini = rnd.Next(n - 200);
                    var amp = 0.1 + 0.11 * r + rnd.NextDouble() * 0.05;
                    for (int i = ini; i < ini + 150; i++)
                        for (int c = 0; c < canales; c++) x[i * canales + c] = (float)(amp * (2 * rnd.NextDouble() - 1));
                }
                break;
            }
            default:  // música «comprimida»: seno grave con ruido y algún pico al tope
            {
                for (int i = 0; i < n; i++)
                    for (int c = 0; c < canales; c++)
                    {
                        var v = 0.6 * Math.Sin(2 * Math.PI * 0.003 * i) + 0.3 * (2 * rnd.NextDouble() - 1);
                        x[i * canales + c] = (float)Math.Clamp(v, -1, 1);
                    }
                break;
            }
        }
        return x;
    }

    [Theory]
    [MemberData(nameof(Semillas))]
    public void La_variante_rapida_da_lo_mismo_bit_a_bit(int semilla)
    {
        var canales = semilla % 2 == 0 ? 2 : 1;
        var x = Senal(semilla, canales);
        var rapida = new TruePeakMeter(canales);
        var completa = new TruePeakMeter(canales, completo: true);

        // En trozos de tamaño variable, como llegan del decodificador.
        var rnd = new Random(semilla + 1000);
        for (int i = 0; i < x.Length;)
        {
            var k = Math.Min(x.Length - i, canales * (1 + rnd.Next(3000)));
            var trozo = x[i..(i + k)];
            rapida.Add(trozo, k);
            completa.Add(trozo, k);
            i += k;
        }

        Assert.Equal(completa.Db, rapida.Db);   // exactamente el mismo double
        Assert.True(rapida.Interpoladas <= completa.Interpoladas);
    }

    // Revisión final: un búfer no tiene por qué acabar en una trama completa. A 22,05 kHz en estéreo, el
    // de 250 ms tenía 11.025 muestras: se descartaba la sobrante, el siguiente empezaba por el canal
    // derecho tomándolo por el izquierdo, y el historial del filtro mezclaba los dos canales. En trozos
    // de cualquier tamaño, también impar, el resultado tiene que ser el de medirlo de una vez.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Una_trama_partida_entre_dos_buferes_da_lo_mismo_que_entera(int semilla)
    {
        var x = Senal(semilla, 2);
        var entero = new TruePeakMeter(2);
        entero.Add(x, x.Length);

        var partido = new TruePeakMeter(2);
        var rnd = new Random(semilla + 2000);
        for (int i = 0; i < x.Length;)
        {
            var k = Math.Min(x.Length - i, 1 + rnd.Next(3001));
            partido.Add(x[i..(i + k)], k);
            i += k;
        }

        Assert.Equal(entero.Db, partido.Db);   // exactamente el mismo double
    }

    // El caso de la revisión: un transitorio justo en la muestra que sobraba del primer búfer de 11.025.
    // Antes no contaba: el pico real salía a −26 dBTP (el del resto de la señal) en vez de −0,45.
    [Fact]
    public void Un_transitorio_en_la_muestra_sobrante_cuenta()
    {
        const int porBufer = 11025;
        var x = new float[porBufer * 4];
        for (int i = 0; i < x.Length; i += 2) x[i] = 0.05f;   // izquierdo a 0,05; derecho en silencio
        x[porBufer - 1] = 0.95f;                              // la última muestra del primer búfer (canal izquierdo)

        var m = new TruePeakMeter(2);
        for (int i = 0; i < x.Length; i += porBufer) m.Add(x[i..(i + porBufer)], porBufer);

        Assert.True(m.Db >= 20 * Math.Log10(0.95f) - 1e-6, $"pico real {m.Db:0.00} dBTP");
    }

    // Y el atajo ahorra trabajo de verdad en una señal con dinámica (ráfagas tras silencio).
    [Fact]
    public void La_variante_rapida_interpola_una_fraccion_de_las_muestras()
    {
        var x = Senal(3, 2);
        var rapida = new TruePeakMeter(2);
        var completa = new TruePeakMeter(2, completo: true);
        rapida.Add(x, x.Length);
        completa.Add(x, x.Length);
        Assert.Equal(completa.Db, rapida.Db);
        Assert.True(rapida.Interpoladas < completa.Interpoladas / 2,
            $"interpoló {rapida.Interpoladas} de {completa.Interpoladas}");
    }
}
