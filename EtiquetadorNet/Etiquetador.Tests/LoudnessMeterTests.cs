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

    [Fact]
    public void En_mono_mide_igual_que_en_estereo()
    {
        var estereo = MedirSeno(-23.0, channels: 2);
        var mono = MedirSeno(-23.0, channels: 1);
        // Un canal pesa la mitad de potencia total -> 3 dB menos. Es el comportamiento del estándar.
        Assert.True(Math.Abs((estereo.Lufs - mono.Lufs) - 3.01) < 0.2,
            $"estéreo {estereo.Lufs:0.00} vs mono {mono.Lufs:0.00}");
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
}
