using Etiquetador.Core.Analysis;
using Xunit;

namespace Etiquetador.Tests;

/// <summary>
/// Conformidad del medidor con EBU Tech 3341: un tono de 1 kHz a -X dBFS debe medir -X LUFS
/// (±0,1). Si esto falla, cualquier normalización posterior estaría mal calculada.
/// </summary>
public class LoudnessMeterTests
{
    /// <summary>Genera un seno de 1 kHz al nivel pedido y lo mide.</summary>
    private static LoudnessResult MedirSeno(double dbfs, int sampleRate = 48000, int channels = 2,
        double segundos = 10.0)
    {
        var meter = new LoudnessMeter(sampleRate, channels);
        var amp = Math.Pow(10.0, dbfs / 20.0);
        var total = (int)(sampleRate * segundos);
        var buf = new float[channels * 1024];

        int escritas = 0;
        while (escritas < total)
        {
            var n = Math.Min(1024, total - escritas);
            for (int i = 0; i < n; i++)
            {
                var t = (escritas + i) / (double)sampleRate;
                var v = (float)(amp * Math.Sin(2 * Math.PI * 1000.0 * t));
                for (int c = 0; c < channels; c++) buf[i * channels + c] = v;
            }
            meter.Add(buf, n * channels);
            escritas += n;
        }
        return meter.GetResult();
    }

    [Theory]
    [InlineData(-23.0)]
    [InlineData(-20.0)]
    [InlineData(-14.0)]
    [InlineData(-40.0)]
    public void Un_tono_de_1khz_mide_su_propio_nivel(double dbfs)
    {
        var r = MedirSeno(dbfs);
        Assert.True(Math.Abs(r.Lufs - dbfs) < 0.15,
            $"esperaba ~{dbfs} LUFS y midió {r.Lufs:0.00}");
    }

    [Fact]
    public void Funciona_igual_a_44100_que_es_lo_habitual_en_mp3()
    {
        var r = MedirSeno(-23.0, sampleRate: 44100);
        Assert.True(Math.Abs(r.Lufs - (-23.0)) < 0.15, $"midió {r.Lufs:0.00}");
    }

    // Un archivo mono se mide como «mono doble» (EBU Tech 3344, EBUR128_DUAL_MONO de libebur128): un
    // programa de DJ lo envía por los dos altavoces y suena igual que un estéreo con los dos canales
    // iguales. Antes medía 3,01 LU menos, y el ajuste lo subía dos pasos de más.
    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void En_mono_mide_igual_que_en_estereo(int fs)
    {
        var estereo = MedirSeno(-23.0, sampleRate: fs, channels: 2);
        var mono = MedirSeno(-23.0, sampleRate: fs, channels: 1);
        Assert.True(Math.Abs(estereo.Lufs - mono.Lufs) < 0.02,
            $"estéreo {estereo.Lufs:0.000} vs mono {mono.Lufs:0.000}");
    }

    // ---------- EBU Tech 3341, casos 1 a 5 (sonoridad integrada, tolerancia ±0,1 LU) ----------
    //
    // Todo el ajuste de volumen depende de esta medida. Cumple con un error máximo de 0,024 LU, pero
    // las pruebas de arriba (un tono a ±0,15 y una puerta con un rango amplio) no detectarían que un
    // cambio en el filtro o en las puertas la empeorase hasta 0,1-2 LU. Estas, sí: los casos 3 y 4
    // ejercitan la puerta relativa y la absoluta, y el 5, la integración con niveles distintos.

    /// <summary>
    /// Seno estéreo de 1 kHz con la fase continua y el nivel de cada tramo (dBFS por canal), como las
    /// señales de prueba de la norma. Se genera con una tabla de un periodo entero para que la prueba
    /// tarde poco.
    /// </summary>
    private static LoudnessResult MedirTramos(int fs, params (double Segundos, double Dbfs)[] tramos)
    {
        var periodo = fs / Mcd(fs, 1000);   // 48 muestras a 48 kHz; 441 (10 ciclos) a 44,1 kHz
        var tabla = new double[periodo];
        for (int i = 0; i < periodo; i++) tabla[i] = Math.Sin(2 * Math.PI * 1000.0 * i / fs);

        var meter = new LoudnessMeter(fs, 2);
        var buf = new float[2 * 4800];
        long n = 0;
        foreach (var (segundos, dbfs) in tramos)
        {
            var amp = Math.Pow(10.0, dbfs / 20.0);
            var total = (long)Math.Round(segundos * fs);
            for (long hechas = 0; hechas < total;)
            {
                var k = (int)Math.Min(4800, total - hechas);
                for (int i = 0; i < k; i++)
                {
                    var v = (float)(amp * tabla[(int)((n + i) % periodo)]);
                    buf[2 * i] = v;
                    buf[2 * i + 1] = v;
                }
                meter.Add(buf, 2 * k);
                hechas += k;
                n += k;
            }
        }
        return meter.GetResult();
    }

    private static int Mcd(int a, int b) => b == 0 ? a : Mcd(b, a % b);

    public static TheoryData<int, int> Casos3341()
    {
        var d = new TheoryData<int, int>();
        foreach (var fs in new[] { 48000, 44100 })
            for (int caso = 1; caso <= 5; caso++) d.Add(fs, caso);
        return d;
    }

    [Theory]
    [MemberData(nameof(Casos3341))]
    public void Cumple_EBU_Tech_3341_casos_1_a_5(int fs, int caso)
    {
        var (esperado, tramos) = caso switch
        {
            1 => (-23.0, new[] { (20.0, -23.0) }),
            2 => (-33.0, new[] { (20.0, -33.0) }),
            3 => (-23.0, new[] { (10.0, -36.0), (60.0, -23.0), (10.0, -36.0) }),
            4 => (-23.0, new[] { (10.0, -72.0), (10.0, -36.0), (60.0, -23.0), (10.0, -36.0), (10.0, -72.0) }),
            _ => (-23.0, new[] { (20.0, -26.0), (20.1, -20.0), (20.0, -26.0) }),
        };

        var r = MedirTramos(fs, tramos);

        Assert.True(Math.Abs(r.Lufs - esperado) <= 0.1,
            $"caso {caso} a {fs} Hz: esperaba {esperado} ±0,1 LUFS y midió {r.Lufs:0.000}");
    }

    [Fact]
    public void El_pico_se_mide_bien()
    {
        var r = MedirSeno(-6.0);
        Assert.True(Math.Abs(r.PeakDb - (-6.0)) < 0.1, $"pico {r.PeakDb:0.00}");
    }

    [Fact]
    public void El_silencio_no_da_una_medida_valida()
    {
        var meter = new LoudnessMeter(48000, 2);
        meter.Add(new float[48000 * 2], 48000 * 2);   // medio segundo de silencio
        Assert.False(meter.GetResult().Ok);
    }

    [Fact]
    public void La_puerta_ignora_los_silencios_largos()
    {
        // Música real: pasajes con sonido y silencios. Los silencios NO deben bajar la medida.
        // Se alternan tramos de 1 SEGUNDO (48000 muestras), no trocitos: si no, cada bloque de
        // 400 ms saldría mitad tono mitad silencio y la prueba no mediría lo que pretende.
        const int fs = 48000;
        var meter = new LoudnessMeter(fs, 2);
        var amp = Math.Pow(10.0, -23.0 / 20.0);
        var buf = new float[fs * 2];
        for (int seg = 0; seg < 10; seg++)
        {
            var silencio = seg % 2 == 1;
            for (int i = 0; i < fs; i++)
            {
                var t = (seg * fs + i) / (double)fs;
                var v = silencio ? 0f : (float)(amp * Math.Sin(2 * Math.PI * 1000.0 * t));
                buf[i * 2] = v; buf[i * 2 + 1] = v;
            }
            meter.Add(buf, fs * 2);
        }
        var r = meter.GetResult();
        // Sin puerta, la mitad de silencio restaría 3 dB (-26 LUFS). Con la puerta debe quedarse
        // pegado a -23. No sale exacto porque los bloques a caballo entre tono y silencio son
        // parciales y sí cuentan: eso es lo que dice el estándar, no un fallo de la medida.
        Assert.True(r.Lufs > -25.0 && r.Lufs <= -22.9,
            $"la puerta debería dejarlo cerca de -23 (y muy lejos de los -26 sin puerta), midió {r.Lufs:0.00}");
    }

    // Revisión final: a 22,05 kHz en estéreo, el búfer de 250 ms de LoudnessScanner tenía 11.025
    // muestras. El medidor descartaba la sobrante y el búfer siguiente empezaba por el canal derecho
    // tomándolo por el izquierdo. Ahora lo que falta de una trama llega con el siguiente búfer, y medir
    // por trozos impares da exactamente lo mismo que de una vez.
    [Fact]
    public void Una_trama_partida_entre_dos_buferes_no_cruza_los_canales()
    {
        const int fs = 22050, porBufer = 11025;
        var x = new float[porBufer * 40];   // 10 s en estéreo
        for (int i = 0; i < x.Length / 2; i++)
        {
            x[2 * i] = (float)(0.3 * Math.Sin(2 * Math.PI * 997 * i / fs));   // izquierdo
            x[2 * i + 1] = (float)(0.05 * Math.Sin(2 * Math.PI * 211 * i / fs));   // derecho, más bajo
        }
        x[porBufer - 1] = 0.9f;   // un pico justo en la muestra que sobraba del primer búfer

        var entero = new LoudnessMeter(fs, 2);
        entero.Add(x, x.Length);
        var partido = new LoudnessMeter(fs, 2);
        for (int i = 0; i < x.Length; i += porBufer) partido.Add(x[i..(i + porBufer)], porBufer);

        var a = entero.GetResult();
        var b = partido.GetResult();
        Assert.Equal(a.Lufs, b.Lufs);
        Assert.Equal(a.PeakDb, b.PeakDb);
        Assert.Equal(a.Seconds, b.Seconds);
    }
}
